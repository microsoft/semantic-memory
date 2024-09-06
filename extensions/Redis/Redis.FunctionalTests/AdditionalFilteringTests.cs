// Copyright (c) Microsoft. All rights reserved.

using Microsoft.KernelMemory;
using Microsoft.KernelMemory.DocumentStorage.DevTools;
using Microsoft.KernelMemory.FileSystem.DevTools;
using Microsoft.KM.TestHelpers;
using Xunit.Abstractions;

namespace Microsoft.Redis.FunctionalTests;

public class AdditionalFilteringTests : BaseFunctionalTestCase
{
    private readonly IKernelMemory _memory;

    public AdditionalFilteringTests(IConfiguration cfg, ITestOutputHelper output) : base(cfg, output)
    {
        if (cfg.GetValue<bool>("UseAzureOpenAI"))
        {
            //ok in azure we can use managed identities so we need to check the configuration
            if (this.AzureOpenAITextConfiguration.Auth == AzureOpenAIConfig.AuthTypes.APIKey)
            {
                //verify that we really have an api key.
                Assert.False(string.IsNullOrEmpty(this.AzureOpenAITextConfiguration.APIKey));
            }

            if (this.AzureOpenAIEmbeddingConfiguration.Auth == AzureOpenAIConfig.AuthTypes.APIKey)
            {
                //verify that we really have an api key.
                Assert.False(string.IsNullOrEmpty(this.AzureOpenAIEmbeddingConfiguration.APIKey));
            }

            this._memory = new KernelMemoryBuilder()
                .WithRedisMemoryDb(this.RedisConfig)
                .WithSimpleFileStorage(new SimpleFileStorageConfig { StorageType = FileSystemTypes.Volatile, Directory = "_files" })
                .WithAzureOpenAITextGeneration(this.AzureOpenAITextConfiguration)
                .WithAzureOpenAITextEmbeddingGeneration(this.AzureOpenAIEmbeddingConfiguration)
                .Build();
        }
        else
        {
            //use standard openai
            Assert.False(string.IsNullOrEmpty(this.OpenAiConfig.APIKey));

            this._memory = new KernelMemoryBuilder()
                .WithRedisMemoryDb(this.RedisConfig)
                .WithSimpleFileStorage(new SimpleFileStorageConfig { StorageType = FileSystemTypes.Volatile, Directory = "_files" })
                .WithOpenAITextGeneration(this.OpenAiConfig)
                .WithOpenAITextEmbeddingGeneration(this.OpenAiConfig)
                .Build();
        }
    }

    [Fact]
    [Trait("Category", "Redis")]
    public async Task ItFiltersSourcesCorrectly()
    {
        // Arrange
        const string Q = "in one or two words, what colors should I choose?";
        await this._memory.ImportTextAsync("green is a great color", documentId: "1", tags: new TagCollection { { "user", "hulk" } });
        await this._memory.ImportTextAsync("red is a great color", documentId: "2", tags: new TagCollection { { "user", "flash" } });

        // Act + Assert - See only memory about Green color
        var answer = await this._memory.AskAsync(Q, filter: MemoryFilters.ByDocument("1"));
        Assert.Contains("green", answer.Result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("red", answer.Result, StringComparison.OrdinalIgnoreCase);

        answer = await this._memory.AskAsync(Q, filter: MemoryFilters.ByDocument("1").ByTag("user", "hulk"));
        Assert.Contains("green", answer.Result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("red", answer.Result, StringComparison.OrdinalIgnoreCase);

        answer = await this._memory.AskAsync(Q, filter: MemoryFilters.ByTag("user", "hulk"));
        Assert.Contains("green", answer.Result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("red", answer.Result, StringComparison.OrdinalIgnoreCase);

        answer = await this._memory.AskAsync(Q, filters: new[] { MemoryFilters.ByTag("user", "x"), MemoryFilters.ByTag("user", "hulk") });
        Assert.Contains("green", answer.Result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("red", answer.Result, StringComparison.OrdinalIgnoreCase);

        // Act + Assert - See only memory about Red color
        answer = await this._memory.AskAsync(Q, filter: MemoryFilters.ByDocument("2"));
        Assert.Contains("red", answer.Result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("green", answer.Result, StringComparison.OrdinalIgnoreCase);

        answer = await this._memory.AskAsync(Q, filter: MemoryFilters.ByDocument("2").ByTag("user", "flash"));
        Assert.Contains("red", answer.Result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("green", answer.Result, StringComparison.OrdinalIgnoreCase);

        answer = await this._memory.AskAsync(Q, filter: MemoryFilters.ByTag("user", "flash"));
        Assert.Contains("red", answer.Result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("green", answer.Result, StringComparison.OrdinalIgnoreCase);

        answer = await this._memory.AskAsync(Q, filters: new[] { MemoryFilters.ByTag("user", "x"), MemoryFilters.ByTag("user", "flash") });
        Assert.Contains("red", answer.Result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("green", answer.Result, StringComparison.OrdinalIgnoreCase);

        // Act + Assert - See both memories
        answer = await this._memory.AskAsync(Q, filters: new[] { MemoryFilters.ByTag("user", "hulk"), MemoryFilters.ByTag("user", "flash") });
        Assert.Contains("green", answer.Result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("red", answer.Result, StringComparison.OrdinalIgnoreCase);

        // Act + Assert - See no memories about colors
        answer = await this._memory.AskAsync(Q, filter: MemoryFilters.ByDocument("1").ByTag("user", "flash"));
        Assert.DoesNotContain("red", answer.Result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("green", answer.Result, StringComparison.OrdinalIgnoreCase);

        answer = await this._memory.AskAsync(Q, filter: MemoryFilters.ByDocument("2").ByTag("user", "hulk"));
        Assert.DoesNotContain("red", answer.Result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("green", answer.Result, StringComparison.OrdinalIgnoreCase);

        answer = await this._memory.AskAsync(Q, filter: MemoryFilters.ByTag("user", "x"));
        Assert.DoesNotContain("red", answer.Result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("green", answer.Result, StringComparison.OrdinalIgnoreCase);
    }
}
