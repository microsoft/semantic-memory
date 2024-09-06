// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.IO;

namespace Microsoft.KernelMemory.Pipeline;

public static class MimeTypes
{
    public const string PlainText = "text/plain";

    // Multiple values have been used over the years.
    public const string MarkDown = "text/markdown";
    public const string MarkDownOld1 = "text/x-markdown";
    public const string MarkDownOld2 = "text/plain-markdown";

    public const string Html = "text/html";
    public const string XHTML = "application/xhtml+xml";
    public const string XML = "application/xml";
    public const string XML2 = "text/xml";
    public const string JSONLD = "application/ld+json";
    public const string CascadingStyleSheet = "text/css";
    public const string JavaScript = "text/javascript";
    public const string BourneShellScript = "application/x-sh";

    public const string ImageBmp = "image/bmp";
    public const string ImageGif = "image/gif";
    public const string ImageJpeg = "image/jpeg";
    public const string ImagePng = "image/png";
    public const string ImageTiff = "image/tiff";
    public const string ImageWebP = "image/webp";
    public const string ImageSVG = "image/svg+xml";

    public const string WebPageUrl = "text/x-uri";
    public const string TextEmbeddingVector = "float[]";
    public const string Json = "application/json";
    public const string CSVData = "text/csv";

    public const string Pdf = "application/pdf";
    public const string RTFDocument = "application/rtf";

    public const string MsWord = "application/msword";
    public const string MsWordX = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";

    public const string MsPowerPoint = "application/vnd.ms-powerpoint";
    public const string MsPowerPointX = "application/vnd.openxmlformats-officedocument.presentationml.presentation";

    public const string MsExcel = "application/vnd.ms-excel";
    public const string MsExcelX = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public const string OpenDocumentText = "application/vnd.oasis.opendocument.text";
    public const string OpenDocumentSpreadsheet = "application/vnd.oasis.opendocument.spreadsheet";
    public const string OpenDocumentPresentation = "application/vnd.oasis.opendocument.presentation";
    public const string ElectronicPublicationZip = "application/epub+zip";

    public const string AudioAAC = "audio/aac";
    public const string AudioMP3 = "audio/mpeg";
    public const string AudioWaveform = "audio/wav";
    public const string AudioOGG = "audio/ogg";
    public const string AudioOpus = "audio/opus";
    public const string AudioWEBM = "audio/webm";

    public const string VideoMP4 = "video/mp4";
    public const string VideoMPEG = "video/mpeg";
    public const string VideoOGG = "video/ogg";
    public const string VideoOGGGeneric = "application/ogg";
    public const string VideoWEBM = "video/webm";

    public const string ArchiveTar = "application/x-tar";
    public const string ArchiveGzip = "application/gzip";
    public const string ArchiveZip = "application/zip";
    public const string ArchiveRar = "application/vnd.rar";
    public const string Archive7Zip = "application/x-7z-compressed";
    public const string OctetStream = "application/octet-stream";
}

public static class FileExtensions
{
    public const string PlainText = ".txt";
    public const string MarkDown = ".md";

    public const string Htm = ".htm";
    public const string Html = ".html";
    public const string XHTML = ".xhtml";
    public const string XML = ".xml";
    public const string JSONLD = ".jsonld";
    public const string CascadingStyleSheet = ".css";
    public const string JavaScript = ".js";
    public const string BourneShellScript = ".sh";

    public const string ImageBmp = ".bmp";
    public const string ImageGif = ".gif";
    public const string ImageJpeg = ".jpeg";
    public const string ImageJpg = ".jpg";
    public const string ImagePng = ".png";
    public const string ImageTiff = ".tiff";
    public const string ImageTiff2 = ".tif";
    public const string ImageWebP = ".webp";
    public const string ImageSVG = ".svg";

    public const string WebPageUrl = ".url";
    public const string TextEmbeddingVector = ".text_embedding";
    public const string Json = ".json";
    public const string CSVData = ".csv";

    public const string Pdf = ".pdf";
    public const string RTFDocument = ".rtf";

    public const string MsWord = ".doc";
    public const string MsWordX = ".docx";
    public const string MsPowerPoint = ".ppt";
    public const string MsPowerPointX = ".pptx";
    public const string MsExcel = ".xls";
    public const string MsExcelX = ".xlsx";

    public const string OpenDocumentText = ".odt";
    public const string OpenDocumentSpreadsheet = ".ods";
    public const string OpenDocumentPresentation = ".odp";
    public const string ElectronicPublicationZip = ".epub";

    public const string AudioAAC = ".aac";
    public const string AudioMP3 = ".mp3";
    public const string AudioWaveform = ".wav";
    public const string AudioOGG = ".oga";
    public const string AudioOpus = ".opus";
    public const string AudioWEBM = ".weba";

    public const string VideoMP4 = ".mp4";
    public const string VideoMPEG = ".mpeg";
    public const string VideoOGG = ".ogv";
    public const string VideoOGGGeneric = ".ogx";
    public const string VideoWEBM = ".webm";

    public const string ArchiveTar = ".tar";
    public const string ArchiveGzip = ".gz";
    public const string ArchiveZip = ".zip";
    public const string ArchiveRar = ".rar";
    public const string Archive7Zip = ".7z";
    public const string Bin = ".bin";
}

public interface IMimeTypeDetection
{
    public string GetFileType(string filename);
}

public class MimeTypesDetection : IMimeTypeDetection
{
    private static readonly Dictionary<string, string> s_extensionTypes =
        new(StringComparer.OrdinalIgnoreCase)
        {
            { FileExtensions.PlainText, MimeTypes.PlainText },

            { FileExtensions.MarkDown, MimeTypes.MarkDown },

            { FileExtensions.Htm, MimeTypes.Html },
            { FileExtensions.Html, MimeTypes.Html },
            { FileExtensions.XHTML, MimeTypes.XHTML },
            { FileExtensions.XML, MimeTypes.XML },
            { FileExtensions.JSONLD, MimeTypes.JSONLD },
            { FileExtensions.CascadingStyleSheet, MimeTypes.CascadingStyleSheet },
            { FileExtensions.JavaScript, MimeTypes.JavaScript },
            { FileExtensions.BourneShellScript, MimeTypes.BourneShellScript },

            { FileExtensions.ImageBmp, MimeTypes.ImageBmp },
            { FileExtensions.ImageGif, MimeTypes.ImageGif },
            { FileExtensions.ImageJpeg, MimeTypes.ImageJpeg },
            { FileExtensions.ImageJpg, MimeTypes.ImageJpeg },
            { FileExtensions.ImagePng, MimeTypes.ImagePng },
            { FileExtensions.ImageTiff, MimeTypes.ImageTiff },
            { FileExtensions.ImageTiff2, MimeTypes.ImageTiff },
            { FileExtensions.ImageWebP, MimeTypes.ImageWebP },
            { FileExtensions.ImageSVG, MimeTypes.ImageSVG },

            { FileExtensions.WebPageUrl, MimeTypes.WebPageUrl },
            { FileExtensions.TextEmbeddingVector, MimeTypes.TextEmbeddingVector },
            { FileExtensions.Json, MimeTypes.Json },
            { FileExtensions.CSVData, MimeTypes.CSVData },

            { FileExtensions.Pdf, MimeTypes.Pdf },
            { FileExtensions.RTFDocument, MimeTypes.RTFDocument },

            { FileExtensions.MsWord, MimeTypes.MsWord },
            { FileExtensions.MsWordX, MimeTypes.MsWordX },
            { FileExtensions.MsPowerPoint, MimeTypes.MsPowerPoint },
            { FileExtensions.MsPowerPointX, MimeTypes.MsPowerPointX },
            { FileExtensions.MsExcel, MimeTypes.MsExcel },
            { FileExtensions.MsExcelX, MimeTypes.MsExcelX },

            { FileExtensions.OpenDocumentText, MimeTypes.OpenDocumentText },
            { FileExtensions.OpenDocumentSpreadsheet, MimeTypes.OpenDocumentSpreadsheet },
            { FileExtensions.OpenDocumentPresentation, MimeTypes.OpenDocumentPresentation },
            { FileExtensions.ElectronicPublicationZip, MimeTypes.ElectronicPublicationZip },

            { FileExtensions.AudioAAC, MimeTypes.AudioAAC },
            { FileExtensions.AudioMP3, MimeTypes.AudioMP3 },
            { FileExtensions.AudioWaveform, MimeTypes.AudioWaveform },
            { FileExtensions.AudioOGG, MimeTypes.AudioOGG },
            { FileExtensions.AudioOpus, MimeTypes.AudioOpus },
            { FileExtensions.AudioWEBM, MimeTypes.AudioWEBM },

            { FileExtensions.VideoMP4, MimeTypes.VideoMP4 },
            { FileExtensions.VideoMPEG, MimeTypes.VideoMPEG },
            { FileExtensions.VideoOGG, MimeTypes.VideoOGG },
            { FileExtensions.VideoOGGGeneric, MimeTypes.VideoOGGGeneric },
            { FileExtensions.VideoWEBM, MimeTypes.VideoWEBM },

            { FileExtensions.ArchiveTar, MimeTypes.ArchiveTar },
            { FileExtensions.ArchiveGzip, MimeTypes.ArchiveGzip },
            { FileExtensions.ArchiveZip, MimeTypes.ArchiveZip },
            { FileExtensions.ArchiveRar, MimeTypes.ArchiveRar },
            { FileExtensions.Archive7Zip, MimeTypes.Archive7Zip },
            { FileExtensions.Bin, MimeTypes.OctetStream }
        };

    public string GetFileType(string filename)
    {
        string extension = Path.GetExtension(filename);

        if (s_extensionTypes.TryGetValue(extension, out var mimeType))
        {
            return mimeType;
        }

        throw new NotSupportedException($"File type not supported: {filename}");
    }
}
