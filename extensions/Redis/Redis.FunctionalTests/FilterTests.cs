// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Extensions.Configuration;
using Microsoft.KernelMemory;
using Microsoft.KernelMemory.ContentStorage.DevTools;
using Microsoft.KernelMemory.FileSystem.DevTools;
using Xunit.Abstractions;

namespace Redis.FunctionalTests;

public class FilterTests : BaseTestCase
{
    public FilterTests(IConfiguration cfg, ITestOutputHelper output) : base(cfg, output)
    {
    }

    [Fact]
    [Trait("Category", "RedisFunctionalTest")]
    public async Task ItFiltersSourcesCorrectly()
    {
        // Arrange
        var memory = new KernelMemoryBuilder()
            .WithRedisMemoryDb(this.RedisConfiguration)
            .WithSimpleFileStorage(new SimpleFileStorageConfig { StorageType = FileSystemTypes.Volatile, Directory = "_files" })
            .WithOpenAITextGeneration(this.OpenAIConfiguration)
            .WithOpenAITextEmbeddingGeneration(this.OpenAIConfiguration)
            .Build();

        const string Q = "in one or two words, what colors should I choose?";
        await memory.ImportTextAsync("green is a great color", documentId: "1", tags: new TagCollection { { "user", "hulk" } });
        await memory.ImportTextAsync("red is a great color", documentId: "2", tags: new TagCollection { { "user", "flash" } });

        // Act + Assert - See only memory about Green color
        var answer = await memory.AskAsync(Q, filter: MemoryFilters.ByDocument("1"));
        Assert.Contains("green", answer.Result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("red", answer.Result, StringComparison.OrdinalIgnoreCase);

        answer = await memory.AskAsync(Q, filter: MemoryFilters.ByDocument("1").ByTag("user", "hulk"));
        Assert.Contains("green", answer.Result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("red", answer.Result, StringComparison.OrdinalIgnoreCase);

        answer = await memory.AskAsync(Q, filter: MemoryFilters.ByTag("user", "hulk"));
        Assert.Contains("green", answer.Result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("red", answer.Result, StringComparison.OrdinalIgnoreCase);

        answer = await memory.AskAsync(Q, filters: new[] { MemoryFilters.ByTag("user", "x"), MemoryFilters.ByTag("user", "hulk") });
        Assert.Contains("green", answer.Result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("red", answer.Result, StringComparison.OrdinalIgnoreCase);

        // Act + Assert - See only memory about Red color
        answer = await memory.AskAsync(Q, filter: MemoryFilters.ByDocument("2"));
        Assert.Contains("red", answer.Result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("green", answer.Result, StringComparison.OrdinalIgnoreCase);

        answer = await memory.AskAsync(Q, filter: MemoryFilters.ByDocument("2").ByTag("user", "flash"));
        Assert.Contains("red", answer.Result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("green", answer.Result, StringComparison.OrdinalIgnoreCase);

        answer = await memory.AskAsync(Q, filter: MemoryFilters.ByTag("user", "flash"));
        Assert.Contains("red", answer.Result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("green", answer.Result, StringComparison.OrdinalIgnoreCase);

        answer = await memory.AskAsync(Q, filters: new[] { MemoryFilters.ByTag("user", "x"), MemoryFilters.ByTag("user", "flash") });
        Assert.Contains("red", answer.Result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("green", answer.Result, StringComparison.OrdinalIgnoreCase);

        // Act + Assert - See both memories
        answer = await memory.AskAsync(Q, filters: new[] { MemoryFilters.ByTag("user", "hulk"), MemoryFilters.ByTag("user", "flash") });
        Assert.Contains("green", answer.Result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("red", answer.Result, StringComparison.OrdinalIgnoreCase);

        // Act + Assert - See no memories about colors
        answer = await memory.AskAsync(Q, filter: MemoryFilters.ByDocument("1").ByTag("user", "flash"));
        Assert.DoesNotContain("red", answer.Result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("green", answer.Result, StringComparison.OrdinalIgnoreCase);

        answer = await memory.AskAsync(Q, filter: MemoryFilters.ByDocument("2").ByTag("user", "hulk"));
        Assert.DoesNotContain("red", answer.Result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("green", answer.Result, StringComparison.OrdinalIgnoreCase);

        answer = await memory.AskAsync(Q, filter: MemoryFilters.ByTag("user", "x"));
        Assert.DoesNotContain("red", answer.Result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("green", answer.Result, StringComparison.OrdinalIgnoreCase);
    }
}
