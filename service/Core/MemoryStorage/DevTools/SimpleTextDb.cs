// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory.Diagnostics;
using Microsoft.KernelMemory.FileSystem.DevTools;

namespace Microsoft.KernelMemory.MemoryStorage.DevTools;

/// <summary>
/// Development only implementation of IMemoryDb, used to test KM
/// without dependencies on embedding generators.
/// This is NOT meant for real scenarios, only for code development.
/// </summary>
[Experimental("KMEXP03")]
public class SimpleTextDb : IMemoryDb
{
    private readonly IFileSystem _fileSystem;
    private readonly ILogger<SimpleTextDb> _log;

    public SimpleTextDb(
        SimpleTextDbConfig config,
        ILoggerFactory? loggerFactory = null)
    {
        this._log = (loggerFactory ?? DefaultLogger.Factory).CreateLogger<SimpleTextDb>();
        switch (config.StorageType)
        {
            case FileSystemTypes.Disk:
                this._fileSystem = new DiskFileSystem(config.Directory, null, loggerFactory);
                break;

            case FileSystemTypes.Volatile:
                this._fileSystem = VolatileFileSystem.GetInstance(config.Directory, null, loggerFactory);
                break;

            default:
                throw new ArgumentException($"Unknown storage type {config.StorageType}");
        }
    }

    /// <inheritdoc />
    public Task CreateIndexAsync(string index, int vectorSize, CancellationToken cancellationToken = default)
    {
        index = NormalizeIndexName(index);
        return this._fileSystem.CreateVolumeAsync(index, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IEnumerable<string>> GetIndexesAsync(CancellationToken cancellationToken = default)
    {
        return this._fileSystem.ListVolumesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task DeleteIndexAsync(string index, CancellationToken cancellationToken = default)
    {
        index = NormalizeIndexName(index);
        return this._fileSystem.DeleteVolumeAsync(index, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string> UpsertAsync(string index, MemoryRecord record, CancellationToken cancellationToken = default)
    {
        index = NormalizeIndexName(index);
        await this._fileSystem.WriteFileAsync(index, "", EncodeId(record.Id), JsonSerializer.Serialize(record), cancellationToken).ConfigureAwait(false);
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
        if (limit <= 0) { limit = int.MaxValue; }

        index = NormalizeIndexName(index);

        var list = this.GetListAsync(index, filters, limit, withEmbeddings, cancellationToken);
        var records = new Dictionary<string, MemoryRecord>();
        await foreach (MemoryRecord r in list.ConfigureAwait(false))
        {
            records[r.Id] = r;
        }

        var words = Regex.Replace(text, "[^a-zA-Z0-9_]+", " ")
            .Split(' ').Select(x => x.Trim()).Where(x => !string.IsNullOrEmpty(x)).ToList();

        var similarity = new Dictionary<string, int>();
        foreach (var record in records)
        {
            similarity[record.Value.Id] = 0;
            var storedText = record.Value.Payload[Constants.ReservedPayloadTextField].ToString();
            if (string.IsNullOrEmpty(storedText)) { continue; }

            foreach (var word in words)
            {
                if (storedText.Contains(word, StringComparison.OrdinalIgnoreCase))
                {
                    similarity[record.Value.Id] += 1;
                }
            }
        }

        // Sort distances, from closest to most distant, and filter out irrelevant results
        IEnumerable<string> sorted =
            from entry in similarity
            where entry.Value >= minRelevance
            orderby entry.Value descending
            select entry.Key;

        // Return <count> records, including the calculated distance
        var count = 0;
        foreach (string id in sorted)
        {
            if (count++ < limit)
            {
                yield return (records[id], similarity[id]);
            }
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
        if (limit <= 0) { limit = int.MaxValue; }

        index = NormalizeIndexName(index);

        // Remove empty filters
        filters = filters?.Where(f => !f.IsEmpty()).ToList();

        IDictionary<string, string> list;
        try
        {
            list = await this._fileSystem.ReadAllFilesAsTextAsync(index, "", cancellationToken).ConfigureAwait(false);
        }
        catch (System.IO.DirectoryNotFoundException)
        {
            // Index doesn't exist
            list = new Dictionary<string, string>();
        }

        foreach (KeyValuePair<string, string> v in list)
        {
            var record = JsonSerializer.Deserialize<MemoryRecord>(v.Value);
            if (record == null) { continue; }

            if (TagsMatchFilters(record.Tags, filters))
            {
                if (limit-- <= 0) { yield break; }

                yield return record;
            }
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(string index, MemoryRecord record, CancellationToken cancellationToken = default)
    {
        index = NormalizeIndexName(index);
        return this._fileSystem.DeleteFileAsync(index, "", EncodeId(record.Id), cancellationToken);
    }

    #region private

    // Note: normalize "_" to "-" for consistency with other DBs
    private static readonly Regex s_replaceIndexNameCharsRegex = new(@"[\s|\\|/|.|_|:]");
    private const string ValidSeparator = "-";

    private static string NormalizeIndexName(string index)
    {
        ArgumentNullExceptionEx.ThrowIfNullOrWhiteSpace(index, nameof(index), "The index name is empty");
        index = s_replaceIndexNameCharsRegex.Replace(index.Trim().ToLowerInvariant(), ValidSeparator);

        return index.Trim();
    }

    private static bool TagsMatchFilters(TagCollection tags, ICollection<MemoryFilter>? filters)
    {
        if (filters == null || filters.Count == 0) { return true; }

        // Verify that at least one filter matches (OR logic)
        foreach (MemoryFilter filter in filters)
        {
            var match = true;

            // Verify that all conditions are met (AND logic)
            var allFilters = filter.GetFilters();
            foreach (var baseFilter in allFilters)
            {
                if (baseFilter is EqualFilter eq)
                {
                    // Verify that it contains key and value required
                    match = match && tags.ContainsKey(eq.Key) && tags[eq.Key].Contains(eq.Value);
                }
                else if (baseFilter is NotEqualFilter neq)
                {
                    // Verify that tag is not contained at all, or if is present it doesn't contain the value
                    match = match && (!tags.ContainsKey(neq.Key) || !tags[neq.Key].Contains(neq.Value));
                }
                else
                {
                    throw new ArgumentException($"Unknown filter type {baseFilter.GetType()}");
                }
            }

            if (match) { return true; }
        }

        return false;
    }

    private static string EncodeId(string realId)
    {
        var bytes = Encoding.UTF8.GetBytes(realId);
        return Convert.ToBase64String(bytes).Replace('=', '_');
    }

    private static string DecodeId(string encodedId)
    {
        var bytes = Convert.FromBase64String(encodedId.Replace('_', '='));
        return Encoding.UTF8.GetString(bytes);
    }

    #endregion
}
