﻿// Copyright (c) Microsoft. All rights reserved.

using Microsoft.KernelMemory;
using Microsoft.KernelMemory.AI;
using Microsoft.KernelMemory.AI.OpenAI;
using Microsoft.KM.TestHelpers;
using Xunit.Abstractions;

namespace Microsoft.KM.Core.FunctionalTests.ServerLess.AIClients;

public sealed class OpenAITextGeneratorTest : BaseFunctionalTestCase
{
    private readonly OpenAIConfig _config;

    public OpenAITextGeneratorTest(IConfiguration cfg, ITestOutputHelper output) : base(cfg, output)
    {
        this._config = this.OpenAiConfig;
    }

    [Fact]
    [Trait("Category", "Serverless")]
    public async Task ItStreamsFromChatModel()
    {
        // Arrange
        this._config.TextModel = "gpt-4";
        var client = new OpenAITextGenerator(this._config);

        // Act
        IAsyncEnumerable<GeneratedTextContent> textContents = client.GenerateTextAsync(
            "write 100 words about the Earth", new TextGenerationOptions());

        // Assert
        var count = 0;
        await foreach (var textContent in textContents)
        {
            Console.Write(textContent.Text);
            if (count++ > 10) { break; }
        }

        Assert.True(count > 10);
    }

    [Fact(Skip = "Dropped support for old OpenAI text completion models")]
    [Trait("Category", "Serverless")]
    public async Task ItStreamsFromTextModel()
    {
        // Arrange
        this._config.TextModel = "gpt-3.5-turbo-instruct";
        var client = new OpenAITextGenerator(this._config);

        // Act
        IAsyncEnumerable<GeneratedTextContent> textContents = client.GenerateTextAsync(
            "write 100 words about the Earth", new TextGenerationOptions());

        // Assert
        var count = 0;
        await foreach (var textContent in textContents)
        {
            Console.Write(textContent.Text);
            if (count++ > 10) { break; }
        }

        Assert.True(count > 10);
    }
}
