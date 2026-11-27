using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;
using StudyBuddy.Data;
using StudyBuddy.Models;
using StudyBuddy.Services;
using StudyBuddy.Services.Documents;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using PdfPageSize = UglyToad.PdfPig.Content.PageSize;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace StudyBuddy.Tests;

public class DocumentExtractionQualityTests
{
    private static DocumentExtractionService CreateExtractionService(
        IOcrService? ocrService = null,
        IPdfPageRenderer? pdfRenderer = null)
    {
        var monitor = new StaticOptionsMonitor<DocumentExtractionOptions>(new DocumentExtractionOptions());
        var ocr = ocrService ?? new StubOcrService();
        var renderer = pdfRenderer ?? new StubPdfPageRenderer();

        var extractors = new ITextExtractor[]
        {
            new PlainTextExtractor(),
            new PdfTextExtractor(ocr, renderer, monitor),
            new WordTextExtractor(monitor),
            new PowerPointTextExtractor(ocr, monitor),
            new ImageTextExtractor(ocr, monitor)
        };

        return new DocumentExtractionService(
            extractors,
            ocr,
            monitor,
            NullLogger<DocumentExtractionService>.Instance);
    }

    [Fact]
    public async Task MultiColumnPdf_ReadsLeftColumnBeforeRightColumnAndWarns()
    {
        var svc = CreateExtractionService();
        var pdfBytes = BuildTwoColumnPdf();

        await using var ms = new MemoryStream(pdfBytes);
        var result = await svc.ExtractAsync(ms, "two-column-paper.pdf", pdfBytes.Length);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.NotNull(result.RawExtractedText);
        Assert.NotNull(result.StructuredMarkdown);

        var leftTopIdx = result.StructuredMarkdown!.IndexOf("Left column alpha", StringComparison.Ordinal);
        var leftBottomIdx = result.StructuredMarkdown.IndexOf("Left column beta", StringComparison.Ordinal);
        var rightTopIdx = result.StructuredMarkdown.IndexOf("Right column gamma", StringComparison.Ordinal);
        var rightBottomIdx = result.StructuredMarkdown.IndexOf("Right column delta", StringComparison.Ordinal);

        Assert.True(leftTopIdx >= 0 && leftBottomIdx > leftTopIdx, "Left column lines should be ordered top-to-bottom.");
        Assert.True(rightTopIdx > leftBottomIdx, "Right column should appear after left column.");
        Assert.True(rightBottomIdx > rightTopIdx, "Right column lines should be ordered top-to-bottom.");
        Assert.Contains(result.SafeWarnings, w => w.Contains("multi-column", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Pdf_DetectsHeadingsJoinsParagraphsRejoinsHyphensAndBuildsTable()
    {
        var svc = CreateExtractionService();
        var pdfBytes = BuildStructuredPdf();

        await using var ms = new MemoryStream(pdfBytes);
        var result = await svc.ExtractAsync(ms, "structured-lecture.pdf", pdfBytes.Length);

        Assert.True(result.Succeeded, result.ErrorMessage);
        var md = result.StructuredMarkdown ?? result.ExtractedText;

        // Heading detection from larger font size
        Assert.Contains("# Cellular Bioenergetics", md);

        // Hyphenated line wrap rejoined cleanly
        Assert.Contains("photosynthesis", md, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("photo-\nsynthesis", md, StringComparison.OrdinalIgnoreCase);

        // Pipe table reconstructed
        Assert.Contains("| Pathway | Location | Net ATP |", md);
        Assert.Contains("| --- | --- | --- |", md);
        Assert.Contains("| Glycolysis | Cytosol | 2 |", md);
    }

    [Fact]
    public async Task AcademicMathPdf_PreservesLatexAndEmitsFormulaWarning()
    {
        var svc = CreateExtractionService();
        var pdfBytes = BuildMathPdf();

        await using var ms = new MemoryStream(pdfBytes);
        var result = await svc.ExtractAsync(ms, "physics-formulas.pdf", pdfBytes.Length);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Contains("$E = mc^2$", result.ExtractedText);
        Assert.Contains(@"$$\frac{d}{dx}(x^n) = nx^{n-1}$$", result.ExtractedText);
        Assert.Contains(result.SafeWarnings, w => w.Contains("mathematical expressions", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WordDocx_PreservesHeadingsTablesAndSeparateRawText()
    {
        var svc = CreateExtractionService();
        var docxBytes = BuildStructuredDocx();

        await using var ms = new MemoryStream(docxBytes);
        var result = await svc.ExtractAsync(ms, "organic-chem.docx", docxBytes.Length);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.False(string.IsNullOrWhiteSpace(result.RawExtractedText));
        Assert.False(string.IsNullOrWhiteSpace(result.StructuredMarkdown));

        Assert.Contains("# Functional Groups", result.StructuredMarkdown!);
        Assert.Contains("**Hydroxyl**", result.StructuredMarkdown!);
        Assert.Contains("| Group | Formula | Polarity |", result.StructuredMarkdown!);
        Assert.Contains("| --- | --- | --- |", result.StructuredMarkdown!);
        Assert.Contains("| Alcohol | -OH | Polar |", result.StructuredMarkdown!);

        // Raw text should not have Markdown table separator syntax
        Assert.DoesNotContain("| --- |", result.RawExtractedText!);
    }

    [Fact]
    public async Task PowerPointPptx_PreservesSlideHeadingsTablesNotesAndSeparators()
    {
        var svc = CreateExtractionService();
        var pptxBytes = BuildStructuredPptx();

        await using var ms = new MemoryStream(pptxBytes);
        var result = await svc.ExtractAsync(ms, "os-scheduling.pptx", pptxBytes.Length);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(2, result.PageOrSlideCount);
        Assert.Contains("## Slide 1 — CPU Scheduling", result.StructuredMarkdown!);
        Assert.Contains("| Algorithm | Preemptive |", result.StructuredMarkdown!);
        Assert.Contains("| Round Robin | Yes |", result.StructuredMarkdown!);
        Assert.Contains("### Speaker Notes", result.StructuredMarkdown!);
        Assert.Contains("Quantum size affects context-switch overhead.", result.StructuredMarkdown!);
        Assert.Contains("---", result.StructuredMarkdown!);
        Assert.Contains("## Slide 2 — Deadlocks", result.StructuredMarkdown!);
    }

    [Fact]
    public void SafeMarkdownRenderer_SanitizesUnsafeHtmlAndPreservesTablesAndMath()
    {
        var renderer = new SafeMarkdownRenderer();
        const string unsafeMarkdown = """
            # Safe Heading

            <script>alert('xss')</script>
            <img src="https://evil.example/tracker.png" onerror="alert(1)" />
            <iframe src="https://evil.example/frame"></iframe>
            [Bad Link](javascript:alert(1))
            [Good Link](https://example.org/docs)

            Inline formula $a_1 + b_2 = c_3$ and display:

            $$\int_0^\infty e^{-x^2} dx = \frac{\sqrt{\pi}}{2}$$

            | Col A | Col B |
            | --- | --- |
            | Val 1 | Val 2 |
            """;

        var html = renderer.RenderToSafeHtml(unsafeMarkdown);

        // Zero active scripts, iframes, external images, event handlers, or javascript: URLs
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<img", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<iframe", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror=", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", html, StringComparison.OrdinalIgnoreCase);

        // Safe link hardened with rel/target
        Assert.Contains("rel=\"noopener noreferrer nofollow\"", html);

        // Math preserved without underscore emphasis mangling
        Assert.Contains("<span class=\"sb-math-inline\">$a_1 + b_2 = c_3$</span>", html);
        Assert.Contains("<div class=\"sb-math-display\">$$\\int_0^\\infty e^{-x^2} dx = \\frac{\\sqrt{\\pi}}{2}$$</div>", html);

        // Table wrapped in responsive scrollable container
        Assert.Contains("<div class=\"sb-table-scroll\"", html);
        Assert.Contains("<table>", html);
        Assert.Contains("<td>Val 1</td>", html);
    }

    [Fact]
    public async Task BrokenOcrOutput_TriggersUncertaintyWarning()
    {
        var garbledOcr = new StubOcrService("@@##%%^^&&**~~||\\\\ //??!! 12345 $$$$ %%%% ^^^^^ broken ocr");
        var svc = CreateExtractionService(ocrService: garbledOcr);
        var pngBytes = BuildSamplePng(400, 180);

        await using var ms = new MemoryStream(pngBytes);
        var result = await svc.ExtractAsync(ms, "blurry-scan.png", pngBytes.Length, "eng");

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.True(result.UsedOcr);
        Assert.Contains(result.SafeWarnings, w =>
            w.Contains("OCR", StringComparison.OrdinalIgnoreCase) &&
            w.Contains("unusual symbols", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DeckService_PreservesCanonicalNotesAndUntouchedRawText_WithOwnershipAndEditLock()
    {
        await using var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(conn).Options;
        IDbContextFactory<AppDbContext> dbFactory = new SimpleDbContextFactory(dbOptions);

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
            db.Users.AddRange(
                new ApplicationUser { Id = "owner-8b", UserName = "owner8b@test", Email = "owner8b@test", FullName = "Owner EightB", StudyTimeZoneId = "UTC" },
                new ApplicationUser { Id = "other-8b", UserName = "other8b@test", Email = "other8b@test", FullName = "Other EightB", StudyTimeZoneId = "UTC" });
            await db.SaveChangesAsync();
        }

        const string rawText = "RAW PAGE 1\nphoto-\nsynthesis\nCol1 Col2";
        const string canonicalEditedText = "# Photosynthesis\n\n| Stage | Output |\n| --- | --- |\n| Light | ATP |";

        var uploadMonitor = new StaticOptionsMonitor<DeckUploadOptions>(new DeckUploadOptions());
        var ownerDeckService = new DeckService(dbFactory, new StubCurrentUserService("owner-8b"), uploadMonitor);

        var createResult = await ownerDeckService.CreateDeckAsync(new CreateDeckRequest(
            Title: "Bioenergetics",
            NotesText: canonicalEditedText,
            SourceType: NoteSourceType.UploadedFile,
            OriginalFileName: "bio.pdf",
            DocumentFormat: SourceDocumentFormat.PdfDocument,
            SourcePageOrSlideCount: 2,
            UsedOcr: false,
            RawExtractedText: rawText));

        Assert.True(createResult.Succeeded, createResult.ErrorMessage);
        var deckId = createResult.Value;

        var detail = await ownerDeckService.GetDeckDetailAsync(deckId);
        Assert.NotNull(detail);
        Assert.Equal(canonicalEditedText, detail!.NotesText);
        Assert.Equal(rawText, detail.RawExtractedText);

        // Editing canonical notes preserves RawExtractedText untouched
        const string revisedNotes = "# Photosynthesis Revised\n\nLight reactions generate ATP and NADPH.";
        var updateResult = await ownerDeckService.UpdateDeckAsync(deckId, new UpdateDeckRequest("Bioenergetics v2", revisedNotes));
        Assert.True(updateResult.Succeeded, updateResult.ErrorMessage);

        var updated = await ownerDeckService.GetDeckDetailAsync(deckId);
        Assert.Equal(revisedNotes, updated!.NotesText);
        Assert.Equal(rawText, updated.RawExtractedText);

        // Add a question -> NotesText edits must now be rejected while title edit still succeeds
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            db.Questions.Add(new Question
            {
                DeckId = deckId,
                QuestionText = "What do light reactions generate?",
                SourceSentence = "Light reactions generate ATP and NADPH.",
                Explanation = "Directly stated in the notes."
            });
            await db.SaveChangesAsync();
        }

        var lockedEdit = await ownerDeckService.UpdateDeckAsync(deckId, new UpdateDeckRequest("Bioenergetics v3", "Attempted overwrite"));
        Assert.False(lockedEdit.Succeeded);

        // Cross-account isolation check
        var otherDeckService = new DeckService(dbFactory, new StubCurrentUserService("other-8b"), uploadMonitor);
        var forbidden = await otherDeckService.GetDeckDetailAsync(deckId);
        Assert.Null(forbidden);
    }

    // ---- Test Document Builders ----

    private static byte[] BuildTwoColumnPdf()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PdfPageSize.A4); // width ~595, height ~842

        // Left column (x = 50..240)
        page.AddText("Left column alpha explains neural gradients.", 11, new PdfPoint(50, 760), font);
        page.AddText("Left column beta explains backpropagation.", 11, new PdfPoint(50, 740), font);
        page.AddText("Left column third line continues cleanly.", 11, new PdfPoint(50, 720), font);
        page.AddText("Left column fourth line finishes section.", 11, new PdfPoint(50, 700), font);

        // Right column (x = 340..540) — wide clear gutter (> 80pt)
        page.AddText("Right column gamma covers regularization.", 11, new PdfPoint(340, 760), font);
        page.AddText("Right column delta covers dropout layers.", 11, new PdfPoint(340, 740), font);
        page.AddText("Right column third line explains weight decay.", 11, new PdfPoint(340, 720), font);
        page.AddText("Right column fourth line summarizes results.", 11, new PdfPoint(340, 700), font);

        return builder.Build();
    }

    private static byte[] BuildStructuredPdf()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PdfPageSize.A4);

        // Heading (large font)
        page.AddText("Cellular Bioenergetics", 20, new PdfPoint(50, 780), font);

        // Wrapped paragraph with hyphenated word
        page.AddText("Plants convert sunlight into chemical energy through photo-", 11, new PdfPoint(50, 740), font);
        page.AddText("synthesis inside chloroplasts and release oxygen.", 11, new PdfPoint(50, 724), font);

        // 3-column table with consistent X alignments
        page.AddText("Pathway", 11, new PdfPoint(50, 670), font);
        page.AddText("Location", 11, new PdfPoint(200, 670), font);
        page.AddText("Net ATP", 11, new PdfPoint(350, 670), font);

        page.AddText("Glycolysis", 11, new PdfPoint(50, 650), font);
        page.AddText("Cytosol", 11, new PdfPoint(200, 650), font);
        page.AddText("2", 11, new PdfPoint(350, 650), font);

        page.AddText("Citric Acid", 11, new PdfPoint(50, 630), font);
        page.AddText("Mitochondria", 11, new PdfPoint(200, 630), font);
        page.AddText("2", 11, new PdfPoint(350, 630), font);

        return builder.Build();
    }

    private static byte[] BuildMathPdf()
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var page = builder.AddPage(PdfPageSize.A4);

        page.AddText("Relativity and Calculus Notes", 18, new PdfPoint(50, 780), font);
        page.AddText("Mass-energy equivalence is written as $E = mc^2$ in physics.", 11, new PdfPoint(50, 740), font);
        page.AddText(@"$$\frac{d}{dx}(x^n) = nx^{n-1}$$", 11, new PdfPoint(50, 710), font);

        return builder.Build();
    }

    private static byte[] BuildStructuredDocx()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, true))
        {
            var mainPart = doc.AddMainDocumentPart();
            var body = new W.Body();

            // Heading 1
            body.Append(new W.Paragraph(
                new W.ParagraphProperties(new W.ParagraphStyleId { Val = "Heading1" }),
                new W.Run(new W.Text("Functional Groups"))));

            // Paragraph with bold run
            body.Append(new W.Paragraph(
                new W.Run(new W.RunProperties(new W.Bold()), new W.Text("Hydroxyl")),
                new W.Run(new W.Text(" groups increase water solubility."))));

            // 3-column table
            var table = new W.Table();
            table.Append(BuildDocxRow("Group", "Formula", "Polarity"));
            table.Append(BuildDocxRow("Alcohol", "-OH", "Polar"));
            table.Append(BuildDocxRow("Alkane", "-CH3", "Nonpolar"));
            body.Append(table);

            mainPart.Document = new W.Document(body);
            mainPart.Document.Save();
        }

        return ms.ToArray();
    }

    private static W.TableRow BuildDocxRow(params string[] cells)
    {
        var row = new W.TableRow();
        foreach (var text in cells)
        {
            row.Append(new W.TableCell(new W.Paragraph(new W.Run(new W.Text(text)))));
        }

        return row;
    }

    private static byte[] BuildStructuredPptx()
    {
        using var ms = new MemoryStream();
        using (var doc = PresentationDocument.Create(ms, PresentationDocumentType.Presentation, true))
        {
            var presPart = doc.AddPresentationPart();
            presPart.Presentation = new P.Presentation(new P.SlideIdList());

            // Slide 1 with title, table, and speaker notes
            var slide1Part = presPart.AddNewPart<SlidePart>("rId1");
            var slide1Tree = new P.ShapeTree(
                new P.NonVisualGroupShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                    new P.NonVisualGroupShapeDrawingProperties(),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.GroupShapeProperties(),
                BuildPptxTextShape(2, "Title 1", P.PlaceholderValues.Title, "CPU Scheduling"),
                BuildPptxTableFrame(3, new[]
                {
                    new[] { "Algorithm", "Preemptive" },
                    new[] { "Round Robin", "Yes" }
                }));

            slide1Part.Slide = new P.Slide(new P.CommonSlideData(slide1Tree));
            slide1Part.Slide.Save();

            var notesPart = slide1Part.AddNewPart<NotesSlidePart>("rIdNotes1");
            notesPart.NotesSlide = new P.NotesSlide(
                new P.CommonSlideData(
                    new P.ShapeTree(
                        new P.NonVisualGroupShapeProperties(
                            new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                            new P.NonVisualGroupShapeDrawingProperties(),
                            new P.ApplicationNonVisualDrawingProperties()),
                        new P.GroupShapeProperties(),
                        BuildPptxTextShape(2, "Notes", P.PlaceholderValues.Body, "Quantum size affects context-switch overhead."))));
            notesPart.NotesSlide.Save();

            // Slide 2 with title + body
            var slide2Part = presPart.AddNewPart<SlidePart>("rId2");
            var slide2Tree = new P.ShapeTree(
                new P.NonVisualGroupShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                    new P.NonVisualGroupShapeDrawingProperties(),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.GroupShapeProperties(),
                BuildPptxTextShape(2, "Title 2", P.PlaceholderValues.Title, "Deadlocks"),
                BuildPptxTextShape(3, "Body 2", P.PlaceholderValues.Body, "Coffman conditions include mutual exclusion and circular wait."));

            slide2Part.Slide = new P.Slide(new P.CommonSlideData(slide2Tree));
            slide2Part.Slide.Save();

            presPart.Presentation.SlideIdList!.Append(
                new P.SlideId { Id = 256, RelationshipId = "rId1" },
                new P.SlideId { Id = 257, RelationshipId = "rId2" });
            presPart.Presentation.Save();
        }

        return ms.ToArray();
    }

    private static P.Shape BuildPptxTextShape(uint id, string name, P.PlaceholderValues placeholder, string text)
    {
        return new P.Shape(
            new P.NonVisualShapeProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = name },
                new P.NonVisualShapeDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties(new P.PlaceholderShape { Type = placeholder })),
            new P.ShapeProperties(),
            new P.TextBody(
                new A.BodyProperties(),
                new A.ListStyle(),
                new A.Paragraph(new A.Run(new A.Text(text)))));
    }

    private static P.GraphicFrame BuildPptxTableFrame(uint id, string[][] rows)
    {
        var table = new A.Table();
        foreach (var r in rows)
        {
            var tr = new A.TableRow { Height = 370840 };
            foreach (var cellText in r)
            {
                tr.Append(new A.TableCell(
                    new A.TextBody(
                        new A.BodyProperties(),
                        new A.ListStyle(),
                        new A.Paragraph(new A.Run(new A.Text(cellText))))));
            }
            table.Append(tr);
        }

        return new P.GraphicFrame(
            new P.NonVisualGraphicFrameProperties(
                new P.NonVisualDrawingProperties { Id = id, Name = "Table 1" },
                new P.NonVisualGraphicFrameDrawingProperties(),
                new P.ApplicationNonVisualDrawingProperties()),
            new P.Transform(),
            new A.Graphic(new A.GraphicData(table) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/table" }));
    }

    private static byte[] BuildSamplePng(int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.White);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // ---- Test Doubles ----

    private sealed class StubOcrService : IOcrService
    {
        private readonly string _text;
        public StubOcrService(string text = "Mock OCR text") => _text = text;
        public OcrAvailability GetAvailability() => new(true, "stub-tesseract", new[] { new OcrLanguageOption("eng", "English") }, "Ready");
        public Task<OcrResult> RecognizeImageAsync(byte[] imageBytes, string? language, CancellationToken ct = default)
            => Task.FromResult(new OcrResult(true, _text, null));
    }

    private sealed class StubPdfPageRenderer : IPdfPageRenderer
    {
        public Task<byte[]> RenderPageToPngAsync(byte[] pdfBytes, int zeroBasedPageIndex, CancellationToken ct = default)
            => Task.FromResult(BuildSamplePng(200, 120));
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }

    private sealed class SimpleDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private sealed class StubCurrentUserService(string? userId) : ICurrentUserService
    {
        public Task<string?> GetUserIdAsync(CancellationToken ct = default) => Task.FromResult(userId);
        public Task<string> GetRequiredUserIdAsync(CancellationToken ct = default)
            => Task.FromResult(userId ?? throw new UnauthorizedAccessException());
        public Task<bool> IsAuthenticatedAsync(CancellationToken ct = default)
            => Task.FromResult(!string.IsNullOrWhiteSpace(userId));
        public Task<ApplicationUser?> GetCurrentUserAsync(CancellationToken ct = default)
            => Task.FromResult<ApplicationUser?>(null);
    }
}
