// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Serialization;
using Microsoft.KernelMemory.MemoryDb.Qdrant.Internals;
using static Microsoft.KernelMemory.MemoryDb.Qdrant.Client.Http.Filter;

namespace Microsoft.KernelMemory.MemoryDb.Qdrant.Client.Http;

internal sealed class SearchVectorsRequest
{
    private readonly string _collectionName;

    [JsonPropertyName("vector")]
    [JsonConverter(typeof(Embedding.JsonConverter))]
    public Embedding StartingVector { get; set; }

    [JsonPropertyName("filter")]
    public Filter.AndClause Filters { get; set; }

    [JsonPropertyName("limit")]
    public int Limit { get; set; }

    [JsonPropertyName("offset")]
    public int Offset { get; set; }

    [JsonPropertyName("with_payload")]
    public bool WithPayload { get; set; }

    [JsonPropertyName("with_vector")]
    public bool WithVector { get; set; }

    [JsonPropertyName("score_threshold")]
    public double ScoreThreshold { get; set; } = -1;

    public static SearchVectorsRequest Create(string collectionName)
    {
        return new SearchVectorsRequest(collectionName);
    }

    public static SearchVectorsRequest Create(string collectionName, int vectorSize)
    {
        return new SearchVectorsRequest(collectionName).SimilarTo(new Embedding(vectorSize));
    }

    public SearchVectorsRequest SimilarTo(Embedding vector)
    {
        this.StartingVector = vector;
        return this;
    }

    public SearchVectorsRequest HavingExternalId(string externalId)
    {
        ArgumentNullExceptionEx.ThrowIfNullOrWhiteSpace(externalId, nameof(externalId), "External ID cannot be empty");
        this.Filters.AndValue(QdrantConstants.PayloadIdField, externalId);
        return this;
    }

    public SearchVectorsRequest HavingAllTags(IEnumerable<TagFilter>? tagFilters)
    {
        if (tagFilters == null) { return this; }

        foreach (var tagFilter in tagFilters)
        {
            if (!string.IsNullOrEmpty(tagFilter.Tag))
            {
                if (tagFilter.FilterType == TagFilterType.NotEqual)
                {
                    this.Filters.And(new MustNotClause(QdrantConstants.PayloadTagsField, tagFilter.Tag));
                }
                else if (tagFilter.FilterType == TagFilterType.Equal)
                {
                    this.Filters.AndValue(QdrantConstants.PayloadTagsField, tagFilter.Tag);
                }
                else
                {
                    throw new NotSupportedException($"Filter type {tagFilter.FilterType} is not supported in QDrant Memory");
                }
            }
        }

        return this;
    }

    public SearchVectorsRequest HavingSomeTags(List<IEnumerable<TagFilter>>? tagFiltersGroup)
    {
        if (tagFiltersGroup == null) { return this; }

        var list = tagFiltersGroup.ToList();
        if (list.Count < 2)
        {
            return this.HavingAllTags(list.FirstOrDefault());
        }

        var orFilter = new Filter.OrClause();
        foreach (var tagFilters in list)
        {
            if (tagFilters == null) { continue; }

            var andFilter = new Filter.AndClause();
            foreach (var tagFilter in tagFilters.Where(t => !string.IsNullOrEmpty(t.Tag)))
            {
                if (tagFilter.FilterType == TagFilterType.NotEqual)
                {
                    andFilter.And(new MustNotClause(QdrantConstants.PayloadTagsField, tagFilter.Tag));
                }
                else if (tagFilter.FilterType == TagFilterType.Equal)
                {
                    andFilter.AndValue(QdrantConstants.PayloadTagsField, tagFilter.Tag);
                }
                else
                {
                    throw new NotSupportedException($"Filter type {tagFilter.FilterType} is not supported in QDrant Memory");
                }
            }

            orFilter.Or(andFilter);
        }

        this.Filters.And(orFilter);

        return this;
    }

    public SearchVectorsRequest WithScoreThreshold(double scoreThreshold)
    {
        this.ScoreThreshold = scoreThreshold;
        return this;
    }

    public SearchVectorsRequest IncludePayLoad()
    {
        this.WithPayload = true;
        return this;
    }

    public SearchVectorsRequest IncludeVectorData(bool withVector)
    {
        this.WithVector = withVector;
        return this;
    }

    public SearchVectorsRequest FromPosition(int offset)
    {
        this.Offset = offset;
        return this;
    }

    public SearchVectorsRequest Take(int count)
    {
        this.Limit = count;
        return this;
    }

    public SearchVectorsRequest TakeFirst()
    {
        return this.FromPosition(0).Take(1);
    }

    public HttpRequestMessage Build()
    {
        this.Validate();
        return HttpRequest.CreatePostRequest(
            $"collections/{this._collectionName}/points/search",
            payload: this);
    }

    private void Validate()
    {
        ArgumentNullExceptionEx.ThrowIfNull(this.StartingVector, nameof(this.StartingVector), "Missing target vector, either provide a vector or vector size");
        ArgumentNullExceptionEx.ThrowIfNullOrWhiteSpace(this._collectionName, nameof(this._collectionName), "The collection name cannot be empty");
        ArgumentOutOfRangeExceptionEx.ThrowIfZeroOrNegative(this.Limit, nameof(this.Limit), "The max number of vectors to retrieve must be greater than zero");

        this.Filters.Validate();
    }

    private SearchVectorsRequest(string collectionName)
    {
        this._collectionName = collectionName;
        this.Filters = new Filter.AndClause();
        this.WithPayload = false;
        this.WithVector = false;

        // By default take the closest vector only
        this.FromPosition(0).TakeFirst();
    }
}
