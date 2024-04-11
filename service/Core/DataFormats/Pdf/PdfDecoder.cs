﻿// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.KernelMemory.Diagnostics;
using Microsoft.KernelMemory.Pipeline;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Microsoft.KernelMemory.DataFormats.Pdf;

public class PdfDecoder : IContentDecoder
{
    private readonly ILogger<PdfDecoder> _log;

    public IEnumerable<string> SupportedMimeTypes => new[] { MimeTypes.Pdf };

    public PdfDecoder(ILogger<PdfDecoder>? log = null)
    {
        this._log = log ?? DefaultLogger<PdfDecoder>.Instance;
    }

    public Task<FileContent> ExtractContentAsync(string filename, string mimeType, CancellationToken cancellationToken = default)
    {
        using var stream = File.OpenRead(filename);
        return this.ExtractContentAsync(Path.GetFileName(filename), stream, mimeType, cancellationToken);
    }

    public Task<FileContent> ExtractContentAsync(string name, BinaryData data, string mimeType, CancellationToken cancellationToken = default)
    {
        using var stream = data.ToStream();
        return this.ExtractContentAsync(name, stream, mimeType, cancellationToken);
    }

    public Task<FileContent> ExtractContentAsync(string name, Stream data, string mimeType, CancellationToken cancellationToken = default)
    {
        this._log.LogDebug("Extracting text from PDF file {0}", name);

        var result = new FileContent();

        using PdfDocument? pdfDocument = PdfDocument.Open(data);
        if (pdfDocument == null) { return Task.FromResult(result); }

        foreach (Page? page in pdfDocument.GetPages().Where(x => x != null))
        {
            // Note: no trimming, use original spacing
            string pageContent = ContentOrderTextExtractor.GetText(page) ?? string.Empty;
            result.Sections.Add(new FileSection(page.Number, pageContent, false));
        }

        return Task.FromResult(result);
    }
}
