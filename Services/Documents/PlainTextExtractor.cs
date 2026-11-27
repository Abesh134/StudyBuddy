using System.Text;
using StudyBuddy.Models;

namespace StudyBuddy.Services.Documents;

public sealed class PlainTextExtractor : ITextExtractor
{
    public bool CanHandle(SourceDocumentFormat format) =>
        format is SourceDocumentFormat.TextDocument or SourceDocumentFormat.MarkdownDocument;

    public Task<DocumentExtractionResult> ExtractAsync(
        byte[] fileBytes,
        string sanitizedFileName,
        SourceDocumentFormat format,
        string? ocrLanguage,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string text;
        try
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            text = strictUtf8.GetString(fileBytes);
        }
        catch (DecoderFallbackException)
        {
            return Task.FromResult(DocumentExtractionResult.Fail(
                "File encoding is not valid UTF-8 text. Please save your notes as UTF-8 .txt or .md.",
                sanitizedFileName,
                fileBytes.Length,
                format));
        }

        // Strip leading UTF-8 BOM while preserving exact original text verbatim
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        var controlCount = 0;
        foreach (var ch in text)
        {
            if (ch == '\0')
            {
                return Task.FromResult(DocumentExtractionResult.Fail(
                    "Binary or NUL characters are not permitted in plain-text notes.",
                    sanitizedFileName,
                    fileBytes.Length,
                    format));
            }

            if (char.IsControl(ch) && ch != '\r' && ch != '\n' && ch != '\t')
            {
                controlCount++;
            }
        }

        if (text.Length > 0 && (double)controlCount / text.Length > 0.02)
        {
            return Task.FromResult(DocumentExtractionResult.Fail(
                "The uploaded file contains excessive control characters and appears to be binary data.",
                sanitizedFileName,
                fileBytes.Length,
                format));
        }

        return Task.FromResult(DocumentExtractionResult.Ok(
            extractedText: text,
            sanitizedFileName: sanitizedFileName,
            fileSizeBytes: fileBytes.Length,
            format: format));
    }
}

