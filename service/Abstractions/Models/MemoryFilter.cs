// Copyright (c) Microsoft. All rights reserved.

using System.Collections.Generic;
using System.Linq;

namespace Microsoft.KernelMemory;

public class MemoryFilter : TagCollection
{
    /// <summary>
    /// This collection of tags contains all the tags that are used to
    /// negatively filter out memory records.
    /// </summary>
    private readonly TagCollection _notTags = new();

    public bool IsEmpty()
    {
        return this.Count == 0 && this._notTags.Count == 0;
    }

    public MemoryFilter ByTag(string name, string value)
    {
        this.Add(name, value);
        return this;
    }

    public MemoryFilter ByNotTag(string name, string value)
    {
        this._notTags.Add(name, value);
        return this;
    }

    public MemoryFilter ByDocument(string docId)
    {
        this.Add(Constants.ReservedDocumentIdTag, docId);
        return this;
    }

    /// <summary>
    /// Get a composition of all filters, And and Not.
    /// </summary>
    /// <returns></returns>
    public IEnumerable<BaseFilter> GetFilters()
    {
        var equalFilters = this.Pairs
            .Where(f => !string.IsNullOrEmpty(f.Value))
            .Select(pair => (BaseFilter)new EqualFilter(pair.Key, pair.Value!));

        var notEqualFilters = this._notTags.Pairs
            .Where(f => !string.IsNullOrEmpty(f.Value))
            .Select(pair => (BaseFilter)new NotEqualFilter(pair.Key, pair.Value!));

        return equalFilters.Union(notEqualFilters);
    }
}

/// <summary>
/// This is the base filter, which is used to create different types of filters
/// </summary>
/// <param name="Key"></param>
/// <param name="Value"></param>
public record BaseFilter(string Key, string Value);

/// <summary>
/// Filter for equality, tag named <paramref name="Key"/> must have the value <paramref name="Value"/>
/// </summary>
/// <param name="Key"></param>
/// <param name="Value"></param>
public record EqualFilter(string Key, string Value) : BaseFilter(Key, Value);

/// <summary>
/// Filter for inequality, tag named <paramref name="Key"/> must not have the value <paramref name="Value"/>
/// </summary>
/// <param name="Key"></param>
/// <param name="Value"></param>
public record NotEqualFilter(string Key, string Value) : BaseFilter(Key, Value);

/// <summary>
/// Factory for <see cref="MemoryFilter"/>, to allow for a simpler syntax
/// Instead of: new MemoryFilter().ByDocument(id).ByTag(k, v)
/// Recommended: MemoryFilters.ByDocument(id).ByTag(k, v)
/// </summary>
public static class MemoryFilters
{
    public static MemoryFilter ByTag(string name, string value)
    {
        return new MemoryFilter().ByTag(name, value);
    }

    /// <summary>
    /// Filter for all memory records that do not have the specified tag with that
    /// specific value.
    /// </summary>
    /// <param name="name"></param>
    /// <param name="value"></param>
    /// <returns></returns>
    public static MemoryFilter ByNotTag(string name, string value)
    {
        return new MemoryFilter().ByNotTag(name, value);
    }

    public static MemoryFilter ByDocument(string docId)
    {
        return new MemoryFilter().ByDocument(docId);
    }
}
