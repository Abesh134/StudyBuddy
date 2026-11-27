using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Drawing;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using Microsoft.Extensions.Options;
using SkiaSharp;
using StudyBuddy.Models;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace StudyBuddy.Services.Documents;

public sealed class PowerPointTextExtractor : ITextExtractor
{
    private readonly IOcrService _ocrService;
    private readonly IOptionsMonitor<DocumentExtractionOptions> _optionsMonitor;

    public PowerPointTextExtractor(
        IOcrService ocrService,
        IOptionsMonitor<DocumentExtractionOptions> optionsMonitor)
    {
        _ocrService = ocrService;
        _optionsMonitor = optionsMonitor;
    }

    public bool CanHandle(SourceDocumentFormat format) =>
        format == SourceDocumentFormat.PowerPointPresentation;

    public async Task<DocumentExtractionResult> ExtractAsync(
        byte[] fileBytes,
        string sanitizedFileName,
        SourceDocumentFormat format,
        string? ocrLanguage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var opts = _optionsMonitor.CurrentValue;

        var safetyError = FileSignatureValidator.ValidateOpenXmlPackageSafety(
            fileBytes,
            "ppt/presentation.xml",
            opts);
        if (safetyError is not null)
        {
            return DocumentExtractionResult.Fail(safetyError, sanitizedFileName, fileBytes.Length, format);
        }

        try
        {
            using var ms = new MemoryStream(fileBytes, writable: false);
            var openSettings = new OpenSettings
            {
                AutoSave = false,
                MarkupCompatibilityProcessSettings = new MarkupCompatibilityProcessSettings(
                    MarkupCompatibilityProcessMode.NoProcess,
                    FileFormatVersions.Office2007)
            };

            using var doc = PresentationDocument.Open(ms, false, openSettings);
            var presPart = doc.PresentationPart;
            var slideIdList = presPart?.Presentation?.SlideIdList;
            if (presPart is null || slideIdList is null)
            {
                return DocumentExtractionResult.Fail(
                    "The PowerPoint presentation contains no readable slide sequence.",
                    sanitizedFileName,
                    fileBytes.Length,
                    format);
            }

            // Preserve original presentation slide order via SlideIdList relationship sequence
            var orderedSlideIds = slideIdList.Elements<SlideId>()
                .Select(s => s.RelationshipId?.Value)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();

            if (orderedSlideIds.Count == 0)
            {
                return DocumentExtractionResult.Fail(
                    "The PowerPoint presentation contains no slides.",
                    sanitizedFileName,
                    fileBytes.Length,
                    format);
            }

            var maxSlides = Math.Max(1, opts.MaxPowerPointSlides);
            if (orderedSlideIds.Count > maxSlides)
            {
                return DocumentExtractionResult.Fail(
                    $"Presentation has {orderedSlideIds.Count} slides, which exceeds the configured limit of {maxSlides} slides.",
                    sanitizedFileName,
                    fileBytes.Length,
                    format);
            }

            var warnings = new List<string>();
            var hasVisualOnlyElements = false;
            var ocrUsed = false;
            var ocrProcessed = 0;
            var maxOcrImages = Math.Max(0, opts.Ocr.MaxImagesPerDocument);
            var ocrAvailability = opts.Ocr.Enabled ? _ocrService.GetAvailability() : null;
            var skippedOcrDueToUnavailable = false;
            var skippedOcrDueToLimit = false;

            var rawSb = new StringBuilder();
            var mdSb = new StringBuilder();
            var presentationTitle = doc.PackageProperties?.Title?.Trim();
            if (!string.IsNullOrWhiteSpace(presentationTitle))
            {
                rawSb.Append(presentationTitle).Append("\n\n");
                mdSb.Append("# ").Append(presentationTitle).Append("\n\n");
            }

            var slideNumber = 0;
            foreach (var relId in orderedSlideIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                slideNumber++;

                if (presPart.GetPartById(relId!) is not SlidePart slidePart || slidePart.Slide is null)
                {
                    continue;
                }

                var (slideTitle, bodyLines, tableBlocks, hasVisuals) = ExtractSlideContent(slidePart.Slide);
                if (hasVisuals)
                {
                    hasVisualOnlyElements = true;
                }

                var speakerNotes = ExtractSpeakerNotes(slidePart);

                // Bounded OCR on embedded content images (skipping slide-master/layout decorative backgrounds)
                var imageOcrLines = new List<string>();
                var embeddedImages = slidePart.ImageParts.ToList();
                if (embeddedImages.Count > 0 && opts.Ocr.Enabled)
                {
                    if (ocrAvailability?.IsAvailable != true)
                    {
                        skippedOcrDueToUnavailable = true;
                    }
                    else
                    {
                        foreach (var imgPart in embeddedImages)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (ocrProcessed >= maxOcrImages)
                            {
                                skippedOcrDueToLimit = true;
                                break;
                            }

                            var ocrText = await TryOcrEmbeddedImageAsync(imgPart, opts, ocrLanguage, cancellationToken);
                            if (!string.IsNullOrWhiteSpace(ocrText))
                            {
                                imageOcrLines.Add(ocrText);
                                ocrUsed = true;
                                ocrProcessed++;
                            }
                        }
                    }
                }

                var heading = string.IsNullOrWhiteSpace(slideTitle)
                    ? $"Slide {slideNumber}"
                    : $"Slide {slideNumber} — {slideTitle}";

                var rawBlock = new StringBuilder();
                rawBlock.Append(heading);
                foreach (var line in bodyLines) rawBlock.Append('\n').Append(line);
                foreach (var table in tableBlocks) rawBlock.Append("\n\n").Append(table.Raw);
                if (imageOcrLines.Count > 0) rawBlock.Append("\n\nImage Text (OCR):\n").Append(string.Join("\n", imageOcrLines));
                if (!string.IsNullOrWhiteSpace(speakerNotes)) rawBlock.Append("\n\nSpeaker Notes:\n").Append(speakerNotes);

                var mdBlock = new StringBuilder();
                mdBlock.Append("## ").Append(heading);
                if (bodyLines.Count > 0) mdBlock.Append("\n\n").Append(string.Join("\n", bodyLines));
                foreach (var table in tableBlocks) mdBlock.Append("\n\n").Append(table.Markdown);
                if (imageOcrLines.Count > 0) mdBlock.Append("\n\n### Image Text (OCR)\n\n").Append(string.Join("\n", imageOcrLines));
                if (!string.IsNullOrWhiteSpace(speakerNotes)) mdBlock.Append("\n\n### Speaker Notes\n\n").Append(speakerNotes);

                if (rawSb.Length > 0) rawSb.Append("\n\n");
                rawSb.Append(rawBlock);

                if (mdSb.Length > 0) mdSb.Append("\n\n---\n\n");
                mdSb.Append(mdBlock);
            }

            if (hasVisualOnlyElements)
            {
                warnings.Add("Charts, SmartArt, diagrams, or mathematical equations were detected in this presentation. Only visible text was extracted; visual semantics cannot be interpreted automatically.");
            }

            if (ocrUsed)
            {
                warnings.Add($"Applied local OCR to {ocrProcessed} embedded slide image(s). Please review extracted image text before saving.");
            }
            else if (skippedOcrDueToUnavailable)
            {
                warnings.Add("Embedded images were found in slides, but local OCR is not installed/configured on this machine, so image text was not extracted.");
            }

            if (skippedOcrDueToLimit)
            {
                warnings.Add($"OCR was bounded to the configured limit of {maxOcrImages} embedded images.");
            }

            var structured = mdSb.ToString().Trim();
            var raw = rawSb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(structured))
            {
                return DocumentExtractionResult.Fail(
                    "No readable text could be extracted from this PowerPoint presentation.",
                    sanitizedFileName,
                    fileBytes.Length,
                    format);
            }

            return DocumentExtractionResult.Ok(
                extractedText: structured,
                sanitizedFileName: sanitizedFileName,
                fileSizeBytes: fileBytes.Length,
                format: format,
                pageOrSlideCount: orderedSlideIds.Count,
                usedOcr: ocrUsed,
                ocrItemsProcessed: ocrProcessed,
                warnings: warnings,
                rawExtractedText: raw,
                structuredMarkdown: structured);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DocumentExtractionResult.Fail(
                "The uploaded PowerPoint (.pptx) presentation is corrupted or malformed.",
                sanitizedFileName,
                fileBytes.Length,
                format);
        }
    }

    private static (string? Title, List<string> BodyLines, List<(string Raw, string Markdown)> Tables, bool HasVisualElements) ExtractSlideContent(Slide slide)
    {
        string? title = null;
        var bodyLines = new List<string>();
        var tables = new List<(string Raw, string Markdown)>();
        var hasVisuals = false;

        var shapeTree = slide.CommonSlideData?.ShapeTree;
        if (shapeTree is null)
        {
            return (null, bodyLines, tables, false);
        }

        // Detect charts, diagrams (SmartArt), or math elements
        if (slide.Descendants<A.GraphicData>().Any(g =>
                (g.Uri?.Value?.Contains("chart", StringComparison.OrdinalIgnoreCase) ?? false) ||
                (g.Uri?.Value?.Contains("diagram", StringComparison.OrdinalIgnoreCase) ?? false)))
        {
            hasVisuals = true;
        }

        foreach (var shape in shapeTree.Descendants<P.Shape>())
        {
            var placeholderType = shape.NonVisualShapeProperties?
                .ApplicationNonVisualDrawingProperties?
                .PlaceholderShape?.Type?.Value;

            // Skip slide number / footer / date placeholders
            if (placeholderType == PlaceholderValues.SlideNumber ||
                placeholderType == PlaceholderValues.Footer ||
                placeholderType == PlaceholderValues.DateAndTime)
            {
                continue;
            }

            var paragraphs = ExtractDrawingParagraphs(shape.TextBody);
            if (paragraphs.Count == 0)
            {
                continue;
            }

            if (title is null &&
                (placeholderType == PlaceholderValues.Title ||
                 placeholderType == PlaceholderValues.CenteredTitle ||
                 placeholderType == PlaceholderValues.SubTitle))
            {
                title = string.Join(" ", paragraphs);
            }
            else
            {
                bodyLines.AddRange(paragraphs);
            }
        }

        foreach (var table in shapeTree.Descendants<A.Table>())
        {
            var rows = new List<List<string>>();
            foreach (var row in table.Elements<A.TableRow>())
            {
                var cellTexts = row.Elements<A.TableCell>()
                    .Select(c => string.Join(" ", ExtractDrawingParagraphs(c.TextBody)))
                    .ToList();
                if (cellTexts.Any(t => !string.IsNullOrWhiteSpace(t)))
                {
                    rows.Add(cellTexts);
                }
            }

            if (rows.Count > 0)
            {
                var raw = string.Join("\n", rows.Select(r => string.Join(" | ", r)));
                var maxCols = rows.Max(r => r.Count);
                List<string> Pad(List<string> r)
                {
                    var copy = r.Select(c => c.Replace("|", @"\|")).ToList();
                    while (copy.Count < maxCols) copy.Add(string.Empty);
                    return copy;
                }

                var md = new StringBuilder();
                md.Append("| ").Append(string.Join(" | ", Pad(rows[0]))).Append(" |\n");
                md.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", maxCols))).Append(" |");
                for (var i = 1; i < rows.Count; i++)
                {
                    md.Append("\n| ").Append(string.Join(" | ", Pad(rows[i]))).Append(" |");
                }

                tables.Add((raw, md.ToString()));
            }
        }

        return (title, bodyLines, tables, hasVisuals);
    }

    private static List<string> ExtractDrawingParagraphs(OpenXmlElement? textBody)
    {
        var lines = new List<string>();
        if (textBody is null)
        {
            return lines;
        }

        foreach (var para in textBody.Elements<A.Paragraph>())
        {
            var text = string.Concat(para.Descendants<A.Text>().Select(t => t.Text)).Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var lvl = para.ParagraphProperties?.Level?.Value ?? 0;
            var hasBullet = para.ParagraphProperties?.GetFirstChild<A.CharacterBullet>() is not null ||
                            para.ParagraphProperties?.GetFirstChild<A.AutoNumberedBullet>() is not null ||
                            lvl > 0;

            if (hasBullet)
            {
                var indent = new string(' ', Math.Clamp(lvl, 0, 4) * 2);
                lines.Add($"{indent}- {text}");
            }
            else
            {
                lines.Add(text);
            }
        }

        return lines;
    }

    private static string ExtractSpeakerNotes(SlidePart slidePart)
    {
        var notesSlide = slidePart.NotesSlidePart?.NotesSlide;
        if (notesSlide is null)
        {
            return string.Empty;
        }

        var lines = new List<string>();
        foreach (var shape in notesSlide.Descendants<P.Shape>())
        {
            var phType = shape.NonVisualShapeProperties?
                .ApplicationNonVisualDrawingProperties?
                .PlaceholderShape?.Type?.Value;

            if (phType == PlaceholderValues.SlideNumber || phType == PlaceholderValues.SlideImage)
            {
                continue;
            }

            lines.AddRange(ExtractDrawingParagraphs(shape.TextBody));
        }

        return string.Join("\n", lines).Trim();
    }

    private async Task<string?> TryOcrEmbeddedImageAsync(
        ImagePart imagePart,
        DocumentExtractionOptions opts,
        string? ocrLanguage,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var rawStream = imagePart.GetStream(FileMode.Open, FileAccess.Read);
            using var ms = new MemoryStream();
            var maxImgBytes = Math.Max(1024L, opts.MaxImageBytes);
            var buffer = new byte[16 * 1024];
            long total = 0;

            while (true)
            {
                var read = await rawStream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > maxImgBytes)
                {
                    return null;
                }

                ms.Write(buffer, 0, read);
            }

            if (total < 256)
            {
                // Skip tiny decorative icons/bullets
                return null;
            }

            var rawImgBytes = ms.ToArray();
            using (var data = SKData.CreateCopy(rawImgBytes))
            using (var codec = SKCodec.Create(data))
            {
                if (codec is null || codec.Info.Width < 48 || codec.Info.Height < 24)
                {
                    return null;
                }
            }

            var (pngBytes, error) = ImageTextExtractor.PreprocessImageToPng(rawImgBytes, opts);
            if (error is not null || pngBytes is null)
            {
                return null;
            }

            var ocrResult = await _ocrService.RecognizeImageAsync(pngBytes, ocrLanguage, cancellationToken);
            return ocrResult.Succeeded && !string.IsNullOrWhiteSpace(ocrResult.Text)
                ? ocrResult.Text.Trim()
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
