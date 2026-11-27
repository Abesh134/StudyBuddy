using System.IO.Compression;
using StudyBuddy.Models;

namespace StudyBuddy.Services.Documents;

internal static class FileSignatureValidator
{
    private static readonly Dictionary<string, SourceDocumentFormat> ExtensionMap = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = SourceDocumentFormat.TextDocument,
        [".md"] = SourceDocumentFormat.MarkdownDocument,
        [".pdf"] = SourceDocumentFormat.PdfDocument,
        [".docx"] = SourceDocumentFormat.WordDocument,
        [".pptx"] = SourceDocumentFormat.PowerPointPresentation,
        [".jpg"] = SourceDocumentFormat.ImageOcr,
        [".jpeg"] = SourceDocumentFormat.ImageOcr,
        [".png"] = SourceDocumentFormat.ImageOcr,
        [".webp"] = SourceDocumentFormat.ImageOcr
    };

    public static bool TryDetectFormatFromExtension(string fileName, out SourceDocumentFormat format)
    {
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(ext))
        {
            format = SourceDocumentFormat.PastedNotes;
            return false;
        }

        return ExtensionMap.TryGetValue(ext, out format);
    }

    public static string? ValidateSignature(byte[] bytes, string fileName, SourceDocumentFormat format)
    {
        if (bytes.Length == 0)
        {
            return "The uploaded file is empty.";
        }

        var ext = Path.GetExtension(fileName).ToLowerInvariant();

        return format switch
        {
            SourceDocumentFormat.PdfDocument => ValidatePdfSignature(bytes),
            SourceDocumentFormat.WordDocument => ValidateZipSignature(bytes, "Word (.docx)"),
            SourceDocumentFormat.PowerPointPresentation => ValidateZipSignature(bytes, "PowerPoint (.pptx)"),
            SourceDocumentFormat.ImageOcr => ValidateImageSignature(bytes, ext),
            SourceDocumentFormat.TextDocument or SourceDocumentFormat.MarkdownDocument => ValidatePlainTextSignature(bytes),
            _ => "Unsupported file format."
        };
    }

    public static string? ValidateOpenXmlPackageSafety(
        byte[] bytes,
        string requiredEntryPath,
        DocumentExtractionOptions options)
    {
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            using var archive = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: false);

            var maxEntries = Math.Max(10, options.MaxZipEntries);
            if (archive.Entries.Count == 0)
            {
                return "The uploaded document archive is empty or corrupted.";
            }

            if (archive.Entries.Count > maxEntries)
            {
                return $"The uploaded document contains too many internal entries (limit: {maxEntries}).";
            }

            var maxDecompressed = Math.Max(1024L * 1024L, options.MaxDecompressedBytes);
            long totalUncompressed = 0;
            var hasRequiredEntry = false;

            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.Contains("..", StringComparison.Ordinal) ||
                    entry.FullName.StartsWith('/') ||
                    entry.FullName.StartsWith('\\'))
                {
                    return "The uploaded document contains invalid archive paths and was rejected.";
                }

                // Reject VBA macro binaries inside OpenXML packages
                if (entry.FullName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) &&
                    entry.FullName.Contains("vbaProject", StringComparison.OrdinalIgnoreCase))
                {
                    return "Macro-enabled documents are not supported for security reasons.";
                }

                if (string.Equals(entry.FullName, requiredEntryPath, StringComparison.OrdinalIgnoreCase))
                {
                    hasRequiredEntry = true;
                }

                if (entry.Length < 0)
                {
                    return "The uploaded document contains invalid compressed entry metadata.";
                }

                totalUncompressed += entry.Length;
                if (totalUncompressed > maxDecompressed)
                {
                    var maxMb = Math.Round(maxDecompressed / (1024.0 * 1024.0), 0);
                    return $"The decompressed document exceeds the {maxMb:0} MB safety limit.";
                }

                // Verify actual decompressed stream size for entries claiming small size (ZIP bomb guard)
                if (entry.CompressedLength > 0 && entry.Length > entry.CompressedLength * 150 && entry.Length > 2 * 1024 * 1024)
                {
                    return "The uploaded document has an unsafe compression ratio and was rejected.";
                }
            }

            if (!hasRequiredEntry)
            {
                return "The uploaded file does not have a valid Office Open XML document structure.";
            }

            return null;
        }
        catch (InvalidDataException)
        {
            return "The uploaded Office document is corrupted or not a valid ZIP-based Open XML file.";
        }
    }

    private static string? ValidatePdfSignature(byte[] bytes)
    {
        // PDF header "%PDF-" may appear within the first 1024 bytes per spec, usually offset 0
        var searchLimit = Math.Min(bytes.Length, 1024);
        for (var i = 0; i <= searchLimit - 5; i++)
        {
            if (bytes[i] == 0x25 && bytes[i + 1] == 0x50 && bytes[i + 2] == 0x44 && bytes[i + 3] == 0x46 && bytes[i + 4] == 0x2D)
            {
                return null;
            }
        }

        return "The uploaded file does not have a valid PDF file signature (%PDF-).";
    }

    private static string? ValidateZipSignature(byte[] bytes, string label)
    {
        if (bytes.Length < 4 ||
            bytes[0] != 0x50 || bytes[1] != 0x4B ||
            !((bytes[2] == 0x03 && bytes[3] == 0x04) ||
              (bytes[2] == 0x05 && bytes[3] == 0x06) ||
              (bytes[2] == 0x07 && bytes[3] == 0x08)))
        {
            return $"The uploaded file is not a valid {label} document.";
        }

        return null;
    }

    private static string? ValidateImageSignature(byte[] bytes, string ext)
    {
        if (ext is ".jpg" or ".jpeg")
        {
            if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            {
                return null;
            }

            return "The uploaded file does not have a valid JPEG image signature.";
        }

        if (ext == ".png")
        {
            if (bytes.Length >= 8 &&
                bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
                bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
            {
                return null;
            }

            return "The uploaded file does not have a valid PNG image signature.";
        }

        if (ext == ".webp")
        {
            if (bytes.Length >= 12 &&
                bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 &&
                bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            {
                return null;
            }

            return "The uploaded file does not have a valid WebP image signature.";
        }

        return "Unsupported image format. Only JPG, JPEG, PNG, and WebP images are supported.";
    }

    private static string? ValidatePlainTextSignature(byte[] bytes)
    {
        if (Array.IndexOf(bytes, (byte)0) >= 0)
        {
            return "Binary files are not supported as plain text. Please upload a valid UTF-8 .txt or .md file.";
        }

        // Reject common disguised binary headers (PE/EXE, PDF, ZIP, PNG, JPEG)
        if ((bytes.Length >= 2 && bytes[0] == 0x4D && bytes[1] == 0x5A) || // MZ
            (bytes.Length >= 5 && bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46 && bytes[4] == 0x2D) || // %PDF-
            (bytes.Length >= 4 && bytes[0] == 0x50 && bytes[1] == 0x4B && bytes[2] == 0x03 && bytes[3] == 0x04) || // PK..
            (bytes.Length >= 8 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) || // PNG
            (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)) // JPEG
        {
            return "The uploaded file appears to be a binary document rather than plain text (.txt or .md).";
        }

        return null;
    }
}

