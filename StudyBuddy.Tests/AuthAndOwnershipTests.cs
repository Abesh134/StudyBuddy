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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using StudyBuddy.Data;
using StudyBuddy.Models;
using StudyBuddy.Services;
using Xunit;

namespace StudyBuddy.Tests;

public class AuthAndOwnershipTests
{
    private sealed class TestAppFactory : WebApplicationFactory<Program>, IAsyncDisposable
    {
        private readonly string _environmentName;
        private readonly string _dbFilePath;

        public TestAppFactory(string environmentName = "Development")
        {
            _environmentName = environmentName;
            _dbFilePath = Path.Combine(
                Path.GetTempPath(),
                $"studybuddy-test-{Guid.NewGuid():N}.db");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment(_environmentName);
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
                try { File.Delete(_dbFilePath); } catch { /* best-effort cleanup */ }
            }
        }
    }

    private static string ExtractAntiforgeryToken(string html)
    {
        var match = Regex.Match(
            html,
            @"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""",
            RegexOptions.IgnoreCase);
        Assert.True(match.Success, "Expected antiforgery token input in HTML form.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/Account/Login")]
    [InlineData("/Account/Register")]
    [InlineData("/Account/ForgotPassword")]
    [InlineData("/Account/ResendEmailConfirmation")]
    [InlineData("/Account/Lockout")]
    public async Task PublicRoutes_ReturnOkWithoutAuthentication(string path)
    {
        await using var factory = new TestAppFactory();
        await factory.EnsureMigratedAsync();

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("/dashboard")]
    [InlineData("/study")]
    [InlineData("/study/1")]
    [InlineData("/decks")]
    [InlineData("/decks/new")]
    [InlineData("/decks/1")]
    [InlineData("/review")]
    [InlineData("/stats")]
    [InlineData("/settings")]
    [InlineData("/Account/Manage/ChangePassword")]
    public async Task ProtectedRoutes_RedirectUnauthenticatedUsersToLogin(string path)
    {
        await using var factory = new TestAppFactory();
        await factory.EnsureMigratedAsync();

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.ToString() ?? string.Empty;
        Assert.Contains("/Account/Login", location, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublicLandingPage_RendersHeroAndSamplePreviewWithoutLeakingUserData()
    {
        await using var factory = new TestAppFactory();
        await factory.EnsureMigratedAsync();

        using (var scope = factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var secretUser = new ApplicationUser
            {
                UserName = "private.user@example.test",
                Email = "private.user@example.test",
                EmailConfirmed = true,
                FullName = "Private Secret Name"
            };
            Assert.True((await userManager.CreateAsync(secretUser, "SecretPass123")).Succeeded);

            db.Decks.Add(new Deck
            {
                UserId = secretUser.Id,
                Title = "Confidential Private Deck Title",
                NotesText = "Confidential study notes"
            });
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Study smarter.", html);
        Assert.Contains("Remember longer.", html);
        Assert.Contains("Interactive Sample", html);
        Assert.Contains("Pause motion", html);
        Assert.Contains("/images/hero/book.svg", html);
        Assert.Contains("/Account/Register", html);
        Assert.Contains("/Account/Login", html);

        // Must never leak private user or deck data on public landing page
        Assert.DoesNotContain("Private Secret Name", html);
        Assert.DoesNotContain("Confidential Private Deck Title", html);
        Assert.DoesNotContain("private.user@example.test", html);
    }

    [Fact]
    public async Task StyledRegisterThenLogin_CreatesAccountAndRoutesToDashboardWithoutSocialLogin()
    {
        await using var factory = new TestAppFactory();
        await factory.EnsureMigratedAsync();

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        // 1. Load styled Register page and verify progressive enhancement hooks & no social login
        var regPage = await client.GetAsync("/Account/Register");
        Assert.Equal(HttpStatusCode.OK, regPage.StatusCode);
        var regHtml = await regPage.Content.ReadAsStringAsync();
        Assert.Contains("register-illustration.svg", regHtml);
        Assert.Contains("data-password-strength", regHtml);
        Assert.Contains("data-password-toggle", regHtml);
        Assert.DoesNotContain("or continue with", regHtml, StringComparison.OrdinalIgnoreCase);

        var regToken = ExtractAntiforgeryToken(regHtml);
        var regForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = regToken,
            ["_handler"] = "register",
            ["Input.FullName"] = "Clara Okafor",
            ["Input.Email"] = "clara@example.test",
            ["Input.Password"] = "ClaraStudy2026",
            ["Input.ConfirmPassword"] = "ClaraStudy2026"
        });

        var postReg = await client.PostAsync("/Account/Register", regForm);
        Assert.Equal(HttpStatusCode.Redirect, postReg.StatusCode);
        var regLocation = postReg.Headers.Location;
        Assert.NotNull(regLocation);
        Assert.Equal("/Account/Login", regLocation!.IsAbsoluteUri ? regLocation.AbsolutePath : regLocation.OriginalString);

        // 2. Load styled Login page and sign in with newly registered account
        var loginPage = await client.GetAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.OK, loginPage.StatusCode);
        var loginHtml = await loginPage.Content.ReadAsStringAsync();
        Assert.Contains("login-illustration.svg", loginHtml);
        Assert.Contains("data-password-toggle", loginHtml);
        Assert.DoesNotContain("or continue with", loginHtml, StringComparison.OrdinalIgnoreCase);

        var loginToken = ExtractAntiforgeryToken(loginHtml);
        var loginForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = loginToken,
            ["_handler"] = "login",
            ["Input.Email"] = "clara@example.test",
            ["Input.Password"] = "ClaraStudy2026",
            ["Input.RememberMe"] = "false"
        });

        var postLogin = await client.PostAsync("/Account/Login", loginForm);
        Assert.Equal(HttpStatusCode.Redirect, postLogin.StatusCode);
        var loginLocation = postLogin.Headers.Location;
        Assert.NotNull(loginLocation);
        Assert.Equal("/dashboard", loginLocation!.IsAbsoluteUri ? loginLocation.AbsolutePath : loginLocation.OriginalString);

        // 3. Verify authenticated dashboard renders Clara's profile
        var dashboard = await client.GetAsync("/dashboard");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        var dashboardHtml = await dashboard.Content.ReadAsStringAsync();
        Assert.Contains("Clara Okafor", dashboardHtml);
    }

    [Fact]
    public async Task TwoAccounts_LoginDashboardOpenRedirectGuardAndLogoutWork()
    {
        await using var factory = new TestAppFactory();
        await factory.EnsureMigratedAsync();

        using (var scope = factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

            var userA = new ApplicationUser
            {
                UserName = "alice@example.test",
                Email = "alice@example.test",
                EmailConfirmed = true,
                FullName = "Alice Mercer"
            };
            var userB = new ApplicationUser
            {
                UserName = "bob@example.test",
                Email = "bob@example.test",
                EmailConfirmed = true,
                FullName = "Bob Tanaka"
            };

            Assert.True((await userManager.CreateAsync(userA, "AlicePass123")).Succeeded);
            Assert.True((await userManager.CreateAsync(userB, "BobPass123")).Succeeded);
        }

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        // Request login page with malicious external ReturnUrl to test open-redirect guard
        var loginPageResponse = await client.GetAsync("/Account/Login?ReturnUrl=https://evil.example/steal");
        Assert.Equal(HttpStatusCode.OK, loginPageResponse.StatusCode);
        var loginHtml = await loginPageResponse.Content.ReadAsStringAsync();
        var token = ExtractAntiforgeryToken(loginHtml);

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["_handler"] = "login",
            ["Input.Email"] = "alice@example.test",
            ["Input.Password"] = "AlicePass123",
            ["Input.RememberMe"] = "false"
        });

        var postLogin = await client.PostAsync("/Account/Login?ReturnUrl=https://evil.example/steal", form);
        Assert.Equal(HttpStatusCode.Redirect, postLogin.StatusCode);
        var loginLocation = postLogin.Headers.Location;
        Assert.NotNull(loginLocation);
        Assert.DoesNotContain("evil.example", loginLocation!.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/dashboard", loginLocation.IsAbsoluteUri ? loginLocation.AbsolutePath : loginLocation.OriginalString);

        // Access protected dashboard as Alice
        var dashboardResponse = await client.GetAsync("/dashboard");
        Assert.Equal(HttpStatusCode.OK, dashboardResponse.StatusCode);
        var dashboardHtml = await dashboardResponse.Content.ReadAsStringAsync();
        Assert.Contains("Alice Mercer", dashboardHtml);
        Assert.Contains("0-day streak", dashboardHtml);
        Assert.Contains("/study", dashboardHtml);
        Assert.Contains("/decks", dashboardHtml);
        Assert.Contains("/stats", dashboardHtml);
        Assert.DoesNotContain("Bob Tanaka", dashboardHtml);

        // Logout via antiforgery-protected POST endpoint
        var logoutToken = ExtractAntiforgeryToken(dashboardHtml);
        var logoutForm = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = logoutToken,
            ["returnUrl"] = "/"
        });
        var postLogout = await client.PostAsync("/Account/Logout", logoutForm);
        Assert.Equal(HttpStatusCode.Redirect, postLogout.StatusCode);
        Assert.Equal("/", postLogout.Headers.Location?.ToString());

        // Protected dashboard now redirects to login again
        var afterLogout = await client.GetAsync("/dashboard");
        Assert.Equal(HttpStatusCode.Redirect, afterLogout.StatusCode);
        Assert.Contains("/Account/Login", afterLogout.Headers.Location?.ToString() ?? string.Empty);
    }

    [Fact]
    public async Task AccountLockout_LocksOutAfterFiveFailedAttempts()
    {
        await using var factory = new TestAppFactory();
        await factory.EnsureMigratedAsync();

        using (var scope = factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                UserName = "locktest@example.test",
                Email = "locktest@example.test",
                EmailConfirmed = true,
                FullName = "Lockout Test"
            };
            Assert.True((await userManager.CreateAsync(user, "ValidPass123")).Succeeded);
        }

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        HttpResponseMessage? lastResponse = null;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var getLogin = await client.GetAsync("/Account/Login");
            var token = ExtractAntiforgeryToken(await getLogin.Content.ReadAsStringAsync());

            var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["_handler"] = "login",
                ["Input.Email"] = "locktest@example.test",
                ["Input.Password"] = "WrongPass999",
                ["Input.RememberMe"] = "false"
            });

            lastResponse = await client.PostAsync("/Account/Login", form);
        }

        Assert.NotNull(lastResponse);
        Assert.Equal(HttpStatusCode.Redirect, lastResponse!.StatusCode);
        var lockoutLocation = lastResponse.Headers.Location;
        Assert.NotNull(lockoutLocation);
        Assert.Equal("/Account/Lockout", lockoutLocation!.IsAbsoluteUri ? lockoutLocation.AbsolutePath : lockoutLocation.OriginalString);
    }

    [Fact]
    public async Task ProductionRegisterConfirmation_NeverExposesTokensWithoutEmailProvider()
    {
        await using var factory = new TestAppFactory(Environments.Production);
        await factory.EnsureMigratedAsync();

        using (var scope = factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                UserName = "prod@example.test",
                Email = "prod@example.test",
                FullName = "Prod User"
            };
            Assert.True((await userManager.CreateAsync(user, "ProdPass123")).Succeeded);
        }

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/Account/RegisterConfirmation?email=prod@example.test");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("/Account/ConfirmEmail", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("code=", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("no email delivery provider is currently configured", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OwnerScopedDeckService_IsolatesDecksAndQuestionsBetweenTwoAccounts()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connection)
            .Options;

        IDbContextFactory<AppDbContext> dbFactory = new TestDbContextFactory(options);
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();

            var userA = new ApplicationUser { Id = "user-a", UserName = "a@test", Email = "a@test", FullName = "User A" };
            var userB = new ApplicationUser { Id = "user-b", UserName = "b@test", Email = "b@test", FullName = "User B" };
            db.Users.AddRange(userA, userB);

            var deckA = new Deck { UserId = userA.Id, Title = "User A Deck", NotesText = "Notes A" };
            deckA.Questions.Add(new Question
            {
                QuestionText = "QA?",
                CorrectIndex = 0,
                Explanation = "Exp A",
                Topic = "Topic A"
            });

            var deckB = new Deck { UserId = userB.Id, Title = "User B Deck", NotesText = "Notes B" };
            deckB.Questions.Add(new Question
            {
                QuestionText = "QB?",
                CorrectIndex = 1,
                Explanation = "Exp B",
                Topic = "Topic B"
            });

            db.Decks.AddRange(deckA, deckB);
            await db.SaveChangesAsync();
        }

        int deckAId, deckBId, questionAId, questionBId;
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            deckAId = (await db.Decks.SingleAsync(d => d.UserId == "user-a")).Id;
            deckBId = (await db.Decks.SingleAsync(d => d.UserId == "user-b")).Id;
            questionAId = (await db.Questions.SingleAsync(q => q.DeckId == deckAId)).Id;
            questionBId = (await db.Questions.SingleAsync(q => q.DeckId == deckBId)).Id;
        }

        // Act as User A
        var userAService = new OwnerScopedDeckService(
            dbFactory,
            new CurrentUserService(new StubAuthStateProvider("user-a"), new HttpContextAccessor(), dbFactory));

        var userADecks = await userAService.GetMyDecksAsync();
        Assert.Single(userADecks);
        Assert.Equal(deckAId, userADecks[0].Id);
        Assert.NotNull(await userAService.GetMyDeckByIdAsync(deckAId));
        Assert.Null(await userAService.GetMyDeckByIdAsync(deckBId));
        Assert.True(await userAService.OwnsDeckAsync(deckAId));
        Assert.False(await userAService.OwnsDeckAsync(deckBId));
        Assert.True(await userAService.OwnsQuestionAsync(questionAId));
        Assert.False(await userAService.OwnsQuestionAsync(questionBId));

        // Act as unauthenticated user
        var anonService = new OwnerScopedDeckService(
            dbFactory,
            new CurrentUserService(new StubAuthStateProvider(null), new HttpContextAccessor(), dbFactory));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => anonService.GetMyDecksAsync());
        Assert.False(await anonService.OwnsDeckAsync(deckAId));
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
