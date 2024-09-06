// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.KernelMemory.MemoryDb.AzureAISearch;

internal static class AzureAISearchFiltering
{
    private static readonly char[] s_searchInDelimitersAvailable = { '|', ',', ';', '-', '_', '.', ' ', '^', '$', '*', '`', '#', '@', '&', '/', '~' };

    /// <summary>
    /// Build a search query optimized to scale for in case a key has several (hundreds+) values
    /// Note:
    /// * One filter can have multiple key-values: these are combined with AND conditions
    /// * Multiple filters are combined using OR
    /// * Multiple filters can apply to the same key:
    ///     * if filters have only one condition, these are rendered using "search.in"
    ///     * if filters have multiple conditions, the values are combined with OR
    /// </summary>
    /// <param name="filters">List of filters</param>
    /// <returns>Query string for Azure AI Search</returns>
    internal static string BuildSearchFilter(IEnumerable<MemoryFilter> filters)
    {
        List<string> conditions = new();
        var filterList = filters?.ToList() ?? new List<MemoryFilter>();

        // Get all non empty filters with the same key and more than one value, and combine them using "search.in"
        // - If the filter has more than one filter we will exclude it, it means that needs to be composed with an AND (f.i. memoryFilter.ByTag("tag1", "value1").ByTag("tag2", "value2"))
        // - If the filter has only one filter, it means that it can be grouped with other filters with the same key to be composed with an OR
        var filtersForSearchInQuery = filterList
            // Filters with only one key, but not multiple values (i.e: excluding MemoryFilters.ByTag("department", "HR").ByTag("department", "Marketing") as here we want an `AND`)
            .Where(filter => !filter.IsEmpty() && filter.Keys.Count == 1 && filter.Values.First().Count == 1)
            .SelectMany(filter => filter.GetFilters()) // Flattening to pairs
            .GroupBy(pair => pair.Key) // Grouping by the tag key
            .Where(g => g.Count() > 1)
            .Select(group => new
            {
                Key = group.Key,
                EqualValues = group.OfType<EqualFilter>().Select(baseFilter => $"{baseFilter.Key}:{baseFilter.Value?.Replace("'", "''", StringComparison.Ordinal)}").ToList(),
                NotEqualValues = group.OfType<NotEqualFilter>().Select(baseFilter => $"{baseFilter.Key}:{baseFilter.Value?.Replace("'", "''", StringComparison.Ordinal)}").ToList(),
                SearchInDelimiter = s_searchInDelimitersAvailable.FirstOrDefault(specialChar =>
                    !group.Any(pair =>
                        (pair.Value != null && pair.Value.Contains(specialChar, StringComparison.Ordinal)) ||
                        (pair.Key != null && pair.Key.Contains(specialChar, StringComparison.Ordinal))))
            })
            .Where(item => item.SearchInDelimiter != '\0') // Only items with a valid SearchInDelimiter
            .ToList();

        foreach (var filterGroup in filtersForSearchInQuery)
        {
            // search.in syntax: https://learn.microsoft.com/en-us/azure/search/search-query-odata-search-in-function#syntax
            // delimiter: A string where each character is treated as a separator when parsing the valueList parameter.
            // The default value of this parameter is ' ,' which means that any values with spaces and/or commas between them will be separated.
            // If you need to use separators other than spaces and commas because your values include those characters,
            // you can specify alternate delimiters such as '|' in this parameter.
            if (filterGroup.EqualValues.Count != 0)
            {
                conditions.Add($"tags/any(s: search.in(s, '{string.Join(filterGroup.SearchInDelimiter, filterGroup.EqualValues)}', '{filterGroup.SearchInDelimiter}'))");
            }

            if (filterGroup.NotEqualValues.Count != 0)
            {
                conditions.Add($"not tags/any(s: search.in(s, '{string.Join(filterGroup.SearchInDelimiter, filterGroup.NotEqualValues)}', '{filterGroup.SearchInDelimiter}'))");
            }
        }

        //Exclude filters that were grouped before in the search.in process
        var keysToExclude = filtersForSearchInQuery.Select(item => item.Key).ToHashSet();
        var remainingFilters = filterList
            .Where(filter => !filter.IsEmpty() && !keysToExclude.Contains(filter.Keys.FirstOrDefault() ?? ""))
            .ToList();

        // Note: empty filters would lead to a syntax error, so even if they are supposed
        // to be removed upstream, we check again and remove them here too.
        foreach (var filter in remainingFilters)
        {
            var filterConditions = filter.GetFilters()
                .Select(baseFilter =>
                {
                    if (baseFilter is EqualFilter eq)
                    {
                        var fieldValue = eq.Value?.Replace("'", "''", StringComparison.Ordinal);
                        return $"tags/any(s: s eq '{baseFilter.Key}{Constants.ReservedEqualsChar}{fieldValue}')";
                    }
                    else if (baseFilter is NotEqualFilter neq)
                    {
                        var fieldValue = neq.Value?.Replace("'", "''", StringComparison.Ordinal);
                        return $"not tags/any(s: s eq '{baseFilter.Key}{Constants.ReservedEqualsChar}{fieldValue}')";
                    }
                    else
                    {
                        throw new AzureAISearchMemoryException($"Filter type {baseFilter.GetType().Name} is not supported.");
                    }
                })
                .ToList();

            conditions.Add($"({string.Join(" and ", filterConditions)})");
        }

        // Examples:
        // In search.in queries delimiter will vary according to the special chars found in the values
        // (tags/any(s: search.in(s, 'Authorized:0000-0000-0000-00000000|Authorized:0000-0000-0000-00000001', '|'))) or (tags/any(s: s eq 'user:someone2') and tags/any(s: s eq 'type:news'))
        // (tags/any(s: s eq 'user:someone1') and tags/any(s: s eq 'type:news')) or (tags/any(s: s eq 'user:someone2') and tags/any(s: s eq 'type:news'))
        // (tags/any(s: s eq 'user:someone1') and tags/any(s: s eq 'type:news')) or (tags/any(s: s eq 'user:admin') and tags/any(s: s eq 'type:fact'))
        return string.Join(" or ", conditions);
    }

    /*
    /// <summary>
    /// This is here to allow comparing the new logic above with the old/original version, e.g. unit tests and debugging.
    /// </summary>
    internal static string BuildSearchFilterV1(IEnumerable<MemoryFilter> filters)
    {
        List<string> conditions = new();

        // Note: empty filters would lead to a syntax error, so even if they are supposed
        // to be removed upstream, we check again and remove them here too.
        foreach (var filter in filters.Where(f => !f.IsEmpty()))
        {
            var filterConditions = filter.GetFilters()
                .Select(keyValue =>
                {
                    var fieldValue = keyValue.Value?.Replace("'", "''", StringComparison.Ordinal);
                    return $"tags/any(s: s eq '{keyValue.Key}{Constants.ReservedEqualsChar}{fieldValue}')";
                })
                .ToList();

            conditions.Add($"({string.Join(" and ", filterConditions)})");
        }

        // Examples:
        // (tags/any(s: s eq 'user:someone1') and tags/any(s: s eq 'type:news')) or (tags/any(s: s eq 'user:someone2') and tags/any(s: s eq 'type:news'))
        // (tags/any(s: s eq 'user:someone1') and tags/any(s: s eq 'type:news')) or (tags/any(s: s eq 'user:admin') and tags/any(s: s eq 'type:fact'))
        return string.Join(" or ", conditions);
    }
    */
}
