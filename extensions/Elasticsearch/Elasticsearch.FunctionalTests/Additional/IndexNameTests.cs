// Copyright (c) Microsoft. All rights reserved.

using Microsoft.KernelMemory.MemoryDb.Elasticsearch;
using Microsoft.KernelMemory.MemoryDb.Elasticsearch.Internals;
using Microsoft.KM.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.Elasticsearch.FunctionalTests.Additional;

public class IndexNameTests : BaseFunctionalTestCase
{
    private readonly ITestOutputHelper _output;

    public IndexNameTests(IConfiguration cfg, ITestOutputHelper output)
        : base(cfg, output)
    {
        this._output = output ?? throw new ArgumentNullException(nameof(output));
    }

    [Theory]
    [Trait("Category", "Elasticsearch")]
    [InlineData("")] // default index
    [InlineData("nondefault")]
    [InlineData("WithUppercase")]
    [InlineData("With-Dashes")]
    [InlineData("123numberfirst")]
    public void GoodIndexNamesAreAccepted(string indexName)
    {
        Assert.True(IndexNameHelper.TryConvert(indexName, base.ElasticsearchConfig, out var convResult));
        Assert.Empty(convResult.Errors);

        this._output.WriteLine($"The index name '{indexName}' will be translated to '{convResult.ActualIndexName}'.");
    }

    [Theory]
    [Trait("Category", "Elasticsearch")]
    // An index name cannot start with a hyphen (-) or underscore (_).
    //[InlineData("-test", 1)]
    //[InlineData("test_", 1)]
    // An index name can only contain letters, digits, and hyphens (-).
    [InlineData("test space", 1)]
    [InlineData("test/slash", 1)]
    [InlineData("test\\backslash", 1)]
    [InlineData("test.dot", 1)]
    [InlineData("test:colon", 1)]
    [InlineData("test*asterisk", 1)]
    [InlineData("test<less", 1)]
    [InlineData("test>greater", 1)]
    [InlineData("test|pipe", 1)]
    [InlineData("test?question", 1)]
    [InlineData("test\"quote", 1)]
    [InlineData("test'quote", 1)]
    [InlineData("test`backtick", 1)]
    [InlineData("test~tilde", 1)]
    [InlineData("test!exclamation", 1)]
    // Avoid names that are dot-only or dot and numbers
    // Multi error
    [InlineData(".", 1)]
    [InlineData("..", 1)]
    [InlineData("1.2.3", 1)]
    //[InlineData("_test", 1)]
    public void BadIndexNamesAreRejected(string indexName, int errorCount)
    {
        // Creates the index using IMemoryDb
        var exception = Assert.Throws<InvalidIndexNameException>(() =>
        {
            IndexNameHelper.Convert(indexName, base.ElasticsearchConfig);
        });

        this._output.WriteLine(
            $"The index name '{indexName}' had the following errors:\n{string.Join("\n", exception.Errors)}" +
            $"" +
            $"The expected number of errors was {errorCount}.");

        Assert.True(errorCount == exception.Errors.Count(), $"The number of errors expected is different than the number of errors found.");
    }

    [Fact]
    [Trait("Category", "Elasticsearch")]
    public void IndexNameCannotBeLongerThan255Bytes()
    {
        var indexName = new string('a', 256);
        var exception = Assert.Throws<InvalidIndexNameException>(() =>
        {
            IndexNameHelper.Convert(indexName, base.ElasticsearchConfig);
        });

        Assert.Equal(1, exception.Errors.Count());
    }
}
