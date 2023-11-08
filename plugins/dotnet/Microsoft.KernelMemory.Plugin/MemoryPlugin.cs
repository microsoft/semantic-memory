﻿// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory.Plugin;
using Microsoft.SemanticKernel;

// ReSharper disable once CheckNamespace
// ReSharper disable ArrangeAttributes
namespace Microsoft.KernelMemory;

/// <summary>
/// Kernel Memory Plugin
///
/// Recommended name: "memory"
///
/// Functions:
/// * memory.save
/// * memory.saveFile
/// * memory.saveWebPage
/// * memory.ask
/// * memory.search
/// * memory.delete
/// </summary>
public class MemoryPlugin
{
    /// <summary>
    /// Name of the input variable used to specify which memory index to use.
    /// </summary>
    public const string IndexParam = "index";

    /// <summary>
    /// Name of the input variable used to specify a unique id associated with stored information.
    ///
    /// Important: the text is stored in memory over multiple records, using an internal format,
    /// and Document ID is used across all the internal memory records generated. Each of these internal
    /// records has an internal ID that is not exposed to memory clients. Document ID can be used
    /// to ask questions about a specific text, to overwrite (update) the text, and to delete it.
    /// </summary>
    public const string DocumentIdParam = "documentId";

    /// <summary>
    /// Name of the input variable used to specify optional tags associated with stored information.
    ///
    /// Tags can be used to filter memories over one or multiple keys, e.g. userID, tenant, groups,
    /// project ID, room number, content type, year, region, etc.
    /// Each tag can have multiple values, e.g. to link a memory to multiple users.
    /// </summary>
    public const string TagsParam = "tags";

    /// <summary>
    /// Name of the input variable used to specify custom memory ingestion steps.
    /// The list is usually: "extract", "partition", "gen_embeddings", "save_embeddings"
    /// </summary>
    public const string StepsParam = "steps";

    /// <summary>
    /// Default document ID. When null, a new value is generated every time some information
    /// is saved into memory.
    /// </summary>
    private const string? DefaultDocumentId = null;

    /// <summary>
    /// Default index where to store and retrieve memory from. When null the service
    /// will use a default index for all information.
    /// </summary>
    private readonly string? _defaultIndex = null;

    /// <summary>
    /// Default collection of tags to add to information when ingesting.
    /// </summary>
    private readonly TagCollection? _defaultIngestionTags = null;

    /// <summary>
    /// Default collection of tags required when retrieving memory (using filters).
    /// </summary>
    private readonly TagCollection? _defaultRetrievalTags = null;

    /// <summary>
    /// Default ingestion steps when storing new memories.
    /// </summary>
    private readonly List<string>? _defaultIngestionSteps = null;

    /// <summary>
    /// Whether to wait for the asynchronous ingestion to be complete when storing new memories.
    /// Note: the plugin will wait max <see cref="_maxIngestionWait"/> seconds to avoid blocking callers for too long.
    /// </summary>
    private readonly bool _waitForIngestionToComplete;

    /// <summary>
    /// Max time to wait for ingestion completion when <see cref="_waitForIngestionToComplete"/> is set to True.
    /// </summary>
    private TimeSpan _maxIngestionWait = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Client to memory read/write. This is usually an instance of MemoryWebClient
    /// but the plugin allows to inject any IKernelMemory, e.g. in case of custom
    /// implementations and the embedded Serverless client.
    /// </summary>
    private readonly IKernelMemory _memory;

    /// <summary>
    /// Create new instance using MemoryWebClient pointed at the given endpoint.
    /// </summary>
    /// <param name="endpoint">Memory Service endpoint</param>
    /// <param name="apiKey">Memory Service authentication API Key</param>
    /// <param name="defaultIndex">Default Memory Index to use when none is specified. Optional. Can be overridden on each call.</param>
    /// <param name="defaultIngestionTags">Default Tags to add to memories when importing data. Optional. Can be overridden on each call.</param>
    /// <param name="defaultRetrievalTags">Default Tags to require when searching memories. Optional. Can be overridden on each call.</param>
    /// <param name="defaultIngestionSteps">Pipeline steps to use when importing memories. Optional. Can be overridden on each call.</param>
    /// <param name="waitForIngestionToComplete">Whether to wait for the asynchronous ingestion to be complete when storing new memories.</param>
    public MemoryPlugin(
        Uri endpoint,
        string apiKey = "",
        string defaultIndex = "",
        TagCollection? defaultIngestionTags = null,
        TagCollection? defaultRetrievalTags = null,
        List<string>? defaultIngestionSteps = null,
        bool waitForIngestionToComplete = false)
        : this(
            new MemoryWebClient(endpoint.AbsoluteUri, apiKey),
            defaultIndex,
            defaultIngestionTags,
            defaultRetrievalTags,
            defaultIngestionSteps,
            waitForIngestionToComplete)
    {
    }

    /// <summary>
    /// Create new instance using MemoryWebClient pointed at the given endpoint.
    /// </summary>
    /// <param name="serviceUrl">Memory Service endpoint</param>
    /// <param name="apiKey">Memory Service authentication API  Key</param>
    /// <param name="waitForIngestionToComplete">Whether to wait for the asynchronous ingestion to be complete when storing new memories.</param>
    public MemoryPlugin(
        string serviceUrl,
        string apiKey = "",
        bool waitForIngestionToComplete = false)
        : this(
            endpoint: new Uri(serviceUrl),
            apiKey: apiKey,
            waitForIngestionToComplete: waitForIngestionToComplete)
    {
    }

    /// <summary>
    /// Create a new instance using a custom IKernelMemory implementation.
    /// </summary>
    /// <param name="memoryClient">Custom IKernelMemory implementation</param>
    /// <param name="defaultIndex">Default Memory Index to use when none is specified. Optional. Can be overridden on each call.</param>
    /// <param name="defaultIngestionTags">Default Tags to add to memories when importing data. Optional. Can be overridden on each call.</param>
    /// <param name="defaultRetrievalTags">Default Tags to require when searching memories. Optional. Can be overridden on each call.</param>
    /// <param name="defaultIngestionSteps">Pipeline steps to use when importing memories. Optional. Can be overridden on each call.</param>
    /// <param name="waitForIngestionToComplete">Whether to wait for the asynchronous ingestion to be complete when storing new memories.</param>
    public MemoryPlugin(
        IKernelMemory memoryClient,
        string defaultIndex = "",
        TagCollection? defaultIngestionTags = null,
        TagCollection? defaultRetrievalTags = null,
        List<string>? defaultIngestionSteps = null,
        bool waitForIngestionToComplete = false)
    {
        this._memory = memoryClient;
        this._defaultIndex = defaultIndex;
        this._defaultIngestionTags = defaultIngestionTags;
        this._defaultRetrievalTags = defaultRetrievalTags;
        this._defaultIngestionSteps = defaultIngestionSteps;
        this._waitForIngestionToComplete = waitForIngestionToComplete;
    }

    /// <summary>
    /// Store text information in long term memory.
    ///
    /// Usage from prompts: '{{memory.save ...}}'
    /// </summary>
    /// <example>
    /// SKContext.Variables["input"] = "the capital of France is Paris"
    /// {{memory.importText $input }}
    /// </example>
    /// <example>
    /// SKContext.Variables["input"] = "the capital of France is Paris"
    /// SKContext.Variables[MemoryPlugin.IndexParam] = "geography"
    /// {{memory.save $input }}
    /// </example>
    /// <example>
    /// SKContext.Variables["input"] = "the capital of France is Paris"
    /// SKContext.Variables[MemoryPlugin.DocumentIdParam] = "france001"
    /// {{memory.save $input }}
    /// </example>
    /// <returns>Document ID</returns>
    [SKFunction, Description("Store in memory the information extracted from the given text")]
    public async Task<string> SaveAsync(
        [Description("The information to save")]
        string input,
        [SKName(DocumentIdParam), Description("The document ID associated with the information to save"), DefaultValue(null)]
        string? documentId = null,
        [SKName(IndexParam), Description("Memories index associated with the information to save"), DefaultValue(null)]
        string? index = null,
        [SKName(TagsParam), Description("Memories index associated with the information to save"), DefaultValue(null)]
        TagCollectionWrapper? tags = null,
        [SKName(StepsParam), Description("Steps to parse the information and store in memory"), DefaultValue(null)]
        ListOfStringsWrapper? steps = null,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        if (this._waitForIngestionToComplete)
        {
            var cs = new CancellationTokenSource(this._maxIngestionWait);
            cancellationToken = cs.Token;
        }

        return await this._memory.ImportTextAsync(
                text: input,
                documentId: documentId,
                index: index ?? this._defaultIndex,
                tags: tags ?? this._defaultIngestionTags,
                steps: steps ?? this._defaultIngestionSteps,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    [SKFunction, Description("Store in memory the information extracted from a file")]
    public async Task<string> SaveFileAsync(
        [Description("Path of the file to save in memory")]
        string input,
        [SKName(DocumentIdParam), Description("The document ID associated with the information to save"), DefaultValue(null)]
        string? documentId = null,
        [SKName(IndexParam), Description("Memories index associated with the information to save"), DefaultValue(null)]
        string? index = null,
        [SKName(TagsParam), Description("Memories index associated with the information to save"), DefaultValue(null)]
        TagCollectionWrapper? tags = null,
        [SKName(StepsParam), Description("Steps to parse the information and store in memory"), DefaultValue(null)]
        ListOfStringsWrapper? steps = null,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)
    {
        if (this._waitForIngestionToComplete)
        {
            var cs = new CancellationTokenSource(this._maxIngestionWait);
            cancellationToken = cs.Token;
        }

        return await this._memory.ImportTextAsync(
                text: input,
                documentId: documentId,
                index: index ?? this._defaultIndex,
                tags: tags ?? this._defaultIngestionTags,
                steps: steps ?? this._defaultIngestionSteps,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    [SKFunction, Description("Store in memory the information extracted from a web page")]
    public async Task<string> SaveWebPageAsync()
    {
        throw new NotImplementedException();
    }

    [SKFunction, Description("Return up to N memories related to the input text")]
    public async Task<string> SearchAsync()
    {
        throw new NotImplementedException();
    }

    /// <summary>
    /// Answer a question using the information stored in long term memory.
    ///
    /// Usage from prompts: '{{memory.ask ...}}'
    /// </summary>
    /// <param name="input">The question to answer</param>
    /// <returns>The answer returned by the memory.</returns>
    [SKFunction, Description("Use long term memory to answer a quesion")]
    public async Task<string> AskAsync(
        [Description("The question to answer")]
        string input,
        [SKName(IndexParam), Description("Memories index to search for answers"), DefaultValue(null)]
        string? index = null,
        [SKName(IndexParam), Description("Memories index to search for answers"), DefaultValue(null)]
        double minRelevance = 0,
        ILoggerFactory? loggerFactory = null,
        CancellationToken cancellationToken = default)

    {
        if (this._waitForIngestionToComplete)
        {
            // var cs = new CancellationTokenSource(this._maxIngestionWait);
            var cs = new CancellationTokenSource(TimeSpan.FromMilliseconds(1));
            cancellationToken = cs.Token;
        }

        MemoryAnswer? answer = await this._memory.AskAsync(question: input, cancellationToken: cancellationToken).ConfigureAwait(false);
        return answer?.Result ?? string.Empty;
    }

    [SKFunction, Description("Remobe from memory all the information extracted from the given document ID")]
    public async Task<string> DeleteAsync()
    {
        throw new NotImplementedException();
    }
}
