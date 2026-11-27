using StudyBuddy.Models;

namespace StudyBuddy.Services;

public sealed record DashboardDeckItemDto(
    int Id,
    string Title,
    NoteSourceType SourceType,
    DateTime UpdatedAtUtc,
    int QuestionCount,
    int DueCount,
    int AttemptCount,
    double? AccuracyPercent,
    SourceDocumentFormat DocumentFormat = SourceDocumentFormat.PastedNotes,
    string? OriginalFileName = null,
    int? SourcePageOrSlideCount = null)
{
    public bool CanStudy => QuestionCount > 0;
}

public sealed record WeakTopicDto(
    string Topic,
    int AttemptCount,
    int CorrectCount,
    double AccuracyPercent);

public sealed record DashboardSummaryDto(
    string UserFullName,
    string StudyTimeZoneId,
    string TimeOfDayGreeting,
    string LocalDateFormatted,
    int TotalDecks,
    int TotalQuestions,
    int TotalAttempts,
    double? OverallAccuracyPercent,
    int DueReviewCount,
    int DailyStreakDays,
    bool StudiedToday,
    int? ContinueStudyingDeckId,
    string? ContinueStudyingDeckTitle,
    IReadOnlyList<DashboardDeckItemDto> RecentDecks,
    IReadOnlyList<WeakTopicDto> WeakTopics);

/// <summary>
/// Owner-scoped dashboard and study-selection query contract.
/// Always derives identity server-side via ICurrentUserService; never exposes global counts
/// or another user's decks, questions, attempts, review states, or statistics.
/// </summary>
public interface IDashboardService
{
    Task<DashboardSummaryDto> GetDashboardSummaryAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DashboardDeckItemDto>> GetStudyDecksAsync(CancellationToken cancellationToken = default);
}

