using PDFtoImage;

namespace StudyBuddy.Services.Documents;

/// <summary>
/// Renders individual PDF pages to PNG bytes using PDFtoImage (PDFium) for local OCR fallback.
/// </summary>
public sealed class PdfToImagePageRenderer : IPdfPageRenderer
{
    public Task<byte[]> RenderPageToPngAsync(
        byte[] pdfBytes,
        int oneBasedPageNumber,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var pdfStream = new MemoryStream(pdfBytes, writable: false);
            using var outputStream = new MemoryStream();
            var zeroBasedIndex = Math.Max(0, oneBasedPageNumber - 1);
#pragma warning disable CA1416 // Platform compatibility checked at runtime; PDFtoImage ships Windows/Linux/macOS PDFium runtimes.
            Conversion.SavePng(outputStream, pdfStream, page: zeroBasedIndex, options: new RenderOptions(Dpi: 220));
#pragma warning restore CA1416
            return outputStream.ToArray();
        }, cancellationToken);
    }
}

