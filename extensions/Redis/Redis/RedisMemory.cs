// Copyright (c) Microsoft. All rights reserved.

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory.AI;
using Microsoft.KernelMemory.Diagnostics;
using Microsoft.KernelMemory.MemoryStorage;
using NRedisStack;
using NRedisStack.RedisStackCommands;
using NRedisStack.Search;
using NRedisStack.Search.Literals.Enums;
using StackExchange.Redis;

namespace Microsoft.KernelMemory.MemoryDb.Redis;

/// <summary>
/// Implementation of an IMemoryDb using Redis.
/// </summary>
[Experimental("KMEXP03")]
public sealed class RedisMemory : IMemoryDb
{
    private readonly IDatabase _db;
    private readonly ISearchCommandsAsync _search;
    private readonly RedisConfig _config;
    private readonly ITextEmbeddingGenerator _embeddingGenerator;
    private readonly ILogger<RedisMemory> _logger;
    private readonly string[] _fieldNamesNoEmbeddings;

    /// <summary>
    /// Initializes the <see cref="RedisMemory"/> instance
    /// </summary>
    /// <param name="multiplexer"></param>
    /// <param name="config"></param>
    /// <param name="embeddingGenerator"></param>
    /// <param name="logger"></param>
    public RedisMemory(
        RedisConfig config,
        IConnectionMultiplexer multiplexer,
        ITextEmbeddingGenerator embeddingGenerator,
        ILogger<RedisMemory>? logger = null)
    {
        this._config = config;
        this._embeddingGenerator = embeddingGenerator;
        this._logger = logger ?? DefaultLogger<RedisMemory>.Instance;
        this._search = multiplexer.GetDatabase().FT();
        this._db = multiplexer.GetDatabase();
        this._fieldNamesNoEmbeddings = config.Tags.Keys.Append(PayloadFieldName).Append(DistanceFieldName).ToArray();
    }

    /// <inheritdoc />
    public async Task CreateIndexAsync(string index, int vectorSize, CancellationToken cancellationToken = default)
    {
        var normalizedIndexName = NormalizeIndexName(index, this._config.AppPrefix);
        var schema = new Schema().AddVectorField(EmbeddingFieldName, this._config.VectorAlgorithm, new Dictionary<string, object>()
        {
            { "TYPE", "FLOAT32" },
            { "DIM", vectorSize },
            { "DISTANCE_METRIC", "COSINE" }
        });

        var ftParams = new FTCreateParams().On(IndexDataType.HASH).Prefix($"{normalizedIndexName}:");

        foreach (var tag in this._config.Tags)
        {
            var fieldName = tag.Key;
            var separator = tag.Value ?? DefaultSeparator;
            schema.AddTagField(fieldName, separator: separator.ToString());
        }

        try
        {
            await this._search.CreateAsync(normalizedIndexName, ftParams, schema).ConfigureAwait(false);
        }
        catch (RedisServerException ex)
        {
            if (!ex.Message.Contains("Index already exists", StringComparison.OrdinalIgnoreCase))
            {
                throw;
            }
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<string>> GetIndexesAsync(CancellationToken cancellationToken = default)
    {
        var result = await this._search._ListAsync().ConfigureAwait(false);
        return result.Select(x => ((string)x!)).Where(x => x.StartsWith($"{this._config.AppPrefix}", StringComparison.Ordinal)).Select(x => x.Substring(this._config.AppPrefix.Length));
    }

    /// <inheritdoc />
    public async Task DeleteIndexAsync(string index, CancellationToken cancellationToken = default)
    {
        var normalizedIndexName = NormalizeIndexName(index, this._config.AppPrefix);
        try
        {
            // we are explicitly dropping all records associated with the index here.
            await this._search.DropIndexAsync(normalizedIndexName, dd: true).ConfigureAwait(false);
        }
        catch (RedisServerException exception)
        {
            if (!exception.Message.Equals("unknown index name", StringComparison.OrdinalIgnoreCase))
            {
                throw;
            }
        }
    }

    /// <inheritdoc />
    public async Task<string> UpsertAsync(string index, MemoryRecord record, CancellationToken cancellationToken = default)
    {
        var normalizedIndexName = NormalizeIndexName(index, this._config.AppPrefix);
        var key = Key(normalizedIndexName, record.Id);
        var args = new List<RedisValue>
        {
            NormalizeIndexName(index, this._config.AppPrefix),
            EmbeddingFieldName,
            record.Vector.VectorBlob()
        };
        foreach (var item in record.Tags)
        {
            var isIndexed = this._config.Tags.TryGetValue(item.Key, out var c);
            var separator = c ?? DefaultSeparator;
            if (!isIndexed)
            {
                this._logger.LogError("Attempt to insert un-indexed tag field: '{Key}', will not be able to filter on it, please adjust the tag settings in your Redis Configuration", item.Key);
                throw new ArgumentException($"Attempt to insert un-indexed tag field: '{item.Key}', will not be able to filter on it, please adjust the tag settings in your Redis Configuration");
            }

            if (item.Value.Any(s => s is not null && s.Contains(separator.ToString(), StringComparison.InvariantCulture)))
            {
                this._logger.LogError("Attempted to insert record with tag field: {Key} containing the separator: '{Separator}'. " +
                                      "Update your {RedisConfig} to use a different separator, or remove the separator from the field.", item.Key, separator, nameof(RedisConfig));
                throw new ArgumentException($"Attempted to insert record with tag field: '{item.Key}' containing the separator: '{separator}'. " +
                                            $"Update your {nameof(RedisConfig)} to use a different separator, or remove the separator from the field.");
            }

            args.Add(item.Key);
            args.Add(string.Join(separator, item.Value));
        }

        if (record.Payload.Count != 0)
        {
            args.Add(PayloadFieldName);
            args.Add(JsonSerializer.Serialize(record.Payload));
        }

        var scriptResult = (await this._db.ScriptEvaluateAsync(Scripts.CheckIndexAndUpsert, new RedisKey[] { key }, args.ToArray()).ConfigureAwait(false)).ToString()!;
        if (scriptResult == "false")
        {
            await this.CreateIndexAsync(index, record.Vector.Length, cancellationToken).ConfigureAwait(false);
            await this._db.ScriptEvaluateAsync(Scripts.CheckIndexAndUpsert, new RedisKey[] { key }, args.ToArray()).ConfigureAwait(false);
        }
        else if (scriptResult.StartsWith("(error)", StringComparison.Ordinal))
        {
            throw new RedisException(scriptResult);
        }

        return record.Id;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<(MemoryRecord, double)> GetSimilarListAsync(string index, string text, ICollection<MemoryFilter>? filters = null, double minRelevance = 0, int limit = 1, bool withEmbeddings = false, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var normalizedIndexName = NormalizeIndexName(index, this._config.AppPrefix);
        var embedding = await this._embeddingGenerator.GenerateEmbeddingAsync(text, cancellationToken).ConfigureAwait(false);
        var blob = embedding.VectorBlob();
        var parameters = new Dictionary<string, object>
        {
            { "blob", blob },
            { "limit", limit }
        };

        var sb = new StringBuilder();
        if (filters?.Any(f => !f.IsEmpty()) == true)
        {
            sb.Append('(');
            //now filtering for each filter that is not empty
            foreach (var filter in filters.Where(f => !f.IsEmpty()))
            {
                sb.Append('(');
                var validFilters = filter.GetFilters();
                foreach (BaseFilter baseFilter in validFilters)
                {
                    if (baseFilter is EqualFilter eq)
                    {
                        sb.Append(CultureInfo.InvariantCulture, $"@{eq.Key}:{{{eq.Value}}} ");
                    }
                    else if (baseFilter is NotEqualFilter neq)
                    {
                        //use the -(tag:value) syntax for not equal to avoid returning ANY record where ONE of the
                        //tag is the one we are filtering on.
                        sb.Append(CultureInfo.InvariantCulture, $"-(@{neq.Key}:{{{neq.Value}}}) ");
                    }
                    else
                    {
                        throw new RedisException($"Filter of type {baseFilter.GetType().Name} is not supported by redis");
                    }
                }

                sb.Replace(" ", ")|", sb.Length - 1, 1);
            }

            sb.Replace('|', ')', sb.Length - 1, 1);
        }
        else
        {
            sb.Append('*');
        }

        sb.Append($"=>[KNN $limit @{EmbeddingFieldName} $blob]");

        var query = new Query(sb.ToString());
        query.Params(parameters);
        query.Limit(0, limit);
        query.Dialect(2);
        query.SortBy = DistanceFieldName;
        if (!withEmbeddings)
        {
            query.ReturnFields(this._fieldNamesNoEmbeddings);
        }

        NRedisStack.Search.SearchResult? result = null;

        try
        {
            result = await this._search.SearchAsync(normalizedIndexName, query).ConfigureAwait(false);
        }
        catch (RedisServerException e)
        {
            if (!e.Message.Contains("no such index", StringComparison.OrdinalIgnoreCase))
            {
                throw;
            }
        }

        if (result is null)
        {
            yield break;
        }

        foreach (var doc in result.Documents)
        {
            var next = this.FromDocument(doc, withEmbeddings);
            if (next.Item2 > minRelevance)
            {
                yield return next;
            }
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<MemoryRecord> GetListAsync(string index, ICollection<MemoryFilter>? filters = null, int limit = 1, bool withEmbeddings = false, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var normalizedIndexName = NormalizeIndexName(index, this._config.AppPrefix);
        var sb = new StringBuilder();
        if (filters != null && filters.Any(x => x.Pairs.Any()))
        {
            foreach ((string key, string? value) in filters.SelectMany(x => x.Pairs))
            {
                if (value is null)
                {
                    this._logger.LogWarning("Attempted to perform null check on tag field. This behavior is not supported by Redis");
                }

                sb.Append(CultureInfo.InvariantCulture, $" @{key}:{{{EscapeTagField(value!)}}}");
            }
        }
        else
        {
            sb.Append('*');
        }

        var query = new Query(sb.ToString());
        if (!withEmbeddings)
        {
            query.ReturnFields(this._fieldNamesNoEmbeddings);
        }

        List<NRedisStack.Search.Document> documents = new();
        try
        {
            // handle the case of negative indexes (-1 = end, -2 = 1 from end, etc. . .)
            if (limit < 0)
            {
                var numOfDocumentsFromEnd = -1 * (limit + 1);

                var probingQueryTask = this._search.SearchAsync(normalizedIndexName, query);
                var configurationCheckTask = this._search.ConfigGetAsync("MAXSEARCHRESULTS"); // need to query Max Search Results since Redis doesn't support unbounded queries.

                // pull back in one round trip, hence the separated awaits.
                var firstTripDocs = await probingQueryTask.ConfigureAwait(false);
                var configurationResult = await configurationCheckTask.ConfigureAwait(false);

                var docsNeeded = (int)firstTripDocs.TotalResults - numOfDocumentsFromEnd;

                documents.AddRange(firstTripDocs.Documents.Take(docsNeeded));
                if (docsNeeded > 10)
                {
                    if (configurationResult.TryGetValue("MAXSEARCHRESULTS", out string? value) && int.TryParse(value, out var maxSearchResults))
                    {
                        limit = Math.Min(docsNeeded - 10, maxSearchResults);
                        query.Limit(10, limit);
                        var secondTripResults = await this._search.SearchAsync(normalizedIndexName, query).ConfigureAwait(false);
                        documents.AddRange(secondTripResults.Documents);
                    }
                    else // shouldn't be reachable.
                    {
                        throw new RedisException("Redis does not contain a valid value for MAXSEARCHRESULTS, possible configuration issue in Redis.");
                    }
                }
            }
            else
            {
                query.Limit(0, limit);
                var result = await this._search.SearchAsync(normalizedIndexName, query).ConfigureAwait(false);
                documents.AddRange(result.Documents);
            }
        }
        catch (RedisServerException ex) when (ex.Message.Contains("no such index", StringComparison.InvariantCulture))
        {
            // NOOP
        }

        foreach (var doc in documents)
        {
            yield return this.FromDocument(doc, withEmbeddings).Item1;
        }
    }

    /// <inheritdoc />
    public Task DeleteAsync(string index, MemoryRecord record, CancellationToken cancellationToken = default)
    {
        var normalizedIndexName = NormalizeIndexName(index, this._config.AppPrefix);
        var key = Key(normalizedIndexName, record.Id);
        return this._db.KeyDeleteAsync(key);
    }

    #region private ================================================================================

    private const string EmbeddingFieldName = "embedding";
    private const string PayloadFieldName = "payload";
    private const char DefaultSeparator = ',';
    private const string DistanceFieldName = $"__{EmbeddingFieldName}_score";

    /// <summary>
    /// Characters to escape when serializing a tag expression.
    /// </summary>
    private static readonly char[] s_tagEscapeChars =
    {
        ',', '.', '<', '>', '{', '}', '[', ']', '"', '\'', ':', ';',
        '!', '@', '#', '$', '%', '^', '&', '*', '(', ')', '-', '+', '=', '~', '|', ' ', '/',
    };

    /// <summary>
    /// Special chars to specifically replace within index names to keep
    /// index names consistent with other connectors.
    /// </summary>
    private static readonly Regex s_replaceIndexNameCharsRegex = new(@"[\s|\\|/|.|_|:]");

    /// <summary>
    /// Use designated KM separator
    /// </summary>
    private const string KmSeparator = "-";

    private (MemoryRecord, double) FromDocument(NRedisStack.Search.Document doc, bool withEmbedding)
    {
        double distance = 0;
        var memoryRecord = new MemoryRecord();
        memoryRecord.Id = doc.Id.Split(":", 2)[1];
        foreach (var field in doc.GetProperties())
        {
            if (field.Key == EmbeddingFieldName)
            {
                if (withEmbedding)
                {
                    var floats = ByteArrayToFloatArray((byte[])field.Value!);
                    memoryRecord.Vector = new Embedding(floats);
                }
            }
            else if (field.Key == PayloadFieldName)
            {
                var payload = JsonSerializer.Deserialize<Dictionary<string, object>>(field.Value.ToString());
                memoryRecord.Payload = payload ?? new Dictionary<string, object>();
            }
            else if (field.Key == DistanceFieldName)
            {
                distance = 1 - (double)field.Value;
            }
            else
            {
                this._config.Tags.TryGetValue(field.Key, out var c);
                var separator = c ?? DefaultSeparator;
                var values = ((string)field.Value!).Split(separator);
                memoryRecord.Tags.Add(new KeyValuePair<string, List<string?>>(field.Key, new List<string?>(values)));
            }
        }

        return (memoryRecord, distance);
    }

    /// <summary>
    /// Normalizes the provided index name to maintain consistent looking
    /// index names across connections. Naturally Redis's index names
    /// are binary safe so this is purely for consistency.
    /// </summary>
    private static string NormalizeIndexName(string index, string? prefix = null)
    {
        ArgumentNullExceptionEx.ThrowIfNullOrWhiteSpace(index, nameof(index), "The index name is empty");

        var indexWithPrefix = !string.IsNullOrWhiteSpace(prefix) ? $"{prefix}{index}" : index;

        indexWithPrefix = s_replaceIndexNameCharsRegex.Replace(indexWithPrefix.Trim().ToLowerInvariant(), KmSeparator);

        return indexWithPrefix;
    }

    /// <summary>
    /// Escapes a tag field string.
    /// </summary>
    /// <param name="text">the text toe escape.</param>
    /// <returns>The Escaped Text.</returns>
    private static string EscapeTagField(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text)
        {
            if (s_tagEscapeChars.Contains(c))
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static RedisKey Key(string indexWithPrefix, string id) => $"{indexWithPrefix}:{id}";

    private static float[] ByteArrayToFloatArray(byte[] bytes)
    {
        if (bytes.Length % 4 != 0)
        {
            throw new InvalidOperationException("Encountered an unbalanced array of bytes for float array conversion");
        }

        var res = new float[bytes.Length / 4];
        for (int i = 0; i < bytes.Length / 4; i++)
        {
            res[i] = BitConverter.ToSingle(bytes, i * 4);
        }

        return res;
    }

    #endregion
}
