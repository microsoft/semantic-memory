﻿// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Data.SqlClient;

namespace Microsoft.KernelMemory.MemoryDb.SQLServer.QueryProviders;

internal sealed class VectorQueryProvider : SqlServerQueryProvider
{
    public VectorQueryProvider(SqlServerConfig config) : base(config)
    {
    }

    public override string GetCreateIndexQuery(int sqlServerVersion, string index, int vectorSize)
    {
        var sql = $"""
                   BEGIN TRANSACTION;

                   INSERT INTO {this.GetFullTableName(this.Config.MemoryCollectionTableName)}([id])
                   VALUES (@index);

                   IF OBJECT_ID(N'{this.GetFullTableName($"{this.Config.TagsTableName}_{index}")}', N'U') IS NULL
                   CREATE TABLE {this.GetFullTableName($"{this.Config.TagsTableName}_{index}")}
                   (
                       [memory_id] UNIQUEIDENTIFIER NOT NULL,
                       [name] NVARCHAR(256)  NOT NULL,
                       [value] NVARCHAR(256) NOT NULL,
                       FOREIGN KEY ([memory_id]) REFERENCES {this.GetFullTableName(this.Config.MemoryTableName)}([id])
                   );

                   COMMIT;
                   """;

        return sql;
    }

    public override string GetDeleteQuery(string index)
    {
        var sql = $"""
                   BEGIN TRANSACTION;

                   DELETE [tags]
                   FROM {this.GetFullTableName($"{this.Config.TagsTableName}_{index}")} [tags]
                   INNER JOIN {this.GetFullTableName(this.Config.MemoryTableName)} ON [tags].[memory_id] = {this.GetFullTableName(this.Config.MemoryTableName)}.[id]
                   WHERE
                       {this.GetFullTableName(this.Config.MemoryTableName)}.[collection] = @index
                   AND {this.GetFullTableName(this.Config.MemoryTableName)}.[key]=@key;

                   DELETE FROM {this.GetFullTableName(this.Config.MemoryTableName)} WHERE [collection] = @index AND [key]=@key;

                   COMMIT;
                   """;

        return sql;
    }

    public override string GetDeleteIndexQuery(string index)
    {
        var sql = $"""
                   BEGIN TRANSACTION;

                   DROP TABLE {this.GetFullTableName($"{this.Config.TagsTableName}_{index}")};

                   DELETE FROM {this.GetFullTableName(this.Config.MemoryCollectionTableName)}
                                    WHERE [id] = @index;

                   COMMIT;
                   """;

        return sql;
    }

    public override string GetIndexesQuery()
    {
        var sql = $"SELECT [id] FROM {this.GetFullTableName(this.Config.MemoryCollectionTableName)}";
        return sql;
    }

    public override string GetListQuery(string index,
        ICollection<MemoryFilter>? filters,
        bool withEmbeddings,
        SqlParameterCollection parameters)
    {
        var queryColumns = "[key], [payload], [tags]";
        if (withEmbeddings) { queryColumns += ", VECTOR_TO_JSON_ARRAY([embedding]) AS [embedding]"; }

        var sql = $"""
                   WITH [filters] AS
                   (
                       SELECT
                           cast([filters].[key] AS NVARCHAR(256)) COLLATE SQL_Latin1_General_CP1_CI_AS AS [name],
                           cast([filters].[value] AS NVARCHAR(256)) COLLATE SQL_Latin1_General_CP1_CI_AS AS [value]
                           FROM openjson(@filters) [filters]
                   )
                   SELECT TOP (@limit)
                       {queryColumns}
                   FROM
                       {this.GetFullTableName(this.Config.MemoryTableName)}
                   WHERE 1=1
                       AND {this.GetFullTableName(this.Config.MemoryTableName)}.[collection] = @index
                       {this.GenerateFilters(index, parameters, filters)};
                   """;

        return sql;
    }

    public override string GetSimilarityListQuery(string index,
        ICollection<MemoryFilter>? filters,
        bool withEmbedding,
        SqlParameterCollection parameters)
    {
        var queryColumns = $"{this.GetFullTableName(this.Config.MemoryTableName)}.[id]," +
                           $"{this.GetFullTableName(this.Config.MemoryTableName)}.[key]," +
                           $"{this.GetFullTableName(this.Config.MemoryTableName)}.[payload]," +
                           $"{this.GetFullTableName(this.Config.MemoryTableName)}.[tags]";

        if (withEmbedding)
        {
            queryColumns += $"," +
                            $"VECTOR_TO_JSON_ARRAY({this.GetFullTableName(this.Config.MemoryTableName)}.[embedding]) AS [embedding]";
        }

        var generatedFilters = this.GenerateFilters(index, parameters, filters);

        var sql = $"""
                   SELECT TOP (@limit)
                       {queryColumns},
                       VECTOR_DISTANCE('cosine', JSON_ARRAY_TO_VECTOR(@vector), Embedding) AS [distance]
                   FROM
                       {this.GetFullTableName(this.Config.MemoryTableName)}
                   WHERE 1=1
                       AND VECTOR_DISTANCE('cosine', JSON_ARRAY_TO_VECTOR(@vector), Embedding) <= @max_distance
                       {generatedFilters}
                   ORDER BY [distance] ASC
                   """;

        return sql;
    }

    public override string GetUpsertBatchQuery(string index)
    {
        var sql = $"""
                   BEGIN TRANSACTION;

                   MERGE INTO {this.GetFullTableName(this.Config.MemoryTableName)}
                   USING (SELECT @key) as [src]([key])
                   ON {this.GetFullTableName(this.Config.MemoryTableName)}.[key] = [src].[key]
                   WHEN MATCHED THEN
                       UPDATE SET payload=@payload, embedding=JSON_ARRAY_TO_VECTOR(@embedding), tags=@tags
                   WHEN NOT MATCHED THEN
                       INSERT ([id], [key], [collection], [payload], [tags], [embedding])
                       VALUES (NEWID(), @key, @index, @payload, @tags, JSON_ARRAY_TO_VECTOR(@embedding));

                   DELETE FROM [tgt]
                   FROM  {this.GetFullTableName($"{this.Config.TagsTableName}_{index}")} AS [tgt]
                   INNER JOIN {this.GetFullTableName(this.Config.MemoryTableName)} ON [tgt].[memory_id] = {this.GetFullTableName(this.Config.MemoryTableName)}.[id]
                   WHERE {this.GetFullTableName(this.Config.MemoryTableName)}.[key] = @key
                           AND {this.GetFullTableName(this.Config.MemoryTableName)}.[collection] = @index;

                   MERGE {this.GetFullTableName($"{this.Config.TagsTableName}_{index}")} AS [tgt]
                   USING (
                       SELECT
                           {this.GetFullTableName(this.Config.MemoryTableName)}.[id],
                           cast([tags].[key] AS NVARCHAR(MAX)) COLLATE SQL_Latin1_General_CP1_CI_AS AS [tag_name],
                           [tag_value].[value] AS [value]
                       FROM {this.GetFullTableName(this.Config.MemoryTableName)}
                       CROSS APPLY openjson(@tags) [tags]
                       CROSS APPLY openjson(cast([tags].[value] AS NVARCHAR(MAX)) COLLATE SQL_Latin1_General_CP1_CI_AS) [tag_value]
                       WHERE {this.GetFullTableName(this.Config.MemoryTableName)}.[key] = @key
                           AND {this.GetFullTableName(this.Config.MemoryTableName)}.[collection] = @index
                   ) AS [src]
                   ON [tgt].[memory_id] = [src].[id] AND [tgt].[name] = [src].[tag_name]
                   WHEN MATCHED THEN
                       UPDATE SET [tgt].[value] = [src].[value]
                   WHEN NOT MATCHED THEN
                       INSERT ([memory_id], [name], [value])
                       VALUES ([src].[id],
                               [src].[tag_name],
                               [src].[value]);

                   COMMIT;
                   """;

        return sql;
    }

    public override string GetCreateTablesQuery()
    {
        var sql = $"""
                   IF NOT EXISTS (SELECT  *
                                   FROM    sys.schemas
                                   WHERE   name = N'{this.Config.Schema}' )
                   EXEC('CREATE SCHEMA [{this.Config.Schema}]');
                   IF OBJECT_ID(N'{this.GetFullTableName(this.Config.MemoryCollectionTableName)}', N'U') IS NULL
                   CREATE TABLE {this.GetFullTableName(this.Config.MemoryCollectionTableName)}
                   (   [id] NVARCHAR(256) NOT NULL,
                       PRIMARY KEY ([id])
                   );

                   IF OBJECT_ID(N'{this.GetFullTableName(this.Config.MemoryTableName)}', N'U') IS NULL
                   CREATE TABLE {this.GetFullTableName(this.Config.MemoryTableName)}
                   (   [id] UNIQUEIDENTIFIER NOT NULL,
                       [key] NVARCHAR(256)  NOT NULL,
                       [collection] NVARCHAR(256) NOT NULL,
                       [payload] NVARCHAR(MAX),
                       [tags] NVARCHAR(MAX),
                       [embedding] VARBINARY(8000),
                       PRIMARY KEY ([id]),
                       FOREIGN KEY ([collection]) REFERENCES {this.GetFullTableName(this.Config.MemoryCollectionTableName)}([id]) ON DELETE CASCADE,
                       CONSTRAINT UK_{this.Config.MemoryTableName} UNIQUE([collection], [key])
                   );
                   """;

        return sql;
    }
}
