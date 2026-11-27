using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using DocumentFormat.OpenXml.Wordprocessing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;
using StudyBuddy.Models;
using StudyBuddy.Services.Documents;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace StudyBuddy.Tests;

public class DocumentExtractionTests
{
    private static DocumentExtractionService CreateService(
        DocumentExtractionOptions? options = null,
        IOcrService? ocrService = null,
        IPdfPageRenderer? pdfRenderer = null)
    {
        var opts = options ?? new DocumentExtractionOptions();
        var monitor = new StaticOptionsMonitor<DocumentExtractionOptions>(opts);
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
    public async Task PlainTextAndMarkdown_PreserveExactText()
    {
        var svc = CreateService();
        const string md = "# Study Notes\n\nMitosis divides one cell into two identical daughter cells.";
        var bytes = Encoding.UTF8.GetBytes(md);

        await using var ms = new MemoryStream(bytes);
        var result = await svc.ExtractAsync(ms, "biology-unit1.md", bytes.Length);

        Assert.True(result.Succeeded);
        Assert.Equal(SourceDocumentFormat.MarkdownDocument, result.DocumentFormat);
        Assert.Equal(md, result.ExtractedText);
        Assert.Equal("biology-unit1.md", result.SanitizedFileName);
    }

    [Fact]
    public async Task NativeMultiPagePdf_ExtractsPagesInOrderWithPageCount()
    {
        var svc = CreateService();
        var pdfBytes = BuildTestPdf(
            "Page one explains the CAP theorem in distributed systems.",
            "Page two covers linearizability and consensus protocols.");

        await using var ms = new MemoryStream(pdfBytes);
        var result = await svc.ExtractAsync(ms, "distributed.pdf", pdfBytes.Length);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(SourceDocumentFormat.PdfDocument, result.DocumentFormat);
        Assert.Equal(2, result.PageOrSlideCount);
        Assert.False(result.UsedOcr);
        var idx1 = result.ExtractedText.IndexOf("Page one explains", StringComparison.Ordinal);
        var idx2 = result.ExtractedText.IndexOf("Page two covers", StringComparison.Ordinal);
        Assert.True(idx1 >= 0 && idx2 > idx1, "Expected Page 1 text before Page 2 text.");
    }

    [Fact]
    public async Task MixedNativeAndScannedPdf_RunsOcrFallbackOnlyOnScannedPage()
    {
        var ocr = new StubOcrService("Scanned page two discusses Raft leader election and quorum commits.");
        var renderer = new StubPdfPageRenderer();
        var svc = CreateService(ocrService: ocr, pdfRenderer: renderer);

        // Page 1 has native text, Page 2 is blank (simulating scanned/image-only page)
        var pdfBytes = BuildTestPdf(
            "Native page one explains write-ahead logging in storage engines.",
            null);

        await using var ms = new MemoryStream(pdfBytes);
        var result = await svc.ExtractAsync(ms, "mixed-lecture.pdf", pdfBytes.Length, "eng");

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(2, result.PageOrSlideCount);
        Assert.True(result.UsedOcr);
        Assert.Equal(1, result.OcrItemsProcessed);
        Assert.Equal(new[] { 2 }, renderer.RenderedPages);
        Assert.Contains("write-ahead logging", result.ExtractedText);
        Assert.Contains("Raft leader election", result.ExtractedText);
        Assert.NotEmpty(result.SafeWarnings);
    }

    [Fact]
    public async Task WordDocx_ExtractsHeadingsParagraphsAndTablesInOrder()
    {
        var svc = CreateService();
        var docxBytes = BuildTestDocx();

        await using var ms = new MemoryStream(docxBytes);
        var result = await svc.ExtractAsync(ms, "chemistry-notes.docx", docxBytes.Length);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(SourceDocumentFormat.WordDocument, result.DocumentFormat);
        Assert.Contains("Thermodynamics Overview", result.ExtractedText);
        Assert.Contains("Enthalpy measures total heat content", result.ExtractedText);
        Assert.Contains("Variable | Meaning", result.ExtractedText);
        Assert.Contains("Delta H | Change in enthalpy", result.ExtractedText);
    }

    [Fact]
    public async Task PowerPointPptx_PreservesSlideOrderExtractsNotesTablesAndEmbeddedImageOcr()
    {
        var ocr = new StubOcrService("Diagram label: ATP Synthase Proton Channel");
        var svc = CreateService(ocrService: ocr);
        var pptxBytes = BuildTestPptxWithEmbeddedImage();

        await using var ms = new MemoryStream(pptxBytes);
        var result = await svc.ExtractAsync(ms, "bio-slides.pptx", pptxBytes.Length, "eng");

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(SourceDocumentFormat.PowerPointPresentation, result.DocumentFormat);
        Assert.Equal(2, result.PageOrSlideCount);
        Assert.True(result.UsedOcr);
        Assert.Equal(1, result.OcrItemsProcessed);

        var s1Idx = result.ExtractedText.IndexOf("Slide 1 — Introduction", StringComparison.Ordinal);
        var s2Idx = result.ExtractedText.IndexOf("Slide 2 — Main Concepts", StringComparison.Ordinal);
        Assert.True(s1Idx >= 0 && s2Idx > s1Idx, "Slides must follow SlideIdList order.");
        Assert.Contains("Speaker Notes", result.ExtractedText);
        Assert.Contains("Remember to emphasize net ATP yield.", result.ExtractedText);
        Assert.Contains("Stage | Location", result.ExtractedText);
        Assert.Contains("ATP Synthase Proton Channel", result.ExtractedText);
    }

    [Theory]
    [InlineData("notes.jpg", SKEncodedImageFormat.Jpeg, "eng", "Printed English textbook excerpt about photosynthesis.")]
    [InlineData("notes.jpeg", SKEncodedImageFormat.Jpeg, "eng+ben", "English and বাংলা printed study note excerpt.")]
    [InlineData("slide.png", SKEncodedImageFormat.Png, "ben", "কোষীয় শ্বসন গ্লুকোজ থেকে এটিপি তৈরি করে।")]
    [InlineData("page.webp", SKEncodedImageFormat.Webp, "eng", "WebP screenshot of computer networks lecture notes.")]
    public async Task Images_JpgJpegPngWebp_DecodePreprocessAndRunOcr(
        string fileName,
        SKEncodedImageFormat format,
        string lang,
        string ocrText)
    {
        var ocr = new StubOcrService(ocrText);
        var svc = CreateService(ocrService: ocr);
        var imgBytes = BuildTestImage(160, 80, format);

        await using var ms = new MemoryStream(imgBytes);
        var result = await svc.ExtractAsync(ms, fileName, imgBytes.Length, lang);

        Assert.True(result.Succeeded, result.ErrorMessage);
        Assert.Equal(SourceDocumentFormat.ImageOcr, result.DocumentFormat);
        Assert.True(result.UsedOcr);
        Assert.Equal(ocrText, result.ExtractedText);
        Assert.Equal(lang, ocr.LastLanguage);
        Assert.Contains(result.SafeWarnings, w => w.Contains("handwritten", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SecurityBoundaries_RejectInvalidSignatureUnsupportedCorruptedOversizedPixelLimitAndMacros()
    {
        var svc = CreateService(new DocumentExtractionOptions
        {
            MaxTxtBytes = 2048,
            MaxImageWidth = 200,
            MaxImageHeight = 200,
            MaxImagePixels = 10_000,
            MaxDecompressedBytes = 1024 * 1024
        });

        // 1. Unsupported legacy / executable extensions
        var sample = Encoding.UTF8.GetBytes("Some text content long enough for validation.");
        await using (var ms = new MemoryStream(sample))
            Assert.False((await svc.ExtractAsync(ms, "legacy.doc", sample.Length)).Succeeded);
        await using (var ms = new MemoryStream(sample))
            Assert.False((await svc.ExtractAsync(ms, "legacy.ppt", sample.Length)).Succeeded);
        await using (var ms = new MemoryStream(sample))
            Assert.False((await svc.ExtractAsync(ms, "malware.exe", sample.Length)).Succeeded);

        // 2. Disguised plain text renamed to .pdf -> fails %PDF- signature check
        await using (var ms = new MemoryStream(sample))
        {
            var badPdf = await svc.ExtractAsync(ms, "fake.pdf", sample.Length);
            Assert.False(badPdf.Succeeded);
            Assert.Contains("signature", badPdf.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        // 3. Corrupted PDF header with garbage body
        var corruptPdf = Encoding.ASCII.GetBytes("%PDF-1.7\ngarbage-not-a-real-pdf-structure");
        await using (var ms = new MemoryStream(corruptPdf))
            Assert.False((await svc.ExtractAsync(ms, "corrupt.pdf", corruptPdf.Length)).Succeeded);

        // 4. Image exceeding pixel limit (300x300 = 90,000 > 10,000)
        var bigImg = BuildTestImage(300, 300, SKEncodedImageFormat.Png);
        await using (var ms = new MemoryStream(bigImg))
        {
            var tooManyPixels = await svc.ExtractAsync(ms, "huge.png", bigImg.Length);
            Assert.False(tooManyPixels.Succeeded);
            Assert.Contains("safety limit", tooManyPixels.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        // 5. Macro-bearing ZIP package disguised as .docx
        var macroZip = BuildZipWithEntries(("word/document.xml", "<w:document/>"), ("word/vbaProject.bin", "macro"));
        await using (var ms = new MemoryStream(macroZip))
        {
            var macroResult = await svc.ExtractAsync(ms, "macros.docx", macroZip.Length);
            Assert.False(macroResult.Succeeded);
            Assert.Contains("Macro", macroResult.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        // 6. Lying stream exceeding MaxTxtBytes
        var oversizedBytes = Encoding.UTF8.GetBytes(new string('A', 4096));
        await using (var ms = new MemoryStream(oversizedBytes))
            Assert.False((await svc.ExtractAsync(ms, "lying.txt", 100)).Succeeded);
    }

    [Fact]
    public async Task Extraction_HonorsCallerCancellationAndUnavailableOcrReturnsActionableError()
    {
        var unavailableOcr = new StubOcrService(available: false);
        var svc = CreateService(ocrService: unavailableOcr);

        var imgBytes = BuildTestImage(120, 60, SKEncodedImageFormat.Png);
        await using (var ms = new MemoryStream(imgBytes))
        {
            var noOcr = await svc.ExtractAsync(ms, "note.png", imgBytes.Length);
            Assert.False(noOcr.Succeeded);
            Assert.True(string.IsNullOrEmpty(noOcr.ExtractedText));
            Assert.Contains("Tesseract", noOcr.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        // Caller cancellation propagates cleanly
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await using var ms2 = new MemoryStream(imgBytes);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            svc.ExtractAsync(ms2, "note.png", imgBytes.Length, cancellationToken: cts.Token));
    }

    private static byte[] BuildTestPdf(params string?[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        foreach (var pageText in pages)
        {
            var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            if (!string.IsNullOrWhiteSpace(pageText))
            {
                page.AddText(pageText, 12, new PdfPoint(40, 750), font);
            }
        }

        return builder.Build();
    }

    private static byte[] BuildTestDocx()
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document, true))
        {
            var mainPart = doc.AddMainDocumentPart();
            mainPart.Document = new W.Document(new W.Body());
            var body = mainPart.Document.Body!;

            var heading = new W.Paragraph(
                new W.ParagraphProperties(new W.ParagraphStyleId { Val = "Heading1" }),
                new W.Run(new W.Text("Thermodynamics Overview")));
            var para = new W.Paragraph(
                new W.Run(new W.Text("Enthalpy measures total heat content in a constant-pressure system.")));

            var table = new W.Table(
                new W.TableRow(
                    new W.TableCell(new W.Paragraph(new W.Run(new W.Text("Variable")))),
                    new W.TableCell(new W.Paragraph(new W.Run(new W.Text("Meaning"))))),
                new W.TableRow(
                    new W.TableCell(new W.Paragraph(new W.Run(new W.Text("Delta H")))),
                    new W.TableCell(new W.Paragraph(new W.Run(new W.Text("Change in enthalpy"))))));

            body.Append(heading, para, table);
            mainPart.Document.Save();
        }

        return ms.ToArray();
    }

    private static byte[] BuildTestPptxWithEmbeddedImage()
    {
        using var ms = new MemoryStream();
        using (var doc = PresentationDocument.Create(ms, PresentationDocumentType.Presentation, true))
        {
            var presPart = doc.AddPresentationPart();
            presPart.Presentation = new Presentation(new SlideIdList());

            // Create Slide 2 first in package, then Slide 1, and list Slide 1 BEFORE Slide 2 in SlideIdList
            var slide2Part = presPart.AddNewPart<SlidePart>("rIdSlide2");
            slide2Part.Slide = CreateSlide("Main Concepts", "Oxidative phosphorylation generates ATP.", includeTable: true);
            slide2Part.Slide.Save();

            var slide1Part = presPart.AddNewPart<SlidePart>("rIdSlide1");
            slide1Part.Slide = CreateSlide("Introduction", "Cellular respiration converts glucose into ATP.", includeTable: false);
            slide1Part.Slide.Save();

            // Add speaker notes to Slide 1
            var notesPart = slide1Part.AddNewPart<NotesSlidePart>();
            notesPart.NotesSlide = new NotesSlide(
                new CommonSlideData(
                    new ShapeTree(
                        new P.NonVisualGroupShapeProperties(
                            new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                            new P.NonVisualGroupShapeDrawingProperties(),
                            new ApplicationNonVisualDrawingProperties()),
                        new GroupShapeProperties(),
                        new P.Shape(
                            new P.NonVisualShapeProperties(
                                new P.NonVisualDrawingProperties { Id = 2, Name = "Notes" },
                                new P.NonVisualShapeDrawingProperties(),
                                new ApplicationNonVisualDrawingProperties(new PlaceholderShape { Type = PlaceholderValues.Body })),
                            new P.ShapeProperties(),
                            new P.TextBody(
                                new A.BodyProperties(),
                                new A.ListStyle(),
                                new A.Paragraph(new A.Run(new A.Text("Remember to emphasize net ATP yield."))))))));
            notesPart.NotesSlide.Save();

            // Embed a real PNG image (> 1 KB) on Slide 2 to exercise bounded embedded-image OCR
            var imgPart = slide2Part.AddImagePart(ImagePartType.Png);
            var embeddedPng = BuildTestImage(220, 120, SKEncodedImageFormat.Png);
            using (var imgStream = new MemoryStream(embeddedPng))
            {
                imgPart.FeedData(imgStream);
            }

            // Explicit slide order: Slide 1 first, Slide 2 second
            presPart.Presentation.SlideIdList!.Append(
                new SlideId { Id = 256, RelationshipId = "rIdSlide1" },
                new SlideId { Id = 257, RelationshipId = "rIdSlide2" });
            presPart.Presentation.Save();
        }

        return ms.ToArray();
    }

    private static Slide CreateSlide(string title, string body, bool includeTable)
    {
        var shapeTree = new ShapeTree(
            new P.NonVisualGroupShapeProperties(
                new P.NonVisualDrawingProperties { Id = 1, Name = "" },
                new P.NonVisualGroupShapeDrawingProperties(),
                new ApplicationNonVisualDrawingProperties()),
            new GroupShapeProperties(),
            new P.Shape(
                new P.NonVisualShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 2, Name = "Title" },
                    new P.NonVisualShapeDrawingProperties(),
                    new ApplicationNonVisualDrawingProperties(new PlaceholderShape { Type = PlaceholderValues.Title })),
                new P.ShapeProperties(),
                new P.TextBody(
                    new A.BodyProperties(),
                    new A.ListStyle(),
                    new A.Paragraph(new A.Run(new A.Text(title))))),
            new P.Shape(
                new P.NonVisualShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 3, Name = "Body" },
                    new P.NonVisualShapeDrawingProperties(),
                    new ApplicationNonVisualDrawingProperties(new PlaceholderShape { Type = PlaceholderValues.Body })),
                new P.ShapeProperties(),
                new P.TextBody(
                    new A.BodyProperties(),
                    new A.ListStyle(),
                    new A.Paragraph(new A.Run(new A.Text(body))))));

        if (includeTable)
        {
            var table = new A.Table(
                new A.TableRow(
                    new A.TableCell(new A.TextBody(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph(new A.Run(new A.Text("Stage"))))),
                    new A.TableCell(new A.TextBody(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph(new A.Run(new A.Text("Location")))))),
                new A.TableRow(
                    new A.TableCell(new A.TextBody(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph(new A.Run(new A.Text("Glycolysis"))))),
                    new A.TableCell(new A.TextBody(new A.BodyProperties(), new A.ListStyle(), new A.Paragraph(new A.Run(new A.Text("Cytosol")))))));

            shapeTree.Append(new P.GraphicFrame(
                new P.NonVisualGraphicFrameProperties(
                    new P.NonVisualDrawingProperties { Id = 4, Name = "Table" },
                    new P.NonVisualGraphicFrameDrawingProperties(),
                    new ApplicationNonVisualDrawingProperties()),
                new P.Transform(),
                new A.Graphic(new A.GraphicData(table) { Uri = "http://schemas.openxmlformats.org/drawingml/2006/table" })));
        }

        return new Slide(new CommonSlideData(shapeTree));
    }

    private static byte[] BuildTestImage(int width, int height, SKEncodedImageFormat format)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Navy, IsAntialias = true };
        for (var i = 0; i < width; i += 8)
        {
            canvas.DrawLine(i, 0, i + 4, height, paint);
        }
        canvas.Flush();

        using var img = surface.Snapshot();
        using var data = img.Encode(format, 90);
        return data.ToArray();
    }

    private static byte[] BuildZipWithEntries(params (string Path, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in entries)
            {
                var entry = archive.CreateEntry(path);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(content);
            }
        }

        return ms.ToArray();
    }

    private sealed class StubOcrService : IOcrService
    {
        private readonly bool _available;
        private readonly string _text;

        public StubOcrService(string text = "Sample OCR text from study material.", bool available = true)
        {
            _text = text;
            _available = available;
        }

        public string? LastLanguage { get; private set; }

        public OcrAvailability GetAvailability() => _available
            ? new OcrAvailability(true, "tesseract", new[]
            {
                new OcrLanguageOption("eng", "English"),
                new OcrLanguageOption("ben", "Bengali"),
                new OcrLanguageOption("eng+ben", "English + Bengali")
            }, "Stub OCR available")
            : new OcrAvailability(false, null, Array.Empty<OcrLanguageOption>(), "Tesseract OCR is not installed.");

        public Task<OcrResult> RecognizeImageAsync(byte[] pngImageBytes, string? languageCode = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LastLanguage = languageCode;
            if (!_available)
            {
                return Task.FromResult(new OcrResult(false, string.Empty, "Tesseract OCR is not installed."));
            }

            return Task.FromResult(new OcrResult(true, _text, null));
        }
    }

    private sealed class StubPdfPageRenderer : IPdfPageRenderer
    {
        public List<int> RenderedPages { get; } = new();

        public Task<byte[]> RenderPageToPngAsync(byte[] pdfBytes, int oneBasedPageNumber, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RenderedPages.Add(oneBasedPageNumber);
            return Task.FromResult(BuildTestImage(100, 60, SKEncodedImageFormat.Png));
        }
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue => currentValue;
        public T Get(string? name) => currentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
