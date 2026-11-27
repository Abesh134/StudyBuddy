namespace StudyBuddy.Services;

public sealed record UserStreakSummary(
    int CurrentStreakDays,
    bool StudiedToday,
    int TotalAttempts);

/// <summary>
/// Computes real, owner-scoped study streak metrics in the authenticated user's configured timezone.
/// Never fabricates metrics.
/// </summary>
public interface IUserStreakService
{
    Task<UserStreakSummary> GetCurrentUserStreakAsync(CancellationToken cancellationToken = default);
}

