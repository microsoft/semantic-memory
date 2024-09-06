// Copyright (c) Microsoft. All rights reserved.

using System.Text.Json;
using Microsoft.KernelMemory.MemoryDb.Qdrant.Client.Http;
using Microsoft.KernelMemory.MemoryDb.Qdrant.Internals;
using Microsoft.KM.TestHelpers;
using Xunit.Abstractions;

namespace Microsoft.Qdrant.UnitTests;

public class ScrollVectorsRequestTest : BaseUnitTestCase
{
    public ScrollVectorsRequestTest(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    [Trait("Category", "UnitTest")]
    [Trait("Category", "Qdrant")]
    public void FiltersAreRenderedToJson()
    {
        const string Expected = """
                                {
                                    "filter":
                                    {
                                        "must":
                                        [
                                            {
                                                "key": "tags",
                                                "match":
                                                {
                                                    "value": "user:devis"
                                                }
                                            },
                                            {
                                                "key": "tags",
                                                "match":
                                                {
                                                    "value": "type:blog"
                                                }
                                            },
                                            {
                                                "should":
                                                [
                                                    {
                                                        "must":
                                                        [
                                                            {
                                                                "key": "tags",
                                                                "match":
                                                                {
                                                                    "value": "month:january"
                                                                }
                                                            },
                                                            {
                                                                "key": "tags",
                                                                "match":
                                                                {
                                                                    "value": "year:2000"
                                                                }
                                                            }
                                                        ]
                                                    },
                                                    {
                                                        "must":
                                                        [
                                                            {
                                                                "key": "tags",
                                                                "match":
                                                                {
                                                                    "value": "month:july"
                                                                }
                                                            },
                                                            {
                                                                "key": "tags",
                                                                "match":
                                                                {
                                                                    "value": "year:2003"
                                                                }
                                                            }
                                                        ]
                                                    }
                                                ]
                                            }
                                        ]
                                    },
                                    "limit": 1,
                                    "offset": 0,
                                    "with_payload": false,
                                    "with_vector": false
                                }
                                """;
        // Arrange
        var request = ScrollVectorsRequest
            .Create("coll")
            .HavingAllTags([new TagFilter("user:devis", TagFilterType.Equal), new TagFilter("type:blog", TagFilterType.Equal)])
            .HavingSomeTags(new[]
            {
                new[] { new TagFilter("month:january", TagFilterType.Equal), new TagFilter("year:2000", TagFilterType.Equal), },
                new[] { new TagFilter("month:july", TagFilterType.Equal), new TagFilter("year:2003", TagFilterType.Equal), },
            });

        // Act
        var actual = JsonSerializer.Serialize(request);

        // Assert
        var expected = JsonSerializer.Serialize(JsonSerializer.Deserialize<object>(Expected));
        Assert.Equal(expected, actual);
    }

    [Fact]
    [Trait("Category", "UnitTest")]
    [Trait("Category", "Qdrant")]
    public void ItRendersOptimizedConditions()
    {
        const string Expected = """
                                {
                                    "filter":
                                    {
                                        "must":
                                        [
                                            {
                                                "key": "tags",
                                                "match":
                                                {
                                                    "value": "user:devis"
                                                }
                                            },
                                            {
                                                "key": "tags",
                                                "match":
                                                {
                                                    "value": "type:blog"
                                                }
                                            },
                                            {
                                                "key": "tags",
                                                "match":
                                                {
                                                    "value": "month:january"
                                                }
                                            },
                                            {
                                                "key": "tags",
                                                "match":
                                                {
                                                    "value": "year:2000"
                                                }
                                            }
                                        ]
                                    },
                                    "limit": 1,
                                    "offset": 0,
                                    "with_payload": false,
                                    "with_vector": false
                                }
                                """;
        // Arrange
        var request = ScrollVectorsRequest
            .Create("coll")
            .HavingAllTags([new TagFilter("user:devis", TagFilterType.Equal), new TagFilter("type:blog", TagFilterType.Equal)])
            .HavingSomeTags([new[] { new TagFilter("month:january", TagFilterType.Equal), new TagFilter("year:2000", TagFilterType.Equal) }]);

        // Act
        var actual = JsonSerializer.Serialize(request);

        // Assert
        var expected = JsonSerializer.Serialize(JsonSerializer.Deserialize<object>(Expected));
        Assert.Equal(expected, actual);
    }
}
