﻿// Copyright (c) Microsoft. All rights reserved.

using Microsoft.SemanticMemory.DataFormats.Image;

public class MyOcrEngine : IOcrEngine
{
    public Task<string> ExtractTextFromImageAsync(Stream imageContent, CancellationToken cancellationToken = default)
    {
        return Task.FromResult("test");
    }
}
