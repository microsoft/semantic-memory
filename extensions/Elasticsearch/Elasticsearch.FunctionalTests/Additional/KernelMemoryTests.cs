// Copyright (c) Microsoft. All rights reserved.

using System.Diagnostics.CodeAnalysis;
using Microsoft.KernelMemory;
using Microsoft.KernelMemory.MemoryDb.Elasticsearch.Internals;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Elasticsearch.FunctionalTests.Additional;

public class KernelMemoryTests : MemoryDbFunctionalTest
{
    private const string NoAnswer = "INFO NOT FOUND";

    public KernelMemoryTests(IConfiguration cfg, ITestOutputHelper output)
        : base(cfg, output)
    {
        this.KernelMemory = new KernelMemoryBuilder()
            .With(new KernelMemoryConfig { DefaultIndexName = "default4tests" })
            .WithSearchClientConfig(new SearchClientConfig { EmptyAnswer = NotFound })
            .WithOpenAI(this.OpenAiConfig)
            .WithElasticsearchMemoryDb(this.ElasticsearchConfig)
            .Build<MemoryServerless>();
    }

    public IKernelMemory KernelMemory { get; }

    [Fact]
    [Trait("Category", "Elasticsearch")]
    public async Task ItSupportsLimitsAndMinRelevanceScoreAsync()
    {
        string indexName = nameof(this.ItSupportsLimitsAndMinRelevanceScoreAsync);
        this.Output.WriteLine($"Index name: {indexName}");

        const string Id = "ItSupportsLimitsAndMinRelevance-file7-Silicon-Carbon.txt";

        this.Output.WriteLine("Uploading document");
        await this.KernelMemory.ImportDocumentAsync(
            new Document(Id)
                .AddFile(TestsHelper.SKWikipediaSiliconFileName),
            index: indexName,
            steps: Constants.PipelineWithoutSummary);

        while (!await this.KernelMemory.IsDocumentReadyAsync(documentId: Id, index: indexName))
        {
            this.Output.WriteLine("Waiting for memory ingestion to complete...");
            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        //no minRelevance or limit:
        var results = await this.KernelMemory.SearchAsync("What is Silicon?", index: indexName, null, null, minRelevance: 0, limit: -1);

        Assert.True((results.Results.Count > 0));
        var partitions = results.Results[0].Partitions;
        var maxRelevance = partitions.Max(p => p.Relevance);
        var minRelevance = partitions.Min(p => p.Relevance);

        this.Output.WriteLine($"{partitions.Count}, Max Relevance: {maxRelevance}, Min Relevance: {minRelevance}");
        Assert.True((partitions.Count > 5));
        Assert.True((minRelevance < 0.82));

        //test limit 5:
        results = await this.KernelMemory.SearchAsync("What is Silicon?", index: indexName, null, null, minRelevance: 0, limit: 5);
        partitions = results.Results[0].Partitions;
        Assert.True((partitions.Count == 5));

        //test min relevance 0.82:
        results = await this.KernelMemory.SearchAsync("What is Silicon?", index: indexName, null, null, minRelevance: 0.82, limit: 10);
        partitions = results.Results[0].Partitions;
        minRelevance = partitions.Min(p => p.Relevance);
        Assert.True((minRelevance >= 0.82));

        await this.KernelMemory.DeleteDocumentAsync(Id, index: indexName);

        this.Output.WriteLine("Deleting index");
        await this.KernelMemory.DeleteIndexAsync(indexName);
    }

    [Fact]
    [Trait("Category", "Elasticsearch")]
    [SuppressMessage("Reliability", "CA2007:Consider calling ConfigureAwait on the awaited task", Justification = "<Pending>")]
    public async Task ItSupportsMultipleFiltersAsync()
    {
        // This is an adaptation of the same test in Elasticsearch.FunctionalTests

        string indexName = nameof(this.ItSupportsMultipleFiltersAsync);
        this.Output.WriteLine($"Index name: {indexName}");

        const string Id = "ItSupportsMultipleFilters-file1-NASA-news.pdf";
        const string Found = "spacecraft";

        this.Output.WriteLine("Uploading document");
        await this.KernelMemory.ImportDocumentAsync(
            new Document(Id)
                .AddFile(TestsHelper.NASANewsFileName)
                .AddTag("type", "news")
                .AddTag("user", "admin")
                .AddTag("user", "owner"),
            index: indexName,
            steps: Constants.PipelineWithoutSummary);

        while (!await this.KernelMemory.IsDocumentReadyAsync(documentId: Id, index: indexName))
        {
            this.Output.WriteLine("Waiting for memory ingestion to complete...");
            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        // Multiple filters: unknown users cannot see the memory
        var answer = await this.KernelMemory.AskAsync("What is Orion?", filters:
        [
            MemoryFilters.ByTag("user", "someone1"),
            MemoryFilters.ByTag("user", "someone2"),
        ], index: indexName);
        this.Output.WriteLine(answer.Result);
        Assert.Contains(NotFound, answer.Result, StringComparison.OrdinalIgnoreCase);

        // Multiple filters: unknown users cannot see the memory even if the type is correct (testing AND logic)
        answer = await this.KernelMemory.AskAsync("What is Orion?", filters:
        [
            MemoryFilters.ByTag("user", "someone1").ByTag("type", "news"),
            MemoryFilters.ByTag("user", "someone2").ByTag("type", "news"),
        ], index: indexName);
        this.Output.WriteLine(answer.Result);
        Assert.Contains(NotFound, answer.Result, StringComparison.OrdinalIgnoreCase);

        // Multiple filters: AND + OR logic works
        answer = await this.KernelMemory.AskAsync("What is Orion?", filters:
        [
            MemoryFilters.ByTag("user", "someone1").ByTag("type", "news"),
            MemoryFilters.ByTag("user", "admin").ByTag("type", "fact"),
        ], index: indexName);
        this.Output.WriteLine(answer.Result);
        Assert.Contains(NotFound, answer.Result, StringComparison.OrdinalIgnoreCase);

        // Multiple filters: OR logic works
        answer = await this.KernelMemory.AskAsync("What is Orion?", filters:
        [
            MemoryFilters.ByTag("user", "someone1"),
            MemoryFilters.ByTag("user", "admin"),
        ], index: indexName);
        this.Output.WriteLine(answer.Result);
        Assert.Contains(Found, answer.Result, StringComparison.OrdinalIgnoreCase);

        // Multiple filters: OR logic works
        answer = await this.KernelMemory.AskAsync("What is Orion?", filters:
        [
            MemoryFilters.ByTag("user", "someone1").ByTag("type", "news"),
            MemoryFilters.ByTag("user", "admin").ByTag("type", "news"),
        ], index: indexName);
        this.Output.WriteLine(answer.Result);
        Assert.Contains(Found, answer.Result, StringComparison.OrdinalIgnoreCase);

        await this.KernelMemory.DeleteDocumentAsync(Id, index: indexName);

        this.Output.WriteLine("Deleting index");
        await this.KernelMemory.DeleteIndexAsync(indexName);
    }

    [Fact]
    [Trait("Category", "Elasticsearch")]
    public async Task ItSupportsTagsAsync()
    {
        // This is an adaptation of the same test in Elasticsearch.FunctionalTests

        // Arrange
        const string Id = "ItSupportTags-file1-NASA-news.pdf";
        await this.KernelMemory.ImportDocumentAsync(
            TestsHelper.NASANewsFileName,
            documentId: Id,
            tags: new TagCollection
            {
                { "type", "news" },
                { "type", "test" },
                { "ext", "pdf" }
            },
            steps: Constants.PipelineWithoutSummary).ConfigureAwait(false);

        while (!await this.KernelMemory.IsDocumentReadyAsync(documentId: Id).ConfigureAwait(false))
        {
            this.Output.WriteLine("Waiting for memory ingestion to complete...");
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }

        // Act
        var defaultRetries = 0; // withRetries ? 4 : 0;

        var retries = defaultRetries;
        var answer1 = await this.KernelMemory.AskAsync("What is Orion?", filter: MemoryFilters.ByTag("type", "news")).ConfigureAwait(false);
        this.Output.WriteLine("answer1: " + answer1.Result);
        while (retries-- > 0 && !answer1.Result.Contains("spacecraft", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            answer1 = await this.KernelMemory.AskAsync("What is Orion?", filter: MemoryFilters.ByTag("type", "news")).ConfigureAwait(false);
            this.Output.WriteLine("answer1: " + answer1.Result);
        }

        retries = defaultRetries;
        var answer2 = await this.KernelMemory.AskAsync("What is Orion?", filter: MemoryFilters.ByTag("type", "test")).ConfigureAwait(false);
        this.Output.WriteLine("answer2: " + answer2.Result);
        while (retries-- > 0 && !answer2.Result.Contains("spacecraft", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            answer2 = await this.KernelMemory.AskAsync("What is Orion?", filter: MemoryFilters.ByTag("type", "test")).ConfigureAwait(false);
            this.Output.WriteLine("answer2: " + answer2.Result);
        }

        retries = defaultRetries;
        var answer3 = await this.KernelMemory.AskAsync("What is Orion?", filter: MemoryFilters.ByTag("ext", "pdf")).ConfigureAwait(false);
        this.Output.WriteLine("answer3: " + answer3.Result);
        while (retries-- > 0 && !answer3.Result.Contains("spacecraft", StringComparison.OrdinalIgnoreCase))
        {
            await Task.Delay(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            answer3 = await this.KernelMemory.AskAsync("What is Orion?", filter: MemoryFilters.ByTag("type", "test")).ConfigureAwait(false);
            this.Output.WriteLine("answer3: " + answer3.Result);
        }

        var answer4 = await this.KernelMemory.AskAsync("What is Orion?", filter: MemoryFilters.ByTag("foo", "bar")).ConfigureAwait(false);
        this.Output.WriteLine(answer4.Result);

        // Assert
        Assert.Contains("spacecraft", answer1.Result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("spacecraft", answer2.Result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("spacecraft", answer3.Result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NOT FOUND", answer4.Result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Elasticsearch")]
    public async Task ItSupportsASingleFilterAsync()
    {
        // This is an adaptation of the same test in Elasticsearch.FunctionalTests

        string indexName = nameof(this.ItSupportsASingleFilterAsync);
        const string Id = "ItSupportsASingleFilter-file1-NASA-news.pdf";
        const string Found = "spacecraft";

        this.Output.WriteLine("Uploading document");
        await this.KernelMemory.ImportDocumentAsync(
            new Document(Id)
                .AddFile(TestsHelper.NASANewsFileName)
                .AddTag("type", "news")
                .AddTag("user", "admin")
                .AddTag("user", "owner"),
            index: indexName,
            steps: Constants.PipelineWithoutSummary).ConfigureAwait(false);

        while (!await this.KernelMemory.IsDocumentReadyAsync(documentId: Id, index: indexName).ConfigureAwait(false))
        {
            this.Output.WriteLine("Waiting for memory ingestion to complete...");
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }

        //await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(false);

        MemoryAnswer answer;
        // Simple filter: unknown user cannot see the memory
        answer = await this.KernelMemory.AskAsync("What is Orion?", filter: MemoryFilters.ByTag("user", "someone"), index: indexName).ConfigureAwait(false);
        this.Output.WriteLine(answer.Result);
        Assert.Contains(NotFound, answer.Result, StringComparison.OrdinalIgnoreCase);

        // Simple filter: test AND logic: valid type + invalid user
        answer = await this.KernelMemory.AskAsync("What is Orion?", filter: MemoryFilters.ByTag("type", "news").ByTag("user", "someone"), index: indexName).ConfigureAwait(false);
        this.Output.WriteLine(answer.Result);
        Assert.Contains(NotFound, answer.Result, StringComparison.OrdinalIgnoreCase);

        // Simple filter: test AND logic: invalid type + valid user
        answer = await this.KernelMemory.AskAsync("What is Orion?", filter: MemoryFilters.ByTag("type", "fact").ByTag("user", "owner"), index: indexName).ConfigureAwait(false);
        this.Output.WriteLine(answer.Result);
        //Assert.Contains(Found, answer.Result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(NotFound, answer.Result, StringComparison.OrdinalIgnoreCase);

        // Simple filter: known user can see the memory
        answer = await this.KernelMemory.AskAsync("What is Orion?", filter: MemoryFilters.ByTag("user", "admin"), index: indexName).ConfigureAwait(false);
        this.Output.WriteLine(answer.Result);
        Assert.Contains(Found, answer.Result, StringComparison.OrdinalIgnoreCase);

        // Simple filter: known user can see the memory
        answer = await this.KernelMemory.AskAsync("What is Orion?", filter: MemoryFilters.ByTag("user", "owner"), index: indexName).ConfigureAwait(false);
        this.Output.WriteLine(answer.Result);
        Assert.Contains(Found, answer.Result, StringComparison.OrdinalIgnoreCase);

        // Simple filter: test AND logic with correct values
        answer = await this.KernelMemory.AskAsync("What is Orion?", filter: MemoryFilters.ByTag("type", "news").ByTag("user", "owner"), index: indexName).ConfigureAwait(false);
        this.Output.WriteLine(answer.Result);
        Assert.Contains(Found, answer.Result, StringComparison.OrdinalIgnoreCase);

        this.Output.WriteLine("Deleting memories extracted from the document");
        await this.KernelMemory.DeleteDocumentAsync(Id, index: indexName).ConfigureAwait(false);

        this.Output.WriteLine("Deleting index");
        await this.KernelMemory.DeleteIndexAsync(indexName).ConfigureAwait(false);
    }

    [Fact]
    [Trait("Category", "Elasticsearch")]
    public async Task CanImportOneDocumentAndAskAsync()
    {
        var indexName = nameof(this.CanImportOneDocumentAndAskAsync);

        // Imports a document into the index
        var id = await this.KernelMemory.ImportDocumentAsync(
                filePath: TestsHelper.WikipediaCarbonFileName,
                documentId: "doc001",
                tags: new TagCollection
                {
                    { "indexedOn", DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz") }
                },
                index: indexName)
            .ConfigureAwait(false);

        this.Output.WriteLine($"Indexed document with id '{id}'.");

        // Waits for the documents to be saved
        var actualIndexName = IndexNameHelper.Convert(indexName, base.ElasticsearchConfig);
        //await this.Client.WaitForDocumentsAsync(actualIndexName, expectedDocuments: 2)
        //          .ConfigureAwait(false);

        // Asks a question on the data we just inserted
        MemoryAnswer? answer = await this.TryToGetTopAnswerAsync(indexName, "What can carbon bond to?")
            .ConfigureAwait(false);
        this.PrintAnswerOfDocument(answer, "doc001");
    }

    [Fact]
    [Trait("Category", "Elasticsearch")]
    public async Task CanImportTwoDocumentsAndAskAsync()
    {
        var indexName = nameof(this.CanImportTwoDocumentsAndAskAsync);

        // Proceeds
        var docId = await this.KernelMemory.ImportDocumentAsync(
            TestsHelper.WikipediaCarbonFileName,
            index: indexName,
            documentId: "doc001").ConfigureAwait(false);

        this.Output.WriteLine($"Indexed {docId}");

        docId = await this.KernelMemory.ImportDocumentAsync(
                new Document("doc002")
                    .AddFiles([
                        TestsHelper.WikipediaMoonFilename,
                        TestsHelper.LoremIpsumFileName,
                        TestsHelper.SKReadmeFileName
                    ])
                    .AddTag("user", "Blake"),
                index: indexName)
            .ConfigureAwait(false);

        this.Output.WriteLine($"Indexed {docId}");

        docId = await this.KernelMemory.ImportDocumentAsync(new Document("doc003")
                    .AddFile(TestsHelper.NASANewsFileName)
                    .AddTag("user", "Taylor")
                    .AddTag("collection", "meetings")
                    .AddTag("collection", "NASA")
                    .AddTag("collection", "space")
                    .AddTag("type", "news"),
                index: indexName)
            .ConfigureAwait(false);

        this.Output.WriteLine($"Indexed {docId}");

        // Waits for the documents to be saved
        var actualIndexName = IndexNameHelper.Convert(indexName, this.ElasticsearchConfig);
        //await this.Client.WaitForDocumentsAsync(actualIndexName, expectedDocuments: 10)
        //                 .ConfigureAwait(false);

        // This should return a citation to doc001
        var answer = await this.KernelMemory.AskAsync("What's E = m*c^2?", indexName)
            .ConfigureAwait(false);

        this.PrintAnswerOfDocument(answer, "doc001");

        // This should return a citation to doc002
        answer = await this.KernelMemory.AskAsync("What's Semantic Kernel?", indexName)
            .ConfigureAwait(false);

        this.PrintAnswerOfDocument(answer, "doc002");
    }

    private void PrintAnswerOfDocument(MemoryAnswer? answer, string expectedDocumentId)
    {
        ArgumentNullException.ThrowIfNull(answer);

        this.Output.WriteLine($"Question: {answer.Question}");
        this.Output.WriteLine($"Answer: {answer.Result}");

        var foundDocumentReference = false;
        foreach (var citation in answer.RelevantSources)
        {
            this.Output.WriteLine($"  - {citation.SourceName}  - {citation.Link} [{citation.Partitions.First().LastUpdate:D}]");

            if (citation.DocumentId == expectedDocumentId)
            {
                foundDocumentReference = true;
            }
        }

        if (!foundDocumentReference)
        {
            throw new InvalidOperationException($"It should have found a citation to document '{expectedDocumentId}'.");
        }
    }

    private async Task<MemoryAnswer?> TryToGetTopAnswerAsync(string indexName, string question)
    {
        MemoryAnswer? answer = null;

        // We need to wait a bit for the indexing to complete, so this is why we retry a few times with a delay.
        // TODO: add Polly.
        for (int i = 0; i < 3; i++)
        {
            answer = await this.KernelMemory.AskAsync(
                    question: question,
                    index: indexName,
                    filter: null,
                    filters: null,
                    minRelevance: 0)
                .ConfigureAwait(false);

            if (answer.Result != NoAnswer)
            {
                break;
            }

            await Task.Delay(500)
                .ConfigureAwait(false);
        }

        return answer;
    }
}
