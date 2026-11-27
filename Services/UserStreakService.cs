using Microsoft.EntityFrameworkCore;
using StudyBuddy.Data;

namespace StudyBuddy.Services;

public sealed class UserStreakService : IUserStreakService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ICurrentUserService _currentUserService;

    public UserStreakService(
        IDbContextFactory<AppDbContext> dbFactory,
        ICurrentUserService currentUserService)
    {
        _dbFactory = dbFactory;
        _currentUserService = currentUserService;
    }

    public async Task<UserStreakSummary> GetCurrentUserStreakAsync(CancellationToken cancellationToken = default)
    {
        var user = await _currentUserService.GetCurrentUserAsync(cancellationToken);
        if (user is null)
        {
            return new UserStreakSummary(0, false, 0);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        var attemptUtcDates = await db.Attempts
            .AsNoTracking()
            .Where(a => a.Question != null && a.Question.Deck != null && a.Question.Deck.UserId == user.Id)
            .Select(a => a.AttemptedAtUtc)
            .ToListAsync(cancellationToken);

        if (attemptUtcDates.Count == 0)
        {
            return new UserStreakSummary(0, false, 0);
        }

        var tz = ResolveTimeZone(user.StudyTimeZoneId);
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz));

        var studiedDays = new HashSet<DateOnly>();
        foreach (var utc in attemptUtcDates)
        {
            var normalizedUtc = utc.Kind == DateTimeKind.Utc
                ? utc
                : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(normalizedUtc, tz));
            studiedDays.Add(localDate);
        }

        var studiedToday = studiedDays.Contains(localToday);
        var cursor = studiedToday ? localToday : localToday.AddDays(-1);
        var streak = 0;

        while (studiedDays.Contains(cursor))
        {
            streak++;
            cursor = cursor.AddDays(-1);
        }

        return new UserStreakSummary(streak, studiedToday, attemptUtcDates.Count);
    }

    private static TimeZoneInfo ResolveTimeZone(string? timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Utc;
        }

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

