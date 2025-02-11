# Kernel Memory with Postgres + pgvector

[![Nuget package](https://img.shields.io/nuget/v/Microsoft.KernelMemory.MemoryDb.Postgres)](https://www.nuget.org/packages/Microsoft.KernelMemory.MemoryDb.Postgres/)
[![Discord](https://img.shields.io/discord/1063152441819942922?label=Discord&logo=discord&logoColor=white&color=d82679)](https://aka.ms/KMdiscord)

This project contains the [Postgres](https://www.postgresql.org) adapter allowing
to use Kernel Memory with [Postgres+pgvector](https://github.com/pgvector/pgvector).

> [!IMPORTANT]
> Your Postgres instance must support vectors. You can run this SQL to see the list of
> extensions **installed** and **enabled**:
>
>     SELECT * FROM pg_extension
>
> To enable the extension this should suffice:
>
>     CREATE EXTENSION vector
>
> For more information, check:
> * [Using pgvector on Azure PostgreSQL](https://learn.microsoft.com/azure/postgresql/flexible-server/how-to-use-pgvector)
> * [pg_vector extension documentation](https://github.com/pgvector/pgvector).

To use Postgres with Kernel Memory:

1. Have a PostgreSQL instance ready, e.g. checkout [Azure Database for PostgreSQL](https://learn.microsoft.com/azure/postgresql)
2. Verify your Postgres instance supports vectors, e.g. run `SELECT * FROM pg_extension`
3. Add Postgres connection string to appsettings.json (or appsettings.development.json), for example:

    ```json
    {
      "KernelMemory": {
        "Services": {
          "Postgres": {
            "ConnectionString": "Host=localhost;Port=5432;Username=myuser;Password=mypassword;Database=mydatabase",
            "UseHybridSearch": true,
            "TextSearchLanguage": "english",
            "RRF_K" : 60,          
          }
        }
      }
    }
    ```
4. Configure KM builder to store memories in Postgres, and to persist documents, for example:
    ```csharp
    // using Microsoft.KernelMemory;
    // using Microsoft.KernelMemory.Postgres;
    // using Microsoft.Extensions.Configuration;
    
    var postgresConfig = new PostgresConfig();
    
    new ConfigurationBuilder()
        .AddJsonFile("appsettings.json")
        .AddJsonFile("appsettings.development.json", optional: true)
        .AddJsonFile("appsettings.Development.json", optional: true)
        .Build()
        .BindSection("KernelMemory:Services:Postgres", postgresConfig);
    
    var memory = new KernelMemoryBuilder()
        .WithPostgresMemoryDb(postgresConfig)
        .WithSimpleFileStorage(SimpleFileStorageConfig.Persistent)
        .WithAzureOpenAITextGeneration(azureOpenAIConfig)
        .WithAzureOpenAITextEmbeddingGeneration(azureOpenAIConfig)
        .Build();
    ```

## Neighbor search indexes, quality and performance

The connector does not create IVFFlat or HNSW indexes on Postgres tables, and
uses exact nearest neighbor search. HNSW (_Hierarchical Navigable Small World_)
has been introduced in pgvector 0.5.0 and is not available in some Postgres
instances.

Depending on your scenario you might want to create these indexes manually,
considering precision and performance trade-offs, or you can customize the
SQL used to create tables via configuration.

> [!NOTE]
> An **IVFFlat** index divides vectors into lists, and then searches a subset
> of those lists that are closest to the query vector. It has **faster build times**
> and uses **less memory** than HNSW, but has **lower query performance**
> (in terms of speed-recall tradeoff).

SQL to add IVFFlat: `CREATE INDEX ON %%table_name%% USING ivfflat (embedding vector_cosine_ops) WITH (lists = 1000);`

> [!NOTE]
> An **HNSW** index creates a multilayer graph. It has **slower build times**
> and uses **more memory** than IVFFlat, but has **better query performance**
> (in terms of speed-recall tradeoff). There’s no training step like IVFFlat,
> so the index can be created without any data in the table.

SQL to add HNSW: `CREATE INDEX ON %%table_name%% USING hnsw (embedding vector_cosine_ops);`

See https://github.com/pgvector/pgvector for more information.

## Memory Indexes and Postgres tables

The Postgres memory connector will create "memory indexes" automatically, one
DB table for each memory index.

Table names have a configurable **prefix**, used to filter out other tables that
might be present. The prefix is mandatory, cannot be empty, we suggest using
the default `km-` prefix. Note that the Postgres connector automatically converts
`_` underscores to `-` dashes to have a consistent behavior with other storage
types supported by Kernel Memory.

Overall we recommend not mixing external tables in the same DB used for
Kernel Memory.

## Hybrid Search

The Postgres memory connector support Hybrid Search.

Hybrid Search configuration parameters:

- **UseHybridSearch**:  This parameter enables (true) or disables (false) hybrid search.
- **TextSearchLanguage**: This parameter sets the language used during text search.
- **RRFK**: This parameter allows to configured [RRF](https://en.wikipedia.org/wiki/Mean_reciprocal_rank) for the hybrid search.
  A smaller value of `RRF_k` gives more weight to higher ranked items, whereas a
  larger value of `RRF_k` gives more weight to lower ranked items. For hybrid search,
  this impacts the final score when combining the two scores from the search.
  It defaults to 50. The range of value should be 1-100.

For more details, check out the
[pgvector GitHub project](https://github.com/pgvector/pgvector) and
[this article](https://jkatz05.com/post/postgres/hybrid-search-postgres-pgvector)
on hybrid search in Postgres.

## Column names and table schema

The connector uses a default schema with predefined columns and indexes.

You can change the field names, and if you need to add additional columns
or indexes, you can also customize the `CREATE TABLE` SQL statement. You
can use this approach, for example, to use IVFFlat or HNSW.

See `PostgresConfig` class for more details. 

Here's an example where `PostgresConfig` is stored in `appsettings.json` and
the table schema is customized, with custom names and additional fields.

The SQL statement requires two special **placeholders**:

* `%%table_name%%`: replaced at runtime with the table name
* `%%vector_size%%`: replaced at runtime with the embedding vectors size

There's a third optional placeholder we recommend using, to better handle
concurrency, e.g. in combination with `pg_advisory_xact_lock` (_exclusive transaction
level advisory locks_):

* `%%lock_id%%`: replaced at runtime with a number

Also:

* `TableNamePrefix` is mandatory string added to all KM tables
* `Columns` is a required map describing where KM will store its data. If you have
  additional columns you don't need to list them here, only in SQL statement.
* `CreateTableSql` is your optional custom SQL statement used to create tables. The
  column names must match those used in `Columns`.

```json
{
  "KernelMemory": {
    "Services": {
      "Postgres": {

        "TableNamePrefix": "memory_",

        "Columns": {
          "id":        "_pk",
          "embedding": "embedding",
          "tags":      "labels",
          "content":   "chunk",
          "payload":   "extras"
        },

        "CreateTableSql": [
          "BEGIN;                                                                      ",
          "SELECT pg_advisory_xact_lock(%%lock_id%%);                                  ",
          "CREATE TABLE IF NOT EXISTS %%table_name%% (                                 ",
          "  _pk         TEXT NOT NULL PRIMARY KEY,                                    ",
          "  embedding   vector(%%vector_size%%),                                      ",
          "  labels      TEXT[] DEFAULT '{}'::TEXT[] NOT NULL,                         ",
          "  chunk       TEXT DEFAULT '' NOT NULL,                                     ",
          "  extras      JSONB DEFAULT '{}'::JSONB NOT NULL,                           ",
          "  my_field1   TEXT DEFAULT '',                                              ",
          "  _update     TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP            ",
          ");                                                                          ",
          "CREATE INDEX ON %%table_name%% USING GIN(labels);                           ",
          "CREATE INDEX ON %%table_name%% USING ivfflat (embedding vector_cosine_ops); ",
          "COMMIT;                                                                     "
        ]

      }
    }
  }
}
```
