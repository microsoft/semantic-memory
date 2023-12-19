﻿// Copyright (c) Microsoft. All rights reserved.

using Microsoft.KernelMemory.AI;
using Microsoft.KernelMemory.Configuration;
using Microsoft.SemanticKernel.AI.Embeddings;
using Microsoft.SemanticKernel.AI.TextGeneration;

namespace Microsoft.KernelMemory;

/// <summary>
/// Kernel Memory builder extensions.
/// </summary>
public static partial class KernelMemoryBuilderExtensions
{
    /// <summary>
    /// Inject an implementation of <see cref="ITextGenerationService">SK text generation service</see>
    /// for local dependencies on <see cref="ITextGenerator"/>
    /// </summary>
    /// <param name="builder">KM builder</param>
    /// <param name="service">SK text generation service instance</param>
    /// <param name="config">SK text generator settings</param>
    /// <param name="tokenizer">Tokenizer used to count tokens used by prompts</param>
    /// <returns>KM builder</returns>
    public static IKernelMemoryBuilder WithSemanticKernelTextGenerationService(
        this IKernelMemoryBuilder builder,
        ITextGenerationService service,
        SemanticKernelConfig config,
        ITextTokenizer tokenizer)
    {
        service = service ?? throw new ConfigurationException("The semantic kernel text generation service instance is NULL");
        tokenizer = tokenizer ?? throw new ConfigurationException("The text tokenizer instance is NULL");

        return builder.AddSingleton<ITextGenerator>(new SemanticKernelTextGenerator(service, config, tokenizer));
    }

    /// <summary>
    ///Inject an implementation of<see cref="ITextEmbeddingGeneration">SK text embedding generation</see>
    /// for local dependencies on <see cref="ITextEmbeddingGenerator"/>
    /// </summary>
    /// <param name="builder">KM builder</param>
    /// <param name="service">SK text embedding generation instance</param>
    /// <param name="config">SK text embedding generator settings</param>
    /// <param name="tokenizer">Tokenizer used to count tokens sent to the embedding generator</param>
    /// <param name="onlyForRetrieval">Whether to use this embedding generator only during data ingestion, and not for retrieval (search and ask API)</param>
    /// <returns>KM builder</returns>
    public static IKernelMemoryBuilder WithSemanticKernelTextEmbeddingGeneration(
        this IKernelMemoryBuilder builder,
        ITextEmbeddingGeneration service,
        SemanticKernelConfig config,
        ITextTokenizer tokenizer,
        bool onlyForRetrieval = false)
    {
        service = service ?? throw new ConfigurationException("The semantic kernel text embedding generation service instance is NULL");
        tokenizer = tokenizer ?? throw new ConfigurationException("The text tokenizer instance is NULL");

        var generator = new SemanticKernelTextEmbeddingGenerator(service, config, tokenizer);

        builder.AddSingleton<ITextEmbeddingGenerator>(generator);

        if (!onlyForRetrieval)
        {
            builder.AddIngestionEmbeddingGenerator(generator);
        }

        return builder;
    }
}