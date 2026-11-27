using StudyBuddy.Models;

namespace StudyBuddy.Services;

/// <summary>
/// Owner-scoped study data contract. All queries derive the current user server-side
/// via ICurrentUserService and enforce ownership on every direct ID lookup.
/// </summary>
public interface IOwnerScopedDeckService
{
    Task<IReadOnlyList<Deck>> GetMyDecksAsync(CancellationToken cancellationToken = default);
    Task<Deck?> GetMyDeckByIdAsync(int deckId, CancellationToken cancellationToken = default);
    Task<bool> OwnsDeckAsync(int deckId, CancellationToken cancellationToken = default);
    Task<bool> OwnsQuestionAsync(int questionId, CancellationToken cancellationToken = default);
}

