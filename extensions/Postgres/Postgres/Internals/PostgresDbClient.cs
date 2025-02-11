// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory.Diagnostics;
using Microsoft.KernelMemory.MemoryStorage;
using Npgsql;
using NpgsqlTypes;
using Pgvector;

namespace Microsoft.KernelMemory.Postgres;

/// <summary>
/// An implementation of a client for Postgres. This class is used to managing postgres database operations.
/// </summary>
internal sealed class PostgresDbClient : IDisposable, IAsyncDisposable
{
    // Dependencies
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger _log;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresDbClient"/> class.
    /// </summary>
    /// <param name="config">Configuration</param>
    /// <param name="loggerFactory">Application logger factory</param>
    public PostgresDbClient(PostgresConfig config, ILoggerFactory? loggerFactory = null)
    {
        config.Validate();
        this._log = (loggerFactory ?? DefaultLogger.Factory).CreateLogger<PostgresDbClient>();

        NpgsqlDataSourceBuilder dataSourceBuilder = new(config.ConnectionString);
        dataSourceBuilder.UseVector();
        this._dataSource = dataSourceBuilder.Build();

        this._dbNamePresent = config.ConnectionString.Contains("Database=", StringComparison.OrdinalIgnoreCase);
        this._schema = config.Schema;
        this._tableNamePrefix = config.TableNamePrefix;
        this._textSearchLanguage = config.TextSearchLanguage;
        this._rrf_K = config.RRFK;

        this._colId = config.Columns[PostgresConfig.ColumnId];
        this._colEmbedding = config.Columns[PostgresConfig.ColumnEmbedding];
        this._colTags = config.Columns[PostgresConfig.ColumnTags];
        this._colContent = config.Columns[PostgresConfig.ColumnContent];
        this._colPayload = config.Columns[PostgresConfig.ColumnPayload];

        PostgresSchema.ValidateSchemaName(this._schema);
        PostgresSchema.ValidateTableNamePrefix(this._tableNamePrefix);
        PostgresSchema.ValidateFieldName(this._colId);
        PostgresSchema.ValidateFieldName(this._colEmbedding);
        PostgresSchema.ValidateFieldName(this._colTags);
        PostgresSchema.ValidateFieldName(this._colContent);
        PostgresSchema.ValidateFieldName(this._colPayload);

        this._columnsListNoEmbeddings = $"{this._colId},{this._colTags},{this._colContent},{this._colPayload}";
        this._columnsListWithEmbeddings = $"{this._colId},{this._colTags},{this._colContent},{this._colPayload},{this._colEmbedding}";
        this._columnsListHybrid = $"{this._colId},{this._colTags},{this._colContent},{this._colPayload},{this._colEmbedding}";
        this._columnsListHybridCoalesce = $@"
                                            COALESCE(semantic_search.{this._colId}, keyword_search.{this._colId}) AS {this._colId},
                                            COALESCE(semantic_search.{this._colTags}, keyword_search.{this._colTags}) AS {this._colTags},
                                            COALESCE(semantic_search.{this._colContent}, keyword_search.{this._colContent}) AS {this._colContent},
                                            COALESCE(semantic_search.{this._colPayload}, keyword_search.{this._colPayload}) AS {this._colPayload},
                                            COALESCE(semantic_search.{this._colEmbedding}, keyword_search.{this._colEmbedding}) AS {this._colEmbedding}
                                         ";

        this._createTableSql = string.Empty;
        if (config.CreateTableSql?.Count > 0)
        {
            this._createTableSql = string.Join('\n', config.CreateTableSql).Trim();
        }
    }

    /// <summary>
    /// Check if a table exists.
    /// </summary>
    /// <param name="tableName">The name assigned to a table of entries</param>
    /// <param name="cancellationToken">Async task cancellation token</param>
    /// <returns>True if the table exists</returns>
    public async Task<bool> DoesTableExistAsync(
        string tableName,
        CancellationToken cancellationToken = default)
    {
        tableName = this.WithTableNamePrefix(tableName);
        this._log.LogTrace("Checking if table {0} exists", tableName);

        NpgsqlConnection connection = await this.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using (connection)
        {
            try
            {
                NpgsqlCommand cmd = connection.CreateCommand();
                await using (cmd.ConfigureAwait(false))
                {
#pragma warning disable CA2100 // SQL reviewed
                    cmd.CommandText = $@"
                        SELECT table_name
                        FROM information_schema.tables
                            WHERE table_schema = @schema
                                AND table_name = @table
                                AND table_type = 'BASE TABLE'
                        LIMIT 1
                    ";

                    cmd.Parameters.AddWithValue("@schema", this._schema);
                    cmd.Parameters.AddWithValue("@table", tableName);
#pragma warning restore CA2100

                    this._log.LogTrace("Schema: {0}, Table: {1}, SQL: {2}", this._schema, tableName, cmd.CommandText);

                    NpgsqlDataReader dataReader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    await using (dataReader.ConfigureAwait(false))
                    {
                        if (await dataReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            var name = dataReader.GetString(dataReader.GetOrdinal("table_name"));

                            return string.Equals(name, tableName, StringComparison.OrdinalIgnoreCase);
                        }

                        this._log.LogTrace("Table {0} does not exist", tableName);
                        return false;
                    }
                }
            }
            finally
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Create a table.
    /// </summary>
    /// <param name="tableName">The name assigned to a table of entries</param>
    /// <param name="vectorSize">Embedding vectors dimension</param>
    /// <param name="cancellationToken">Async task cancellation token</param>
    public async Task CreateTableAsync(
        string tableName,
        int vectorSize,
        CancellationToken cancellationToken = default)
    {
        var origInputTableName = tableName;
        var indexTags = this.WithTableNamePrefix(tableName) + "_idx_tags";
        var indexContent = this.WithTableNamePrefix(tableName) + "_idx_content";
        tableName = this.WithSchemaAndTableNamePrefix(tableName);
        this._log.LogTrace("Creating table: {0}", tableName);

        Npgsql.PostgresException? createErr = null;

        NpgsqlConnection connection = await this.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using (connection)
        {
            try
            {
                NpgsqlCommand cmd = connection.CreateCommand();
                await using (cmd.ConfigureAwait(false))
                {
                    var lockId = GenLockId(tableName);

#pragma warning disable CA2100 // SQL reviewed
                    if (!string.IsNullOrEmpty(this._createTableSql))
                    {
                        cmd.CommandText = this._createTableSql
                            .Replace(PostgresConfig.SqlPlaceholdersTableName, tableName, StringComparison.Ordinal)
                            .Replace(PostgresConfig.SqlPlaceholdersVectorSize, $"{vectorSize}", StringComparison.Ordinal)
                            .Replace(PostgresConfig.SqlPlaceholdersLockId, $"{lockId}", StringComparison.Ordinal);

                        this._log.LogTrace("Creating table with custom SQL: {0}", cmd.CommandText);
                    }
                    else
                    {
                        cmd.CommandText = $@"
                            BEGIN;
                            SELECT pg_advisory_xact_lock({lockId});
                            CREATE TABLE IF NOT EXISTS {tableName} (
                                {this._colId}        TEXT NOT NULL PRIMARY KEY,
                                {this._colEmbedding} vector({vectorSize}),
                                {this._colTags}      TEXT[] DEFAULT '{{}}'::TEXT[] NOT NULL,
                                {this._colContent}   TEXT DEFAULT '' NOT NULL,
                                {this._colPayload}   JSONB DEFAULT '{{}}'::JSONB NOT NULL
                            );
                            CREATE INDEX IF NOT EXISTS ""{indexTags}"" ON {tableName} USING GIN({this._colTags});
                            CREATE INDEX IF NOT EXISTS ""{indexContent}"" ON {tableName} USING GIN(to_tsvector('{this._textSearchLanguage}',{this._colContent}));
                            COMMIT;
                        ";
#pragma warning restore CA2100

                        this._log.LogTrace("Creating table with default SQL: {0}", cmd.CommandText);
                    }

                    int result = await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    this._log.LogTrace("Table '{0}' creation result: {1}", tableName, result);
                }
            }
            catch (Npgsql.PostgresException e) when (IsVectorTypeDoesNotExistException(e))
            {
                this._log.LogError(e, "Vector type not installed, check 'SELECT * FROM pg_extension'");
                throw;
            }
            catch (Npgsql.PostgresException e) when (e.SqlState == PgErrUniqueViolation)
            {
                createErr = e;
            }
            catch (Exception e)
            {
                this._log.LogError(e, "Table '{0}' creation error: {1}. Err: {2}. InnerEx: {3}", tableName, e, e.Message, e.InnerException);
                throw;
            }
            finally
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }

        if (createErr != null)
        {
            // If the table exists, assume the table state is fine, logs some warnings, and continue
            if (await this.DoesTableExistAsync(origInputTableName, cancellationToken).ConfigureAwait(false))
            {
                // Check if the custom SQL contains the lock placeholder (assuming it's not commented out)
                bool missingLockStatement = !string.IsNullOrEmpty(this._createTableSql)
                                            && !this._createTableSql.Contains(PostgresConfig.SqlPlaceholdersLockId, StringComparison.Ordinal);

                if (missingLockStatement)
                {
                    this._log.LogWarning(
                        "Concurrency error: {0}; {1}; {2}. Add '{3}' to the custom SQL statement used to create tables. The table exists so the application will continue",
                        createErr.SqlState, createErr.Message, createErr.Detail, PostgresConfig.SqlPlaceholdersLockId);
                }
                else
                {
                    this._log.LogWarning("Postgres error while creating table: {0}; {1}; {2}. The table exists so the application will continue",
                        createErr.SqlState, createErr.Message, createErr.Detail);
                }
            }
            else
            {
                // But if the table doesn't exist, throw
                this._log.LogError(createErr, "Table creation failed: {0}", tableName);
                throw createErr;
            }
        }
    }

    /// <summary>
    /// Get all tables
    /// </summary>
    /// <param name="cancellationToken">Async task cancellation token</param>
    /// <returns>A group of tables</returns>
    public async IAsyncEnumerable<string> GetTablesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        NpgsqlConnection connection = await this.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using (connection)
        {
            try
            {
                NpgsqlCommand cmd = connection.CreateCommand();
                await using (cmd.ConfigureAwait(false))
                {
                    cmd.CommandText = @"SELECT table_name FROM information_schema.tables
                                WHERE table_schema = @schema AND table_type = 'BASE TABLE';";
                    cmd.Parameters.AddWithValue("@schema", this._schema);

                    this._log.LogTrace("Fetching list of tables. SQL: {0}. Schema: {1}", cmd.CommandText, this._schema);

                    NpgsqlDataReader dataReader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    await using (dataReader.ConfigureAwait(false))
                    {
                        while (await dataReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            var tableNameWithPrefix = dataReader.GetString(dataReader.GetOrdinal("table_name"));
                            if (tableNameWithPrefix.StartsWith(this._tableNamePrefix, StringComparison.OrdinalIgnoreCase))
                            {
                                yield return tableNameWithPrefix.Remove(0, this._tableNamePrefix.Length);
                            }
                        }
                    }
                }
            }
            finally
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Delete a table.
    /// </summary>
    /// <param name="tableName">Name of the table to delete</param>
    /// <param name="cancellationToken">Async task cancellation token</param>
    public async Task DeleteTableAsync(
        string tableName,
        CancellationToken cancellationToken = default)
    {
        tableName = this.WithSchemaAndTableNamePrefix(tableName);

        NpgsqlConnection connection = await this.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using (connection)
        {
            try
            {
                NpgsqlCommand cmd = connection.CreateCommand();
                await using (cmd.ConfigureAwait(false))
                {
#pragma warning disable CA2100 // SQL reviewed
                    cmd.CommandText = $"DROP TABLE IF EXISTS {tableName}";
#pragma warning restore CA2100

                    this._log.LogTrace("Deleting table. SQL: {0}", cmd.CommandText);
                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Npgsql.PostgresException e) when (IsTableNotFoundException(e))
            {
                this._log.LogTrace("Table not found: {0}", tableName);
            }
            finally
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Upsert entry into a table.
    /// </summary>
    /// <param name="tableName">The name assigned to a table of entries</param>
    /// <param name="record">Record to create/update</param>
    /// <param name="cancellationToken">Async task cancellation token</param>
    public async Task UpsertAsync(
        string tableName,
        PostgresMemoryRecord record,
        CancellationToken cancellationToken = default)
    {
        tableName = this.WithSchemaAndTableNamePrefix(tableName);

        const string EmptyPayload = "{}";
        const string EmptyContent = "";
        string[] emptyTags = [];

        NpgsqlConnection connection = await this.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using (connection)
        {
            try
            {
                NpgsqlCommand cmd = connection.CreateCommand();
                await using (cmd.ConfigureAwait(false))
                {
#pragma warning disable CA2100 // SQL reviewed
                    cmd.CommandText = $@"
                        INSERT INTO {tableName}
                            ({this._colId}, {this._colEmbedding}, {this._colTags}, {this._colContent}, {this._colPayload})
                            VALUES
                            (@id, @embedding, @tags, @content, @payload)
                        ON CONFLICT ({this._colId})
                        DO UPDATE SET
                            {this._colEmbedding} = @embedding,
                            {this._colTags}      = @tags,
                            {this._colContent}   = @content,
                            {this._colPayload}   = @payload
                    ";

                    cmd.Parameters.AddWithValue("@id", record.Id);
                    cmd.Parameters.AddWithValue("@embedding", record.Embedding);
                    cmd.Parameters.AddWithValue("@tags", NpgsqlDbType.Array | NpgsqlDbType.Text, record.Tags.ToArray() ?? emptyTags);
                    cmd.Parameters.AddWithValue("@content", NpgsqlDbType.Text, CleanContent(record.Content) ?? EmptyContent);
                    cmd.Parameters.AddWithValue("@payload", NpgsqlDbType.Jsonb, record.Payload ?? EmptyPayload);
#pragma warning restore CA2100

                    this._log.LogTrace("Upserting record '{0}' in table '{1}'", record.Id, tableName);

                    await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Npgsql.PostgresException e) when (IsTableNotFoundException(e))
            {
                throw new IndexNotFoundException(e.Message, e);
            }
            catch (Exception e)
            {
                throw new PostgresException(e.Message, e);
            }
            finally
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Get a list of records
    /// </summary>
    /// <param name="tableName">Table containing the records to fetch</param>
    /// <param name="query">Prompt query. Only used in the case of hybrid search</param>
    /// <param name="target">Source vector to compare for similarity</param>
    /// <param name="minSimilarity">Minimum similarity threshold</param>
    /// <param name="filterSql">SQL filter to apply</param>
    /// <param name="sqlUserValues">List of user values passed with placeholders to avoid SQL injection</param>
    /// <param name="limit">Max number of records to retrieve</param>
    /// <param name="offset">Records to skip from the top</param>
    /// <param name="withEmbeddings">Whether to include embedding vectors</param>
    /// <param name="useHybridSearch">Whether to use hybrid search or vector search</param>
    /// <param name="cancellationToken">Async task cancellation token</param>
    public async IAsyncEnumerable<(PostgresMemoryRecord record, double similarity)> GetSimilarAsync(
        string tableName,
        string query,
        Vector target,
        double minSimilarity,
        string? filterSql = null,
        Dictionary<string, object>? sqlUserValues = null,
        int limit = 1,
        int offset = 0,
        bool withEmbeddings = false,
        bool useHybridSearch = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        tableName = this.WithSchemaAndTableNamePrefix(tableName);

        if (limit <= 0) { limit = int.MaxValue; }

        // Column names
        string columns = withEmbeddings ? this._columnsListWithEmbeddings : this._columnsListNoEmbeddings;


        // Filtering logic, including filter by similarity
        //
        filterSql = filterSql?.Trim().Replace(PostgresSchema.PlaceholdersTags, this._colTags, StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(filterSql))
        {
            filterSql = "TRUE";
        }

        string filterSqlHybridText = filterSql;

        var maxDistance = 1 - minSimilarity;
        filterSql += $" AND {this._colEmbedding} <=> @embedding < @maxDistance";

        if (sqlUserValues == null) { sqlUserValues = []; }

        this._log.LogTrace("Searching by similarity. Table: {0}. Threshold: {1}. Limit: {2}. Offset: {3}. Using SQL filter: {4}",
            tableName, minSimilarity, limit, offset, string.IsNullOrWhiteSpace(filterSql) ? "false" : "true");

        NpgsqlConnection connection = await this.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using (connection)
        {
            try
            {
                NpgsqlCommand cmd = connection.CreateCommand();
                await using (cmd.ConfigureAwait(false))
                {
#pragma warning disable CA2100 // SQL reviewed
                    string colDistance = "__distance";

                    if (useHybridSearch)
                    {
                        // When using 1 - (embedding <=> target) the index is not being used, therefore we calculate
                        // the similarity (1 - distance) later. Furthermore, colDistance can't be used in the WHERE clause.
                        cmd.CommandText = @$"
                        WITH semantic_search AS (
                            SELECT {this._columnsListHybrid}, RANK () OVER (ORDER BY {this._colEmbedding} <=> @embedding) AS rank
                            FROM {tableName}
                            WHERE {filterSql}
                            ORDER BY {this._colEmbedding} <=> @embedding
                            LIMIT @limit
                        ),
                        keyword_search AS (
                            SELECT {this._columnsListHybrid}, RANK () OVER (ORDER BY ts_rank_cd(to_tsvector('english', {this._colContent}), query) DESC)
                            FROM {tableName}, plainto_tsquery('english', @query) query
                            WHERE  {filterSqlHybridText} AND to_tsvector('english', {this._colContent}) @@ query
                            ORDER BY ts_rank_cd(to_tsvector('english', {this._colContent}), query) DESC
                            LIMIT @limit
                        )
                        SELECT
                            {this._columnsListHybridCoalesce},
                            COALESCE(1.0 / ({this._rrf_K} + semantic_search.rank), 0.0) +
                            COALESCE(1.0 / ({this._rrf_K} + keyword_search.rank), 0.0) AS {colDistance}
                        FROM semantic_search
                        FULL OUTER JOIN keyword_search ON semantic_search.{this._colId} = keyword_search.{this._colId}
                        ORDER BY {colDistance} DESC
                        LIMIT @limit
                        OFFSET @offset
                    ";
                        cmd.Parameters.AddWithValue("@query", query);
                        cmd.Parameters.AddWithValue("@minSimilarity", minSimilarity);
                    }
                    else
                    {
                        // When using 1 - (embedding <=> target) the index is not being used, therefore we calculate
                        // the similarity (1 - distance) later. Furthermore, colDistance can't be used in the WHERE clause.
                        cmd.CommandText = @$"
                            SELECT {columns}, {this._colEmbedding} <=> @embedding AS {colDistance}
                            FROM {tableName}
                            WHERE {filterSql}
                            ORDER BY {colDistance} ASC
                            LIMIT @limit
                            OFFSET @offset
                        ";
                    }

                    cmd.Parameters.AddWithValue("@embedding", target);
                    cmd.Parameters.AddWithValue("@maxDistance", maxDistance);
                    cmd.Parameters.AddWithValue("@limit", limit);
                    cmd.Parameters.AddWithValue("@offset", offset);

                    foreach (KeyValuePair<string, object> kv in sqlUserValues)
                    {
                        cmd.Parameters.AddWithValue(kv.Key, kv.Value);
                    }
#pragma warning restore CA2100
                    // TODO: rewrite code to stream results (need to combine yield and try-catch)
                    var result = new List<(PostgresMemoryRecord record, double similarity)>();
                    try
                    {
                        NpgsqlDataReader dataReader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                        await using (dataReader.ConfigureAwait(false))
                        {
                            while (await dataReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                            {
                                double distance = dataReader.GetDouble(dataReader.GetOrdinal(colDistance));
                                double similarity = 1 - distance;
                                result.Add((this.ReadEntry(dataReader, withEmbeddings), similarity));
                            }
                        }
                    }
                    catch (Npgsql.PostgresException e) when (IsTableNotFoundException(e))
                    {
                        this._log.LogTrace("Table not found: {0}", tableName);
                    }

                    // TODO: rewrite code to stream results (need to combine yield and try-catch)
                    foreach (var x in result)
                    {
                        yield return x;

                        // If requested cancel potentially long-running loop
                        if (cancellationToken is { IsCancellationRequested: true })
                        {
                            break;
                        }
                    }
                }
            }
            finally
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Get a list of records
    /// </summary>
    /// <param name="tableName">Table containing the records to fetch</param>
    /// <param name="filterSql">SQL filter to apply</param>
    /// <param name="sqlUserValues">List of user values passed with placeholders to avoid SQL injection</param>
    /// <param name="orderBySql">SQL to order the records</param>
    /// <param name="limit">Max number of records to retrieve</param>
    /// <param name="offset">Records to skip from the top</param>
    /// <param name="withEmbeddings">Whether to include embedding vectors</param>
    /// <param name="cancellationToken">Async task cancellation token</param>
    public async IAsyncEnumerable<PostgresMemoryRecord> GetListAsync(
        string tableName,
        string? filterSql = null,
        Dictionary<string, object>? sqlUserValues = null,
        string? orderBySql = null,
        int limit = 1,
        int offset = 0,
        bool withEmbeddings = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        tableName = this.WithSchemaAndTableNamePrefix(tableName);

        if (limit <= 0) { limit = int.MaxValue; }

        string columns = withEmbeddings ? this._columnsListWithEmbeddings : this._columnsListNoEmbeddings;

        // Filtering logic
        filterSql = filterSql?.Trim().Replace(PostgresSchema.PlaceholdersTags, this._colTags, StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(filterSql))
        {
            filterSql = "TRUE";
        }

        // Custom ordering
        if (string.IsNullOrWhiteSpace(orderBySql))
        {
            orderBySql = this._colId;
        }

        this._log.LogTrace("Fetching list of records. Table: {0}. Order by: {1}. Limit: {2}. Offset: {3}. Using SQL filter: {4}",
            tableName, orderBySql, limit, offset, string.IsNullOrWhiteSpace(filterSql) ? "false" : "true");

        NpgsqlConnection connection = await this.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using (connection)
        {
            try
            {
                NpgsqlCommand cmd = connection.CreateCommand();
                await using (cmd.ConfigureAwait(false))
                {
#pragma warning disable CA2100 // SQL reviewed
                    cmd.CommandText = @$"
                        SELECT {columns} FROM {tableName}
                        WHERE {filterSql}
                        ORDER BY {orderBySql}
                        LIMIT @limit
                        OFFSET @offset
                    ";

                    cmd.Parameters.AddWithValue("@limit", limit);
                    cmd.Parameters.AddWithValue("@offset", offset);

                    if (sqlUserValues != null)
                    {
                        foreach (KeyValuePair<string, object> kv in sqlUserValues)
                        {
                            cmd.Parameters.AddWithValue(kv.Key, kv.Value);
                        }
                    }
#pragma warning restore CA2100

                    // TODO: rewrite code to stream results (need to combine yield and try-catch)
                    var result = new List<PostgresMemoryRecord>();
                    try
                    {
                        NpgsqlDataReader dataReader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                        await using (dataReader.ConfigureAwait(false))
                        {
                            while (await dataReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                            {
                                result.Add(this.ReadEntry(dataReader, withEmbeddings));
                            }
                        }
                    }
                    catch (Npgsql.PostgresException e) when (IsTableNotFoundException(e))
                    {
                        this._log.LogTrace("Table not found: {0}", tableName);
                    }

                    // TODO: rewrite code to stream results (need to combine yield and try-catch)
                    foreach (var x in result)
                    {
                        yield return x;

                        // If requested cancel potentially long-running loop
                        if (cancellationToken is { IsCancellationRequested: true })
                        {
                            break;
                        }
                    }
                }
            }
            finally
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Delete an entry
    /// </summary>
    /// <param name="tableName">The name assigned to a table of entries</param>
    /// <param name="id">The key of the entry to delete</param>
    /// <param name="cancellationToken">Async task cancellation token</param>
    public async Task DeleteAsync(
        string tableName,
        string id,
        CancellationToken cancellationToken = default)
    {
        tableName = this.WithSchemaAndTableNamePrefix(tableName);
        this._log.LogTrace("Deleting record '{0}' from table '{1}'", id, tableName);

        NpgsqlConnection connection = await this.ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using (connection)
        {
            try
            {
                NpgsqlCommand cmd = connection.CreateCommand();
                await using (cmd.ConfigureAwait(false))
                {
#pragma warning disable CA2100 // SQL reviewed
                    cmd.CommandText = $"DELETE FROM {tableName} WHERE {this._colId}=@id";
                    cmd.Parameters.AddWithValue("@id", id);
#pragma warning restore CA2100

                    try
                    {
                        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (Npgsql.PostgresException e) when (IsTableNotFoundException(e))
                    {
                        this._log.LogTrace("Table not found: {0}", tableName);
                    }
                }
            }
            finally
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        this._dataSource?.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await this._dataSource.DisposeAsync().ConfigureAwait(false);
        }
        catch (NullReferenceException)
        {
            // ignore
        }
    }

    #region private ================================================================================

    // See: https://www.postgresql.org/docs/current/errcodes-appendix.html
    private const string PgErrUndefinedTable = "42P01"; // undefined_table
    private const string PgErrUniqueViolation = "23505"; // unique_violation
    private const string PgErrTypeDoesNotExist = "42704"; // undefined_object
    private const string PgErrDatabaseDoesNotExist = "3D000"; // invalid_catalog_name

    private readonly string _schema;
    private readonly string _tableNamePrefix;
    private readonly string _createTableSql;
    private readonly string _colId;
    private readonly string _colEmbedding;
    private readonly string _colTags;
    private readonly string _colContent;
    private readonly string _colPayload;
    private readonly string _columnsListNoEmbeddings;
    private readonly string _columnsListWithEmbeddings;
    private readonly string _columnsListHybrid;
    private readonly string _columnsListHybridCoalesce;
    private readonly bool _dbNamePresent;
    private readonly string _textSearchLanguage;
    private readonly int _rrf_K;

    /// <summary>
    /// Try to connect to PG, handling exceptions in case the DB doesn't exist
    /// </summary>
    /// <param name="cancellationToken"></param>
    private async Task<NpgsqlConnection> ConnectAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await this._dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Npgsql.PostgresException e) when (IsDbNotFoundException(e))
        {
            if (this._dbNamePresent)
            {
                this._log.LogCritical("DB not found. Try checking the connection string, e.g. whether the `Database` parameter is empty or incorrect: {0}", e.Message);
            }
            else
            {
                this._log.LogCritical("DB not found. Try checking the connection string, e.g. specifying the `Database` parameter: {0}", e.Message);
            }

            throw;
        }
    }

    private static string CleanContent(string input)
    {
        // Remove 0x00 null, not supported by Postgres text fields, to avoid
        // exception: 22021: invalid byte sequence for encoding "UTF8": 0x00
        return input.Replace("\0", "", StringComparison.Ordinal);
    }

    private PostgresMemoryRecord ReadEntry(NpgsqlDataReader dataReader, bool withEmbeddings)
    {
        string id = dataReader.GetString(dataReader.GetOrdinal(this._colId));
        string content = dataReader.GetString(dataReader.GetOrdinal(this._colContent));
        string payload = dataReader.GetString(dataReader.GetOrdinal(this._colPayload));
        List<string> tags = dataReader.GetFieldValue<List<string>>(dataReader.GetOrdinal(this._colTags));

        Vector embedding = withEmbeddings
            ? dataReader.GetFieldValue<Vector>(dataReader.GetOrdinal(this._colEmbedding))
            : new Vector(new ReadOnlyMemory<float>());

        return new PostgresMemoryRecord
        {
            Id = id,
            Embedding = embedding,
            Tags = tags,
            Content = content,
            Payload = payload
        };
    }

    /// <summary>
    /// Get full table name with schema from table name
    /// </summary>
    /// <param name="tableName"></param>
    /// <returns>Valid table name including schema</returns>
    private string WithSchemaAndTableNamePrefix(string tableName)
    {
        tableName = this.WithTableNamePrefix(tableName);
        PostgresSchema.ValidateTableName(tableName);

        return $"{this._schema}.\"{tableName}\"";
    }

    private string WithTableNamePrefix(string tableName)
    {
        return $"{this._tableNamePrefix}{tableName}";
    }

    private static bool IsDbNotFoundException(Npgsql.PostgresException e)
    {
        return e.SqlState == PgErrDatabaseDoesNotExist;
    }

    private static bool IsTableNotFoundException(Npgsql.PostgresException e)
    {
        return e.SqlState == PgErrUndefinedTable || e.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsVectorTypeDoesNotExistException(Npgsql.PostgresException e)
    {
        return e.SqlState == PgErrTypeDoesNotExist
               && e.Message.Contains("type", StringComparison.OrdinalIgnoreCase)
               && e.Message.Contains("vector", StringComparison.OrdinalIgnoreCase)
               && e.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Generate a consistent lock id for a given resource, reducing the chance of collisions.
    /// If a collision happens because two resources have the same lock id, when locks are used
    /// these resources will be accessible one at a time, and not concurrently.
    /// </summary>
    /// <param name="resourceId">Resource Id</param>
    /// <returns>A number assigned to the resource</returns>
    private static long GenLockId(string resourceId)
    {
        return BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(resourceId)), 0)
               % short.MaxValue;
    }

    #endregion
}
