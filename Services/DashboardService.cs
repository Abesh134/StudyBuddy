using Microsoft.EntityFrameworkCore;
using StudyBuddy.Data;

namespace StudyBuddy.Services;

public sealed class DashboardService : IDashboardService
{
    private const int MinAttemptsForWeakTopic = 2;
    private const double WeakTopicThresholdPercent = 80.0;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ICurrentUserService _currentUserService;
    private readonly IUserStreakService _streakService;

    public DashboardService(
        IDbContextFactory<AppDbContext> dbFactory,
        ICurrentUserService currentUserService,
        IUserStreakService streakService)
    {
        _dbFactory = dbFactory;
        _currentUserService = currentUserService;
        _streakService = streakService;
    }

    public async Task<DashboardSummaryDto> GetDashboardSummaryAsync(CancellationToken cancellationToken = default)
    {
        var userId = await _currentUserService.GetRequiredUserIdAsync(cancellationToken);
        var user = await _currentUserService.GetCurrentUserAsync(cancellationToken);
        var displayName = !string.IsNullOrWhiteSpace(user?.FullName)
            ? user.FullName.Trim()
            : (!string.IsNullOrWhiteSpace(user?.Email) ? user.Email.Trim() : "Learner");

        var timeZoneId = string.IsNullOrWhiteSpace(user?.StudyTimeZoneId) ? "UTC" : user.StudyTimeZoneId;
        var tz = ResolveTimeZone(timeZoneId);
        var nowUtc = DateTime.UtcNow;
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);

        var greeting = localNow.Hour switch
        {
            < 12 => "Good morning",
            < 17 => "Good afternoon",
            _ => "Good evening"
        };
        var localDateFormatted = localNow.ToString("dddd, MMMM d, yyyy");

        var allDecks = await QueryOwnerDecksAsync(userId, nowUtc, cancellationToken);
        var streak = await _streakService.GetCurrentUserStreakAsync(cancellationToken);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        // Owner-scoped attempts for accuracy & weak topics
        var attemptStats = await db.Attempts
            .AsNoTracking()
            .Where(a => a.Question != null && a.Question.Deck != null && a.Question.Deck.UserId == userId)
            .Select(a => new
            {
                a.IsCorrect,
                Topic = a.Question!.Topic
            })
            .ToListAsync(cancellationToken);

        var totalAttempts = attemptStats.Count;
        double? overallAccuracy = totalAttempts > 0
            ? Math.Round(attemptStats.Count(a => a.IsCorrect) * 100.0 / totalAttempts, 1)
            : null;

        var weakTopics = attemptStats
            .Where(a => !string.IsNullOrWhiteSpace(a.Topic))
            .GroupBy(a => a.Topic.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var count = g.Count();
                var correct = g.Count(x => x.IsCorrect);
                var accuracy = Math.Round(correct * 100.0 / count, 1);
                return new WeakTopicDto(g.First().Topic.Trim(), count, correct, accuracy);
            })
            .Where(t => t.AttemptCount >= MinAttemptsForWeakTopic && t.AccuracyPercent < WeakTopicThresholdPercent)
            .OrderBy(t => t.AccuracyPercent)
            .ThenByDescending(t => t.AttemptCount)
            .Take(5)
            .ToList();

        var totalDecks = allDecks.Count;
        var totalQuestions = allDecks.Sum(d => d.QuestionCount);
        var dueReviewCount = allDecks.Sum(d => d.DueCount);

        var continueDeck = allDecks.FirstOrDefault(d => d.CanStudy);

        return new DashboardSummaryDto(
            UserFullName: displayName,
            StudyTimeZoneId: tz.Id,
            TimeOfDayGreeting: greeting,
            LocalDateFormatted: localDateFormatted,
            TotalDecks: totalDecks,
            TotalQuestions: totalQuestions,
            TotalAttempts: totalAttempts,
            OverallAccuracyPercent: overallAccuracy,
            DueReviewCount: dueReviewCount,
            DailyStreakDays: streak.CurrentStreakDays,
            StudiedToday: streak.StudiedToday,
            ContinueStudyingDeckId: continueDeck?.Id,
            ContinueStudyingDeckTitle: continueDeck?.Title,
            RecentDecks: allDecks.Take(6).ToList(),
            WeakTopics: weakTopics);
    }

    public async Task<IReadOnlyList<DashboardDeckItemDto>> GetStudyDecksAsync(CancellationToken cancellationToken = default)
    {
        var userId = await _currentUserService.GetRequiredUserIdAsync(cancellationToken);
        return await QueryOwnerDecksAsync(userId, DateTime.UtcNow, cancellationToken);
    }

    private async Task<List<DashboardDeckItemDto>> QueryOwnerDecksAsync(
        string userId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var rawDecks = await db.Decks
            .AsNoTracking()
            .Where(d => d.UserId == userId)
            .OrderByDescending(d => d.UpdatedAtUtc)
            .Select(d => new
            {
                d.Id,
                d.Title,
                d.SourceType,
                d.DocumentFormat,
                d.OriginalFileName,
                d.SourcePageOrSlideCount,
                d.UpdatedAtUtc,
                QuestionCount = d.Questions.Count,
                DueCount = d.Questions.Count(q => q.ReviewState != null && q.ReviewState.NextReviewUtc <= nowUtc),
                AttemptCount = d.Questions.SelectMany(q => q.Attempts).Count(),
                CorrectCount = d.Questions.SelectMany(q => q.Attempts).Count(a => a.IsCorrect)
            })
            .ToListAsync(cancellationToken);

        return rawDecks
            .Select(d => new DashboardDeckItemDto(
                Id: d.Id,
                Title: d.Title,
                SourceType: d.SourceType,
                UpdatedAtUtc: d.UpdatedAtUtc,
                QuestionCount: d.QuestionCount,
                DueCount: d.DueCount,
                AttemptCount: d.AttemptCount,
                AccuracyPercent: d.AttemptCount > 0
                    ? Math.Round(d.CorrectCount * 100.0 / d.AttemptCount, 1)
                    : null,
                DocumentFormat: d.DocumentFormat,
                OriginalFileName: d.OriginalFileName,
                SourcePageOrSlideCount: d.SourcePageOrSlideCount))
            .ToList();
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}

