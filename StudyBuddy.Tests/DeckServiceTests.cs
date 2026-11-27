using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using StudyBuddy.Data;
using StudyBuddy.Models;
using StudyBuddy.Services;
using Xunit;

namespace StudyBuddy.Tests;

public class DeckServiceTests
{
    private static async Task<(SqliteConnection Connection, IDbContextFactory<AppDbContext> DbFactory)> CreateDbAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        IDbContextFactory<AppDbContext> dbFactory = new SimpleDbContextFactory(options);

        await using var db = await dbFactory.CreateDbContextAsync();
        await db.Database.MigrateAsync();

        db.Users.AddRange(
            new ApplicationUser { Id = "user-a", UserName = "a@test", Email = "a@test", FullName = "Alice Mercer", StudyTimeZoneId = "UTC" },
            new ApplicationUser { Id = "user-b", UserName = "b@test", Email = "b@test", FullName = "Bob Tanaka", StudyTimeZoneId = "UTC" });
        await db.SaveChangesAsync();

        return (connection, dbFactory);
    }

    private static DeckService CreateDeckService(
        IDbContextFactory<AppDbContext> dbFactory,
        string? userId,
        DeckUploadOptions? uploadOptions = null)
    {
        var currentUser = new CurrentUserService(new StubAuthStateProvider(userId), new HttpContextAccessor(), dbFactory);
        var monitor = new StaticOptionsMonitor<DeckUploadOptions>(uploadOptions ?? new DeckUploadOptions());
        return new DeckService(dbFactory, currentUser, monitor);
    }

    [Fact]
    public async Task CrudSearchSortAndDashboardIntegration_WorkForOwner()
    {
        var (conn, dbFactory) = await CreateDbAsync();
        await using var _ = conn;

        var svc = CreateDeckService(dbFactory, "user-a");
        var currentA = new CurrentUserService(new StubAuthStateProvider("user-a"), new HttpContextAccessor(), dbFactory);
        var dashSvc = new DashboardService(dbFactory, currentA, new UserStreakService(dbFactory, currentA));

        const string exactNotes = "Line 1: Exact   spacing & punctuation!\nLine 2: Preserved verbatim for SourceSentence.";
        var create1 = await svc.CreateDeckAsync(new CreateDeckRequest(
            "Beta Systems", exactNotes, NoteSourceType.PastedText, null));
        Assert.True(create1.Succeeded);

        var create2 = await svc.CreateDeckAsync(new CreateDeckRequest(
            "Alpha Biology", "Cellular respiration converts glucose into ATP across mitochondria.", NoteSourceType.UploadedFile, "bio-notes.md"));
        Assert.True(create2.Succeeded);

        // Dashboard reflects real created decks immediately
        var dashSummary = await dashSvc.GetDashboardSummaryAsync();
        Assert.Equal(2, dashSummary.TotalDecks);
        Assert.Equal(2, (await dashSvc.GetStudyDecksAsync()).Count);

        // Detail preserves exact verbatim text
        var detail = await svc.GetDeckDetailAsync(create1.Value);
        Assert.NotNull(detail);
        Assert.Equal(exactNotes, detail!.NotesText);
        Assert.True(detail.CanEditNotes);

        // Search & sort
        var sortedAsc = await svc.GetDecksAsync(sort: DeckSortOrder.TitleAsc);
        Assert.Equal(new[] { "Alpha Biology", "Beta Systems" }, sortedAsc.Select(d => d.Title).ToArray());

        var searchByFile = await svc.GetDecksAsync(search: "bio-notes");
        Assert.Single(searchByFile);
        Assert.Equal("Alpha Biology", searchByFile[0].Title);

        // Update both title & notes when 0 questions exist
        var updateOk = await svc.UpdateDeckAsync(create1.Value, new UpdateDeckRequest(
            "Beta Systems Revised", "Updated verbatim notes text with plenty of characters to pass validation."));
        Assert.True(updateOk.Succeeded);

        var updatedDetail = await svc.GetDeckDetailAsync(create1.Value);
        Assert.Equal("Beta Systems Revised", updatedDetail!.Title);
        Assert.Contains("Updated verbatim notes", updatedDetail.NotesText);

        // Delete removes deck and updates dashboard
        var del = await svc.DeleteDeckAsync(create1.Value);
        Assert.True(del.Succeeded);
        Assert.Null(await svc.GetDeckDetailAsync(create1.Value));
        Assert.Equal(1, (await dashSvc.GetDashboardSummaryAsync()).TotalDecks);
    }

    [Fact]
    public async Task NotesEdit_BlockedOnceQuestionsExist_TitleEditAllowed_AndCascadeDeleteWorks()
    {
        var (conn, dbFactory) = await CreateDbAsync();
        await using var _ = conn;

        var svc = CreateDeckService(dbFactory, "user-a");
        var created = await svc.CreateDeckAsync(new CreateDeckRequest(
            "Algorithms", "Binary search halves the sorted interval on each comparison step.", NoteSourceType.PastedText, null));
        Assert.True(created.Succeeded);

        // Add a question + option + attempt + review state
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var q = new Question
            {
                DeckId = created.Value,
                QuestionText = "Complexity?",
                CorrectIndex = 0,
                Explanation = "Halving",
                Topic = "Search",
                SourceSentence = "Binary search halves the sorted interval on each comparison step."
            };
            q.Options.Add(new QuestionOption { Index = 0, Text = "O(log n)" });
            q.Attempts.Add(new Attempt { SelectedIndex = 0, IsCorrect = true, Sm2Quality = 5, SubmissionId = "sub-del-1" });
            q.ReviewState = new ReviewState { EaseFactor = 2.5, NextReviewUtc = DateTime.UtcNow };
            db.Questions.Add(q);
            await db.SaveChangesAsync();
        }

        var detail = await svc.GetDeckDetailAsync(created.Value);
        Assert.NotNull(detail);
        Assert.False(detail!.CanEditNotes);
        Assert.True(detail.CanStudy);

        // Attempting to change notes fails
        var blockedNotesEdit = await svc.UpdateDeckAsync(created.Value, new UpdateDeckRequest(
            "Algorithms Renamed", "Completely different notes text that should be rejected."));
        Assert.False(blockedNotesEdit.Succeeded);
        Assert.Contains("Create a new deck", blockedNotesEdit.ErrorMessage ?? string.Empty);

        // Title-only edit succeeds
        var titleOnlyEdit = await svc.UpdateDeckAsync(created.Value, new UpdateDeckRequest(
            "Algorithms Renamed", null));
        Assert.True(titleOnlyEdit.Succeeded);
        Assert.Equal("Algorithms Renamed", (await svc.GetDeckDetailAsync(created.Value))!.Title);

        // Cascade delete removes deck + question + option + attempt + review state
        var deleted = await svc.DeleteDeckAsync(created.Value);
        Assert.True(deleted.Succeeded);

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            Assert.Empty(await db.Decks.ToListAsync());
            Assert.Empty(await db.Questions.ToListAsync());
            Assert.Empty(await db.QuestionOptions.ToListAsync());
            Assert.Empty(await db.Attempts.ToListAsync());
            Assert.Empty(await db.ReviewStates.ToListAsync());
        }
    }

    [Fact]
    public async Task CrossUserIdGuesses_CannotReadUpdateOrDeleteAnotherUsersDeck()
    {
        var (conn, dbFactory) = await CreateDbAsync();
        await using var _ = conn;

        var aliceSvc = CreateDeckService(dbFactory, "user-a");
        var bobSvc = CreateDeckService(dbFactory, "user-b");

        var created = await aliceSvc.CreateDeckAsync(new CreateDeckRequest(
            "Alice Private Deck", "Confidential study notes belonging strictly to Alice.", NoteSourceType.PastedText, null));
        Assert.True(created.Succeeded);
        var aliceDeckId = created.Value;

        // Bob guesses Alice's exact deck ID
        Assert.Null(await bobSvc.GetDeckDetailAsync(aliceDeckId));

        var bobUpdate = await bobSvc.UpdateDeckAsync(aliceDeckId, new UpdateDeckRequest("Hacked Title", null));
        Assert.False(bobUpdate.Succeeded);
        Assert.True(bobUpdate.NotFound);

        var bobDelete = await bobSvc.DeleteDeckAsync(aliceDeckId);
        Assert.False(bobDelete.Succeeded);
        Assert.True(bobDelete.NotFound);

        // Alice's deck remains untouched
        var stillIntact = await aliceSvc.GetDeckDetailAsync(aliceDeckId);
        Assert.NotNull(stillIntact);
        Assert.Equal("Alice Private Deck", stillIntact!.Title);
    }

    [Fact]
    public async Task ExtractTextFromUpload_EnforcesExtensionSizeBinaryAndLengthBoundaries()
    {
        var (conn, dbFactory) = await CreateDbAsync();
        await using var _ = conn;

        var svc = CreateDeckService(dbFactory, "user-a", new DeckUploadOptions
        {
            MaxFileBytes = 2048,
            MinNotesLength = 20,
            MaxNotesLength = 500
        });

        // 1. Valid UTF-8 Markdown with BOM and path traversal filename -> sanitized & exact text
        var mdText = "# Heading\n\nPhotosynthesis converts light energy into chemical energy.";
        var bomBytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(mdText)).ToArray();
        await using (var ms = new MemoryStream(bomBytes))
        {
            var ok = await svc.ExtractTextFromUploadAsync(ms, "../../subdir/lecture.md", bomBytes.Length);
            Assert.True(ok.Succeeded);
            Assert.Equal("lecture.md", ok.SanitizedFileName);
            Assert.Equal(mdText, ok.ExtractedText);
        }

        // 2. Disallowed extension (.pdf)
        await using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(mdText)))
        {
            var badExt = await svc.ExtractTextFromUploadAsync(ms, "slides.pdf", mdText.Length);
            Assert.False(badExt.Succeeded);
            Assert.Contains(".txt", badExt.ErrorMessage ?? string.Empty);
        }

        // 3. Empty file
        await using (var ms = new MemoryStream())
        {
            var empty = await svc.ExtractTextFromUploadAsync(ms, "empty.txt", 0);
            Assert.False(empty.Succeeded);
        }

        // 4. Oversized declared size
        await using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(mdText)))
        {
            var oversizedDeclared = await svc.ExtractTextFromUploadAsync(ms, "big.txt", 10_000);
            Assert.False(oversizedDeclared.Succeeded);
        }

        // 5. Lying declared size whose actual stream exceeds MaxFileBytes (bounded stream protection)
        var actualBigBytes = Encoding.UTF8.GetBytes(new string('A', 3000));
        await using (var ms = new MemoryStream(actualBigBytes))
        {
            var oversizedStream = await svc.ExtractTextFromUploadAsync(ms, "lying.txt", 100);
            Assert.False(oversizedStream.Succeeded);
        }

        // 6. Binary content with NUL byte
        var binaryBytes = new byte[] { 0x48, 0x65, 0x6C, 0x00, 0x6F, 0x20, 0x77, 0x6F, 0x72, 0x6C, 0x64 };
        await using (var ms = new MemoryStream(binaryBytes))
        {
            var binary = await svc.ExtractTextFromUploadAsync(ms, "fake.txt", binaryBytes.Length);
            Assert.False(binary.Succeeded);
            Assert.Contains("Binary", binary.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        // 7. Invalid UTF-8 byte sequence
        var invalidUtf8 = new byte[] { 0xFF, 0xFE, 0xFD, 0xFC, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46 };
        await using (var ms = new MemoryStream(invalidUtf8))
        {
            var badUtf8 = await svc.ExtractTextFromUploadAsync(ms, "bad-encoding.txt", invalidUtf8.Length);
            Assert.False(badUtf8.Succeeded);
            Assert.Contains("UTF-8", badUtf8.ErrorMessage ?? string.Empty);
        }

        // 8. Too short text (< MinNotesLength)
        var shortBytes = Encoding.UTF8.GetBytes("Too short");
        await using (var ms = new MemoryStream(shortBytes))
        {
            var tooShort = await svc.ExtractTextFromUploadAsync(ms, "short.txt", shortBytes.Length);
            Assert.False(tooShort.Succeeded);
        }

        // 9. Too long text (> MaxNotesLength)
        var longBytes = Encoding.UTF8.GetBytes(new string('x', 600));
        await using (var ms = new MemoryStream(longBytes))
        {
            var tooLong = await svc.ExtractTextFromUploadAsync(ms, "long.txt", longBytes.Length);
            Assert.False(tooLong.Succeeded);
        }
    }

    private sealed class SimpleDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private sealed class StubAuthStateProvider(string? userId) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
            }

            var identity = new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, userId) },
                authenticationType: "TestAuth");
            return Task.FromResult(new AuthenticationState(new ClaimsPrincipal(identity)));
        }
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue => currentValue;
        public T Get(string? name) => currentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

