﻿// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Core.FunctionalTests.DefaultTestCases;
using Microsoft.KernelMemory;
using Microsoft.TestHelpers;
using Xunit.Abstractions;

namespace AzureCosmosDBMongoDB.FunctionalTests;

public class DefaultTests : BaseFunctionalTestCase
{
    private readonly MemoryServerless _memory;

    public DefaultTests(IConfiguration cfg, ITestOutputHelper output) : base(cfg, output)
    {
        Assert.False(string.IsNullOrEmpty(this.AzureCosmosDBMongoDBConfig.ConnectionString));
        Assert.False(string.IsNullOrEmpty(this.AzureCosmosDBMongoDBConfig.IndexName));
        Assert.NotNull(this.AzureCosmosDBMongoDBConfig.Kind);
        Assert.NotNull(this.AzureCosmosDBMongoDBConfig.Similarity);
        Assert.False(string.IsNullOrEmpty(this.OpenAiConfig.APIKey));

        this._memory = new KernelMemoryBuilder()
            .WithSearchClientConfig(new SearchClientConfig { EmptyAnswer = NotFound })
            .WithOpenAI(this.OpenAiConfig)
            .WithAzureCosmosDBMongoDBMemoryDb(this.AzureCosmosDBMongoDBConfig)
            .Build<MemoryServerless>();
    }

    [Fact]
    [Trait("Category", "AzCosmosDBForMongoDB")]
    public async Task ItListsIndexes()
    {
        await IndexListTest.ItListsIndexes(this._memory, this.Log);
    }

    [Fact]
    [Trait("Category", "AzCosmosDBForMongoDB")]
    public async Task ItNormalizesIndexNames()
    {
        await IndexListTest.ItNormalizesIndexNames(this._memory, this.Log);
    }

    [Fact]
    [Trait("Category", "AzCosmosDBForMongoDB")]
    public async Task ItUsesDefaultIndexName()
    {
        await IndexListTest.ItUsesDefaultIndexName(this._memory, this.Log, "default4tests");
    }

    [Fact]
    [Trait("Category", "AzCosmosDBForMongoDB")]
    public async Task ItDeletesIndexes()
    {
        await IndexDeletionTest.ItDeletesIndexes(this._memory, this.Log);
    }

    [Fact]
    [Trait("Category", "AzCosmosDBForMongoDB")]
    public async Task ItHandlesMissingIndexesConsistently()
    {
        await MissingIndexTest.ItHandlesMissingIndexesConsistently(this._memory, this.Log);
    }

    [Fact]
    [Trait("Category", "AzCosmosDBForMongoDB")]
    public async Task ItUploadsPDFDocsAndDeletes()
    {
        await DocumentUploadTest.ItUploadsPDFDocsAndDeletes(this._memory, this.Log);
    }
}
