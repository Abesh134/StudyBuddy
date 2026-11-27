using StudyBuddy.Models;

namespace StudyBuddy.Services.Documents;

public interface IOcrService
{
    OcrAvailability GetAvailability();

    Task<OcrResult> RecognizeImageAsync(
        byte[] pngImageBytes,
        string? languageCode = null,
        CancellationToken cancellationToken = default);
}

public interface IPdfPageRenderer
{
    Task<byte[]> RenderPageToPngAsync(
        byte[] pdfBytes,
        int oneBasedPageNumber,
        CancellationToken cancellationToken = default);
}

public interface ITextExtractor
{
    bool CanHandle(SourceDocumentFormat format);

    Task<DocumentExtractionResult> ExtractAsync(
        byte[] fileBytes,
        string sanitizedFileName,
        SourceDocumentFormat format,
        string? ocrLanguage,
        CancellationToken cancellationToken);
}

public interface IDocumentExtractionService
{
    OcrAvailability GetOcrAvailability();

    Task<DocumentExtractionResult> ExtractAsync(
        Stream stream,
        string fileName,
        long declaredSize,
        string? ocrLanguage = null,
        CancellationToken cancellationToken = default);
}

