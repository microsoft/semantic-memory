// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory.AI;
using Microsoft.KernelMemory.Diagnostics;
using Microsoft.KernelMemory.MemoryStorage;
using Pgvector;

namespace Microsoft.KernelMemory.Postgres;

/// <summary>
/// Postgres connector for Kernel Memory.
/// </summary>
[Experimental("KMEXP03")]
public sealed class PostgresMemory : IMemoryDb, IDisposable
{
    private readonly ILogger<PostgresMemory> _log;
    private readonly ITextEmbeddingGenerator _embeddingGenerator;
    private readonly PostgresDbClient _db;

    /// <summary>
    /// Create a new instance of Postgres KM connector
    /// </summary>
    /// <param name="config">Postgres configuration</param>
    /// <param name="embeddingGenerator">Text embedding generator</param>
    /// <param name="loggerFactory">Application logger factory</param>
    public PostgresMemory(
        PostgresConfig config,
        ITextEmbeddingGenerator embeddingGenerator,
        ILoggerFactory? loggerFactory = null)
    {
        this._log = (loggerFactory ?? DefaultLogger.Factory).CreateLogger<PostgresMemory>();

        this._embeddingGenerator = embeddingGenerator;
        if (this._embeddingGenerator == null)
        {
            throw new PostgresException("Embedding generator not configured");
        }

        // Normalize underscore and check for invalid symbols
        config.TableNamePrefix = NormalizeTableNamePrefix(config.TableNamePrefix);

        this._db = new PostgresDbClient(config, loggerFactory);
    }

    /// <inheritdoc />
    public async Task CreateIndexAsync(
        string index,
        int vectorSize,
        CancellationToken cancellationToken = default)
    {
        index = NormalizeIndexName(index);

        try
        {
            if (await this._db.DoesTableExistAsync(index, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await this._db.CreateTableAsync(index, vectorSize, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            this._log.LogError(e, "DB error while attempting to create index");
            throw new PostgresException("DB error while attempting to create index", e);
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<string>> GetIndexesAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new List<string>();
        try
        {
            var tables = this._db.GetTablesAsync(cancellationToken).ConfigureAwait(false);
            await foreach (string name in tables)
            {
                result.Add(name);
            }
        }
        catch (Exception e)
        {
            this._log.LogError(e, "DB error while fetching the list of indexes");
            throw;
        }

        return result;
    }

    /// <inheritdoc />
    public async Task DeleteIndexAsync(
        string index,
        CancellationToken cancellationToken = default)
    {
        index = NormalizeIndexName(index);

        try
        {
            await this._db.DeleteTableAsync(index, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            this._log.LogError(e, "DB error while deleting index");
            throw;
        }
    }

    /// <inheritdoc />
    public async Task<string> UpsertAsync(
        string index,
        MemoryRecord record,
        CancellationToken cancellationToken = default)
    {
        index = NormalizeIndexName(index);

        try
        {
            await this._db.UpsertAsync(
                tableName: index,
                PostgresMemoryRecord.FromMemoryRecord(record),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            this._log.LogError(e, "DB error upserting record");
            throw;
        }

        return record.Id;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<(MemoryRecord, double)> GetSimilarListAsync(
        string index,
        string text,
        ICollection<MemoryFilter>? filters = null,
        double minRelevance = 0,
        int limit = 1,
        bool withEmbeddings = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        index = NormalizeIndexName(index);

        var (sql, unsafeSqlUserValues) = this.PrepareSql(filters);

        Embedding textEmbedding = await this._embeddingGenerator.GenerateEmbeddingAsync(text, cancellationToken).ConfigureAwait(false);

        var records = this._db.GetSimilarAsync(
            index,
            target: new Vector(textEmbedding.Data),
            minSimilarity: minRelevance,
            filterSql: sql,
            sqlUserValues: unsafeSqlUserValues,
            limit: limit,
            withEmbeddings: withEmbeddings,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await foreach ((PostgresMemoryRecord record, double similarity) result in records)
        {
            yield return (PostgresMemoryRecord.ToMemoryRecord(result.record), result.similarity);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<MemoryRecord> GetListAsync(
        string index,
        ICollection<MemoryFilter>? filters = null,
        int limit = 1,
        bool withEmbeddings = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        index = NormalizeIndexName(index);

        var (sql, unsafeSqlUserValues) = this.PrepareSql(filters);

        var records = this._db.GetListAsync(
            index,
            filterSql: sql,
            sqlUserValues: unsafeSqlUserValues,
            limit: limit,
            withEmbeddings: withEmbeddings,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await foreach (PostgresMemoryRecord pgRecord in records)
        {
            yield return PostgresMemoryRecord.ToMemoryRecord(pgRecord);
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(
        string index,
        MemoryRecord record,
        CancellationToken cancellationToken = default)
    {
        index = NormalizeIndexName(index);

        return this._db.DeleteAsync(tableName: index, id: record.Id, cancellationToken);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        this.Dispose(true);
        GC.SuppressFinalize(this);
    }

    private void Dispose(bool disposing)
    {
        if (disposing)
        {
            (this._db as IDisposable)?.Dispose();
        }
    }

    #region private ================================================================================

    // Note: "_" is allowed in Postgres, but we normalize it to "-" for consistency with other DBs
    private static readonly Regex s_replaceIndexNameCharsRegex = new(@"[\s|\\|/|.|_|:]");
    private const string ValidSeparator = "-";

    private static string NormalizeIndexName(string index)
    {
        ArgumentNullExceptionEx.ThrowIfNullOrWhiteSpace(index, nameof(index), "The index name is empty");
        index = s_replaceIndexNameCharsRegex.Replace(index.Trim().ToLowerInvariant(), ValidSeparator);

        PostgresSchema.ValidateTableName(index);

        return index;
    }

    private static string NormalizeTableNamePrefix(string? name)
    {
        if (name == null) { return string.Empty; }

        name = s_replaceIndexNameCharsRegex.Replace(name.Trim().ToLowerInvariant(), ValidSeparator);
        PostgresSchema.ValidateTableNamePrefix(name);

        return name;
    }

    private (string sql, Dictionary<string, object> unsafeSqlUserValues) PrepareSql(
        ICollection<MemoryFilter>? filters = null)
    {
        var sql = "";
        Dictionary<string, object> unsafeSqlUserValues = new();

        var nonEmptyFilters = filters?.Where(filters => !filters.IsEmpty()).ToArray() ?? Array.Empty<MemoryFilter>();

        //No query, we do not have filters.
        if (nonEmptyFilters.Length == 0)
        {
            return (sql, unsafeSqlUserValues);
        }

        var tagCounter = 0;
        var orConditions = new List<string>();

        foreach (MemoryFilter filter in nonEmptyFilters)
        {
            if (filter is PostgresMemoryFilter extendedFilter)
            {
                // use PostgresMemoryFilter filtering logic
                throw new NotImplementedException("PostgresMemoryFilter is not supported yet");
            }

            var allFilters = filter.GetFilters().ToArray();

            List<string> equalTags = allFilters
                .OfType<EqualFilter>()
                .Select(x => $"{x.Key}{Constants.ReservedEqualsChar}{x.Value}").ToList();
            List<string> safeSqlPlaceholders = new();

            List<string> conditions = new();

            if (equalTags.Count > 0)
            {
                var safeSqlPlaceholder = $"@placeholder{tagCounter++}";
                safeSqlPlaceholders.Add(safeSqlPlaceholder);
                unsafeSqlUserValues[safeSqlPlaceholder] = equalTags;

                // All tags are required
                //  tags @> ARRAY['user:001', 'type:news', '__document_id:b405']::text[]  <== all tags are required <=== we are using this
                //  tags && ARRAY['user:001', 'type:news', '__document_id:b405']::text[]  <== one tag is sufficient
                // Available syntax:
                //  $"{PostgresSchema.PlaceholdersTags} @> " + safeSqlPlaceholder
                //  $"{PostgresSchema.PlaceholdersTags} @> " + safeSqlPlaceholder + "::text[]"
                //  $"{PostgresSchema.PlaceholdersTags} @> ARRAY[" + safeSqlPlaceholder + "]::text[]"
                conditions.Add($"{PostgresSchema.PlaceholdersTags} @> " + safeSqlPlaceholder);
            }

            List<string> notEqualTags = allFilters
                .OfType<NotEqualFilter>()
                .Select(x => $"{x.Key}{Constants.ReservedEqualsChar}{x.Value}").ToList();
            if (notEqualTags.Count > 0)
            {
                var safeSqlPlaceholder = $"@placeholder{tagCounter++}";
                safeSqlPlaceholders.Add(safeSqlPlaceholder);
                unsafeSqlUserValues[safeSqlPlaceholder] = notEqualTags;

                // Tags should not be present now we need to combine this query with the previous one using AND if we
                // had equality tag.
                conditions.Add($"NOT ({PostgresSchema.PlaceholdersTags} && " + safeSqlPlaceholder + ")");
            }

            orConditions.Add($"( {string.Join(" AND ", conditions)} )");
        }

        sql = string.Join(" OR ", orConditions);

        return (sql, unsafeSqlUserValues);
    }

    #endregion
}
