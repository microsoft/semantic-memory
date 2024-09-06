// Copyright (c) Microsoft. All rights reserved.

using Microsoft.KernelMemory;
using Microsoft.KM.TestHelpers;
using Xunit.Abstractions;

namespace Microsoft.Redis.FunctionalTests;

public class RedisSpecificTests : BaseFunctionalTestCase
{
    private readonly MemoryServerless _memory;

    public RedisSpecificTests(IConfiguration cfg, ITestOutputHelper output) : base(cfg, output)
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
                .WithSearchClientConfig(new SearchClientConfig { EmptyAnswer = NotFound })
                .WithAzureOpenAITextGeneration(this.AzureOpenAITextConfiguration)
                .WithAzureOpenAITextEmbeddingGeneration(this.AzureOpenAIEmbeddingConfiguration)
                .WithRedisMemoryDb(this.RedisConfig)
                .Build<MemoryServerless>();
        }
        else
        {
            //use standard openai
            Assert.False(string.IsNullOrEmpty(this.OpenAiConfig.APIKey));

            this._memory = new KernelMemoryBuilder()
                .WithSearchClientConfig(new SearchClientConfig { EmptyAnswer = NotFound })
                .WithOpenAI(this.OpenAiConfig)
                .WithRedisMemoryDb(this.RedisConfig)
                .Build<MemoryServerless>();
        }
    }

    [Fact]
    [Trait("Category", "Redis")]
    public async Task ItThrowsWhenAttemptingToInsertUnIndexedTag()
    {
        var res = await Assert.ThrowsAsync<ArgumentException>(() => this._memory.ImportTextAsync(
            "Text that may never be used because we have an invalid tag in here",
            documentId: "1",
            tags: new TagCollection { { "non-configuredTag", "foobarbaz" } }));

        Assert.Equal(
            "Attempt to insert un-indexed tag field: 'non-configuredTag', will not be able to filter on it, please adjust the tag settings in your Redis Configuration",
            res.Message);
    }

    [Fact]
    [Trait("Category", "Redis")]
    public async Task ItThrowsWhenAttemptingToInsertTagWithSeparator()
    {
        var res = await Assert.ThrowsAsync<ArgumentException>(() => this._memory.ImportTextAsync(
            "Text that may never be used because we have an invalid tag in here",
            documentId: "1",
            tags: new TagCollection { { "user", "foo|bar|baz" } }));
        Assert.Equal(
            $"Attempted to insert record with tag field: 'user' containing the separator: '|'. Update your {nameof(KernelMemory.RedisConfig)} to use a different separator, or remove the separator from the field.",
            res.Message);
    }

    [Fact]
    [Trait("Category", "Redis")]
    public async Task ResultContainsScore()
    {
        // Arrange
        const string Q = "in one or two words, what colors should I choose?";
        await this._memory.ImportTextAsync("green is a great color", documentId: "1", tags: new TagCollection { { "user", "hulk" } });
        await this._memory.ImportTextAsync("red is a great color", documentId: "2", tags: new TagCollection { { "user", "flash" } });

        // Act + Assert - See only memory about Green color
        var answer = await this._memory.AskAsync(Q, filter: MemoryFilters.ByDocument("1"));
        Assert.Contains("green", answer.Result, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("red", answer.Result, StringComparison.OrdinalIgnoreCase);

        Assert.True(answer.RelevantSources.Any(c => c.Partitions.Any(p => p.Relevance > 0)));

        var searchResult = await this._memory.SearchAsync(Q);
        Assert.True(searchResult.Results.Any(c => c.Partitions.Any(p => p.Relevance > 0)));
    }
}
