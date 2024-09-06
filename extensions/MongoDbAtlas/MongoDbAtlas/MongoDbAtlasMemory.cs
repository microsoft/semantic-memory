// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory.AI;
using Microsoft.KernelMemory.Diagnostics;
using Microsoft.KernelMemory.MemoryStorage;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Microsoft.KernelMemory.MongoDbAtlas;

/// <summary>
/// Implementation of <see cref="IMemoryDb"/> based on MongoDB Atlas.
/// </summary>
[Experimental("KMEXP03")]
public sealed class MongoDbAtlasMemory : MongoDbAtlasBaseStorage, IMemoryDb
{
    private const string ConnectionNamePrefix = "_ix_";

    private readonly ITextEmbeddingGenerator _embeddingGenerator;
    private readonly ILogger<MongoDbAtlasMemory> _log;
    private readonly MongoDbAtlasSearchHelper _utils;

    /// <summary>
    /// Create a new instance of MongoDbVectorMemory from configuration
    /// </summary>
    /// <param name="config">Configuration</param>
    /// <param name="embeddingGenerator">Embedding generator</param>
    /// <param name="loggerFactory">Application logger factory</param>
    public MongoDbAtlasMemory(
        MongoDbAtlasConfig config,
        ITextEmbeddingGenerator embeddingGenerator,
        ILoggerFactory? loggerFactory = null) : base(config)
    {
        ArgumentNullExceptionEx.ThrowIfNull(embeddingGenerator, nameof(embeddingGenerator), "Embedding generator is null");

        this._embeddingGenerator = embeddingGenerator;
        this._log = (loggerFactory ?? DefaultLogger.Factory).CreateLogger<MongoDbAtlasMemory>();
        this._utils = new MongoDbAtlasSearchHelper(this.Config.ConnectionString, this.Config.DatabaseName);
    }

    /// <inheritdoc />
    public async Task CreateIndexAsync(string index, int vectorSize, CancellationToken cancellationToken = default)
    {
        var normalizedIndexName = NormalizeIndexName(index);
        // Index name is the name of the collection, so we need to understand if the collection exists
        var collectionName = this.GetCollectionName(index);
        await this._utils.CreateIndexAsync(collectionName, vectorSize).ConfigureAwait(false);
        await this._utils.WaitForIndexToBeReadyAsync(collectionName, 120).ConfigureAwait(false);

        // Keep tracks of created indexes.
        var collection = this.Database.GetCollection<BsonDocument>(GetIndexListCollectionName());
        // upsert the name of the index
        var filter = Builders<BsonDocument>.Filter.Eq("_id", normalizedIndexName);
        var update = Builders<BsonDocument>.Update.Set("index", normalizedIndexName).Set("lastCreateIndex", DateTime.UtcNow);
        await collection.UpdateOneAsync(filter, update, new UpdateOptions { IsUpsert = true }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteIndexAsync(string index, CancellationToken cancellationToken = default)
    {
        var normalizedIndexName = NormalizeIndexName(index);
        if (this.Config.UseSingleCollectionForVectorSearch)
        {
            // Actually if we use a single collection we do not delete the entire collection we simply delete records of the index
            var collection = this.GetCollectionFromIndexName(index);
            await collection.DeleteManyAsync(x => x.Index == normalizedIndexName, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var collectionName = this.GetCollectionName(index);
            await this._utils.DeleteIndicesAsync(collectionName).ConfigureAwait(false);
            await this.Database.DropCollectionAsync(collectionName, cancellationToken).ConfigureAwait(false);
        }

        var collectionIndexList = this.Database.GetCollection<BsonDocument>(GetIndexListCollectionName());
        await collectionIndexList.DeleteOneAsync(x => x["_id"] == index, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<string>> GetIndexesAsync(CancellationToken cancellationToken = default)
    {
        // We load index from the index list collection
        var collection = this.Database.GetCollection<BsonDocument>(GetIndexListCollectionName());
        var cursor = await collection.FindAsync(Builders<BsonDocument>.Filter.Empty, cancellationToken: cancellationToken).ConfigureAwait(false);
        return cursor.ToEnumerable(cancellationToken: cancellationToken).Select(x => x["_id"].AsString).ToImmutableArray();
    }

    /// <inheritdoc />
    public Task DeleteAsync(string index, MemoryRecord record, CancellationToken cancellationToken = default)
    {
        var collection = this.GetCollectionFromIndexName(index);
        return collection.DeleteOneAsync(x => x.Id == record.Id, cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<MemoryRecord> GetListAsync(
        string index,
        ICollection<MemoryFilter>? filters = null,
        int limit = 1,
        bool withEmbeddings = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
        {
            limit = 10;
        }

        // Need to create a query and execute it without using $vector
        var collection = this.GetCollectionFromIndexName(index);
        var finalFilter = this.TranslateFilters(filters, index);

        // We need to perform a simple query without using vector search
        var cursor = await collection
            .FindAsync(finalFilter,
                new FindOptions<MongoDbAtlasMemoryRecord>()
                {
                    Limit = limit
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        var documents = await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var document in documents)
        {
            var memoryRecord = FromMongodbMemoryRecord(document, withEmbeddings);

            yield return memoryRecord;
        }
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
        if (limit <= 0)
        {
            limit = 10;
        }

        // Need to create a search query and execute it
        var collectionName = this.GetCollectionName(index);
        var embeddings = await this._embeddingGenerator.GenerateEmbeddingAsync(text, cancellationToken).ConfigureAwait(false);

        // Define vector embeddings to search
        var vector = embeddings.Data.Span.ToArray();

        // Need to create the filters
        var finalFilter = this.TranslateFilters(filters, index);

        var options = new VectorSearchOptions<MongoDbAtlasMemoryRecord>()
        {
            IndexName = this._utils.GetIndexName(collectionName),
            NumberOfCandidates = limit,
            Filter = finalFilter
        };
        var collection = this.GetCollectionFromIndexName(index);

        // Run query
        var documents = await collection.Aggregate()
            .VectorSearch(m => m.Embedding, vector, limit, options)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // If you check documentation Atlas normalize the score with formula
        // score = (1 + cosine/dot_product(v1,v2)) / 2
        // Thus it does not output the real cosine similarity, this is annoying so we
        // need to recompute cosine similarity using the embeddings.
        foreach (var document in documents)
        {
            var memoryRecord = FromMongodbMemoryRecord(document, withEmbeddings);

            // we have score that is normalized, so we need to recompute similarity to have a real cosine distance
            var cosineSimilarity = CosineSim(embeddings, document.Embedding);
            if (cosineSimilarity < minRelevance)
            {
                //we have reached the limit for minimum relevance so we can stop iterating
                break;
            }

            yield return (memoryRecord, cosineSimilarity);
        }
    }

    /// <inheritdoc />
    public async Task<string> UpsertAsync(string index, MemoryRecord record, CancellationToken cancellationToken = default)
    {
        var normalizedIndexName = NormalizeIndexName(index);
        var collection = this.GetCollectionFromIndexName(index);
        MongoDbAtlasMemoryRecord mongoRecord = new()
        {
            Id = record.Id,
            Index = normalizedIndexName,
            Embedding = record.Vector.Data.ToArray(),
            Tags = record.Tags.Select(x => new MongoDbAtlasMemoryRecord.Tag(x.Key, x.Value.ToArray())).ToList(),
            Payloads = record.Payload.Select(x => new MongoDbAtlasMemoryRecord.Payload(x.Key, x.Value)).ToList()
        };

        await collection.InsertOneAsync(mongoRecord, cancellationToken: cancellationToken).ConfigureAwait(false);
        await this.Config.AfterIndexCallbackAsync().ConfigureAwait(false);
        return record.Id;
    }

    private FilterDefinition<MongoDbAtlasMemoryRecord>? TranslateFilters(ICollection<MemoryFilter>? filters, string index)
    {
        List<FilterDefinition<MongoDbAtlasMemoryRecord>> outerFiltersArray = new();
        foreach (var filter in filters ?? Array.Empty<MemoryFilter>())
        {
            var thisFilter = filter.GetFilters().ToArray();
            List<FilterDefinition<MongoDbAtlasMemoryRecord>> filtersArray = new();
            foreach (var singleFilter in thisFilter)
            {
                if (singleFilter is EqualFilter ef)
                {
                    var condition = Builders<MongoDbAtlasMemoryRecord>.Filter.And(
                        Builders<MongoDbAtlasMemoryRecord>.Filter.Eq("Tags.Key", singleFilter.Key),
                        Builders<MongoDbAtlasMemoryRecord>.Filter.Eq("Tags.Values", singleFilter.Value)
                    );
                    filtersArray.Add(condition);
                }
                else if (singleFilter is NotEqualFilter nef)
                {
                    var condition = Builders<MongoDbAtlasMemoryRecord>.Filter.And(
                        Builders<MongoDbAtlasMemoryRecord>.Filter.Eq("Tags.Key", singleFilter.Key),
                        Builders<MongoDbAtlasMemoryRecord>.Filter.Ne("Tags.Values", singleFilter.Value)
                    );
                    filtersArray.Add(condition);
                }
                else
                {
                    throw new MongoDbAtlasException($"Filter {singleFilter.GetType().Name} is not supported");
                }
            }

            // if we have more than one condition, we need to compose all conditions with AND
            // but if we have only a single filter we can directly use the filter.
            if (filtersArray.Count > 1)
            {
                // More than one condition we need to create the condition
                var andFilter = Builders<MongoDbAtlasMemoryRecord>.Filter.And(filtersArray);
                outerFiltersArray.Add(andFilter);
            }
            else if (filtersArray.Count == 1)
            {
                // We do not need to include an and filter because we have only one condition
                outerFiltersArray.Add(filtersArray[0]);
            }
        }

        FilterDefinition<MongoDbAtlasMemoryRecord>? finalFilter = null;

        // Outer filters must be composed in or
        if (outerFiltersArray.Count > 1)
        {
            finalFilter = Builders<MongoDbAtlasMemoryRecord>.Filter.Or(outerFiltersArray);
        }
        else if (outerFiltersArray.Count == 1)
        {
            // We do not need to include an or filter because we have only one condition
            finalFilter = outerFiltersArray[0];
        }

        // Remember that if we are using a single collection for all records we need to add an index filter
        if (this.Config.UseSingleCollectionForVectorSearch)
        {
            var indexFilter = Builders<MongoDbAtlasMemoryRecord>.Filter.Eq("Index", index);
            if (finalFilter == null)
            {
                finalFilter = indexFilter;
            }
            else
            {
                // Compose in and
                finalFilter = Builders<MongoDbAtlasMemoryRecord>.Filter.And(indexFilter, finalFilter);
            }
        }

        return finalFilter;
    }

    private IMongoCollection<MongoDbAtlasMemoryRecord> GetCollectionFromIndexName(string indexName)
    {
        var collectionName = this.GetCollectionName(NormalizeIndexName(indexName));
        return this.GetCollection<MongoDbAtlasMemoryRecord>(collectionName);
    }

    private string GetCollectionName(string indexName)
    {
        var normalizedIndexName = NormalizeIndexName(indexName);
        if (this.Config.UseSingleCollectionForVectorSearch)
        {
            return $"{ConnectionNamePrefix}_kernel_memory_single_index";
        }

        return $"{ConnectionNamePrefix}{normalizedIndexName}";
    }

    /// <summary>
    /// Due to different score system of MongoDB Atlas that normalized cosine
    /// we need to manually recompute the cosine similarity distance manually
    /// for each vector to have a real cosine similarity distance returned.
    /// </summary>
    private static double CosineSim(Embedding vec1, float[] vec2)
    {
        var v1 = vec1.Data.ToArray();
        var v2 = vec2;

        int size = vec1.Length;
        double dot = 0.0d;
        double m1 = 0.0d;
        double m2 = 0.0d;
        for (int n = 0; n < size; n++)
        {
            dot += v1[n] * v2[n];
            m1 += Math.Pow(v1[n], 2);
            m2 += Math.Pow(v2[n], 2);
        }

        double cosineSimilarity = dot / (Math.Sqrt(m1) * Math.Sqrt(m2));
        return cosineSimilarity;
    }

    private static string GetIndexListCollectionName()
    {
        return $"{ConnectionNamePrefix}_kernel_memory_index_lists";
    }

    private static MemoryRecord FromMongodbMemoryRecord(MongoDbAtlasMemoryRecord doc, bool withEmbeddings)
    {
        var record = new MemoryRecord
        {
            Id = doc.Id,
            Vector = withEmbeddings ? doc.Embedding : Array.Empty<float>(),
        };

        foreach (var tag in doc.Tags)
        {
            record.Tags[tag.Key] = tag.Values.ToList();
        }

        foreach (var payload in doc.Payloads)
        {
            record.Payload[payload.Key] = payload.Value;
        }

        return record;
    }

    private static string NormalizeIndexName(string indexName)
    {
        ArgumentNullExceptionEx.ThrowIfNullOrWhiteSpace(indexName, nameof(indexName), "The index name is empty");

        return indexName.Replace("_", "-", StringComparison.OrdinalIgnoreCase);
    }
}
