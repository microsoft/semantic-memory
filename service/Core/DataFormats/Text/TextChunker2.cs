// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using Microsoft.KernelMemory.AI;
using static Microsoft.KernelMemory.DataFormats.Text.TextChunker;

namespace Microsoft.KernelMemory.DataFormats.Text;

/// <summary>
/// Split text in chunks, attempting to leave meaning intact.
/// For plain text, split looking at new lines first, then periods, and so on.
/// For markdown, split looking at punctuation first, and so on.
/// </summary>
[Experimental("KMEXP00")]
public static class TextChunker2
{
    /// <summary>
    /// This is the standard content to be split, for all content that cannot be divided in pages
    /// we can simply send a single PageInfo with all the content in a single record.
    /// </summary>
    /// <param name="Content"></param>
    /// <param name="Tag">A simple object that will be added on the extracted chunk, it is a simple object
    /// because the caller can use Page Number or whatever data it needs.</param>
    public record ChunkInfo(string Content, object? Tag)
    {
        /// <summary>
        /// If you want to convert this to string it is possible to simply return the content.
        /// This makes simpler create TextChunker2 based on TextChunker.
        /// </summary>
        /// <returns></returns>
        public override string ToString()
        {
            return this.Content;
        }
    };

    private static readonly char[] s_spaceChar = { ' ' };
    private static readonly string?[] s_plaintextSplitOptions = { "\n\r", ".", "?!", ";", ":", ",", ")]}", " ", "-", null };
    private static readonly string?[] s_markdownSplitOptions = { ".", "?!", ";", ":", ",", ")]}", " ", "-", "\n\r", null };

    private static readonly TokenCounter s_defaultTokenCounter = (new CL100KTokenizer()).CountTokens;

    /// <summary>
    /// Split plain text into lines.
    /// </summary>
    /// <param name="text">Text to split</param>
    /// <param name="tag">Tag to associate to the split</param>
    /// <param name="maxTokensPerLine">Maximum number of tokens per line.</param>
    /// <param name="tokenCounter">Function to count tokens in a string. If not supplied, the default counter will be used.</param>
    /// <returns>List of lines.</returns>
    public static List<ChunkInfo> SplitPlainTextLines(
        string text,
        object? tag,
        int maxTokensPerLine,
        TextChunker.TokenCounter? tokenCounter = null) =>
        InternalSplitLines(
            new ChunkInfo(text, tag),
            maxTokensPerLine,
            trim: true,
            s_plaintextSplitOptions, tokenCounter);

    /// <summary>
    /// Split markdown text into lines.
    /// </summary>
    /// <param name="text">Text to split</param>
    /// <param name="tag">Tag to associate to the split</param>
    /// <param name="maxTokensPerLine">Maximum number of tokens per line.</param>
    /// <param name="tokenCounter">Function to count tokens in a string. If not supplied, the default counter will be used.</param>
    /// <returns>List of lines.</returns>
    public static List<ChunkInfo> SplitMarkDownLines(
        string text,
        object tag,
        int maxTokensPerLine,
        TextChunker.TokenCounter? tokenCounter = null) =>
        InternalSplitLines(
            new ChunkInfo(text, tag),
            maxTokensPerLine,
            trim: true,
            s_markdownSplitOptions, tokenCounter);

    /// <summary>
    /// Split plain text into paragraphs.
    /// Note: in the default KM implementation, one paragraph == one partition.
    /// </summary>
    /// <param name="lines">Lines of text.</param>
    /// <param name="maxTokensPerParagraph">Maximum number of tokens per paragraph.</param>
    /// <param name="overlapTokens">Number of tokens to overlap between paragraphs.</param>
    /// <param name="chunkHeader">Text to be prepended to each individual chunk.</param>
    /// <param name="tokenCounter">Function to count tokens in a string. If not supplied, the default counter will be used.</param>
    /// <returns>List of paragraphs.</returns>
    public static IReadOnlyCollection<ChunkInfo> SplitPlainTextParagraphs(
        List<ChunkInfo> lines,
        int maxTokensPerParagraph,
        int overlapTokens = 0,
        string? chunkHeader = null,
        TextChunker.TokenCounter? tokenCounter = null) =>
        InternalSplitTextParagraphs(
            lines,
            maxTokensPerParagraph,
            overlapTokens,
            chunkHeader,
            static (text, maxTokens, tokenCounter) => InternalSplitLines(
                text,
                maxTokens,
                trim: false,
                s_plaintextSplitOptions,
                tokenCounter),
            tokenCounter);

    /// <summary>
    /// Split markdown text into paragraphs.
    /// </summary>
    /// <param name="lines">Lines of text.</param>
    /// <param name="maxTokensPerParagraph">Maximum number of tokens per paragraph.</param>
    /// <param name="overlapTokens">Number of tokens to overlap between paragraphs.</param>
    /// <param name="chunkHeader">Text to be prepended to each individual chunk.</param>
    /// <param name="tokenCounter">Function to count tokens in a string. If not supplied, the default counter will be used.</param>
    /// <returns>List of paragraphs.</returns>
    public static IReadOnlyCollection<ChunkInfo> SplitMarkdownParagraphs(
        List<ChunkInfo> lines,
        int maxTokensPerParagraph,
        int overlapTokens = 0,
        string? chunkHeader = null,
        TextChunker.TokenCounter? tokenCounter = null) =>
        InternalSplitTextParagraphs(
            lines,
            maxTokensPerParagraph,
            overlapTokens,
            chunkHeader,
            static (text, maxTokens, tokenCounter) => InternalSplitLines(
                text,
                maxTokens,
                trim: false,
                s_markdownSplitOptions,
                tokenCounter),
            tokenCounter);

    private static IReadOnlyCollection<ChunkInfo> InternalSplitTextParagraphs(
        List<ChunkInfo> lines,
        int maxTokensPerParagraph,
        int overlapTokens,
        string? chunkHeader,
        Func<ChunkInfo, int, TextChunker.TokenCounter?, List<ChunkInfo>> longLinesSplitter,
        TextChunker.TokenCounter? tokenCounter)
    {
        if (maxTokensPerParagraph <= 0)
        {
            throw new ArgumentException("maxTokensPerParagraph should be a positive number", nameof(maxTokensPerParagraph));
        }

        if (maxTokensPerParagraph <= overlapTokens)
        {
            throw new ArgumentException("overlapTokens cannot be larger than maxTokensPerParagraph", nameof(maxTokensPerParagraph));
        }

        if (lines.Count == 0)
        {
            return Array.Empty<ChunkInfo>();
        }

        var chunkHeaderTokens = chunkHeader is { Length: > 0 } ? GetTokenCount(chunkHeader, tokenCounter) : 0;

        var adjustedMaxTokensPerParagraph = maxTokensPerParagraph - overlapTokens - chunkHeaderTokens;

        // Split long lines first
        var truncatedLines = lines
            .SelectMany(line => longLinesSplitter(line, adjustedMaxTokensPerParagraph, tokenCounter))
            .ToArray();

        var paragraphs = BuildParagraph(truncatedLines, adjustedMaxTokensPerParagraph, tokenCounter);

        var processedParagraphs = ProcessParagraphs(
            paragraphs, adjustedMaxTokensPerParagraph, overlapTokens, chunkHeader, longLinesSplitter, tokenCounter);

        return processedParagraphs;
    }

    private static List<ChunkInfo> BuildParagraph(
        ChunkInfo[] truncatedLines,
        int maxTokensPerParagraph,
        TextChunker.TokenCounter? tokenCounter)
    {
        StringBuilder paragraphBuilder = new();
        List<ChunkInfo> paragraphs = new();

        if (truncatedLines == null || truncatedLines.Length == 0)
        {
            return paragraphs;
        }

        //paragraph tag is the tag was first associated to the current paraphBuilder.
        object? paragraphTag = truncatedLines[0].Tag;
        foreach (ChunkInfo line in truncatedLines)
        {
            if (paragraphBuilder.Length > 0)
            {
                string? paragraph = null;

                int currentCount = GetTokenCount(line, tokenCounter) + 1;
                if (currentCount < maxTokensPerParagraph)
                {
                    currentCount += GetTokenCount(paragraphBuilder.ToString(), tokenCounter);
                }

                if (currentCount >= maxTokensPerParagraph)
                {
                    // Complete the paragraph and prepare for the next
                    paragraph = paragraphBuilder.ToString();

                    paragraphs.Add(new ChunkInfo(paragraph.Trim(), paragraphTag));
                    paragraphBuilder.Clear();
                    paragraphTag = line.Tag;
                }
            }

            paragraphBuilder.AppendLine(line.Content);
        }

        if (paragraphBuilder.Length > 0)
        {
            // Add the final paragraph if there's anything remaining, now the last paragraph tag is the first
            // tag that contains text on the tag.
            paragraphs.Add(new ChunkInfo(paragraphBuilder.ToString().Trim(), paragraphTag));
        }

        return paragraphs;
    }

    private static List<ChunkInfo> ProcessParagraphs(
        List<ChunkInfo> paragraphs,
        int adjustedMaxTokensPerParagraph,
        int overlapTokens,
        string? chunkHeader,
        Func<ChunkInfo, int, TextChunker.TokenCounter?, List<ChunkInfo>> longLinesSplitter,
        TextChunker.TokenCounter? tokenCounter)
    {
        // distribute text more evenly in the last paragraphs when the last paragraph is too short.
        if (paragraphs.Count > 1)
        {
            var lastParagraph = paragraphs[paragraphs.Count - 1];
            var secondLastParagraph = paragraphs[paragraphs.Count - 2];

            if (GetTokenCount(lastParagraph, tokenCounter) < adjustedMaxTokensPerParagraph / 4)
            {
                var lastParagraphTokens = lastParagraph.Content.Split(s_spaceChar, StringSplitOptions.RemoveEmptyEntries);
                var secondLastParagraphTokens = secondLastParagraph.Content.Split(s_spaceChar, StringSplitOptions.RemoveEmptyEntries);

                var lastParagraphTokensCount = lastParagraphTokens.Length;
                var secondLastParagraphTokensCount = secondLastParagraphTokens.Length;

                if (lastParagraphTokensCount + secondLastParagraphTokensCount <= adjustedMaxTokensPerParagraph)
                {
                    var newSecondLastParagraph = string.Join(" ", secondLastParagraphTokens);
                    var newLastParagraph = string.Join(" ", lastParagraphTokens);

                    paragraphs[paragraphs.Count - 2] = new ChunkInfo($"{newSecondLastParagraph} {newLastParagraph}", secondLastParagraph.Tag);
                    paragraphs.RemoveAt(paragraphs.Count - 1);
                }
            }
        }

        var processedParagraphs = new List<ChunkInfo>();
        var paragraphStringBuilder = new StringBuilder();

        for (int i = 0; i < paragraphs.Count; i++)
        {
            paragraphStringBuilder.Clear();

            if (chunkHeader is not null)
            {
                paragraphStringBuilder.Append(chunkHeader);
            }

            var paragraph = paragraphs[i];

            if (overlapTokens > 0 && i < paragraphs.Count - 1)
            {
                var nextParagraph = paragraphs[i + 1];
                var split = longLinesSplitter(nextParagraph, overlapTokens, tokenCounter);

                paragraphStringBuilder.Append(paragraph.Content);

                if (split.Count != 0)
                {
                    paragraphStringBuilder.Append(' ').Append(split[0]);
                }
            }
            else
            {
                paragraphStringBuilder.Append(paragraph.Content);
            }

            processedParagraphs.Add(new ChunkInfo(paragraphStringBuilder.ToString(), paragraph.Tag));
        }

        return processedParagraphs;
    }

    private static List<ChunkInfo> InternalSplitLines(
        ChunkInfo chunkInput,
        int maxTokensPerLine,
        bool trim,
        string?[] splitOptions,
        TextChunker.TokenCounter? tokenCounter)
    {
        var result = new List<ChunkInfo>();

        var text = chunkInput.Content.Replace("\r\n", "\n", StringComparison.OrdinalIgnoreCase); // normalize line endings
        result.Add(new ChunkInfo(text, chunkInput.Tag));
        for (int i = 0; i < splitOptions.Length; i++)
        {
            int count = result.Count; // track where the original input left off
            var (splits2, inputWasSplit2) = Split(result, maxTokensPerLine, splitOptions[i].AsSpan(), trim, tokenCounter);
            result.AddRange(splits2);
            result.RemoveRange(0, count); // remove the original input
            if (!inputWasSplit2)
            {
                break;
            }
        }

        return result;
    }

    private static (List<ChunkInfo>, bool) Split(
        List<ChunkInfo> input,
        int maxTokens,
        ReadOnlySpan<char> separators,
        bool trim,
        TextChunker.TokenCounter? tokenCounter)
    {
        bool inputWasSplit = false;
        List<ChunkInfo> result = new();
        int count = input.Count;
        for (int i = 0; i < count; i++)
        {
            var currentInput = input[i];
            var (splits, split) = Split(currentInput.Content.AsSpan(), currentInput.Content, maxTokens, separators, trim, tokenCounter);
            result.AddRange(splits.Select(s => new ChunkInfo(s, currentInput.Tag)));
            inputWasSplit |= split;
        }

        return (result, inputWasSplit);
    }

    private static (List<string>, bool) Split(
        ReadOnlySpan<char> input,
        string? inputString,
        int maxTokens,
        ReadOnlySpan<char> separators,
        bool trim,
        TextChunker.TokenCounter? tokenCounter)
    {
        Debug.Assert(inputString is null || input.SequenceEqual(inputString.AsSpan()));
        List<string> result = new();
        var inputWasSplit = false;

        int inputTokenCount = GetTokenCount(inputString ??= input.ToString(), tokenCounter);

        if (inputTokenCount > maxTokens)
        {
            inputWasSplit = true;

            int half = input.Length / 2;
            int cutPoint = -1;

            if (separators.IsEmpty)
            {
                cutPoint = half;
            }
            else if (input.Length > 2)
            {
                int pos = 0;
                while (true)
                {
                    int index = input.Slice(pos, input.Length - 1 - pos).IndexOfAny(separators);
                    if (index < 0)
                    {
                        break;
                    }

                    index += pos;

                    if (Math.Abs(half - index) < Math.Abs(half - cutPoint))
                    {
                        cutPoint = index + 1;
                    }

                    pos = index + 1;
                }
            }

            if (cutPoint > 0)
            {
                var firstHalf = input.Slice(0, cutPoint);
                var secondHalf = input.Slice(cutPoint);
                if (trim)
                {
                    firstHalf = firstHalf.Trim();
                    secondHalf = secondHalf.Trim();
                }

                // Recursion
                var (splits1, split1) = Split(firstHalf, null, maxTokens, separators, trim, tokenCounter);
                result.AddRange(splits1);
                var (splits2, split2) = Split(secondHalf, null, maxTokens, separators, trim, tokenCounter);
                result.AddRange(splits2);

                inputWasSplit = split1 || split2;
                return (result, inputWasSplit);
            }
        }

        result.Add((inputString is not null, trim) switch
        {
            (true, true) => inputString!.Trim(),
            (true, false) => inputString!,
            (false, true) => input.Trim().ToString(),
            (false, false) => input.ToString(),
        });

        return (result, inputWasSplit);
    }

    private static int GetTokenCount(ChunkInfo input, TextChunker.TokenCounter? tokenCounter) => GetTokenCount(input.Content, tokenCounter);

    private static int GetTokenCount(string input, TextChunker.TokenCounter? tokenCounter)
    {
        // Fall back to GPT tokenizer if none configured
        return tokenCounter?.Invoke(input) ?? s_defaultTokenCounter(input);
    }
}
