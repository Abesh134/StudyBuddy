using System.Net;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StudyBuddy.Data;
using StudyBuddy.Models;
using StudyBuddy.Services;
using Xunit;

namespace StudyBuddy.Tests;

public class DashboardAndStudyTests
{
    [Fact]
    public async Task DashboardService_StrictlyIsolatesMetricsDecksAndWeakTopicsBetweenTwoUsers()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        IDbContextFactory<AppDbContext> dbFactory = new TestDbContextFactory(options);

        var nowUtc = DateTime.UtcNow;

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();

            var userA = new ApplicationUser { Id = "dash-a", UserName = "a@test", Email = "a@test", FullName = "Alice Mercer", StudyTimeZoneId = "UTC" };
            var userB = new ApplicationUser { Id = "dash-b", UserName = "b@test", Email = "b@test", FullName = "Bob Tanaka", StudyTimeZoneId = "UTC" };
            db.Users.AddRange(userA, userB);

            // User A: Deck 1 with 2 questions (1 due, 2 attempts on topic "Algorithms" = 50% weak) + Deck 2 with 0 questions
            var deckA1 = new Deck { UserId = userA.Id, Title = "Alice CS Notes", NotesText = "Notes", UpdatedAtUtc = nowUtc };
            var qA1 = new Question
            {
                QuestionText = "Q1",
                CorrectIndex = 0,
                Explanation = "E1",
                Topic = "Algorithms",
                ReviewState = new ReviewState { NextReviewUtc = nowUtc.AddHours(-2), EaseFactor = 2.5 }
            };
            qA1.Attempts.Add(new Attempt { SelectedIndex = 0, IsCorrect = true, Sm2Quality = 4, AttemptedAtUtc = nowUtc.AddMinutes(-10), SubmissionId = "sub-a1" });
            qA1.Attempts.Add(new Attempt { SelectedIndex = 1, IsCorrect = false, Sm2Quality = 2, AttemptedAtUtc = nowUtc.AddMinutes(-5), SubmissionId = "sub-a2" });

            var qA2 = new Question
            {
                QuestionText = "Q2",
                CorrectIndex = 1,
                Explanation = "E2",
                Topic = "Data Structures",
                ReviewState = new ReviewState { NextReviewUtc = nowUtc.AddDays(3), EaseFactor = 2.5 }
            };
            deckA1.Questions.Add(qA1);
            deckA1.Questions.Add(qA2);

            var deckA2 = new Deck { UserId = userA.Id, Title = "Alice Empty Draft", NotesText = "Draft", UpdatedAtUtc = nowUtc.AddMinutes(-30) };

            // User B: Deck with 3 questions, 2 due, 2 failed attempts on "Organic Chemistry"
            var deckB = new Deck { UserId = userB.Id, Title = "Bob Secret Biology", NotesText = "Secret", UpdatedAtUtc = nowUtc };
            for (var i = 0; i < 3; i++)
            {
                var qB = new Question
                {
                    QuestionText = $"QB{i}",
                    CorrectIndex = 0,
                    Explanation = "EB",
                    Topic = "Organic Chemistry",
                    ReviewState = new ReviewState { NextReviewUtc = i < 2 ? nowUtc.AddHours(-1) : nowUtc.AddDays(5), EaseFactor = 2.5 }
                };
                if (i < 2)
                {
                    qB.Attempts.Add(new Attempt { SelectedIndex = 2, IsCorrect = false, Sm2Quality = 1, AttemptedAtUtc = nowUtc.AddMinutes(-2), SubmissionId = $"sub-b{i}" });
                }
                deckB.Questions.Add(qB);
            }

            db.Decks.AddRange(deckA1, deckA2, deckB);
            await db.SaveChangesAsync();
        }

        // Act as User A
        var currentA = new CurrentUserService(new StubAuthStateProvider("dash-a"), new HttpContextAccessor(), dbFactory);
        var streakA = new UserStreakService(dbFactory, currentA);
        var dashA = new DashboardService(dbFactory, currentA, streakA);

        var summaryA = await dashA.GetDashboardSummaryAsync();
        Assert.Equal("Alice Mercer", summaryA.UserFullName);
        Assert.Equal(2, summaryA.TotalDecks);
        Assert.Equal(2, summaryA.TotalQuestions);
        Assert.Equal(2, summaryA.TotalAttempts);
        Assert.Equal(50.0, summaryA.OverallAccuracyPercent);
        Assert.Equal(1, summaryA.DueReviewCount);
        Assert.Equal(1, summaryA.DailyStreakDays);
        Assert.True(summaryA.StudiedToday);
        Assert.Single(summaryA.WeakTopics);
        Assert.Equal("Algorithms", summaryA.WeakTopics[0].Topic);
        Assert.DoesNotContain(summaryA.WeakTopics, t => t.Topic == "Organic Chemistry");
        Assert.DoesNotContain(summaryA.RecentDecks, d => d.Title == "Bob Secret Biology");

        var studyDecksA = await dashA.GetStudyDecksAsync();
        Assert.Equal(2, studyDecksA.Count);
        Assert.True(studyDecksA.Single(d => d.Title == "Alice CS Notes").CanStudy);
        Assert.False(studyDecksA.Single(d => d.Title == "Alice Empty Draft").CanStudy);
    }

    [Fact]
    public async Task DashboardService_NewUserHasNoFabricatedMetrics()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        IDbContextFactory<AppDbContext> dbFactory = new TestDbContextFactory(options);

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
            db.Users.Add(new ApplicationUser { Id = "new-u", UserName = "new@test", Email = "new@test", FullName = "New Learner", StudyTimeZoneId = "UTC" });
            await db.SaveChangesAsync();
        }

        var current = new CurrentUserService(new StubAuthStateProvider("new-u"), new HttpContextAccessor(), dbFactory);
        var dash = new DashboardService(dbFactory, current, new UserStreakService(dbFactory, current));

        var summary = await dash.GetDashboardSummaryAsync();
        Assert.Equal(0, summary.TotalDecks);
        Assert.Equal(0, summary.TotalQuestions);
        Assert.Equal(0, summary.TotalAttempts);
        Assert.Null(summary.OverallAccuracyPercent);
        Assert.Equal(0, summary.DueReviewCount);
        Assert.Equal(0, summary.DailyStreakDays);
        Assert.Null(summary.ContinueStudyingDeckId);
        Assert.Empty(summary.RecentDecks);
        Assert.Empty(summary.WeakTopics);
    }

    [Fact]
    public async Task DashboardAndStudyPages_EnforceIsolationDisabledQuizAndGettingStarted()
    {
        await using var factory = new TestDashboardAppFactory();
        await factory.EnsureMigratedAsync();

        int userAReadyDeckId;
        int userBDeckId;

        using (var scope = factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var newUser = new ApplicationUser { UserName = "brandnew@example.test", Email = "brandnew@example.test", EmailConfirmed = true, FullName = "Brand New" };
            var userA = new ApplicationUser { UserName = "alice@example.test", Email = "alice@example.test", EmailConfirmed = true, FullName = "Alice Mercer" };
            var userB = new ApplicationUser { UserName = "bob@example.test", Email = "bob@example.test", EmailConfirmed = true, FullName = "Bob Tanaka" };

            Assert.True((await userManager.CreateAsync(newUser, "NewPass2026")).Succeeded);
            Assert.True((await userManager.CreateAsync(userA, "AlicePass2026")).Succeeded);
            Assert.True((await userManager.CreateAsync(userB, "BobPass2026")).Succeeded);

            var readyDeck = new Deck { UserId = userA.Id, Title = "Alice Ready Deck", NotesText = "Notes" };
            readyDeck.Questions.Add(new Question { QuestionText = "Q?", CorrectIndex = 0, Explanation = "Exp", Topic = "Topic1" });
            var emptyDeck = new Deck { UserId = userA.Id, Title = "Alice Empty Deck", NotesText = "Draft" };
            var bobDeck = new Deck { UserId = userB.Id, Title = "Bob Confidential Deck", NotesText = "Secret" };
            bobDeck.Questions.Add(new Question { QuestionText = "QB?", CorrectIndex = 0, Explanation = "ExpB", Topic = "SecretTopic" });

            db.Decks.AddRange(readyDeck, emptyDeck, bobDeck);
            await db.SaveChangesAsync();

            userAReadyDeckId = readyDeck.Id;
            userBDeckId = bobDeck.Id;
        }

        // 1. New user sees getting-started guidance and no fabricated accuracy
        var newClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        await SignInAsync(newClient, "brandnew@example.test", "NewPass2026");

        var newDashHtml = await (await newClient.GetAsync("/dashboard")).Content.ReadAsStringAsync();
        Assert.Contains("Brand New", newDashHtml);
        Assert.Contains("Getting Started", newDashHtml);
        Assert.Contains("/decks/new", newDashHtml);
        Assert.Contains("/study", newDashHtml);
        Assert.Contains("/review", newDashHtml);
        Assert.Contains("/stats", newDashHtml);
        Assert.DoesNotContain("Bob Confidential Deck", newDashHtml);

        // 2. Alice sees her own decks, disabled quiz on 0-question deck, and zero leakage of Bob's deck
        var aliceClient = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true });
        await SignInAsync(aliceClient, "alice@example.test", "AlicePass2026");

        var studyHtml = await (await aliceClient.GetAsync("/study")).Content.ReadAsStringAsync();
        Assert.Contains("Alice Ready Deck", studyHtml);
        Assert.Contains("Alice Empty Deck", studyHtml);
        Assert.Contains($"/study/{userAReadyDeckId}", studyHtml);
        Assert.Contains("Quiz unavailable", studyHtml);
        Assert.DoesNotContain("Bob Confidential Deck", studyHtml);

        // 3. Direct ID attempt to Bob's deck returns Deck not found
        var forbiddenStudyHtml = await (await aliceClient.GetAsync($"/study/{userBDeckId}")).Content.ReadAsStringAsync();
        Assert.Contains("Deck not found", forbiddenStudyHtml);
        Assert.DoesNotContain("Bob Confidential Deck", forbiddenStudyHtml);
    }

    private static async Task SignInAsync(HttpClient client, string email, string password)
    {
        var get = await client.GetAsync("/Account/Login");
        var html = await get.Content.ReadAsStringAsync();
        var match = Regex.Match(html, @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""", RegexOptions.IgnoreCase);
        Assert.True(match.Success);
        var token = WebUtility.HtmlDecode(match.Groups[1].Value);

        var post = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["_handler"] = "login",
            ["Input.Email"] = email,
            ["Input.Password"] = password,
            ["Input.RememberMe"] = "false"
        }));
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
    }

    private sealed class TestDashboardAppFactory : WebApplicationFactory<Program>, IAsyncDisposable
    {
        private readonly string _dbFilePath = Path.Combine(Path.GetTempPath(), $"studybuddy-dash-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={_dbFilePath}");
            builder.UseSetting("DevSeed:Enabled", "false");
            builder.UseSetting("Email:Enabled", "false");
        }

        public async Task EnsureMigratedAsync()
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.MigrateAsync();
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbFilePath))
            {
                try { File.Delete(_dbFilePath); } catch { }
            }
        }
    }

    private sealed class TestDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
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
}

