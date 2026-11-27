using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StudyBuddy.Data;
using StudyBuddy.Models;

namespace StudyBuddy.Services;

public sealed class DeckService : IDeckService
{
    private const int MaxTitleLength = 150;
    private const int MaxFileNameLength = 260;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ICurrentUserService _currentUserService;
    private readonly IOptionsMonitor<DeckUploadOptions> _uploadOptions;

    public DeckService(
        IDbContextFactory<AppDbContext> dbFactory,
        ICurrentUserService currentUserService,
        IOptionsMonitor<DeckUploadOptions> uploadOptions)
    {
        _dbFactory = dbFactory;
        _currentUserService = currentUserService;
        _uploadOptions = uploadOptions;
    }

    public async Task<IReadOnlyList<DeckListItemDto>> GetDecksAsync(
        string? search = null,
        DeckSortOrder sort = DeckSortOrder.UpdatedDesc,
        CancellationToken cancellationToken = default)
    {
        var userId = await _currentUserService.GetRequiredUserIdAsync(cancellationToken);
        var nowUtc = DateTime.UtcNow;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        IQueryable<Deck> query = db.Decks
            .AsNoTracking()
            .Where(d => d.UserId == userId);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(d =>
                d.Title.ToLower().Contains(term) ||
                (d.OriginalFileName != null && d.OriginalFileName.ToLower().Contains(term)));
        }

        var projected = await query
            .Select(d => new
            {
                d.Id,
                d.Title,
                d.SourceType,
                d.DocumentFormat,
                d.OriginalFileName,
                d.SourcePageOrSlideCount,
                d.UsedOcr,
                d.CreatedAtUtc,
                d.UpdatedAtUtc,
                QuestionCount = d.Questions.Count,
                DueCount = d.Questions.Count(q => q.ReviewState != null && q.ReviewState.NextReviewUtc <= nowUtc),
                AttemptCount = d.Questions.SelectMany(q => q.Attempts).Count(),
                CorrectCount = d.Questions.SelectMany(q => q.Attempts).Count(a => a.IsCorrect)
            })
            .ToListAsync(cancellationToken);

        var items = projected.Select(d => new DeckListItemDto(
            Id: d.Id,
            Title: d.Title,
            SourceType: d.SourceType,
            OriginalFileName: d.OriginalFileName,
            CreatedAtUtc: d.CreatedAtUtc,
            UpdatedAtUtc: d.UpdatedAtUtc,
            QuestionCount: d.QuestionCount,
            DueCount: d.DueCount,
            AttemptCount: d.AttemptCount,
            AccuracyPercent: d.AttemptCount > 0
                ? Math.Round(d.CorrectCount * 100.0 / d.AttemptCount, 1)
                : null,
            DocumentFormat: ResolveDisplayFormat(d.SourceType, d.DocumentFormat, d.OriginalFileName),
            SourcePageOrSlideCount: d.SourcePageOrSlideCount,
            UsedOcr: d.UsedOcr));

        return (sort switch
        {
            DeckSortOrder.CreatedDesc => items.OrderByDescending(d => d.CreatedAtUtc),
            DeckSortOrder.TitleAsc => items.OrderBy(d => d.Title, StringComparer.OrdinalIgnoreCase),
            DeckSortOrder.TitleDesc => items.OrderByDescending(d => d.Title, StringComparer.OrdinalIgnoreCase),
            DeckSortOrder.MostQuestions => items.OrderByDescending(d => d.QuestionCount).ThenByDescending(d => d.UpdatedAtUtc),
            DeckSortOrder.MostDue => items.OrderByDescending(d => d.DueCount).ThenByDescending(d => d.UpdatedAtUtc),
            _ => items.OrderByDescending(d => d.UpdatedAtUtc)
        }).ToList();
    }

    public async Task<DeckDetailDto?> GetDeckDetailAsync(
        int deckId,
        CancellationToken cancellationToken = default)
    {
        var userId = await _currentUserService.GetRequiredUserIdAsync(cancellationToken);
        var nowUtc = DateTime.UtcNow;

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var raw = await db.Decks
            .AsNoTracking()
            .Where(d => d.Id == deckId && d.UserId == userId)
            .Select(d => new
            {
                d.Id,
                d.Title,
                d.NotesText,
                d.RawExtractedText,
                d.SourceType,
                d.DocumentFormat,
                d.OriginalFileName,
                d.SourcePageOrSlideCount,
                d.UsedOcr,
                d.CreatedAtUtc,
                d.UpdatedAtUtc,
                QuestionCount = d.Questions.Count,
                DueCount = d.Questions.Count(q => q.ReviewState != null && q.ReviewState.NextReviewUtc <= nowUtc),
                AttemptCount = d.Questions.SelectMany(q => q.Attempts).Count(),
                CorrectCount = d.Questions.SelectMany(q => q.Attempts).Count(a => a.IsCorrect)
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (raw is null)
        {
            return null;
        }

        return new DeckDetailDto(
            Id: raw.Id,
            Title: raw.Title,
            NotesText: raw.NotesText,
            SourceType: raw.SourceType,
            OriginalFileName: raw.OriginalFileName,
            CreatedAtUtc: raw.CreatedAtUtc,
            UpdatedAtUtc: raw.UpdatedAtUtc,
            QuestionCount: raw.QuestionCount,
            DueCount: raw.DueCount,
            AttemptCount: raw.AttemptCount,
            AccuracyPercent: raw.AttemptCount > 0
                ? Math.Round(raw.CorrectCount * 100.0 / raw.AttemptCount, 1)
                : null,
            DocumentFormat: ResolveDisplayFormat(raw.SourceType, raw.DocumentFormat, raw.OriginalFileName),
            SourcePageOrSlideCount: raw.SourcePageOrSlideCount,
            UsedOcr: raw.UsedOcr,
            RawExtractedText: raw.RawExtractedText);
    }

    public async Task<DeckOperationResult<int>> CreateDeckAsync(
        CreateDeckRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = await _currentUserService.GetRequiredUserIdAsync(cancellationToken);
        var titleError = ValidateTitle(request.Title, out var cleanTitle);
        if (titleError is not null)
        {
            return DeckOperationResult<int>.Fail(titleError);
        }

        var notesError = ValidateNotesText(request.NotesText);
        if (notesError is not null)
        {
            return DeckOperationResult<int>.Fail(notesError);
        }

        string? sanitizedFileName = null;
        if (request.SourceType == NoteSourceType.UploadedFile && !string.IsNullOrWhiteSpace(request.OriginalFileName))
        {
            sanitizedFileName = SanitizeFileName(request.OriginalFileName);
        }

        var nowUtc = DateTime.UtcNow;
        var resolvedFormat = ResolveDisplayFormat(request.SourceType, request.DocumentFormat, sanitizedFileName);
        var deck = new Deck
        {
            UserId = userId,
            Title = cleanTitle,
            // Preserve original exact text verbatim for SourceSentence matching.
            NotesText = request.NotesText,
            RawExtractedText = request.RawExtractedText,
            SourceType = request.SourceType,
            DocumentFormat = resolvedFormat,
            OriginalFileName = sanitizedFileName,
            SourcePageOrSlideCount = request.SourcePageOrSlideCount,
            UsedOcr = request.UsedOcr,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
        };

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        db.Decks.Add(deck);
        await db.SaveChangesAsync(cancellationToken);

        return DeckOperationResult<int>.Ok(deck.Id);
    }

    public async Task<DeckOperationResult<bool>> UpdateDeckAsync(
        int deckId,
        UpdateDeckRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var userId = await _currentUserService.GetRequiredUserIdAsync(cancellationToken);
        var titleError = ValidateTitle(request.Title, out var cleanTitle);
        if (titleError is not null)
        {
            return DeckOperationResult<bool>.Fail(titleError);
        }

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var deck = await db.Decks
            .FirstOrDefaultAsync(d => d.Id == deckId && d.UserId == userId, cancellationToken);

        if (deck is null)
        {
            return DeckOperationResult<bool>.Missing();
        }

        var hasQuestions = await db.Questions
            .AsNoTracking()
            .AnyAsync(q => q.DeckId == deck.Id, cancellationToken);

        if (request.NotesText is not null && !string.Equals(request.NotesText, deck.NotesText, StringComparison.Ordinal))
        {
            if (hasQuestions)
            {
                return DeckOperationResult<bool>.Fail(
                    "Notes cannot be edited after questions have been generated. Create a new deck for changed notes.");
            }

            var notesError = ValidateNotesText(request.NotesText);
            if (notesError is not null)
            {
                return DeckOperationResult<bool>.Fail(notesError);
            }

            deck.NotesText = request.NotesText;
        }

        deck.Title = cleanTitle;
        deck.UpdatedAtUtc = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        return DeckOperationResult<bool>.Ok(true);
    }

    public async Task<DeckOperationResult<bool>> DeleteDeckAsync(
        int deckId,
        CancellationToken cancellationToken = default)
    {
        var userId = await _currentUserService.GetRequiredUserIdAsync(cancellationToken);

        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

        var deck = await db.Decks
            .FirstOrDefaultAsync(d => d.Id == deckId && d.UserId == userId, cancellationToken);

        if (deck is null)
        {
            return DeckOperationResult<bool>.Missing();
        }

        db.Decks.Remove(deck);
        await db.SaveChangesAsync(cancellationToken);
        return DeckOperationResult<bool>.Ok(true);
    }

    public async Task<NoteExtractionResult> ExtractTextFromUploadAsync(
        Stream stream,
        string fileName,
        long declaredSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var opts = _uploadOptions.CurrentValue;
        var maxBytes = Math.Max(1024L, opts.MaxFileBytes);

        var sanitizedName = SanitizeFileName(fileName);
        if (string.IsNullOrWhiteSpace(sanitizedName))
        {
            return NoteExtractionResult.Fail("A valid file name is required.");
        }

        var extension = Path.GetExtension(sanitizedName);
        var allowed = opts.AllowedExtensions is { Length: > 0 }
            ? opts.AllowedExtensions
            : new[] { ".txt", ".md" };

        if (string.IsNullOrWhiteSpace(extension) ||
            !allowed.Any(ext => string.Equals(ext, extension, StringComparison.OrdinalIgnoreCase)))
        {
            return NoteExtractionResult.Fail("Only plain-text (.txt) and Markdown (.md) files are supported.");
        }

        if (declaredSize <= 0)
        {
            return NoteExtractionResult.Fail("The selected file is empty.");
        }

        if (declaredSize > maxBytes)
        {
            var maxMb = Math.Round(maxBytes / (1024.0 * 1024.0), 1);
            return NoteExtractionResult.Fail($"File size exceeds the {maxMb:0.#} MB maximum limit.");
        }

        // Bounded stream read: read at most maxBytes + 1 to protect memory even if declaredSize is inaccurate.
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        long totalRead = 0;

        while (true)
        {
            var bytesToRead = (int)Math.Min(chunk.Length, (maxBytes + 1) - totalRead);
            if (bytesToRead <= 0)
            {
                break;
            }

            var read = await stream.ReadAsync(chunk.AsMemory(0, bytesToRead), cancellationToken);
            if (read == 0)
            {
                break;
            }

            totalRead += read;
            if (totalRead > maxBytes)
            {
                var maxMb = Math.Round(maxBytes / (1024.0 * 1024.0), 1);
                return NoteExtractionResult.Fail($"File content exceeds the {maxMb:0.#} MB maximum limit.");
            }

            buffer.Write(chunk, 0, read);
        }

        if (totalRead == 0)
        {
            return NoteExtractionResult.Fail("The uploaded file contains no content.");
        }

        var rawBytes = buffer.ToArray();

        // Reject NUL bytes (strong binary file indicator)
        if (Array.IndexOf(rawBytes, (byte)0) >= 0)
        {
            return NoteExtractionResult.Fail("Binary files are not supported. Please upload a plain-text (.txt or .md) file.");
        }

        string text;
        try
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            text = strictUtf8.GetString(rawBytes);
        }
        catch (DecoderFallbackException)
        {
            return NoteExtractionResult.Fail("File encoding is not valid UTF-8 text. Please save your notes as UTF-8 .txt or .md.");
        }

        // Strip leading UTF-8 BOM if present while keeping exact original text intact
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        var notesError = ValidateNotesText(text);
        if (notesError is not null)
        {
            return NoteExtractionResult.Fail(notesError);
        }

        return NoteExtractionResult.Ok(text, sanitizedName);
    }

    private static string? ValidateTitle(string? title, out string cleanTitle)
    {
        cleanTitle = (title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(cleanTitle))
        {
            return "Deck title is required.";
        }

        if (cleanTitle.Length > MaxTitleLength)
        {
            return $"Deck title must be {MaxTitleLength} characters or fewer.";
        }

        return null;
    }

    private string? ValidateNotesText(string? notesText)
    {
        if (string.IsNullOrWhiteSpace(notesText))
        {
            return "Study notes are required and cannot be empty.";
        }

        var opts = _uploadOptions.CurrentValue;
        var minLen = Math.Max(1, opts.MinNotesLength);
        var maxLen = Math.Max(minLen, opts.MaxNotesLength);

        var trimmedLength = notesText.Trim().Length;
        if (trimmedLength < minLen)
        {
            return $"Study notes must be at least {minLen} characters long.";
        }

        if (notesText.Length > maxLen)
        {
            return $"Study notes exceed the maximum allowed length of {maxLen:N0} characters.";
        }

        // Reject binary/non-printable control characters (allowing tab, LF, CR)
        var controlCount = 0;
        foreach (var ch in notesText)
        {
            if (ch == '\0')
            {
                return "Binary or NUL characters are not permitted in study notes.";
            }

            if (char.IsControl(ch) && ch != '\r' && ch != '\n' && ch != '\t')
            {
                controlCount++;
            }
        }

        if (controlCount > 0 && (double)controlCount / notesText.Length > 0.02)
        {
            return "The provided content appears to be binary or unsupported control data.";
        }

        return null;
    }

    private static SourceDocumentFormat ResolveDisplayFormat(
        NoteSourceType sourceType,
        SourceDocumentFormat storedFormat,
        string? fileName)
    {
        if (storedFormat != SourceDocumentFormat.PastedNotes)
        {
            return storedFormat;
        }

        if (sourceType == NoteSourceType.UploadedFile && !string.IsNullOrWhiteSpace(fileName))
        {
            if (Documents.FileSignatureValidator.TryDetectFormatFromExtension(fileName, out var inferred))
            {
                return inferred;
            }

            return SourceDocumentFormat.TextDocument;
        }

        return SourceDocumentFormat.PastedNotes;
    }

    private static string SanitizeFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return string.Empty;
        }

        var justName = Path.GetFileName(fileName.Trim());
        if (justName.Length > MaxFileNameLength)
        {
            justName = justName[..MaxFileNameLength];
        }

        return justName;
    }
}

