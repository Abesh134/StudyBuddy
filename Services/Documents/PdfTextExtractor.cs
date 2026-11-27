using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using StudyBuddy.Models;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Exceptions;

namespace StudyBuddy.Services.Documents;

public sealed partial class PdfTextExtractor : ITextExtractor
{
    private const int ScannedPageMinNativeChars = 25;

    private readonly IOcrService _ocrService;
    private readonly IPdfPageRenderer _pageRenderer;
    private readonly IOptionsMonitor<DocumentExtractionOptions> _optionsMonitor;

    public PdfTextExtractor(
        IOcrService ocrService,
        IPdfPageRenderer pageRenderer,
        IOptionsMonitor<DocumentExtractionOptions> optionsMonitor)
    {
        _ocrService = ocrService;
        _pageRenderer = pageRenderer;
        _optionsMonitor = optionsMonitor;
    }

    public bool CanHandle(SourceDocumentFormat format) =>
        format == SourceDocumentFormat.PdfDocument;

    public async Task<DocumentExtractionResult> ExtractAsync(
        byte[] fileBytes,
        string sanitizedFileName,
        SourceDocumentFormat format,
        string? ocrLanguage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var opts = _optionsMonitor.CurrentValue;

        PdfDocument document;
        try
        {
            document = PdfDocument.Open(fileBytes);
        }
        catch (PdfDocumentEncryptedException)
        {
            return DocumentExtractionResult.Fail(
                "This PDF is password-protected or encrypted. Please remove password protection before uploading.",
                sanitizedFileName,
                fileBytes.Length,
                format);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return DocumentExtractionResult.Fail(
                "The uploaded PDF is corrupted, malformed, or uses an unsupported PDF structure.",
                sanitizedFileName,
                fileBytes.Length,
                format);
        }

        using (document)
        {
            if (document.IsEncrypted)
            {
                return DocumentExtractionResult.Fail(
                    "Encrypted PDF documents are not supported. Please upload an unencrypted PDF.",
                    sanitizedFileName,
                    fileBytes.Length,
                    format);
            }

            var pageCount = document.NumberOfPages;
            if (pageCount <= 0)
            {
                return DocumentExtractionResult.Fail(
                    "The uploaded PDF contains no pages.",
                    sanitizedFileName,
                    fileBytes.Length,
                    format);
            }

            var maxPages = Math.Max(1, opts.MaxPdfPages);
            if (pageCount > maxPages)
            {
                return DocumentExtractionResult.Fail(
                    $"PDF has {pageCount} pages, which exceeds the configured limit of {maxPages} pages.",
                    sanitizedFileName,
                    fileBytes.Length,
                    format);
            }

            var warnings = new HashSet<string>(StringComparer.Ordinal);
            var rawPages = new List<string>(pageCount);
            var structuredPages = new List<string>(pageCount);
            var scannedPageNumbers = new List<int>();
            var usedOcr = false;
            var ocrPagesProcessed = 0;
            var maxOcrPages = Math.Max(0, opts.Ocr.MaxPdfOcrPages);

            for (var pageNumber = 1; pageNumber <= pageCount; pageNumber++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Page page;
                try
                {
                    page = document.GetPage(pageNumber);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return DocumentExtractionResult.Fail(
                        $"Failed to read page {pageNumber} of the PDF. The document may be damaged.",
                        sanitizedFileName,
                        fileBytes.Length,
                        format);
                }

                var rawPageText = ExtractRawPageLines(page);
                var hasImages = page.GetImages().Any();

                if (rawPageText.Length >= ScannedPageMinNativeChars)
                {
                    var structured = BuildLayoutAwarePageMarkdown(page, warnings);
                    rawPages.Add(rawPageText);
                    structuredPages.Add(structured);
                }
                else if (hasImages || rawPageText.Length == 0)
                {
                    scannedPageNumbers.Add(pageNumber);
                    rawPages.Add(string.Empty);
                    structuredPages.Add(string.Empty);
                }
                else
                {
                    rawPages.Add(rawPageText);
                    structuredPages.Add(rawPageText);
                }
            }

            if (scannedPageNumbers.Count > 0)
            {
                if (!opts.Ocr.Enabled)
                {
                    return DocumentExtractionResult.Fail(
                        $"Detected {scannedPageNumbers.Count} scanned or image-only PDF page(s), but local OCR is disabled in configuration.",
                        sanitizedFileName,
                        fileBytes.Length,
                        format);
                }

                if (scannedPageNumbers.Count > maxOcrPages)
                {
                    return DocumentExtractionResult.Fail(
                        $"This PDF has {scannedPageNumbers.Count} scanned page(s) requiring OCR, which exceeds the limit of {maxOcrPages} OCR pages per PDF.",
                        sanitizedFileName,
                        fileBytes.Length,
                        format);
                }

                var ocrAvailability = _ocrService.GetAvailability();
                if (!ocrAvailability.IsAvailable)
                {
                    return DocumentExtractionResult.Fail(
                        $"Detected {scannedPageNumbers.Count} scanned or image-only PDF page(s), but local OCR is unavailable: {ocrAvailability.StatusMessage}",
                        sanitizedFileName,
                        fileBytes.Length,
                        format);
                }

                foreach (var scannedPage in scannedPageNumbers)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    byte[] pngBytes;
                    try
                    {
                        pngBytes = await _pageRenderer.RenderPageToPngAsync(fileBytes, scannedPage, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        return DocumentExtractionResult.Fail(
                            $"Failed to render scanned PDF page {scannedPage} for OCR. No pages were silently skipped.",
                            sanitizedFileName,
                            fileBytes.Length,
                            format);
                    }

                    var ocrResult = await _ocrService.RecognizeImageAsync(pngBytes, ocrLanguage, cancellationToken);
                    if (!ocrResult.Succeeded)
                    {
                        return DocumentExtractionResult.Fail(
                            $"OCR failed on PDF page {scannedPage}: {ocrResult.ErrorMessage ?? "Unknown OCR error."}",
                            sanitizedFileName,
                            fileBytes.Length,
                            format);
                    }

                    usedOcr = true;
                    ocrPagesProcessed++;
                    var cleanOcr = ocrResult.Text.Trim();
                    rawPages[scannedPage - 1] = cleanOcr;
                    structuredPages[scannedPage - 1] = cleanOcr;
                }

                warnings.Add(
                    $"Applied local OCR to {ocrPagesProcessed} scanned PDF page(s). Please review extracted text carefully — scanned layouts, formulas, or handwriting may contain recognition errors.");
            }

            var rawCombined = CombinePages(rawPages, pageCount, useMarkdownSeparator: false);
            var structuredCombined = CombinePages(structuredPages, pageCount, useMarkdownSeparator: true);

            if (string.IsNullOrWhiteSpace(structuredCombined))
            {
                return DocumentExtractionResult.Fail(
                    "No readable text could be extracted from this PDF.",
                    sanitizedFileName,
                    fileBytes.Length,
                    format);
            }

            if (ContainsMathSymbolsOrLatex(structuredCombined))
            {
                warnings.Add("Mathematical expressions or symbols were detected. Verify formula formatting in the preview before saving.");
            }

            return DocumentExtractionResult.Ok(
                extractedText: structuredCombined,
                sanitizedFileName: sanitizedFileName,
                fileSizeBytes: fileBytes.Length,
                format: format,
                pageOrSlideCount: pageCount,
                usedOcr: usedOcr,
                ocrItemsProcessed: ocrPagesProcessed,
                warnings: warnings.ToList(),
                rawExtractedText: rawCombined,
                structuredMarkdown: structuredCombined);
        }
    }

    private static string CombinePages(List<string> pages, int pageCount, bool useMarkdownSeparator)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < pages.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(pages[i]))
            {
                continue;
            }

            if (sb.Length > 0)
            {
                sb.Append(useMarkdownSeparator ? "\n\n---\n\n" : "\n\n");
            }

            if (pageCount > 1)
            {
                sb.Append(useMarkdownSeparator ? $"### Page {i + 1}\n\n" : $"--- Page {i + 1} ---\n");
            }

            sb.Append(pages[i]);
        }

        return sb.ToString().Trim();
    }

    private static string ExtractRawPageLines(Page page)
    {
        var words = page.GetWords().ToList();
        if (words.Count == 0)
        {
            return (page.Text ?? string.Empty).Trim();
        }

        var lines = GroupWordsIntoVisualLines(words);
        return string.Join("\n", lines.Select(l => l.Text)).Trim();
    }

    private static string BuildLayoutAwarePageMarkdown(Page page, HashSet<string> warnings)
    {
        var words = page.GetWords().ToList();
        if (words.Count == 0)
        {
            return (page.Text ?? string.Empty).Trim();
        }

        var medianFontSize = ComputeMedianFontSize(words);

        // Detect two-column academic PDF layout using vertical whitespace gutter
        if (TrySplitTwoColumns(words, page.Width, out var leftWords, out var rightWords))
        {
            warnings.Add("Multi-column PDF layout detected; columns were ordered left-to-right. Verify reading order in the preview.");
            var leftMd = FormatWordColumnToMarkdown(leftWords, medianFontSize, warnings);
            var rightMd = FormatWordColumnToMarkdown(rightWords, medianFontSize, warnings);
            return string.Join("\n\n", new[] { leftMd, rightMd }.Where(s => !string.IsNullOrWhiteSpace(s)));
        }

        return FormatWordColumnToMarkdown(words, medianFontSize, warnings);
    }

    private static bool TrySplitTwoColumns(
        List<Word> words,
        double pageWidth,
        out List<Word> leftColumn,
        out List<Word> rightColumn)
    {
        leftColumn = new List<Word>();
        rightColumn = new List<Word>();

        if (words.Count < 16 || pageWidth < 300)
        {
            return false;
        }

        var midMin = pageWidth * 0.42;
        var midMax = pageWidth * 0.58;
        var midX = pageWidth * 0.50;

        var crossingCount = 0;
        foreach (var w in words)
        {
            if (w.BoundingBox.Left < midMin && w.BoundingBox.Right > midMax)
            {
                crossingCount++;
            }
            else if (w.BoundingBox.Right <= midX + 6)
            {
                leftColumn.Add(w);
            }
            else if (w.BoundingBox.Left >= midX - 6)
            {
                rightColumn.Add(w);
            }
            else
            {
                crossingCount++;
            }
        }

        // Only treat as two-column when both sides have substantial words and almost none cross the center gutter
        var leftRatio = (double)leftColumn.Count / words.Count;
        var rightRatio = (double)rightColumn.Count / words.Count;
        var crossRatio = (double)crossingCount / words.Count;

        if (leftColumn.Count >= 6 && rightColumn.Count >= 6 &&
            leftRatio >= 0.25 && rightRatio >= 0.25 &&
            crossRatio <= 0.08)
        {
            // Verify lines on left and right actually overlap vertically (not just a table or indented list)
            var leftLines = GroupWordsIntoVisualLines(leftColumn);
            var rightLines = GroupWordsIntoVisualLines(rightColumn);
            if (leftLines.Count >= 3 && rightLines.Count >= 3)
            {
                // Avoid misclassifying a 2-column table (where every left line is very short) as 2-column prose
                var avgLeftWords = leftLines.Average(l => l.Words.Count);
                var avgRightWords = rightLines.Average(l => l.Words.Count);
                if (avgLeftWords >= 3.0 && avgRightWords >= 3.0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string FormatWordColumnToMarkdown(
        List<Word> words,
        double medianFontSize,
        HashSet<string> warnings)
    {
        var lines = GroupWordsIntoVisualLines(words);
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var lineHeights = lines.Select(l => l.Height).Where(h => h > 0).OrderBy(h => h).ToList();
        var medianLineHeight = lineHeights.Count > 0 ? lineHeights[lineHeights.Count / 2] : 12.0;

        var blocks = new List<string>();
        var currentParagraph = new StringBuilder();

        void FlushParagraph()
        {
            if (currentParagraph.Length > 0)
            {
                blocks.Add(currentParagraph.ToString().Trim());
                currentParagraph.Clear();
            }
        }

        for (var i = 0; i < lines.Count; i++)
        {
            // 1. Check for a reliable PDF table starting at line i
            if (TryExtractTableBlock(lines, i, out var tableMarkdown, out var consumedRows))
            {
                FlushParagraph();
                blocks.Add(tableMarkdown);
                i += consumedRows - 1;
                continue;
            }

            var line = lines[i];
            var text = line.Text.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            // 2. Heading detection based on font size relative to median body font size
            var isHeading = medianFontSize > 0 &&
                            line.AvgFontSize >= medianFontSize * 1.28 &&
                            text.Length <= 120 &&
                            !text.EndsWith('.');

            if (isHeading)
            {
                FlushParagraph();
                var prefix = line.AvgFontSize >= medianFontSize * 1.55 ? "# " : "## ";
                blocks.Add(prefix + text);
                continue;
            }

            // 3. List items (unordered or ordered)
            if (BulletLineRegex().IsMatch(text))
            {
                FlushParagraph();
                var cleanItem = BulletLineRegex().Replace(text, "- ");
                blocks.Add(cleanItem);
                continue;
            }

            if (OrderedListLineRegex().IsMatch(text))
            {
                FlushParagraph();
                blocks.Add(text);
                continue;
            }

            // 4. Paragraph continuation vs new paragraph based on vertical gap
            if (currentParagraph.Length == 0)
            {
                currentParagraph.Append(text);
            }
            else
            {
                var prev = lines[i - 1];
                var verticalGap = prev.Bottom - line.Top;
                var paragraphGapThreshold = Math.Max(medianLineHeight * 1.25, medianFontSize * 1.15);
                var startsNewParagraph = verticalGap > paragraphGapThreshold;

                if (startsNewParagraph)
                {
                    FlushParagraph();
                    currentParagraph.Append(text);
                }
                else
                {
                    // Rejoin hyphenated word wraps (e.g. "photo-\nsynthesis" -> "photosynthesis")
                    var prevText = currentParagraph.ToString();
                    if (prevText.EndsWith('-') && !prevText.EndsWith(" -") && char.IsLetter(text[0]))
                    {
                        currentParagraph.Length -= 1;
                        currentParagraph.Append(text);
                    }
                    else
                    {
                        currentParagraph.Append(' ').Append(text);
                    }
                }
            }
        }

        FlushParagraph();
        return string.Join("\n\n", blocks);
    }

    /// <summary>
    /// Detects consecutive PDF lines whose words split into consistent horizontal column gaps
    /// and formats them as a GFM pipe table without ever inventing missing cells.
    /// </summary>
    private static bool TryExtractTableBlock(
        List<VisualLine> lines,
        int startIndex,
        out string tableMarkdown,
        out int consumedRows)
    {
        tableMarkdown = string.Empty;
        consumedRows = 0;

        var firstCells = SplitLineIntoTableCells(lines[startIndex]);
        if (firstCells.Count < 2)
        {
            return false;
        }

        var expectedCols = firstCells.Count;
        var rows = new List<List<string>> { firstCells };

        for (var j = startIndex + 1; j < lines.Count; j++)
        {
            var nextCells = SplitLineIntoTableCells(lines[j]);
            if (nextCells.Count != expectedCols)
            {
                break;
            }

            rows.Add(nextCells);
        }

        if (rows.Count < 2)
        {
            return false;
        }

        var sb = new StringBuilder();
        sb.Append("| ").Append(string.Join(" | ", rows[0].Select(EscapePipe))).Append(" |\n");
        sb.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", expectedCols))).Append(" |");

        for (var r = 1; r < rows.Count; r++)
        {
            sb.Append("\n| ").Append(string.Join(" | ", rows[r].Select(EscapePipe))).Append(" |");
        }

        tableMarkdown = sb.ToString();
        consumedRows = rows.Count;
        return true;
    }

    private static List<string> SplitLineIntoTableCells(VisualLine line)
    {
        var ordered = line.Words.OrderBy(w => w.BoundingBox.Left).ToList();
        if (ordered.Count < 2)
        {
            return new List<string>();
        }

        var gapThreshold = Math.Max(18.0, line.AvgFontSize * 2.1);
        var cells = new List<string>();
        var currentCell = new StringBuilder(ordered[0].Text);

        for (var i = 1; i < ordered.Count; i++)
        {
            var gap = ordered[i].BoundingBox.Left - ordered[i - 1].BoundingBox.Right;
            if (gap >= gapThreshold)
            {
                cells.Add(currentCell.ToString().Trim());
                currentCell.Clear();
                currentCell.Append(ordered[i].Text);
            }
            else
            {
                currentCell.Append(' ').Append(ordered[i].Text);
            }
        }

        cells.Add(currentCell.ToString().Trim());
        return cells;
    }

    private static List<VisualLine> GroupWordsIntoVisualLines(List<Word> words)
    {
        var rawGroups = new List<List<Word>>();
        foreach (var word in words)
        {
            var y = word.BoundingBox.Bottom;
            var group = rawGroups.FirstOrDefault(g => Math.Abs(g[0].BoundingBox.Bottom - y) <= 3.5);
            if (group is null)
            {
                rawGroups.Add(new List<Word> { word });
            }
            else
            {
                group.Add(word);
            }
        }

        return rawGroups
            .Select(g =>
            {
                var ordered = g.OrderBy(w => w.BoundingBox.Left).ToList();
                var text = string.Join(" ", ordered.Select(w => w.Text));
                var bottom = ordered.Average(w => w.BoundingBox.Bottom);
                var top = ordered.Average(w => w.BoundingBox.Top);
                var avgFont = ordered
                    .SelectMany(w => w.Letters)
                    .Select(l => (double)l.PointSize)
                    .DefaultIfEmpty(11.0)
                    .Average();
                return new VisualLine(ordered, text, bottom, top, Math.Max(1.0, top - bottom), avgFont);
            })
            .OrderByDescending(l => l.Bottom)
            .ToList();
    }

    private static double ComputeMedianFontSize(List<Word> words)
    {
        var sizes = words
            .SelectMany(w => w.Letters)
            .Select(l => (double)l.PointSize)
            .Where(s => s > 0)
            .OrderBy(s => s)
            .ToList();

        return sizes.Count == 0 ? 11.0 : sizes[sizes.Count / 2];
    }

    private static bool ContainsMathSymbolsOrLatex(string text) =>
        text.Contains("$$", StringComparison.Ordinal) ||
        text.Contains(@"\frac", StringComparison.Ordinal) ||
        text.Contains(@"\sqrt", StringComparison.Ordinal) ||
        text.Contains(@"\sum", StringComparison.Ordinal) ||
        text.Contains(@"\int", StringComparison.Ordinal) ||
        MathSymbolRegex().IsMatch(text);

    private static string EscapePipe(string cell) => cell.Replace("|", @"\|");

    private sealed record VisualLine(
        List<Word> Words,
        string Text,
        double Bottom,
        double Top,
        double Height,
        double AvgFontSize);

    [GeneratedRegex(@"^[\u2022\u2023\u25E6\u2043\u2219\-\*]\s+", RegexOptions.Compiled)]
    private static partial Regex BulletLineRegex();

    [GeneratedRegex(@"^\d{1,3}[\.\)]\s+", RegexOptions.Compiled)]
    private static partial Regex OrderedListLineRegex();

    [GeneratedRegex(@"[∑∫√∞≤≥≠±×÷∂∇αβγδεθλμπσωΔΣΩ]", RegexOptions.Compiled)]
    private static partial Regex MathSymbolRegex();
}
