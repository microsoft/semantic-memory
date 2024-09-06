// Copyright (c) Microsoft. All rights reserved.

using Microsoft.KernelMemory;
using Microsoft.KernelMemory.MemoryDb.SQLServer;
using Microsoft.KernelMemory.MemoryStorage;
using Microsoft.KM.Core.FunctionalTests.DefaultTestCases;
using Microsoft.KM.TestHelpers;

namespace Microsoft.SQLServer.FunctionalTests;

public class DefaultTests : BaseFunctionalTestCase
{
    private readonly MemoryServerless _memory;
    private readonly IMemoryDb _memoryDb;

    public DefaultTests(IConfiguration cfg, ITestOutputHelper output) : base(cfg, output)
    {
        IKernelMemoryBuilder builder;
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

            SqlServerConfig sqlServerConfig = cfg.GetSection("KernelMemory:Services:SqlServer").Get<SqlServerConfig>()!;

            builder = new KernelMemoryBuilder()
                .With(new KernelMemoryConfig { DefaultIndexName = "default4tests" })
                .Configure(kmb => kmb.Services.AddLogging(b => { b.AddConsole().SetMinimumLevel(LogLevel.Trace); }))
                .WithSearchClientConfig(new SearchClientConfig { EmptyAnswer = NotFound })
                .WithAzureOpenAITextGeneration(this.AzureOpenAITextConfiguration)
                .WithAzureOpenAITextEmbeddingGeneration(this.AzureOpenAIEmbeddingConfiguration)
                .WithSqlServerMemoryDb(sqlServerConfig);
        }
        else
        {
            Assert.False(string.IsNullOrEmpty(this.OpenAiConfig.APIKey));

            SqlServerConfig sqlServerConfig = cfg.GetSection("KernelMemory:Services:SqlServer").Get<SqlServerConfig>()!;

            builder = new KernelMemoryBuilder()
                .With(new KernelMemoryConfig { DefaultIndexName = "default4tests" })
                .Configure(kmb => kmb.Services.AddLogging(b => { b.AddConsole().SetMinimumLevel(LogLevel.Trace); }))
                .WithSearchClientConfig(new SearchClientConfig { EmptyAnswer = NotFound })
                .WithOpenAI(this.OpenAiConfig)
                // .WithAzureOpenAITextGeneration(this.AzureOpenAITextConfiguration)
                // .WithAzureOpenAITextEmbeddingGeneration(this.AzureOpenAIEmbeddingConfiguration)
                .WithSqlServerMemoryDb(sqlServerConfig);
        }

        var serviceProvider = builder.Services.BuildServiceProvider();
        this._memory = builder.Build<MemoryServerless>();
        this._memoryDb = serviceProvider.GetService<IMemoryDb>()!;
    }

    [Fact]
    [Trait("Category", "SQLServer")]
    public async Task ItSupportsASingleFilter()
    {
        await FilteringTest.ItSupportsASingleFilter(this._memory, this.Log);
    }

    [Fact]
    [Trait("Category", "SQLServer")]
    public async Task ItSupportsMultipleFilters()
    {
        await FilteringTest.ItSupportsMultipleFilters(this._memory, this.Log);
    }

    [Fact]
    [Trait("Category", "SQLServer")]
    public async Task ItIgnoresEmptyFilters()
    {
        await FilteringTest.ItIgnoresEmptyFilters(this._memory, this.Log, true);
    }

    [Fact]
    [Trait("Category", "SQLServer")]
    public async Task ItDoesntFailIfTheIndexExistsAlready()
    {
        await IndexCreationTest.ItDoesntFailIfTheIndexExistsAlready(this._memory, this.Log);
    }

    [Fact]
    [Trait("Category", "SQLServer")]
    public async Task ItListsIndexes()
    {
        await IndexListTest.ItListsIndexes(this._memory, this.Log);
    }

    [Fact]
    [Trait("Category", "SQLServer")]
    public async Task ItNormalizesIndexNames()
    {
        await IndexListTest.ItNormalizesIndexNames(this._memory, this.Log);
    }

    [Fact]
    [Trait("Category", "SQLServer")]
    public async Task ItUsesDefaultIndexName()
    {
        await IndexListTest.ItUsesDefaultIndexName(this._memory, this.Log, "default4tests");
    }

    [Fact]
    [Trait("Category", "SQLServer")]
    public async Task ItDeletesIndexes()
    {
        await IndexDeletionTest.ItDeletesIndexes(this._memory, this.Log);
    }

    [Fact]
    [Trait("Category", "SQLServer")]
    public async Task ItHandlesMissingIndexesConsistently()
    {
        await MissingIndexTest.ItHandlesMissingIndexesConsistently(this._memory, this.Log);
    }

    [Fact]
    [Trait("Category", "SQLServer")]
    public async Task ItUploadsPDFDocsAndDeletes()
    {
        await DocumentUploadTest.ItUploadsPDFDocsAndDeletes(this._memory, this.Log);
    }

    [Fact]
    [Trait("Category", "SQLServer")]
    public async Task ItSupportsTags()
    {
        await DocumentUploadTest.ItSupportsTags(this._memory, this.Log);
    }

    [Fact]
    [Trait("Category", "SQLServer")]
    public async Task ItDeletesRecords()
    {
        await RecordDeletionTest.ItDeletesRecords(this._memory, this._memoryDb, this.Log);
    }

    [Fact]
    [Trait("Category", "SQLServer")]
    public async Task ItCanImportDocumentWithManyTagsAtATime()
    {
        const string Id = "ItCanImportDocumentWithManyTagsAtATime-file1-NASA-news.pdf";

        var tags = new TagCollection
        {
            { "type", "news" },
            { "type", "test" },
            { "ext", "pdf" }
        };

        for (int i = 0; i < 100; i++)
        {
            tags.AddSyntheticTag($"tagTest{i}");
        }

        await this._memory.ImportDocumentAsync(
            "file1-NASA-news.pdf",
            documentId: Id,
            tags: tags,
            steps: Constants.PipelineWithoutSummary);

        while (!await this._memory.IsDocumentReadyAsync(documentId: Id))
        {
            this.Log("Waiting for memory ingestion to complete...");
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }
}
