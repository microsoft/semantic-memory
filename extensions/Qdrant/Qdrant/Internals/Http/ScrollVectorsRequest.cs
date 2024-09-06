// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Serialization;
using Microsoft.KernelMemory.MemoryDb.Qdrant.Internals;
using static Microsoft.KernelMemory.MemoryDb.Qdrant.Client.Http.Filter;

namespace Microsoft.KernelMemory.MemoryDb.Qdrant.Client.Http;

internal sealed class ScrollVectorsRequest
{
    private readonly string _collectionName;

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

    public static ScrollVectorsRequest Create(string collectionName)
    {
        return new ScrollVectorsRequest(collectionName);
    }

    public ScrollVectorsRequest HavingExternalId(string id)
    {
        ArgumentNullExceptionEx.ThrowIfNullOrWhiteSpace(id, nameof(id), "External ID is empty");
        this.Filters.AndValue(QdrantConstants.PayloadIdField, id);
        return this;
    }

    public ScrollVectorsRequest HavingAllTags(IEnumerable<TagFilter>? tagFilters)
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

    public ScrollVectorsRequest HavingSomeTags(IEnumerable<IEnumerable<TagFilter>>? tagFiltersGroups)
    {
        if (tagFiltersGroups == null) { return this; }

        var list = tagFiltersGroups.ToList();
        if (list.Count < 2)
        {
            return this.HavingAllTags(list.FirstOrDefault());
        }

        var orFilter = new Filter.OrClause();
        foreach (var tagFilters in list)
        {
            if (tagFilters == null) { continue; }

            var andFilter = new Filter.AndClause();
            foreach (var tagFilter in tagFilters)
            {
                if (!string.IsNullOrEmpty(tagFilter.Tag))
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
            }

            orFilter.Or(andFilter);
        }

        this.Filters.And(orFilter);

        return this;
    }

    public ScrollVectorsRequest IncludePayLoad()
    {
        this.WithPayload = true;
        return this;
    }

    public ScrollVectorsRequest IncludeVectorData(bool withVector)
    {
        this.WithVector = withVector;
        return this;
    }

    public ScrollVectorsRequest FromPosition(int offset)
    {
        this.Offset = offset;
        return this;
    }

    public ScrollVectorsRequest Take(int count)
    {
        this.Limit = count;
        return this;
    }

    public ScrollVectorsRequest TakeFirst()
    {
        return this.FromPosition(0).Take(1);
    }

    public HttpRequestMessage Build()
    {
        this.Validate();
        return HttpRequest.CreatePostRequest(
            $"collections/{this._collectionName}/points/scroll",
            payload: this);
    }

    private void Validate()
    {
        ArgumentNullExceptionEx.ThrowIfNullOrWhiteSpace(this._collectionName, nameof(this._collectionName), "The collection name cannot be empty");
        ArgumentOutOfRangeExceptionEx.ThrowIfZeroOrNegative(this.Limit, nameof(this.Limit), "The max number of vectors to retrieve must be greater than zero");

        this.Filters.Validate();
    }

    private ScrollVectorsRequest(string collectionName)
    {
        this._collectionName = collectionName;
        this.Filters = new Filter.AndClause();
        this.WithPayload = false;
        this.WithVector = false;

        // By default take the closest vector only
        this.FromPosition(0).TakeFirst();
    }
}
