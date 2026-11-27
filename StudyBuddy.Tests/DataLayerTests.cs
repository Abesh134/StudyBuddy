using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StudyBuddy.Data;
using StudyBuddy.Models;
using Xunit;

namespace StudyBuddy.Tests;

public class DataLayerTests
{
    private static async Task<(SqliteConnection Connection, ServiceProvider Services)> CreateMigratedHostAsync(
        string environmentName,
        DevSeedOptions? seedOptions = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(environmentName));

        services.AddDbContextFactory<AppDbContext>(options =>
            options.UseSqlite(connection));
        services.AddScoped<AppDbContext>(sp =>
            sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
            })
            .AddEntityFrameworkStores<AppDbContext>()
            .AddDefaultTokenProviders();

        services.Configure<DevSeedOptions>(opts =>
        {
            if (seedOptions is null)
            {
                return;
            }

            opts.Enabled = seedOptions.Enabled;
            opts.DemoUserOne = seedOptions.DemoUserOne;
            opts.DemoUserTwo = seedOptions.DemoUserTwo;
        });

        services.AddScoped<DevDataSeeder>();

        var provider = services.BuildServiceProvider();
        var factory = provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        await db.Database.MigrateAsync();

        return (connection, provider);
    }

    [Fact]
    public async Task Migrations_CreateSchemaAndEnforceConstraints()
    {
        var (connection, provider) = await CreateMigratedHostAsync(Environments.Development);
        await using var _conn = connection;
        await using var _sp = provider;

        var factory = provider.GetRequiredService<IDbContextFactory<AppDbContext>>();

        var user = new ApplicationUser
        {
            UserName = "owner@example.test",
            Email = "owner@example.test",
            FullName = "Test Owner",
            StudyTimeZoneId = "UTC"
        };

        using (var scope = provider.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var createResult = await userManager.CreateAsync(user, "TestPass!123");
            Assert.True(createResult.Succeeded);
        }

        // Valid deck + question + option + attempt + review state
        int deckId;
        int questionId;
        await using (var db = await factory.CreateDbContextAsync())
        {
            var deck = new Deck
            {
                UserId = user.Id,
                Title = "Algorithms",
                NotesText = "Binary search runs in logarithmic time."
            };
            var question = new Question
            {
                QuestionText = "What is the time complexity of binary search?",
                CorrectIndex = 0,
                Explanation = "Each step halves the search space.",
                Topic = "Searching",
                SourceSentence = "Binary search runs in logarithmic time."
            };
            question.Options.Add(new QuestionOption { Index = 0, Text = "O(log n)" });
            question.Options.Add(new QuestionOption { Index = 1, Text = "O(n)" });
            question.Attempts.Add(new Attempt
            {
                SelectedIndex = 0,
                IsCorrect = true,
                Sm2Quality = 5,
                SubmissionId = "sub-unique-1"
            });
            question.ReviewState = new ReviewState
            {
                RepetitionCount = 1,
                IntervalDays = 1,
                EaseFactor = 2.6,
                NextReviewUtc = DateTime.UtcNow.AddDays(1),
                LastReviewedAtUtc = DateTime.UtcNow
            };
            deck.Questions.Add(question);
            db.Decks.Add(deck);
            await db.SaveChangesAsync();

            deckId = deck.Id;
            questionId = question.Id;
        }

        // Check constraint: CorrectIndex > 3 fails
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Questions.Add(new Question
            {
                DeckId = deckId,
                QuestionText = "Bad index",
                CorrectIndex = 4,
                Explanation = "Invalid",
                Topic = "Test"
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        // Unique constraint: duplicate (QuestionId, Index) fails
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.QuestionOptions.Add(new QuestionOption
            {
                QuestionId = questionId,
                Index = 0,
                Text = "Duplicate index"
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        // Unique constraint: duplicate Attempt.SubmissionId fails
        await using (var db = await factory.CreateDbContextAsync())
        {
            db.Attempts.Add(new Attempt
            {
                QuestionId = questionId,
                SelectedIndex = 0,
                IsCorrect = true,
                Sm2Quality = 5,
                SubmissionId = "sub-unique-1"
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        // Check constraint: ReviewState.EaseFactor < 1.3 fails
        await using (var db = await factory.CreateDbContextAsync())
        {
            var rs = await db.ReviewStates.SingleAsync(r => r.QuestionId == questionId);
            rs.EaseFactor = 1.1;
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        // Cascade delete: deleting deck removes questions, options, attempts, review states
        await using (var db = await factory.CreateDbContextAsync())
        {
            var deck = await db.Decks.SingleAsync(d => d.Id == deckId);
            db.Decks.Remove(deck);
            await db.SaveChangesAsync();

            Assert.Empty(await db.Questions.ToListAsync());
            Assert.Empty(await db.QuestionOptions.ToListAsync());
            Assert.Empty(await db.Attempts.ToListAsync());
            Assert.Empty(await db.ReviewStates.ToListAsync());
        }
    }

    [Fact]
    public async Task DevDataSeeder_SeedsTwoIsolatedAccountsIdempotentlyInDevelopment()
    {
        var seedOptions = new DevSeedOptions
        {
            Enabled = true,
            DemoUserOne = new DevSeedAccountOptions
            {
                Email = "alex.demo@studybuddy.local",
                Password = "DemoUser1!Pass",
                FullName = "Alex Rivera",
                StudyTimeZoneId = "America/New_York"
            },
            DemoUserTwo = new DevSeedAccountOptions
            {
                Email = "priya.demo@studybuddy.local",
                Password = "DemoUser2!Pass",
                FullName = "Priya Nair",
                StudyTimeZoneId = "Europe/London"
            }
        };

        var (connection, provider) = await CreateMigratedHostAsync(Environments.Development, seedOptions);
        await using var _conn = connection;
        await using var _sp = provider;

        using (var scope = provider.CreateScope())
        {
            var seeder = scope.ServiceProvider.GetRequiredService<DevDataSeeder>();
            await seeder.SeedIfEnabledAsync();
            // Run a second time to verify idempotency.
            await seeder.SeedIfEnabledAsync();
        }

        var factory = provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();

        var users = await db.Users.OrderBy(u => u.Email).ToListAsync();
        Assert.Equal(2, users.Count);

        var decks = await db.Decks
            .Include(d => d.Questions)
                .ThenInclude(q => q.Options)
            .Include(d => d.Questions)
                .ThenInclude(q => q.Attempts)
            .Include(d => d.Questions)
                .ThenInclude(q => q.ReviewState)
            .ToListAsync();

        Assert.Equal(2, decks.Count);
        Assert.Equal(2, decks.Select(d => d.UserId).Distinct().Count());

        foreach (var deck in decks)
        {
            Assert.Equal(10, deck.Questions.Count);

            foreach (var q in deck.Questions)
            {
                Assert.Equal(4, q.Options.Count);
                Assert.NotNull(q.SourceSentence);
                Assert.Contains(q.SourceSentence!, deck.NotesText, StringComparison.Ordinal);
                Assert.NotNull(q.ReviewState);

                if (q.Attempts.Count > 0)
                {
                    var latest = q.Attempts.OrderByDescending(a => a.AttemptedAtUtc).First();
                    Assert.Equal(latest.SelectedIndex == q.CorrectIndex, latest.IsCorrect);
                    Assert.Equal(latest.AttemptedAtUtc, q.ReviewState!.LastReviewedAtUtc);
                }
                else
                {
                    Assert.Null(q.ReviewState!.LastReviewedAtUtc);
                    Assert.Equal(0, q.ReviewState.RepetitionCount);
                }
            }
        }
    }

    [Fact]
    public async Task DevDataSeeder_NeverRunsInProduction()
    {
        var seedOptions = new DevSeedOptions
        {
            Enabled = true,
            DemoUserOne = new DevSeedAccountOptions
            {
                Email = "alex.demo@studybuddy.local",
                Password = "DemoUser1!Pass"
            },
            DemoUserTwo = new DevSeedAccountOptions
            {
                Email = "priya.demo@studybuddy.local",
                Password = "DemoUser2!Pass"
            }
        };

        var (connection, provider) = await CreateMigratedHostAsync(Environments.Production, seedOptions);
        await using var _conn = connection;
        await using var _sp = provider;

        using (var scope = provider.CreateScope())
        {
            var seeder = scope.ServiceProvider.GetRequiredService<DevDataSeeder>();
            await seeder.SeedIfEnabledAsync();
        }

        var factory = provider.GetRequiredService<IDbContextFactory<AppDbContext>>();
        await using var db = await factory.CreateDbContextAsync();

        Assert.Empty(await db.Users.ToListAsync());
        Assert.Empty(await db.Decks.ToListAsync());
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string environmentName) => EnvironmentName = environmentName;
        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "StudyBuddy.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
