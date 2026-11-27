using Microsoft.Extensions.Options;
using SkiaSharp;
using StudyBuddy.Models;

namespace StudyBuddy.Services.Documents;

public sealed class ImageTextExtractor : ITextExtractor
{
    private readonly IOcrService _ocrService;
    private readonly IOptionsMonitor<DocumentExtractionOptions> _optionsMonitor;

    public ImageTextExtractor(
        IOcrService ocrService,
        IOptionsMonitor<DocumentExtractionOptions> optionsMonitor)
    {
        _ocrService = ocrService;
        _optionsMonitor = optionsMonitor;
    }

    public bool CanHandle(SourceDocumentFormat format) =>
        format == SourceDocumentFormat.ImageOcr;

    public async Task<DocumentExtractionResult> ExtractAsync(
        byte[] fileBytes,
        string sanitizedFileName,
        SourceDocumentFormat format,
        string? ocrLanguage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var opts = _optionsMonitor.CurrentValue;

        // 1. Validate decoded image format, dimensions, and pixel count from headers BEFORE allocating pixel buffer
        var (pngBytes, decodeError) = PreprocessImageToPng(fileBytes, opts);
        if (decodeError is not null || pngBytes is null)
        {
            return DocumentExtractionResult.Fail(
                decodeError ?? "Unable to decode the uploaded image.",
                sanitizedFileName,
                fileBytes.Length,
                format);
        }

        // 2. Run local OCR
        var ocrResult = await _ocrService.RecognizeImageAsync(pngBytes, ocrLanguage, cancellationToken);
        if (!ocrResult.Succeeded)
        {
            return DocumentExtractionResult.Fail(
                ocrResult.ErrorMessage ?? "OCR failed to extract text from this image.",
                sanitizedFileName,
                fileBytes.Length,
                format);
        }

        if (string.IsNullOrWhiteSpace(ocrResult.Text))
        {
            return DocumentExtractionResult.Fail(
                "No readable text was detected in this image. Ensure the image is clear and well-lit, or paste your notes manually.",
                sanitizedFileName,
                fileBytes.Length,
                format);
        }

        var warnings = new List<string>
        {
            "Text was extracted via local OCR. Please review carefully before saving — handwritten notes (especially Bengali handwriting), mathematical formulas, and stylized diagrams may contain recognition errors."
        };

        var nonAlphaRatio = ComputeSuspiciousSymbolRatio(ocrResult.Text);
        if (nonAlphaRatio > 0.25)
        {
            warnings.Add("OCR output contains a high proportion of unusual symbols or broken fragments. Use the Edit Content tab to correct recognition issues before saving.");
        }

        return DocumentExtractionResult.Ok(
            extractedText: ocrResult.Text,
            sanitizedFileName: sanitizedFileName,
            fileSizeBytes: fileBytes.Length,
            format: format,
            pageOrSlideCount: 1,
            usedOcr: true,
            ocrItemsProcessed: 1,
            warnings: warnings,
            rawExtractedText: ocrResult.Text,
            structuredMarkdown: ocrResult.Text);
    }

    private static double ComputeSuspiciousSymbolRatio(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var totalNonSpace = 0;
        var suspicious = 0;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch)) continue;
            totalNonSpace++;
            if (!char.IsLetterOrDigit(ch) && ch is not ('.' or ',' or ';' or ':' or '?' or '!' or '-' or '(' or ')' or '/' or '\'' or '"'))
            {
                suspicious++;
            }
        }

        return totalNonSpace == 0 ? 0 : (double)suspicious / totalNonSpace;
    }

    internal static (byte[]? PngBytes, string? ErrorMessage) PreprocessImageToPng(
        byte[] rawBytes,
        DocumentExtractionOptions opts)
    {
        try
        {
            using var data = SKData.CreateCopy(rawBytes);
            using var codec = SKCodec.Create(data);
            if (codec is null)
            {
                return (null, "The uploaded image is corrupted or not a supported JPG, PNG, or WebP image.");
            }

            if (codec.EncodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp))
            {
                return (null, "Unsupported image encoding. Only JPG, JPEG, PNG, and WebP images are supported.");
            }

            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0)
            {
                return (null, "The uploaded image has invalid dimensions.");
            }

            var maxW = Math.Max(100, opts.MaxImageWidth);
            var maxH = Math.Max(100, opts.MaxImageHeight);
            var maxPixels = Math.Max(100_000L, opts.MaxImagePixels);
            var totalPixels = (long)info.Width * info.Height;

            if (info.Width > maxW || info.Height > maxH || totalPixels > maxPixels)
            {
                return (null, $"Image dimensions ({info.Width}x{info.Height}) exceed the safety limit ({maxW}x{maxH}, max {maxPixels / 1_000_000:N0} MP).");
            }

            var decodeInfo = new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var rawBitmap = new SKBitmap(decodeInfo);
            var result = codec.GetPixels(decodeInfo, rawBitmap.GetPixels());
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
            {
                return (null, "Unable to decode the uploaded image. It may be corrupted.");
            }

            using var oriented = ApplyExifOrientation(rawBitmap, codec.EncodedOrigin);

            // Upscale low-resolution screenshots (up to 2x within safety limits) for sharper local OCR
            var targetWidth = oriented.Width;
            var targetHeight = oriented.Height;
            if (targetWidth < 1000 &&
                targetWidth * 2 <= maxW &&
                targetHeight * 2 <= maxH &&
                (long)(targetWidth * 2) * (targetHeight * 2) <= maxPixels)
            {
                targetWidth *= 2;
                targetHeight *= 2;
            }

            // Apply grayscale + gentle contrast normalization for cleaner printed English & Bengali OCR
            using var surface = SKSurface.Create(new SKImageInfo(targetWidth, targetHeight, SKColorType.Rgba8888, SKAlphaType.Premul));
            if (surface is null)
            {
                return (null, "Unable to allocate image buffer for preprocessing.");
            }

            var canvas = surface.Canvas;
            canvas.Clear(SKColors.White);

            const float c = 1.25f;
            const float t = (1f - c) * 0.5f;
            float[] grayContrastMatrix =
            {
                0.299f * c, 0.587f * c, 0.114f * c, 0, t,
                0.299f * c, 0.587f * c, 0.114f * c, 0, t,
                0.299f * c, 0.587f * c, 0.114f * c, 0, t,
                0,          0,          0,          1, 0
            };

            using var paint = new SKPaint
            {
                ColorFilter = SKColorFilter.CreateColorMatrix(grayContrastMatrix)
            };
            using var orientedImg = SKImage.FromBitmap(oriented);
            var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
            canvas.DrawImage(orientedImg, new SKRect(0, 0, targetWidth, targetHeight), sampling, paint);
            canvas.Flush();

            using var snapshot = surface.Snapshot();
            using var encoded = snapshot.Encode(SKEncodedImageFormat.Png, 100);
            if (encoded is null)
            {
                return (null, "Unable to encode preprocessed image for OCR.");
            }

            return (encoded.ToArray(), null);
        }
        catch (Exception)
        {
            return (null, "Unable to decode the uploaded image. It may be corrupted or unsupported.");
        }
    }

    private static SKBitmap ApplyExifOrientation(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            return source.Copy();
        }

        var swap = origin is SKEncodedOrigin.RightTop or SKEncodedOrigin.LeftBottom
            or SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightBottom;

        var width = swap ? source.Height : source.Width;
        var height = swap ? source.Width : source.Height;

        var rotated = new SKBitmap(width, height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(rotated);

        switch (origin)
        {
            case SKEncodedOrigin.RightTop:
                canvas.Translate(width, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.BottomRight:
                canvas.Translate(width, height);
                canvas.RotateDegrees(180);
                break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, height);
                canvas.RotateDegrees(270);
                break;
            case SKEncodedOrigin.TopRight:
                canvas.Scale(-1, 1, width / 2f, height / 2f);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Scale(1, -1, width / 2f, height / 2f);
                break;
        }

        using var sourceImg = SKImage.FromBitmap(source);
        canvas.DrawImage(sourceImg, 0, 0, SKSamplingOptions.Default);
        canvas.Flush();
        return rotated;
    }
}
