using StudyBuddy.Models;

namespace StudyBuddy.Services.Documents;

public sealed record OcrLanguageOption(string Code, string Label);

public sealed record OcrAvailability(
    bool IsAvailable,
    string? ExecutablePath,
    IReadOnlyList<OcrLanguageOption> AvailableLanguages,
    string StatusMessage);

public sealed record OcrResult(
    bool Succeeded,
    string Text = "",
    string? ErrorMessage = null);

public sealed record DocumentExtractionResult(
    bool Succeeded,
    string ExtractedText = "",
    string SanitizedFileName = "",
    long FileSizeBytes = 0,
    SourceDocumentFormat DocumentFormat = SourceDocumentFormat.PastedNotes,
    int? PageOrSlideCount = null,
    int WordCount = 0,
    int CharacterCount = 0,
    bool UsedOcr = false,
    int OcrItemsProcessed = 0,
    IReadOnlyList<string>? Warnings = null,
    string? ErrorMessage = null,
    string RawExtractedText = "",
    string StructuredMarkdown = "")
{
    public IReadOnlyList<string> SafeWarnings => Warnings ?? Array.Empty<string>();

    public static DocumentExtractionResult Fail(
        string errorMessage,
        string sanitizedFileName = "",
        long fileSizeBytes = 0,
        SourceDocumentFormat format = SourceDocumentFormat.PastedNotes,
        IReadOnlyList<string>? warnings = null) =>
        new(
            Succeeded: false,
            ExtractedText: string.Empty,
            SanitizedFileName: sanitizedFileName,
            FileSizeBytes: fileSizeBytes,
            DocumentFormat: format,
            Warnings: warnings,
            ErrorMessage: errorMessage,
            RawExtractedText: string.Empty,
            StructuredMarkdown: string.Empty);

    public static DocumentExtractionResult Ok(
        string extractedText,
        string sanitizedFileName,
        long fileSizeBytes,
        SourceDocumentFormat format,
        int? pageOrSlideCount = null,
        bool usedOcr = false,
        int ocrItemsProcessed = 0,
        IReadOnlyList<string>? warnings = null,
        string? rawExtractedText = null,
        string? structuredMarkdown = null)
    {
        var canonical = structuredMarkdown ?? extractedText;
        var raw = rawExtractedText ?? extractedText;
        var wordCount = CountWords(canonical);
        return new DocumentExtractionResult(
            Succeeded: true,
            ExtractedText: canonical,
            SanitizedFileName: sanitizedFileName,
            FileSizeBytes: fileSizeBytes,
            DocumentFormat: format,
            PageOrSlideCount: pageOrSlideCount,
            WordCount: wordCount,
            CharacterCount: canonical.Length,
            UsedOcr: usedOcr,
            OcrItemsProcessed: ocrItemsProcessed,
            Warnings: warnings,
            ErrorMessage: null,
            RawExtractedText: raw,
            StructuredMarkdown: canonical);
    }

    public static int CountWords(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var count = 0;
        var inWord = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                inWord = false;
            }
            else if (!inWord)
            {
                inWord = true;
                count++;
            }
        }

        return count;
    }

    public static string GetFormatDisplayName(SourceDocumentFormat format) => format switch
    {
        SourceDocumentFormat.PastedNotes => "Pasted Notes",
        SourceDocumentFormat.TextDocument => "Text Document",
        SourceDocumentFormat.MarkdownDocument => "Markdown Document",
        SourceDocumentFormat.PdfDocument => "PDF Document",
        SourceDocumentFormat.WordDocument => "Word Document",
        SourceDocumentFormat.PowerPointPresentation => "PowerPoint Presentation",
        SourceDocumentFormat.ImageOcr => "Image OCR",
        _ => "Study Notes"
    };
}

