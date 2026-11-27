using StudyBuddy.Models;

namespace StudyBuddy.Services;

/// <summary>
/// Server-side trusted current-user abstraction. Never accepts a client-supplied UserId.
/// </summary>
public interface ICurrentUserService
{
    Task<string?> GetUserIdAsync(CancellationToken cancellationToken = default);
    Task<string> GetRequiredUserIdAsync(CancellationToken cancellationToken = default);
    Task<bool> IsAuthenticatedAsync(CancellationToken cancellationToken = default);
    Task<ApplicationUser?> GetCurrentUserAsync(CancellationToken cancellationToken = default);
}

