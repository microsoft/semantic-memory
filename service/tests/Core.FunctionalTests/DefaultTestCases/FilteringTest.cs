// Copyright (c) Microsoft. All rights reserved.

using Microsoft.KernelMemory;

namespace Microsoft.KM.Core.FunctionalTests.DefaultTestCases;

public static class FilteringTest
{
    private const string NotFound = "INFO NOT FOUND";

    public static async Task ItSupportsASingleFilter(IKernelMemory memory, Action<string> log)
    {
        string indexName = Guid.NewGuid().ToString("D");
        const string Id = "ItSupportsASingleFilter-file1-NASA-news.pdf";
        const string Found = "spacecraft";

        log("Uploading document");
        await memory.ImportDocumentAsync(
            new Document(Id)
                .AddFile("file1-NASA-news.pdf")
                .AddTag("type", "news")
                .AddTag("user", "admin")
                .AddTag("user", "owner"),
            index: indexName,
            steps: Constants.PipelineWithoutSummary);

        while (!await memory.IsDocumentReadyAsync(documentId: Id, index: indexName))
        {
            log("Waiting for memory ingestion to complete...");
            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        // Simple filter: unknown user cannot see the memory
        var answer = await memory.AskAsync("What is Orion?", filter: MemoryFilters.ByTag("user", "someone"), index: indexName);
        log(answer.Result);
        Assert.Contains(NotFound, answer.Result, StringComparison.OrdinalIgnoreCase);

        // Simple filter: test AND logic: valid type + invalid user
        answer = await memory.AskAsync("What is Orion?", filter: MemoryFilters.ByTag("type", "news").ByTag("user", "someone"), index: indexName);
        log(answer.Result);
        Assert.Contains(NotFound, answer.Result, StringComparison.OrdinalIgnoreCase);

        // Simple filter: test AND logic: invalid type + valid user
        answer = await memory.AskAsync("What is Orion?", filter: MemoryFilters.ByTag("type", "fact").ByTag("user", "owner"), index: indexName);
        log(answer.Result);
        Assert.Contains(NotFound, answer.Result, StringComparison.OrdinalIgnoreCase);

        // Simple filter: known user can see the memory
        answer = await memory.AskAsync("What is Orion?", filter: MemoryFilters.ByTag("user", "admin"), index: indexName);
        log(answer.Result);
        Assert.Contains(Found, answer.Result, StringComparison.OrdinalIgnoreCase);

        // Simple filter: known user can see the memory
        answer = await memory.AskAsync("What is Orion?", filter: MemoryFilters.ByTag("user", "owner"), index: indexName);
        log(answer.Result);
        Assert.Contains(Found, answer.Result, StringComparison.OrdinalIgnoreCase);

        // Simple filter: NOT the news.
        answer = await memory.AskAsync("What is Orion?", filter: MemoryFilters.ByNotTag("type", "news"), index: indexName);
        log(answer.Result);
        Assert.Contains(NotFound, answer.Result, StringComparison.OrdinalIgnoreCase);

        // Simple filter: the memory is of the user but we do not want to use memory of that user.
        answer = await memory.AskAsync("What is Orion?", filter: MemoryFilters.ByNotTag("user", "owner"), index: indexName);
        log(answer.Result);
        Assert.Contains(NotFound, answer.Result, StringComparison.OrdinalIgnoreCase);

        // not equality on a field where we have two names
        answer = await memory.AskAsync("What is Orion?", filter: MemoryFilters.ByTag("user", "owner").ByNotTag("type", "news"), index: indexName);
        log(answer.Result);
        Assert.Contains(NotFound, answer.Result, StringComparison.OrdinalIgnoreCase);

        // Simple filter: test AND logic with correct values
        answer = await memory.AskAsync("What is Orion?", filter: MemoryFilters.ByTag("type", "news").ByTag("user", "owner"), index: indexName);
        log(answer.Result);
        Assert.Contains(Found, answer.Result, StringComparison.OrdinalIgnoreCase);

        log("Deleting memories extracted from the document");
        await memory.DeleteDocumentAsync(Id, index: indexName);

        log("Deleting index");
        await memory.DeleteIndexAsync(indexName);
    }

    public static async Task ItSupportsMultipleFilters(IKernelMemory memory, Action<string> log)
    {
        string indexName = Guid.NewGuid().ToString("D");
        Console.WriteLine($"Index name: {indexName}");

        const string Id = "ItSupportsMultipleFilters-file1-NASA-news.pdf";
        const string Found = "spacecraft";

        log("Uploading document");
        await memory.ImportDocumentAsync(
            new Document(Id)
                .AddFile("file1-NASA-news.pdf")
                .AddTag("type", "news")
                .AddTag("user", "admin")
                .AddTag("user", "owner"),
            index: indexName,
            steps: Constants.PipelineWithoutSummary);

        while (!await memory.IsDocumentReadyAsync(documentId: Id, index: indexName))
        {
            log("Waiting for memory ingestion to complete...");
            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        // Multiple filters: unknown users cannot see the memory
        var answer = await memory.AskAsync("What is Orion?", filters: new List<MemoryFilter>
        {
            MemoryFilters.ByTag("user", "someone1"),
            MemoryFilters.ByTag("user", "someone2"),
        }, index: indexName);
        log(answer.Result);
        Assert.Contains(NotFound, answer.Result, StringComparison.OrdinalIgnoreCase);

        // Multiple filters: unknown users can se the memory if it is not a type secured
        answer = await memory.AskAsync("What is Orion?", filters: new List<MemoryFilter>
        {
            MemoryFilters.ByTag("user", "someone1"),
            MemoryFilters.ByTag("user", "someone2"),
            MemoryFilters.ByNotTag("type", "securenews"),
        }, index: indexName);
        log(answer.Result);
        Assert.Contains(Found, answer.Result, StringComparison.OrdinalIgnoreCase);

        //Multiple filters: exclude two user (OR not equality on the same filter).
        answer = await memory.AskAsync("What is Orion?", filters: new List<MemoryFilter>
        {
            MemoryFilters.ByNotTag("user", "someone1"),
            MemoryFilters.ByNotTag("user", "someone2")
        }, index: indexName);
        log(answer.Result);
        Assert.Contains(Found, answer.Result, StringComparison.OrdinalIgnoreCase);

        // Multiple filters: unknown users cannot see the memory even if the type is correct (testing AND logic)
        answer = await memory.AskAsync("What is Orion?", filters: new List<MemoryFilter>
        {
            MemoryFilters.ByTag("user", "someone1").ByTag("type", "news"),
            MemoryFilters.ByTag("user", "someone2").ByTag("type", "news"),
        }, index: indexName);
        log(answer.Result);
        Assert.Contains(NotFound, answer.Result, StringComparison.OrdinalIgnoreCase);

        // Multiple filters: AND + OR logic works
        answer = await memory.AskAsync("What is Orion?", filters: new List<MemoryFilter>
        {
            MemoryFilters.ByTag("user", "someone1").ByTag("type", "news"),
            MemoryFilters.ByTag("user", "admin").ByTag("type", "fact"),
        }, index: indexName);
        log(answer.Result);
        Assert.Contains(NotFound, answer.Result, StringComparison.OrdinalIgnoreCase);

        // Multiple filters: OR logic works
        answer = await memory.AskAsync("What is Orion?", filters: new List<MemoryFilter>
        {
            MemoryFilters.ByTag("user", "someone1"),
            MemoryFilters.ByTag("user", "admin"),
        }, index: indexName);
        log(answer.Result);
        Assert.Contains(Found, answer.Result, StringComparison.OrdinalIgnoreCase);

        // Multiple filters: OR logic works
        answer = await memory.AskAsync("What is Orion?", filters: new List<MemoryFilter>
        {
            MemoryFilters.ByTag("user", "someone1").ByTag("type", "news"),
            MemoryFilters.ByTag("user", "admin").ByTag("type", "news"),
        }, index: indexName);
        log(answer.Result);
        Assert.Contains(Found, answer.Result, StringComparison.OrdinalIgnoreCase);

        log("Deleting memories extracted from the document");
        await memory.DeleteDocumentAsync(Id, index: indexName);

        log("Deleting index");
        await memory.DeleteIndexAsync(indexName);
    }

    public static async Task ItIgnoresEmptyFilters(IKernelMemory memory, Action<string> log, bool withRetries = false)
    {
        // Arrange
        string indexName = Guid.NewGuid().ToString("D");
        const string Id = "ItIgnoresEmptyFilters-file1-NASA-news.pdf";
        const string Found = "spacecraft";

        log("Uploading document");
        await memory.ImportDocumentAsync(
            new Document(Id)
                .AddFile("file1-NASA-news.pdf")
                .AddTag("type", "news")
                .AddTag("user", "admin")
                .AddTag("user", "owner"),
            index: indexName,
            steps: Constants.PipelineWithoutSummary);

        while (!await memory.IsDocumentReadyAsync(documentId: Id, index: indexName))
        {
            log("Waiting for memory ingestion to complete...");
            await Task.Delay(TimeSpan.FromSeconds(2));
        }

        const string Question = "What is Orion?";

        // Act
        // Simple filter: empty filters have no impact
        var answer = await memory.AskAsync(Question, filter: new(), index: indexName);

        // Retry, e.g. let the index populate
        if (withRetries)
        {
            for (int i = 0; i < 4; i++)
            {
                if (answer.Result.Contains(Found, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                await Task.Delay(TimeSpan.FromSeconds(2));
                answer = await memory.AskAsync(Question, filter: new(), index: indexName);
            }
        }

        // Assert
        log(answer.Result);
        Assert.Contains(Found, answer.Result, StringComparison.OrdinalIgnoreCase);

        // Act
        // Multiple filters: empty filters have no impact
        answer = await memory.AskAsync(Question,
            filters: new List<MemoryFilter> { new() }, index: indexName);
        log(answer.Result);

        // Assert
        Assert.Contains(Found, answer.Result, StringComparison.OrdinalIgnoreCase);

        // Clean up
        log("Deleting memories extracted from the document");
        await memory.DeleteDocumentAsync(Id, index: indexName);

        log("Deleting index");
        await memory.DeleteIndexAsync(indexName);
    }
}
