﻿// Copyright (c) Microsoft. All rights reserved.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.KernelMemory.DataFormats;
using Microsoft.KernelMemory.DataFormats.Image;
using Microsoft.KernelMemory.DataFormats.Office;
using Microsoft.KernelMemory.DataFormats.Pdf;
using Microsoft.KernelMemory.DataFormats.Text;
using Microsoft.KernelMemory.DataFormats.WebPages;

// ReSharper disable once CheckNamespace
namespace Microsoft.KernelMemory;

public static partial class KernelMemoryBuilderExtensions
{
    public static IKernelMemoryBuilder WithContentDecoder<T>(
        this IKernelMemoryBuilder builder) where T : class, IContentDecoder
    {
        builder.Services.AddContentDecoder<T>();
        return builder;
    }

    public static IKernelMemoryBuilder WithContentDecoder(
        this IKernelMemoryBuilder builder, IContentDecoder decoder)
    {
        builder.Services.AddContentDecoder(decoder);
        return builder;
    }

    public static IKernelMemoryBuilder WithDefaultContentDecoders(
        this IKernelMemoryBuilder builder)
    {
        builder.Services.AddDefaultContentDecoders();
        return builder;
    }
}

public static partial class DependencyInjection
{
    public static IServiceCollection AddContentDecoder<T>(
        this IServiceCollection services) where T : class, IContentDecoder
    {
        services.AddSingleton<IContentDecoder, T>();
        return services;
    }

    public static IServiceCollection AddContentDecoder(
        this IServiceCollection services, IContentDecoder decoder)
    {
        services.AddSingleton<IContentDecoder>(decoder);
        return services;
    }

    public static IServiceCollection AddDefaultContentDecoders(
        this IServiceCollection services)
    {
        services.AddSingleton<IContentDecoder, TextDecoder>();
        services.AddSingleton<IContentDecoder, MarkDownDecoder>();
        services.AddSingleton<IContentDecoder, HtmlDecoder>();
        services.AddSingleton<IContentDecoder, PdfDecoder>();
        services.AddSingleton<IContentDecoder, ImageDecoder>();
        services.AddSingleton<IContentDecoder, MsExcelDecoder>();
        services.AddSingleton<IContentDecoder, MsPowerPointDecoder>();
        services.AddSingleton<IContentDecoder, MsWordDecoder>();

        return services;
    }
}
