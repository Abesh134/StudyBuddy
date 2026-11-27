namespace StudyBuddy.Services;

public sealed class DeckUploadOptions
{
    public const string SectionName = "DeckUpload";

    /// <summary>Maximum allowed uploaded file size in bytes (default 5 MB).</summary>
    public long MaxFileBytes { get; set; } = 5 * 1024 * 1024;

    /// <summary>Minimum allowed extracted/pasted notes length in characters.</summary>
    public int MinNotesLength { get; set; } = 20;

    /// <summary>Maximum allowed extracted/pasted notes length in characters.</summary>
    public int MaxNotesLength { get; set; } = 100_000;

    public string[] AllowedExtensions { get; set; } = new[] { ".txt", ".md" };
}

