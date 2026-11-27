using Microsoft.EntityFrameworkCore;
using StudyBuddy.Data;
using StudyBuddy.Models;

namespace StudyBuddy.Services;

public sealed class OwnerScopedDeckService : IOwnerScopedDeckService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ICurrentUserService _currentUserService;

    public OwnerScopedDeckService(
        IDbContextFactory<AppDbContext> dbFactory,
        ICurrentUserService currentUserService)
    {
        _dbFactory = dbFactory;
        _currentUserService = currentUserService;
    }

    public async Task<IReadOnlyList<Deck>> GetMyDecksAsync(CancellationToken cancellationToken = default)
    {
        var userId = await _currentUserService.GetRequiredUserIdAsync(cancellationToken);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Decks
            .AsNoTracking()
            .Include(d => d.Questions)
            .Where(d => d.UserId == userId)
            .OrderByDescending(d => d.UpdatedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<Deck?> GetMyDeckByIdAsync(int deckId, CancellationToken cancellationToken = default)
    {
        var userId = await _currentUserService.GetRequiredUserIdAsync(cancellationToken);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Decks
            .AsNoTracking()
            .Include(d => d.Questions)
                .ThenInclude(q => q.Options)
            .FirstOrDefaultAsync(d => d.Id == deckId && d.UserId == userId, cancellationToken);
    }

    public async Task<bool> OwnsDeckAsync(int deckId, CancellationToken cancellationToken = default)
    {
        var userId = await _currentUserService.GetUserIdAsync(cancellationToken);
        if (userId is null)
        {
            return false;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Decks
            .AsNoTracking()
            .AnyAsync(d => d.Id == deckId && d.UserId == userId, cancellationToken);
    }

    public async Task<bool> OwnsQuestionAsync(int questionId, CancellationToken cancellationToken = default)
    {
        var userId = await _currentUserService.GetUserIdAsync(cancellationToken);
        if (userId is null)
        {
            return false;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Questions
            .AsNoTracking()
            .AnyAsync(q => q.Id == questionId && q.Deck != null && q.Deck.UserId == userId, cancellationToken);
    }
}

