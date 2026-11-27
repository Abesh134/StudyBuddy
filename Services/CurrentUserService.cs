using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using StudyBuddy.Data;
using StudyBuddy.Models;

namespace StudyBuddy.Services;

public sealed class CurrentUserService : ICurrentUserService
{
    private readonly AuthenticationStateProvider _authenticationStateProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;

    public CurrentUserService(
        AuthenticationStateProvider authenticationStateProvider,
        IHttpContextAccessor httpContextAccessor,
        IDbContextFactory<AppDbContext> dbFactory)
    {
        _authenticationStateProvider = authenticationStateProvider;
        _httpContextAccessor = httpContextAccessor;
        _dbFactory = dbFactory;
    }

    public async Task<string?> GetUserIdAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ClaimsPrincipal? principal = null;
        try
        {
            var authState = await _authenticationStateProvider.GetAuthenticationStateAsync();
            principal = authState.User;
        }
        catch (InvalidOperationException)
        {
            // Fallback for non-circuit HTTP endpoints where Blazor AuthenticationStateProvider is not initialized.
            principal = _httpContextAccessor.HttpContext?.User;
        }

        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier);
        return string.IsNullOrWhiteSpace(userId) ? null : userId;
    }

    public async Task<string> GetRequiredUserIdAsync(CancellationToken cancellationToken = default)
    {
        var userId = await GetUserIdAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new UnauthorizedAccessException("An authenticated user is required for this operation.");
        }

        return userId;
    }

    public async Task<bool> IsAuthenticatedAsync(CancellationToken cancellationToken = default) =>
        await GetUserIdAsync(cancellationToken) is not null;

    public async Task<ApplicationUser?> GetCurrentUserAsync(CancellationToken cancellationToken = default)
    {
        var userId = await GetUserIdAsync(cancellationToken);
        if (userId is null)
        {
            return null;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
    }
}

