// Copyright (c) Microsoft. All rights reserved.

using Microsoft.KernelMemory;
using Microsoft.KernelMemory.AI.Ollama;
using Microsoft.KernelMemory.DocumentStorage.DevTools;
using Microsoft.KernelMemory.FileSystem.DevTools;

namespace Microsoft.Postgres.TestApplication;

internal static class Program
{
    public static async Task Main()
    {
        await Test1();
        await Test2();
        await Test3();
    }

    private static async Task Test1()
    {
        var cfg = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .AddJsonFile("appsettings.development.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();

        var postgresConfig = cfg.GetSection("KernelMemory:Services:Postgres").Get<PostgresConfig>();
        ArgumentNullExceptionEx.ThrowIfNull(postgresConfig, nameof(postgresConfig), "Postgres config not found");

        var ollamaConfig = cfg.GetSection("KernelMemory:Services:Ollama").Get<OllamaConfig>();
        ArgumentNullExceptionEx.ThrowIfNull(ollamaConfig, nameof(ollamaConfig), "Ollama config not found");

        // Concatenate our 'WithPostgresMemoryDb()' after 'WithOpenAIDefaults()' from the core nuget
        var mem1 = new KernelMemoryBuilder()
            .WithOllamaTextEmbeddingGeneration(ollamaConfig)
            .WithOllamaTextGeneration(ollamaConfig)
            .WithPostgresMemoryDb(postgresConfig)
            .WithSimpleFileStorage(SimpleFileStorageConfig.Persistent)
            .Build();

        // Concatenate our 'WithPostgresMemoryDb()' before 'WithOpenAIDefaults()' from the core nuget
        var mem2 = new KernelMemoryBuilder()
            .WithPostgresMemoryDb(postgresConfig)
            .WithSimpleFileStorage(SimpleFileStorageConfig.Persistent)
            .WithOllamaTextEmbeddingGeneration(ollamaConfig)
            .WithOllamaTextGeneration(ollamaConfig)
            .Build();

        // Concatenate our 'WithPostgresMemoryDb()' before and after KM builder extension methods from the core nuget
        var mem3 = new KernelMemoryBuilder()
            .WithSimpleFileStorage(SimpleFileStorageConfig.Persistent)
            .WithOllamaTextEmbeddingGeneration(ollamaConfig)
            .WithOllamaTextGeneration(ollamaConfig)
            .WithPostgresMemoryDb(postgresConfig)
            .Build();

        await mem1.DeleteIndexAsync("index1");
        await mem2.DeleteIndexAsync("index2");
        await mem3.DeleteIndexAsync("index3");

        var doc1 = await mem1.ImportTextAsync("this is a test 1", index: "index1");
        var doc2 = await mem2.ImportTextAsync("this is a test 2", index: "index2");
        var doc3 = await mem3.ImportTextAsync("this is a test 3", index: "index3");

        Console.WriteLine("\nInsert done. Press ENTER to list indexes...");
        Console.ReadLine();

        foreach (var s in await mem1.ListIndexesAsync())
        {
            Console.WriteLine(s.Name);
        }

        Console.WriteLine("\nList done. Press ENTER to delete records...");
        Console.ReadLine();

        await mem1.DeleteDocumentAsync(index: "index1", documentId: doc1);
        await mem2.DeleteDocumentAsync(index: "index2", documentId: doc2);
        await mem3.DeleteDocumentAsync(index: "index3", documentId: doc3);

        Console.WriteLine("\nDelete done. Press ENTER to delete indexes...");
        Console.ReadLine();

        await mem1.DeleteIndexAsync("index1");
        await mem2.DeleteIndexAsync("index2");
        await mem3.DeleteIndexAsync("index3");

        Console.WriteLine("\n=== end ===");
    }

    private static async Task Test2()
    {
        var postgresConfig = new PostgresConfig();
        var ollamaConfig = new OllamaConfig();

        new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .AddJsonFile("appsettings.development.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build()
            .BindSection("KernelMemory:Services:Postgres", postgresConfig)
            .BindSection("KernelMemory:Services:Ollama", ollamaConfig);

        var memory = new KernelMemoryBuilder()
            .WithPostgresMemoryDb(postgresConfig)
            .WithOllamaTextGeneration(ollamaConfig)
            .WithOllamaTextEmbeddingGeneration(ollamaConfig)
            .WithSimpleFileStorage(new SimpleFileStorageConfig
            {
                StorageType = FileSystemTypes.Disk,
                Directory = "_files"
            })
            .Build();

        await memory.ImportTextAsync("yellow is a color", documentId: "1");
        await memory.ImportTextAsync("the Moon orbits around Earth", documentId: "2");
        await memory.ImportTextAsync("red and yellow give a secret color I call Laurange", documentId: "3");
        await memory.ImportTextAsync("water freezes at 0C (32F) under normal atmospheric pressure", documentId: "4");

        SearchResult result = await memory.SearchAsync("about colors");
        foreach (var x in result.Results)
        {
            Console.WriteLine(x.Partitions.First().Text);
            Console.WriteLine(x.Partitions.First().Relevance);
            Console.WriteLine();
        }

        var answer = await memory.AskAsync("what color did I invent?");
        Console.WriteLine(answer.Result);

        await memory.DeleteDocumentAsync(documentId: "1");
        await memory.DeleteDocumentAsync(documentId: "2");
        await memory.DeleteDocumentAsync(documentId: "3");
        await memory.DeleteDocumentAsync(documentId: "4");
    }

    private static async Task Test3()
    {
        var postgresConfig = new PostgresConfig();
        var ollamaConfig = new OllamaConfig();

        // Note: using appsettings.custom-sql.json
        new ConfigurationBuilder()
            .AddJsonFile("appsettings.json")
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddJsonFile("appsettings.development.json", optional: true)
            .AddJsonFile("appsettings.custom-sql.json")
            .Build()
            .BindSection("KernelMemory:Services:Postgres", postgresConfig)
            .BindSection("KernelMemory:Services:Ollama", ollamaConfig);

        var memory = new KernelMemoryBuilder()
            .WithPostgresMemoryDb(postgresConfig)
            .WithOllamaTextGeneration(ollamaConfig)
            .WithOllamaTextEmbeddingGeneration(ollamaConfig)
            .WithSimpleFileStorage(new SimpleFileStorageConfig
            {
                StorageType = FileSystemTypes.Disk,
                Directory = "_files"
            })
            .Build();

        await memory.ImportTextAsync("green is a great color", documentId: "1");

        var answer = await memory.AskAsync("what color should I choose, and why?");
        Console.WriteLine(answer.Result);

        await memory.DeleteDocumentAsync(documentId: "1");

        answer = await memory.AskAsync("what color should I choose, and why?");
        Console.WriteLine(answer.Result);

        await memory.DeleteIndexAsync();
    }
}
