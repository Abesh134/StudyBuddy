using StudyBuddy.Models;

namespace StudyBuddy.Services;

public enum DeckSortOrder
{
    UpdatedDesc = 0,
    CreatedDesc = 1,
    TitleAsc = 2,
    TitleDesc = 3,
    MostQuestions = 4,
    MostDue = 5
}

public sealed record DeckListItemDto(
    int Id,
    string Title,
    NoteSourceType SourceType,
    string? OriginalFileName,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    int QuestionCount,
    int DueCount,
    int AttemptCount,
    double? AccuracyPercent,
    SourceDocumentFormat DocumentFormat = SourceDocumentFormat.PastedNotes,
    int? SourcePageOrSlideCount = null,
    bool UsedOcr = false)
{
    public bool CanStudy => QuestionCount > 0;
}

public sealed record DeckDetailDto(
    int Id,
    string Title,
    string NotesText,
    NoteSourceType SourceType,
    string? OriginalFileName,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    int QuestionCount,
    int DueCount,
    int AttemptCount,
    double? AccuracyPercent,
    SourceDocumentFormat DocumentFormat = SourceDocumentFormat.PastedNotes,
    int? SourcePageOrSlideCount = null,
    bool UsedOcr = false,
    string? RawExtractedText = null)
{
    public bool CanEditNotes => QuestionCount == 0;
    public bool CanStudy => QuestionCount > 0;
}

public sealed record CreateDeckRequest(
    string Title,
    string NotesText,
    NoteSourceType SourceType,
    string? OriginalFileName,
    SourceDocumentFormat DocumentFormat = SourceDocumentFormat.PastedNotes,
    int? SourcePageOrSlideCount = null,
    bool UsedOcr = false,
    string? RawExtractedText = null);

public sealed record UpdateDeckRequest(
    string Title,
    string? NotesText);

public sealed record DeckOperationResult<T>(
    bool Succeeded,
    T? Value = default,
    string? ErrorMessage = null,
    bool NotFound = false)
{
    public static DeckOperationResult<T> Ok(T value) => new(true, value, null, false);
    public static DeckOperationResult<T> Fail(string error) => new(false, default, error, false);
    public static DeckOperationResult<T> Missing() => new(false, default, "Deck not found.", true);
}

public sealed record NoteExtractionResult(
    bool Succeeded,
    string? ExtractedText = null,
    string? SanitizedFileName = null,
    string? ErrorMessage = null)
{
    public static NoteExtractionResult Ok(string text, string fileName) => new(true, text, fileName, null);
    public static NoteExtractionResult Fail(string error) => new(false, null, null, error);
}

/// <summary>
/// Owner-scoped deck CRUD and bounded TXT/MD note extraction service.
/// Always derives the current user server-side via ICurrentUserService.
/// </summary>
public interface IDeckService
{
    Task<IReadOnlyList<DeckListItemDto>> GetDecksAsync(
        string? search = null,
        DeckSortOrder sort = DeckSortOrder.UpdatedDesc,
        CancellationToken cancellationToken = default);

    Task<DeckDetailDto?> GetDeckDetailAsync(
        int deckId,
        CancellationToken cancellationToken = default);

    Task<DeckOperationResult<int>> CreateDeckAsync(
        CreateDeckRequest request,
        CancellationToken cancellationToken = default);

    Task<DeckOperationResult<bool>> UpdateDeckAsync(
        int deckId,
        UpdateDeckRequest request,
        CancellationToken cancellationToken = default);

    Task<DeckOperationResult<bool>> DeleteDeckAsync(
        int deckId,
        CancellationToken cancellationToken = default);

    Task<NoteExtractionResult> ExtractTextFromUploadAsync(
        Stream stream,
        string fileName,
        long declaredSize,
        CancellationToken cancellationToken = default);
}

