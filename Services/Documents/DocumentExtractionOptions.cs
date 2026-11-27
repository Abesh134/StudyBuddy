using StudyBuddy.Models;

namespace StudyBuddy.Services.Documents;

public sealed class DocumentExtractionOptions
{
    public const string SectionName = "DocumentExtraction";

    public long MaxTxtBytes { get; set; } = 5 * 1024 * 1024;
    public long MaxMarkdownBytes { get; set; } = 5 * 1024 * 1024;
    public long MaxPdfBytes { get; set; } = 20 * 1024 * 1024;
    public long MaxDocxBytes { get; set; } = 20 * 1024 * 1024;
    public long MaxPptxBytes { get; set; } = 20 * 1024 * 1024;
    public long MaxImageBytes { get; set; } = 10 * 1024 * 1024;

    public int MinExtractedTextLength { get; set; } = 20;
    public int MaxExtractedTextLength { get; set; } = 150_000;

    public int MaxPdfPages { get; set; } = 150;
    public int MaxPowerPointSlides { get; set; } = 150;

    public long MaxDecompressedBytes { get; set; } = 64 * 1024 * 1024;
    public int MaxZipEntries { get; set; } = 2_000;

    public int MaxImageWidth { get; set; } = 10_000;
    public int MaxImageHeight { get; set; } = 10_000;
    public long MaxImagePixels { get; set; } = 40_000_000;

    public int ExtractionTimeoutSeconds { get; set; } = 45;
    public int MaxConcurrentExtractions { get; set; } = 3;

    public OcrOptions Ocr { get; set; } = new();

    public long GetMaxBytesForFormat(SourceDocumentFormat format) => format switch
    {
        SourceDocumentFormat.TextDocument => Math.Max(1024L, MaxTxtBytes),
        SourceDocumentFormat.MarkdownDocument => Math.Max(1024L, MaxMarkdownBytes),
        SourceDocumentFormat.PdfDocument => Math.Max(1024L, MaxPdfBytes),
        SourceDocumentFormat.WordDocument => Math.Max(1024L, MaxDocxBytes),
        SourceDocumentFormat.PowerPointPresentation => Math.Max(1024L, MaxPptxBytes),
        SourceDocumentFormat.ImageOcr => Math.Max(1024L, MaxImageBytes),
        _ => Math.Max(1024L, MaxTxtBytes)
    };

    public long MaxAllowedUploadBytes =>
        new[] { MaxTxtBytes, MaxMarkdownBytes, MaxPdfBytes, MaxDocxBytes, MaxPptxBytes, MaxImageBytes }.Max();
}

public sealed class OcrOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Optional path to tesseract executable (auto-detected if null/empty).</summary>
    public string? ExecutablePath { get; set; }

    /// <summary>Optional path to tessdata directory containing .traineddata language models.</summary>
    public string? TessDataPath { get; set; }

    public string[] SupportedLanguages { get; set; } = new[] { "eng", "ben", "eng+ben" };

    public string DefaultLanguage { get; set; } = "eng";

    public int TimeoutSeconds { get; set; } = 30;

    public int MaxImagesPerDocument { get; set; } = 15;

    public int MaxPdfOcrPages { get; set; } = 15;
}

