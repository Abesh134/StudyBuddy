using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Options;
using StudyBuddy.Models;

namespace StudyBuddy.Services.Documents;

public sealed class WordTextExtractor : ITextExtractor
{
    private readonly IOptionsMonitor<DocumentExtractionOptions> _optionsMonitor;

    public WordTextExtractor(IOptionsMonitor<DocumentExtractionOptions> optionsMonitor)
    {
        _optionsMonitor = optionsMonitor;
    }

    public bool CanHandle(SourceDocumentFormat format) =>
        format == SourceDocumentFormat.WordDocument;

    public Task<DocumentExtractionResult> ExtractAsync(
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
            "word/document.xml",
            opts);
        if (safetyError is not null)
        {
            return Task.FromResult(DocumentExtractionResult.Fail(safetyError, sanitizedFileName, fileBytes.Length, format));
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

            using var doc = WordprocessingDocument.Open(ms, false, openSettings);
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body is null)
            {
                return Task.FromResult(DocumentExtractionResult.Fail(
                    "The Word document contains no readable body content.",
                    sanitizedFileName,
                    fileBytes.Length,
                    format));
            }

            var warnings = new List<string>();
            var hasEmbeddedUnsupported = false;
            var rawSb = new StringBuilder();
            var mdSb = new StringBuilder();
            var numberedCounters = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var child in body.ChildElements)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (child is Paragraph para)
                {
                    if (para.Descendants<Drawing>().Any() || para.Descendants<EmbeddedObject>().Any())
                    {
                        hasEmbeddedUnsupported = true;
                    }

                    var (rawLine, mdLine) = ExtractParagraph(para, numberedCounters);
                    if (!string.IsNullOrWhiteSpace(rawLine))
                    {
                        if (rawSb.Length > 0) rawSb.Append("\n\n");
                        rawSb.Append(rawLine);
                    }

                    if (!string.IsNullOrWhiteSpace(mdLine))
                    {
                        if (mdSb.Length > 0) mdSb.Append("\n\n");
                        mdSb.Append(mdLine);
                    }
                }
                else if (child is Table table)
                {
                    var (rawTable, mdTable) = ExtractTable(table);
                    if (!string.IsNullOrWhiteSpace(rawTable))
                    {
                        if (rawSb.Length > 0) rawSb.Append("\n\n");
                        rawSb.Append(rawTable);
                    }

                    if (!string.IsNullOrWhiteSpace(mdTable))
                    {
                        if (mdSb.Length > 0) mdSb.Append("\n\n");
                        mdSb.Append(mdTable);
                    }
                }
            }

            if (hasEmbeddedUnsupported)
            {
                warnings.Add("Embedded images, charts, or objects inside the Word document were skipped; structured text, headings, lists, and tables were extracted.");
            }

            var structured = mdSb.ToString().Trim();
            var raw = rawSb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(structured))
            {
                return Task.FromResult(DocumentExtractionResult.Fail(
                    "No readable text was found in this Word document.",
                    sanitizedFileName,
                    fileBytes.Length,
                    format));
            }

            return Task.FromResult(DocumentExtractionResult.Ok(
                extractedText: structured,
                sanitizedFileName: sanitizedFileName,
                fileSizeBytes: fileBytes.Length,
                format: format,
                warnings: warnings,
                rawExtractedText: raw,
                structuredMarkdown: structured));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Task.FromResult(DocumentExtractionResult.Fail(
                "The uploaded Word (.docx) document is corrupted or malformed.",
                sanitizedFileName,
                fileBytes.Length,
                format));
        }
    }

    private static (string RawLine, string MarkdownLine) ExtractParagraph(
        Paragraph para,
        Dictionary<string, int> numberedCounters)
    {
        var rawSb = new StringBuilder();
        var mdSb = new StringBuilder();

        foreach (var run in para.Elements<Run>())
        {
            var runTextSb = new StringBuilder();
            foreach (var part in run.ChildElements)
            {
                switch (part)
                {
                    case Text text:
                        runTextSb.Append(text.Text);
                        break;
                    case TabChar:
                        runTextSb.Append('\t');
                        break;
                    case Break:
                        runTextSb.Append('\n');
                        break;
                }
            }

            var runText = runTextSb.ToString();
            if (runText.Length == 0)
            {
                continue;
            }

            rawSb.Append(runText);

            var rPr = run.RunProperties;
            var isBold = rPr?.Bold is not null && (rPr.Bold.Val is null || rPr.Bold.Val.Value);
            var isItalic = rPr?.Italic is not null && (rPr.Italic.Val is null || rPr.Italic.Val.Value);

            var trimmed = runText.Trim();
            if (trimmed.Length > 0 && (isBold || isItalic) && !trimmed.Contains('$'))
            {
                var leading = runText[..runText.IndexOf(trimmed, StringComparison.Ordinal)];
                var trailing = runText[(leading.Length + trimmed.Length)..];
                var wrapped = isBold && isItalic
                    ? $"***{trimmed}***"
                    : isBold ? $"**{trimmed}**" : $"*{trimmed}*";
                mdSb.Append(leading).Append(wrapped).Append(trailing);
            }
            else
            {
                mdSb.Append(runText);
            }
        }

        if (rawSb.Length == 0)
        {
            var fallback = para.InnerText;
            rawSb.Append(fallback);
            mdSb.Append(fallback);
        }

        var rawContent = rawSb.ToString().Trim();
        var mdContent = mdSb.ToString().Trim();
        if (string.IsNullOrWhiteSpace(rawContent))
        {
            return (string.Empty, string.Empty);
        }

        var styleId = para.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (!string.IsNullOrWhiteSpace(styleId))
        {
            if (styleId.Equals("Title", StringComparison.OrdinalIgnoreCase) ||
                styleId.Equals("Heading1", StringComparison.OrdinalIgnoreCase))
            {
                return (rawContent, $"# {rawContent}");
            }

            if (styleId.Equals("Heading2", StringComparison.OrdinalIgnoreCase))
            {
                return (rawContent, $"## {rawContent}");
            }

            if (styleId.StartsWith("Heading", StringComparison.OrdinalIgnoreCase))
            {
                return (rawContent, $"### {rawContent}");
            }
        }

        var numPr = para.ParagraphProperties?.NumberingProperties;
        if (numPr is not null)
        {
            var numId = numPr.NumberingId?.Val?.Value.ToString() ?? "list";
            var ilvl = numPr.NumberingLevelReference?.Val?.Value ?? 0;
            var indent = new string(' ', Math.Clamp(ilvl, 0, 4) * 2);

            if (styleId is not null && styleId.Contains("Number", StringComparison.OrdinalIgnoreCase))
            {
                var key = $"{numId}:{ilvl}";
                numberedCounters[key] = numberedCounters.GetValueOrDefault(key) + 1;
                var n = numberedCounters[key];
                return ($"{indent}{n}. {rawContent}", $"{indent}{n}. {mdContent}");
            }

            return ($"{indent}- {rawContent}", $"{indent}- {mdContent}");
        }

        return (rawContent, mdContent);
    }

    private static (string RawTable, string MarkdownTable) ExtractTable(Table table)
    {
        var rows = new List<List<string>>();
        foreach (var row in table.Elements<TableRow>())
        {
            var cells = row.Elements<TableCell>()
                .Select(cell => string.Join(" ", cell.Elements<Paragraph>().Select(p => p.InnerText.Trim()).Where(s => s.Length > 0)))
                .ToList();

            if (cells.Any(c => !string.IsNullOrWhiteSpace(c)))
            {
                rows.Add(cells);
            }
        }

        if (rows.Count == 0)
        {
            return (string.Empty, string.Empty);
        }

        var raw = string.Join("\n", rows.Select(r => string.Join(" | ", r)));

        var maxCols = rows.Max(r => r.Count);
        var md = new StringBuilder();

        List<string> PadRow(List<string> r)
        {
            var copy = r.Select(c => c.Replace("|", @"\|")).ToList();
            while (copy.Count < maxCols) copy.Add(string.Empty);
            return copy;
        }

        md.Append("| ").Append(string.Join(" | ", PadRow(rows[0]))).Append(" |\n");
        md.Append("| ").Append(string.Join(" | ", Enumerable.Repeat("---", maxCols))).Append(" |");
        for (var i = 1; i < rows.Count; i++)
        {
            md.Append("\n| ").Append(string.Join(" | ", PadRow(rows[i]))).Append(" |");
        }

        return (raw, md.ToString());
    }
}
