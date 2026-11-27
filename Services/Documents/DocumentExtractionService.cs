using Microsoft.Extensions.Options;
using StudyBuddy.Models;

namespace StudyBuddy.Services.Documents;

/// <summary>
/// Orchestrates bounded stream reading, file signature validation, extraction concurrency limits,
/// timeouts, cancellation, and format-specific text extraction without ever silently truncating text
/// or logging private user notes.
/// </summary>
public sealed class DocumentExtractionService : IDocumentExtractionService
{
    private const int MaxFileNameLength = 260;

    private readonly IEnumerable<ITextExtractor> _extractors;
    private readonly IOcrService _ocrService;
    private readonly IOptionsMonitor<DocumentExtractionOptions> _optionsMonitor;
    private readonly ILogger<DocumentExtractionService> _logger;
    private readonly SemaphoreSlim _concurrencyGate;

    public DocumentExtractionService(
        IEnumerable<ITextExtractor> extractors,
        IOcrService ocrService,
        IOptionsMonitor<DocumentExtractionOptions> optionsMonitor,
        ILogger<DocumentExtractionService> logger)
    {
        _extractors = extractors;
        _ocrService = ocrService;
        _optionsMonitor = optionsMonitor;
        _logger = logger;

        var maxConcurrent = Math.Clamp(optionsMonitor.CurrentValue.MaxConcurrentExtractions, 1, 16);
        _concurrencyGate = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    public OcrAvailability GetOcrAvailability() => _ocrService.GetAvailability();

    public async Task<DocumentExtractionResult> ExtractAsync(
        Stream stream,
        string fileName,
        long declaredSize,
        string? ocrLanguage = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var opts = _optionsMonitor.CurrentValue;
        var sanitizedName = SanitizeFileName(fileName);
        if (string.IsNullOrWhiteSpace(sanitizedName))
        {
            return DocumentExtractionResult.Fail("A valid file name is required.");
        }

        if (!FileSignatureValidator.TryDetectFormatFromExtension(sanitizedName, out var format))
        {
            return DocumentExtractionResult.Fail(
                "Unsupported file type. Supported formats: TXT, Markdown (.md), PDF, Word (.docx), PowerPoint (.pptx), and images (JPG, JPEG, PNG, WebP).",
                sanitizedName,
                declaredSize);
        }

        var maxBytes = opts.GetMaxBytesForFormat(format);
        if (declaredSize <= 0)
        {
            return DocumentExtractionResult.Fail("The selected file is empty.", sanitizedName, declaredSize, format);
        }

        if (declaredSize > maxBytes)
        {
            var maxMb = Math.Round(maxBytes / (1024.0 * 1024.0), 1);
            return DocumentExtractionResult.Fail(
                $"{DocumentExtractionResult.GetFormatDisplayName(format)} files cannot exceed {maxMb:0.#} MB.",
                sanitizedName,
                declaredSize,
                format);
        }

        var extractor = _extractors.FirstOrDefault(e => e.CanHandle(format));
        if (extractor is null)
        {
            return DocumentExtractionResult.Fail(
                $"No extractor is registered for {DocumentExtractionResult.GetFormatDisplayName(format)}.",
                sanitizedName,
                declaredSize,
                format);
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(opts.ExtractionTimeoutSeconds <= 0 ? 45 : opts.ExtractionTimeoutSeconds, 5, 300));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linkedCts.CancelAfter(timeout);

        var gateAcquired = false;
        try
        {
            await _concurrencyGate.WaitAsync(linkedCts.Token);
            gateAcquired = true;

            // Bounded stream read: read at most maxBytes + 1 to guard against lying declaredSize
            var readResult = await ReadBoundedBytesAsync(stream, maxBytes, linkedCts.Token);
            if (readResult.ExceededLimit)
            {
                var maxMb = Math.Round(maxBytes / (1024.0 * 1024.0), 1);
                return DocumentExtractionResult.Fail(
                    $"Uploaded content exceeds the {maxMb:0.#} MB limit for {DocumentExtractionResult.GetFormatDisplayName(format)}.",
                    sanitizedName,
                    declaredSize,
                    format);
            }

            var fileBytes = readResult.Bytes;
            if (fileBytes.Length == 0)
            {
                return DocumentExtractionResult.Fail("The uploaded file contains no content.", sanitizedName, 0, format);
            }

            var signatureError = FileSignatureValidator.ValidateSignature(fileBytes, sanitizedName, format);
            if (signatureError is not null)
            {
                return DocumentExtractionResult.Fail(signatureError, sanitizedName, fileBytes.Length, format);
            }

            var result = await extractor.ExtractAsync(fileBytes, sanitizedName, format, ocrLanguage, linkedCts.Token);
            if (!result.Succeeded)
            {
                return result;
            }

            // Validate extracted text length without ever silently truncating
            var minLen = Math.Max(1, opts.MinExtractedTextLength);
            var maxLen = Math.Max(minLen, opts.MaxExtractedTextLength);

            if (string.IsNullOrWhiteSpace(result.ExtractedText) || result.ExtractedText.Trim().Length < minLen)
            {
                return DocumentExtractionResult.Fail(
                    $"Extracted text is too short (minimum {minLen} characters). Ensure the document contains readable study notes.",
                    sanitizedName,
                    fileBytes.Length,
                    format,
                    result.Warnings);
            }

            if (result.ExtractedText.Length > maxLen)
            {
                return DocumentExtractionResult.Fail(
                    $"Extracted text is {result.ExtractedText.Length:N0} characters, which exceeds the {maxLen:N0}-character deck limit. Split the document into smaller sections rather than truncating content.",
                    sanitizedName,
                    fileBytes.Length,
                    format,
                    result.Warnings);
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Document extraction timed out after {TimeoutSeconds}s for format {Format}.",
                (int)timeout.TotalSeconds,
                format);
            return DocumentExtractionResult.Fail(
                $"Document extraction timed out after {(int)timeout.TotalSeconds} seconds. Try a smaller file or fewer scanned pages.",
                sanitizedName,
                declaredSize,
                format);
        }
        finally
        {
            if (gateAcquired)
            {
                _concurrencyGate.Release();
            }
        }
    }

    private static async Task<(byte[] Bytes, bool ExceededLimit)> ReadBoundedBytesAsync(
        Stream stream,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[32 * 1024];
        long totalRead = 0;

        while (true)
        {
            var bytesToRead = (int)Math.Min(chunk.Length, (maxBytes + 1) - totalRead);
            if (bytesToRead <= 0)
            {
                break;
            }

            var read = await stream.ReadAsync(chunk.AsMemory(0, bytesToRead), cancellationToken);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
            if (totalRead > maxBytes)
            {
                return (Array.Empty<byte>(), true);
            }

            buffer.Write(chunk, 0, read);
        }

        return (buffer.ToArray(), false);
    }

    private static string SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var justName = Path.GetFileName(fileName.Trim());
        return justName.Length > MaxFileNameLength ? justName[..MaxFileNameLength] : justName;
    }
}

