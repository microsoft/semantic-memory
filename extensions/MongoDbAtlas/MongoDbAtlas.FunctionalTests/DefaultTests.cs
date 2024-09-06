// Copyright (c) Microsoft. All rights reserved.

using Microsoft.KernelMemory;
using Microsoft.KernelMemory.MongoDbAtlas;
using Microsoft.KM.Core.FunctionalTests.DefaultTestCases;
using Microsoft.KM.TestHelpers;

namespace Microsoft.MongoDbAtlas.FunctionalTests;

public class DefaultTestsSingleCollection : DefaultTests
{
    public DefaultTestsSingleCollection(IConfiguration cfg, ITestOutputHelper output)
        : base(cfg, output, multiCollection: false)
    {
    }
}

public class DefaultTestsMultipleCollections : DefaultTests
{
    public DefaultTestsMultipleCollections(IConfiguration cfg, ITestOutputHelper output)
        : base(cfg, output, multiCollection: true)
    {
    }
}

public abstract class DefaultTests : BaseFunctionalTestCase
{
    private readonly MemoryServerless _memory;

    protected DefaultTests(IConfiguration cfg, ITestOutputHelper output, bool multiCollection) : base(cfg, output)
    {
        if (multiCollection)
        {
            // this._config = this.MongoDbAtlasConfig;
            this.MongoDbAtlasConfig
                .WithSingleCollectionForVectorSearch(false)
                // Need to wait for atlas to grab the data from the collection and index.
                .WithAfterIndexCallback(async () => await Task.Delay(2000));

            this.MongoDbAtlasConfig.DatabaseName += "multicoll";
        }
        else
        {
            this.MongoDbAtlasConfig
                .WithSingleCollectionForVectorSearch(true)
                //Need to wait for atlas to grab the data from the collection and index.
                .WithAfterIndexCallback(async () => await Task.Delay(2000));
        }

        // Clear all content in any collection before running the test.
        var ash = new MongoDbAtlasSearchHelper(this.MongoDbAtlasConfig.ConnectionString, this.MongoDbAtlasConfig.DatabaseName);
        if (this.MongoDbAtlasConfig.UseSingleCollectionForVectorSearch)
        {
            //delete everything for every collection
            ash.DropAllDocumentsFromCollectionsAsync().Wait();
        }
        else
        {
            //drop the entire db to be sure we can start with a clean test.
            ash.DropDatabaseAsync().Wait();
        }

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
                .With(new KernelMemoryConfig { DefaultIndexName = "default4tests" })
                .WithSearchClientConfig(new SearchClientConfig { EmptyAnswer = NotFound })
                .WithAzureOpenAITextGeneration(this.AzureOpenAITextConfiguration)
                .WithAzureOpenAITextEmbeddingGeneration(this.AzureOpenAIEmbeddingConfiguration)
                .WithMongoDbAtlasMemoryDb(this.MongoDbAtlasConfig)
                .Build<MemoryServerless>();
        }
        else
        {
            Assert.False(string.IsNullOrEmpty(this.OpenAiConfig.APIKey));

            this._memory = new KernelMemoryBuilder()
                .With(new KernelMemoryConfig { DefaultIndexName = "default4tests" })
                .WithSearchClientConfig(new SearchClientConfig { EmptyAnswer = NotFound })
                .WithOpenAI(this.OpenAiConfig)
                .WithMongoDbAtlasMemoryDb(this.MongoDbAtlasConfig)
                .Build<MemoryServerless>();
        }
    }

    [Fact]
    [Trait("Category", "MongoDbAtlas")]
    public async Task ItSupportsASingleFilter()
    {
        await FilteringTest.ItSupportsASingleFilter(this._memory, this.Log);
    }

    [Fact]
    [Trait("Category", "MongoDbAtlas")]
    public async Task ItSupportsMultipleFilters()
    {
        await FilteringTest.ItSupportsMultipleFilters(this._memory, this.Log);
    }

    [Fact]
    [Trait("Category", "MongoDbAtlas")]
    public async Task ItIgnoresEmptyFilters()
    {
        await FilteringTest.ItIgnoresEmptyFilters(this._memory, this.Log, true);
    }

    [Fact]
    [Trait("Category", "MongoDbAtlas")]
    public async Task ItListsIndexes()
    {
        await IndexListTest.ItListsIndexes(this._memory, this.Log);
    }

    [Fact]
    [Trait("Category", "MongoDbAtlas")]
    public async Task ItNormalizesIndexNames()
    {
        await IndexListTest.ItNormalizesIndexNames(this._memory, this.Log);
    }

    [Fact]
    [Trait("Category", "MongoDbAtlas")]
    public async Task ItUsesDefaultIndexName()
    {
        await IndexListTest.ItUsesDefaultIndexName(this._memory, this.Log, "default4tests");
    }

    [Fact]
    [Trait("Category", "MongoDbAtlas")]
    public async Task ItDeletesIndexes()
    {
        await IndexDeletionTest.ItDeletesIndexes(this._memory, this.Log);
    }

    [Fact]
    [Trait("Category", "MongoDbAtlas")]
    public async Task ItHandlesMissingIndexesConsistently()
    {
        await MissingIndexTest.ItHandlesMissingIndexesConsistently(this._memory, this.Log);
    }

    [Fact]
    [Trait("Category", "MongoDbAtlas")]
    public async Task ItUploadsPDFDocsAndDeletes()
    {
        await DocumentUploadTest.ItUploadsPDFDocsAndDeletes(this._memory, this.Log);
    }

    [Fact]
    [Trait("Category", "MongoDbAtlas")]
    public async Task ItSupportsTags()
    {
        await DocumentUploadTest.ItSupportsTags(this._memory, this.Log);
    }

    [Fact]
    [Trait("Category", "MongoDbAtlas")]
    public async Task ItDownloadsPDFDocs()
    {
        await DocumentUploadTest.ItDownloadsPDFDocs(this._memory, this.Log);
    }
}
