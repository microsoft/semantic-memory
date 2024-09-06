// Copyright (c) Microsoft. All rights reserved.

using System.Runtime.CompilerServices;
using Elastic.Clients.Elasticsearch;
using Elastic.Clients.Elasticsearch.Mapping;
using Elastic.Clients.Elasticsearch.QueryDsl;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory.AI;
using Microsoft.KernelMemory.Diagnostics;
using Microsoft.KernelMemory.MemoryDb.Elasticsearch.Internals;
using Microsoft.KernelMemory.MemoryStorage;

namespace Microsoft.KernelMemory.MemoryDb.Elasticsearch;

/// <summary>
/// Elasticsearch connector for Kernel Memory.
/// </summary>
public class ElasticsearchMemory : IMemoryDb
{
    private readonly ITextEmbeddingGenerator _embeddingGenerator;
    private readonly ElasticsearchConfig _config;
    private readonly ILogger<ElasticsearchMemory> _log;
    private readonly ElasticsearchClient _client;

    /// <summary>
    /// Create a new instance of Elasticsearch KM connector
    /// </summary>
    /// <param name="config">Elasticsearch configuration</param>
    /// <param name="embeddingGenerator">Embedding generator</param>
    /// <param name="client">Elasticsearch client</param>
    /// <param name="loggerFactory">Application logger factory</param>
    public ElasticsearchMemory(
        ElasticsearchConfig config,
        ITextEmbeddingGenerator embeddingGenerator,
        ElasticsearchClient? client = null,
        ILoggerFactory? loggerFactory = null)
    {
        ArgumentNullExceptionEx.ThrowIfNull(embeddingGenerator, nameof(embeddingGenerator), "The embedding generator is NULL");
        ArgumentNullExceptionEx.ThrowIfNull(config, nameof(config), "The configuration is NULL");

        this._embeddingGenerator = embeddingGenerator;
        this._config = config;
        this._client = client ?? new ElasticsearchClient(this._config.ToElasticsearchClientSettings());
        this._log = (loggerFactory ?? DefaultLogger.Factory).CreateLogger<ElasticsearchMemory>();
    }

    /// <inheritdoc />
    public async Task CreateIndexAsync(
        string index,
        int vectorSize,
        CancellationToken cancellationToken = default)
    {
        index = IndexNameHelper.Convert(index, this._config);

        var existsResponse = await this._client.Indices.ExistsAsync(index, cancellationToken).ConfigureAwait(false);
        if (existsResponse.Exists)
        {
            this._log.LogTrace("Index {Index} already exists.", index);
            return;
        }

        var createIdxResponse = await this._client.Indices.CreateAsync(index,
            cfg =>
            {
                cfg.Settings(setts =>
                {
                    setts.NumberOfShards(this._config.ShardCount);
                    setts.NumberOfReplicas(this._config.ReplicaCount);
                });
            },
            cancellationToken).ConfigureAwait(false);

        //int Dimensions = vectorSize; // TODO: make not hardcoded

        var np = new NestedProperty()
        {
            Properties = new Properties()
            {
                { ElasticsearchTag.NameField, new KeywordProperty() },
                { ElasticsearchTag.ValueField, new KeywordProperty() }
            }
        };

        var mapResponse = await this._client.Indices.PutMappingAsync(index, x => x
                .Properties<ElasticsearchMemoryRecord>(propDesc =>
                {
                    propDesc.Keyword(x => x.Id);
                    propDesc.Nested(ElasticsearchMemoryRecord.TagsField, np);
                    propDesc.Text(x => x.Payload, pd => pd.Index(false));
                    propDesc.Text(x => x.Content);
                    propDesc.DenseVector(x => x.Vector, d => d.Index(true).Dims(vectorSize).Similarity("cosine"));

                    this._config.ConfigureProperties?.Invoke(propDesc);
                }),
            cancellationToken).ConfigureAwait(false);

        this._log.LogTrace("Index {Index} created.", index);
    }

    /// <inheritdoc />
    public async Task<IEnumerable<string>> GetIndexesAsync(
        CancellationToken cancellationToken = default)
    {
        var resp = await this._client.Indices.GetAsync(this._config.IndexPrefix + "*", cancellationToken).ConfigureAwait(false);

        var names = resp.Indices
            .Select(x => x.Key.ToString().Replace(this._config.IndexPrefix, string.Empty, StringComparison.Ordinal))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        this._log.LogTrace("Returned {IndexCount} indices: {Indices}.", names.Count, string.Join(", ", names));

        return names;
    }

    /// <inheritdoc />
    public async Task DeleteIndexAsync(
        string index,
        CancellationToken cancellationToken = default)
    {
        index = IndexNameHelper.Convert(index, this._config);

        var delResponse = await this._client.Indices.DeleteAsync(
            index,
            cancellationToken).ConfigureAwait(false);

        if (delResponse.IsSuccess())
        {
            this._log.LogTrace("Index {Index} deleted.", index);
        }
        else
        {
            this._log.LogWarning("Index {Index} delete failed.", index);
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(
        string index,
        MemoryRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullExceptionEx.ThrowIfNull(record, nameof(record), "The record is NULL");
        index = IndexNameHelper.Convert(index, this._config);

        var delResponse = await this._client.DeleteAsync<ElasticsearchMemoryRecord>(
                index,
                record.Id,
                (delReq) =>
                {
                    delReq.Refresh(Refresh.WaitFor);
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (delResponse.IsSuccess())
        {
            this._log.LogTrace("Record {RecordId} deleted.", record.Id);
        }
        else
        {
            this._log.LogWarning("Record {RecordId} delete failed.", record.Id);
        }
    }

    /// <inheritdoc />
    public async Task<string> UpsertAsync(
        string index,
        MemoryRecord record,
        CancellationToken cancellationToken = default)
    {
        index = IndexNameHelper.Convert(index, this._config);

        var memRec = ElasticsearchMemoryRecord.FromMemoryRecord(record);

        var response = await this._client.UpdateAsync<ElasticsearchMemoryRecord, ElasticsearchMemoryRecord>(
                index,
                memRec.Id,
                (updateReq) =>
                {
                    updateReq.Refresh(Refresh.WaitFor);

                    var memRec2 = memRec;
                    updateReq.Doc(memRec2);
                    updateReq.DocAsUpsert(true);
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (response.IsSuccess())
        {
            this._log.LogTrace("Record {RecordId} upserted.", memRec.Id);
        }
        else
        {
            this._log.LogError("Record {RecordId} upsert failed.", memRec.Id);
        }

        return response.Id;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<(MemoryRecord, double)> GetSimilarListAsync(
        string index,
        string text,
        ICollection<MemoryFilter>? filters = null,
        double minRelevance = 0, int limit = 1, bool withEmbeddings = false, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (limit < 0)
        {
            limit = 10;
        }

        index = IndexNameHelper.Convert(index, this._config);

        this._log.LogTrace("Searching for '{Text}' on index '{IndexName}' with filters {Filters}. {MinRelevance} {Limit} {WithEmbeddings}",
            text, index, filters.ToDebugString(), minRelevance, limit, withEmbeddings);

        Embedding embedding = await this._embeddingGenerator.GenerateEmbeddingAsync(text, cancellationToken).ConfigureAwait(false);
        var coll = embedding.Data.ToArray();

        var resp = await this._client.SearchAsync<ElasticsearchMemoryRecord>(s =>
                    s.Index(index)
                        .Knn(qd =>
                        {
                            qd.k(limit)
                                .Filter(q => this.ConvertTagFilters(q, filters))
                                .NumCandidates(limit + 100)
                                .Field(x => x.Vector)
                                .QueryVector(coll);
                        }),
                cancellationToken)
            .ConfigureAwait(false);

        if ((resp.HitsMetadata is null) || (resp.HitsMetadata.Hits is null))
        {
            this._log.LogWarning("The search returned a null result. Should retry?");
            yield break;
        }

        foreach (var hit in resp.HitsMetadata.Hits)
        {
            if (hit?.Source == null)
            {
                continue;
            }

            this._log.LogTrace("Hit: {HitScore}, {HitId}", hit.Score, hit.Id);
            yield return (hit.Source!.ToMemoryRecord(), hit.Score ?? 0);
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
        this._log.LogTrace("Querying index '{IndexName}' with filters {Filters}. {Limit} {WithEmbeddings}",
            index, filters.ToDebugString(), limit, withEmbeddings);

        if (limit < 0)
        {
            limit = 10;
        }

        index = IndexNameHelper.Convert(index, this._config);

        // ES has a limit
        if (limit > 10000)
        {
            limit = 10000;
        }

        var resp = await this._client.SearchAsync<ElasticsearchMemoryRecord>(s =>
                    s.Index(index)
                        .Size(limit)
                        .Query(qd =>
                        {
                            this.ConvertTagFilters(qd, filters);
                        }),
                cancellationToken)
            .ConfigureAwait(false);

        if ((resp.HitsMetadata is null) || (resp.HitsMetadata.Hits is null))
        {
            yield break;
        }

        foreach (var hit in resp.Hits)
        {
            if (hit?.Source == null)
            {
                continue;
            }

            this._log.LogTrace("Hit: {HitScore}, {HitId}", hit.Score, hit.Id);
            yield return hit.Source!.ToMemoryRecord();
        }
    }

    //private string ConvertIndexName(string index) => ESIndexName.Convert(this._config.IndexPrefix + index);

    private QueryDescriptor<ElasticsearchMemoryRecord> ConvertTagFilters(
        QueryDescriptor<ElasticsearchMemoryRecord> qd,
        ICollection<MemoryFilter>? filters = null)
    {
        var hasOneNotEmptyFilter = filters != null && filters.Any(f => !f.IsEmpty());
        if (!hasOneNotEmptyFilter)
        {
            qd.MatchAll();
            return qd;
        }

        List<Query> super = new();

        foreach (MemoryFilter filter in filters!)
        {
            List<Query> thisMust = new();

            // Each filter is a list of key/value pairs.
            foreach (var baseFilter in filter.GetFilters())
            {
                Query newTagQuery = new TermQuery(ElasticsearchMemoryRecord.TagsName) { Value = baseFilter.Key };
                Query termQuery = new TermQuery(ElasticsearchMemoryRecord.TagsValue) { Value = baseFilter.Value ?? string.Empty };

                newTagQuery &= termQuery;

                var nestedQd = new NestedQuery();
                nestedQd.Path = ElasticsearchMemoryRecord.TagsField;
                nestedQd.Query = newTagQuery;

                if (baseFilter is EqualFilter eq)
                {
                    thisMust.Add(nestedQd);
                }
                else if (baseFilter is NotEqualFilter neq)
                {
                    var notQuery = new BoolQuery();
                    notQuery.MustNot = [nestedQd];
                    thisMust.Add(notQuery);
                }
                else
                {
                    throw new ElasticsearchException($"Filter type {baseFilter.GetType().Name} is not supported.");
                }
            }

            var filterQuery = new BoolQuery();
            filterQuery.Must = thisMust.ToArray();
            //filterQuery.MinimumShouldMatch = 1;

            super.Add(filterQuery);
        }

        qd.Bool(bq => bq.Should(super.ToArray()).MinimumShouldMatch(1));

        // ---------------------

        //qd.Nested(nqd =>
        //{
        //    nqd.Path(ElasticsearchMemoryRecord.TagsField);

        //    nqd.Query(nq =>
        //    {
        //        // Each filter is a tag collection.
        //        foreach (MemoryFilter filter in filters)
        //        {
        //            List<Query> all = new();

        //            // Each tag collection is an element of a List<string, List<string?>>>
        //            foreach (var tagName in filter.Keys)
        //            {
        //                List<string?> tagValues = filter[tagName];
        //                List<FieldValue> terms = tagValues.Select(x => (FieldValue)(x ?? FieldValue.Null))
        //                                                  .ToList();
        //                // ----------------

        //                Query newTagQuery = new TermQuery(ElasticsearchMemoryRecord.Tags_Name) { Value = tagName };
        //                newTagQuery &= new TermsQuery() {
        //                    Field = ElasticsearchMemoryRecord.Tags_Value,
        //                    Terms = new TermsQueryField(terms)
        //                };

        //                all.Add(newTagQuery);
        //            }

        //            nq.Bool(bq => bq.Must(all.ToArray()));
        //        }
        //    });
        //});

        return qd;
    }
}
