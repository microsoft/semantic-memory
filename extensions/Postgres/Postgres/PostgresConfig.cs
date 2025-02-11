// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;

#pragma warning disable IDE0130 // reduce number of "using" statements
// ReSharper disable once CheckNamespace - reduce number of "using" statements
namespace Microsoft.KernelMemory;

/// <summary>
/// Postgres configuration
/// </summary>
public class PostgresConfig
{
    /// <summary>
    /// Key for the Columns dictionary
    /// </summary>
    public const string ColumnId = "id";

    /// <summary>
    /// Key for the Columns dictionary
    /// </summary>
    public const string ColumnEmbedding = "embedding";

    /// <summary>
    /// Key for the Columns dictionary
    /// </summary>
    public const string ColumnTags = "tags";

    /// <summary>
    /// Key for the Columns dictionary
    /// </summary>
    public const string ColumnContent = "content";

    /// <summary>
    /// Key for the Columns dictionary
    /// </summary>
    public const string ColumnPayload = "payload";

    /// <summary>
    /// Name of the default schema
    /// </summary>
    public const string DefaultSchema = "public";

    /// <summary>
    /// Default prefix used for table names
    /// </summary>
    public const string DefaultTableNamePrefix = "km-";

    /// <summary>
    /// Connection string required to connect to Postgres
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Name of the schema where to read and write records.
    /// </summary>
    public string Schema { get; set; } = DefaultSchema;

    /// <summary>
    /// Mandatory prefix to add to tables created by KM.
    /// This is used to distinguish KM tables from others in the same schema.
    /// </summary>
    /// <remarks>Default value is set to "km-" but can be override when creating a config.</remarks>
    public string TableNamePrefix { get; set; } = DefaultTableNamePrefix;

    /// <summary>
    /// Configurable column names used with Postgres
    /// </summary>
    public Dictionary<string, string> Columns { get; set; }

    /// <summary>
    /// Mandatory placeholder required in CreateTableSql
    /// </summary>
    public const string SqlPlaceholdersTableName = "%%table_name%%";

    /// <summary>
    /// Mandatory placeholder required in CreateTableSql
    /// </summary>
    public const string SqlPlaceholdersVectorSize = "%%vector_size%%";

    /// <summary>
    /// Optional placeholder required in CreateTableSql
    /// </summary>
    public const string SqlPlaceholdersLockId = "%%lock_id%%";

    /// <summary>
    /// Optional, custom SQL statements for creating new tables, in case
    /// you need to add custom columns, indexing, etc.
    /// The SQL must contain two placeholders: %%table_name%% and %%vector_size%%.
    /// You can put the SQL in one line or split it over multiple lines for
    /// readability. Lines are automatically merged with a new line char.
    /// Example:
    ///   BEGIN;
    ///   "SELECT pg_advisory_xact_lock(%%lock_id%%);
    ///   CREATE TABLE IF NOT EXISTS %%table_name%% (
    ///     id           TEXT NOT NULL PRIMARY KEY,
    ///     embedding    vector(%%vector_size%%),
    ///     tags         TEXT[] DEFAULT '{}'::TEXT[] NOT NULL,
    ///     content      TEXT DEFAULT '' NOT NULL,
    ///     payload      JSONB DEFAULT '{}'::JSONB NOT NULL,
    ///     some_text    TEXT DEFAULT '',
    ///     last_update  TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP NOT NULL
    ///   );
    ///   CREATE INDEX IF NOT EXISTS idx_tags ON %%table_name%% USING GIN(tags);
    ///   COMMIT;
    /// </summary>
    public List<string> CreateTableSql { get; set; } = [];

    /// <summary>
    /// Important: when using hybrid search, relevance scores
    /// are very different from when using just vector search.
    /// </summary>
    public bool UseHybridSearch { get; set; } = false;

    /// <summary>
    /// Defines the dictionary language used for the textual part of hybrid search.
    /// see: https://www.postgresql.org/docs/current/textsearch-dictionaries.html
    /// This query can help you to get the list of dictionaries: SELECT * FROM pg_catalog.pg_ts_dict;
    /// </summary>
    public string TextSearchLanguage { get; set; } = "english";

    /// <summary>
    /// Reciprocal Ranked Fusion to score results of Hybrid Search
    /// </summary>
    public int RRFK { get; set; } = 50;

    /// <summary>
    /// Create a new instance of the configuration
    /// </summary>
    public PostgresConfig()
    {
        this.Columns = new Dictionary<string, string>
        {
            [ColumnId] = "id",
            [ColumnEmbedding] = "embedding",
            [ColumnTags] = "tags",
            [ColumnContent] = "content",
            [ColumnPayload] = "payload"
        };
    }

    /// <summary>
    /// Verify that the current state is valid.
    /// </summary>
    public void Validate()
    {
        this.TableNamePrefix = this.TableNamePrefix?.Trim() ?? string.Empty;
        this.ConnectionString = this.ConnectionString?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(this.ConnectionString))
        {
            throw new ConfigurationException($"Postgres: {nameof(this.ConnectionString)} is empty.");
        }

        if (string.IsNullOrWhiteSpace(this.TableNamePrefix))
        {
            throw new ConfigurationException($"Postgres: {nameof(this.TableNamePrefix)} is empty.");
        }

        // ID

        if (!this.Columns.TryGetValue(ColumnId, out var columnName))
        {
            throw new ConfigurationException("Postgres: the name of the Id column is not defined.");
        }

        if (string.IsNullOrWhiteSpace(columnName))
        {
            throw new ConfigurationException("Postgres: the name of the Id column is empty.");
        }

        // Embedding

        if (!this.Columns.TryGetValue(ColumnEmbedding, out columnName))
        {
            throw new ConfigurationException("Postgres: the name of the Embedding column is not defined.");
        }

        if (string.IsNullOrWhiteSpace(columnName))
        {
            throw new ConfigurationException("Postgres: the name of the Embedding column is empty.");
        }

        // Tags

        if (!this.Columns.TryGetValue(ColumnTags, out columnName))
        {
            throw new ConfigurationException("Postgres: the name of the Tags column is not defined.");
        }

        if (string.IsNullOrWhiteSpace(columnName))
        {
            throw new ConfigurationException("Postgres: the name of the Tags column is empty.");
        }

        // Content

        if (!this.Columns.TryGetValue(ColumnContent, out columnName))
        {
            throw new ConfigurationException("Postgres: the name of the Content column is not defined.");
        }

        if (string.IsNullOrWhiteSpace(columnName))
        {
            throw new ConfigurationException("Postgres: the name of the Content column is empty.");
        }

        // Payload

        if (!this.Columns.TryGetValue(ColumnPayload, out columnName))
        {
            throw new ConfigurationException("Postgres: the name of the Payload column is not defined.");
        }

        if (string.IsNullOrWhiteSpace(columnName))
        {
            throw new ConfigurationException("Postgres: the name of the Payload column is empty.");
        }

        // Custom schema

        if (this.CreateTableSql?.Count > 0)
        {
            var sql = string.Join('\n', this.CreateTableSql).Trim();
            if (!sql.Contains(SqlPlaceholdersTableName, StringComparison.Ordinal))
            {
                throw new ConfigurationException(
                    "Postgres: the custom SQL to create tables is not valid, " +
                    $"it should contain a {SqlPlaceholdersTableName} placeholder.");
            }

            if (!sql.Contains(SqlPlaceholdersVectorSize, StringComparison.Ordinal))
            {
                throw new ConfigurationException(
                    "Postgres: the custom SQL to create tables is not valid, " +
                    $"it should contain a {SqlPlaceholdersVectorSize} placeholder.");
            }
        }

        this.Columns[ColumnId] = this.Columns[ColumnId].Trim();
        this.Columns[ColumnEmbedding] = this.Columns[ColumnEmbedding].Trim();
        this.Columns[ColumnTags] = this.Columns[ColumnTags].Trim();
        this.Columns[ColumnContent] = this.Columns[ColumnContent].Trim();
        this.Columns[ColumnPayload] = this.Columns[ColumnPayload].Trim();
    }
}
