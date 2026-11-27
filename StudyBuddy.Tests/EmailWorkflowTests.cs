using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StudyBuddy.Data;
using StudyBuddy.Models;
using StudyBuddy.Services;
using StudyBuddy.Services.Email;
using StudyBuddy.Services.Email.Templates;
using Xunit;

namespace StudyBuddy.Tests;

public class EmailWorkflowTests
{
    [Fact]
    public void VerificationEmailTemplate_IncludesBrandingGreetingExpiryAndEncodesHtml()
    {
        var rendered = VerificationEmail.Build(
            fullName: "Maya <script>alert(1)</script>",
            verificationUrl: "https://studybuddy.example/Account/ConfirmEmail?userId=1&code=tok",
            tokenLifespan: TimeSpan.FromHours(24),
            supportEmail: null);

        Assert.Equal("Verify your Study Buddy account", rendered.Subject);
        Assert.Contains("Study Buddy", rendered.HtmlBody);
        Assert.Contains("Verify Email Address", rendered.HtmlBody);
        Assert.Contains("24 hours", rendered.HtmlBody);
        Assert.Contains("Did not create this account?", rendered.HtmlBody);
        Assert.Contains("&lt;script&gt;", rendered.HtmlBody);
        Assert.DoesNotContain("<script>", rendered.HtmlBody);
        Assert.DoesNotContain("@keyframes", rendered.HtmlBody);
        Assert.DoesNotContain("mailto:", rendered.HtmlBody);

        // Plain-text fallback preserves raw URL and greeting
        Assert.Contains("https://studybuddy.example/Account/ConfirmEmail?userId=1&code=tok", rendered.TextBody);
        Assert.Contains("24 hours", rendered.TextBody);
    }

    [Fact]
    public void WelcomeEmailTemplate_IncludesHonestUpcomingFeaturesAndOptionalSupport()
    {
        var withoutSupport = WelcomeEmail.Build("Maya Lin", "https://studybuddy.example/dashboard", null);
        Assert.Equal("Welcome to Study Buddy! Your learning journey starts here.", withoutSupport.Subject);
        Assert.Contains("Hi Maya Lin,", withoutSupport.HtmlBody);
        Assert.Contains("Start Learning", withoutSupport.HtmlBody);
        Assert.Contains("https://studybuddy.example/dashboard", withoutSupport.HtmlBody);
        Assert.Contains("Upcoming", withoutSupport.HtmlBody);
        Assert.Contains("SM-2 Spaced Repetition", withoutSupport.HtmlBody);
        Assert.DoesNotContain("mailto:", withoutSupport.HtmlBody);

        var withSupport = WelcomeEmail.Build("Maya Lin", "https://studybuddy.example/dashboard", "help@studybuddy.example");
        Assert.Contains("mailto:help@studybuddy.example", withSupport.HtmlBody);
        Assert.Contains("help@studybuddy.example", withSupport.TextBody);
    }

    [Fact]
    public void TrustedUrlHelper_MasksEmailAndValidatesBaseUrl()
    {
        Assert.Equal("ma***@example.com", TrustedUrlHelper.MaskEmail("maya.lin@example.com"));
        Assert.Equal("a***@b.co", TrustedUrlHelper.MaskEmail("a@b.co"));

        var valid = TrustedUrlHelper.BuildAbsoluteUrl("https://app.studybuddy.edu", "https://localhost:7095/", "/dashboard");
        Assert.Equal("https://app.studybuddy.edu/dashboard", valid);

        var fallback = TrustedUrlHelper.BuildAbsoluteUrl("javascript:alert(1)", "https://localhost:7095/", "/dashboard");
        Assert.Equal("https://localhost:7095/dashboard", fallback);
    }

    [Fact]
    public async Task EndToEnd_RegisterVerifyWelcomeOnceRepeatedClickAndLogin()
    {
        var sentEmails = new List<CapturedResendCall>();
        await using var factory = new MockedEmailAppFactory((req, body) =>
        {
            sentEmails.Add(CapturedResendCall.Parse(req, body));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"id":"re_ok_{{sentEmails.Count}}"}""", Encoding.UTF8, "application/json")
            };
        });
        await factory.EnsureMigratedAsync();

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        // 1. Register -> sends branded verification email, redirects to Check Your Email
        var regGet = await client.GetAsync("/Account/Register");
        var regToken = ExtractAntiforgeryToken(await regGet.Content.ReadAsStringAsync());

        var regPost = await client.PostAsync("/Account/Register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = regToken,
            ["_handler"] = "register",
            ["Input.FullName"] = "Maya Lin",
            ["Input.Email"] = "maya.lin@example.test",
            ["Input.Password"] = "MayaStudy2026",
            ["Input.ConfirmPassword"] = "MayaStudy2026"
        }));

        Assert.Equal(HttpStatusCode.Redirect, regPost.StatusCode);
        var confirmRedirect = regPost.Headers.Location?.ToString() ?? string.Empty;
        Assert.Contains("/Account/RegisterConfirmation", confirmRedirect);

        // Check Your Email page shows masked email and envelope icon
        var checkEmailResp = await client.GetAsync(confirmRedirect);
        Assert.Equal(HttpStatusCode.OK, checkEmailResp.StatusCode);
        var checkEmailHtml = await checkEmailResp.Content.ReadAsStringAsync();
        Assert.Contains("Check your", checkEmailHtml);
        Assert.Contains("ma***@example.test", checkEmailHtml);
        Assert.Contains("sb-confirm-icon--mail", checkEmailHtml);

        // Exactly 1 verification email was sent
        Assert.Single(sentEmails);
        Assert.Equal(VerificationEmail.SubjectLine, sentEmails[0].Subject);
        Assert.Contains("Hi Maya Lin,", sentEmails[0].Text);

        var confirmUrlMatch = Regex.Match(sentEmails[0].Text, @"https?://[^\s]+/Account/ConfirmEmail\?[^\s]+");
        Assert.True(confirmUrlMatch.Success, "Expected confirmation URL in plain-text verification email.");
        var confirmPathAndQuery = new Uri(confirmUrlMatch.Value).PathAndQuery;

        // 2. Confirm email -> confirms account and triggers exactly ONE welcome email
        var verifyResp = await client.GetAsync(confirmPathAndQuery);
        Assert.Equal(HttpStatusCode.OK, verifyResp.StatusCode);
        var verifyHtml = await verifyResp.Content.ReadAsStringAsync();
        Assert.Contains("sb-confirm-icon--success", verifyHtml);
        Assert.Contains("Start Learning", verifyHtml);

        Assert.Equal(2, sentEmails.Count);
        Assert.Equal(WelcomeEmail.SubjectLine, sentEmails[1].Subject);
        Assert.Equal("maya.lin@example.test", sentEmails[1].To);
        Assert.False(string.IsNullOrWhiteSpace(sentEmails[1].IdempotencyKey));

        // 3. Repeated click on same confirmation link -> friendly verified UI, NO duplicate welcome email
        var repeatResp = await client.GetAsync(confirmPathAndQuery);
        Assert.Equal(HttpStatusCode.OK, repeatResp.StatusCode);
        Assert.Contains("already verified", await repeatResp.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, sentEmails.Count);

        // 4. Verify durable EmailNotification record in SQLite
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var notif = await db.EmailNotifications.SingleAsync();
            Assert.Equal(EmailNotificationService.WelcomeNotificationType, notif.NotificationType);
            Assert.Equal(EmailNotificationStatus.Accepted, notif.Status);
            Assert.Equal("re_ok_2", notif.ProviderMessageId);
            Assert.Equal(1, notif.AttemptCount);
            Assert.NotNull(notif.AcceptedAtUtc);
            Assert.Null(notif.LastErrorCode);
        }

        // 5. Log in -> succeeds and routes to /dashboard without sending any welcome email
        var loginGet = await client.GetAsync("/Account/Login");
        var loginToken = ExtractAntiforgeryToken(await loginGet.Content.ReadAsStringAsync());
        var loginPost = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = loginToken,
            ["_handler"] = "login",
            ["Input.Email"] = "maya.lin@example.test",
            ["Input.Password"] = "MayaStudy2026",
            ["Input.RememberMe"] = "false"
        }));

        Assert.Equal(HttpStatusCode.Redirect, loginPost.StatusCode);
        var loginLoc = loginPost.Headers.Location!;
        Assert.Equal("/dashboard", loginLoc.IsAbsoluteUri ? loginLoc.AbsolutePath : loginLoc.OriginalString);
        Assert.Equal(2, sentEmails.Count);
    }

    [Fact]
    public async Task FailedWelcomeEmail_PreservesConfirmationAndAllowsLogin()
    {
        var callIndex = 0;
        await using var factory = new MockedEmailAppFactory((_, _) =>
        {
            callIndex++;
            if (callIndex == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"id":"re_verify_1"}""", Encoding.UTF8, "application/json")
                };
            }

            // Fail welcome email with 500
            return new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("""{"message":"Temporary provider outage"}""", Encoding.UTF8, "application/json")
            };
        });
        await factory.EnsureMigratedAsync();

        string userId;
        string encodedToken;
        using (var scope = factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                UserName = "resilient@example.test",
                Email = "resilient@example.test",
                FullName = "Resilient Learner"
            };
            Assert.True((await userManager.CreateAsync(user, "Resilient2026")).Succeeded);
            userId = user.Id;
            var rawCode = await userManager.GenerateEmailConfirmationTokenAsync(user);
            encodedToken = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(rawCode));
        }

        // Simulate verification already sent (advance counter to 1 so next call is welcome = 500)
        callIndex = 1;

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        var confirmResp = await client.GetAsync($"/Account/ConfirmEmail?userId={Uri.EscapeDataString(userId)}&code={Uri.EscapeDataString(encodedToken)}");
        Assert.Equal(HttpStatusCode.OK, confirmResp.StatusCode);
        Assert.Contains("sb-confirm-icon--success", await confirmResp.Content.ReadAsStringAsync());

        // Email confirmation persisted and notification failure recorded safely
        using (var scope = factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var confirmedUser = await userManager.FindByIdAsync(userId);
            Assert.True(confirmedUser!.EmailConfirmed);

            var notif = await db.EmailNotifications.SingleAsync(n => n.UserId == userId);
            Assert.Equal(EmailNotificationStatus.Failed, notif.Status);
            Assert.Equal("ProviderError", notif.LastErrorCode);
            Assert.Equal(1, notif.AttemptCount);
        }

        // User can still log in cleanly despite failed welcome email
        var loginGet = await client.GetAsync("/Account/Login");
        var loginToken = ExtractAntiforgeryToken(await loginGet.Content.ReadAsStringAsync());
        var loginPost = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = loginToken,
            ["_handler"] = "login",
            ["Input.Email"] = "resilient@example.test",
            ["Input.Password"] = "Resilient2026",
            ["Input.RememberMe"] = "false"
        }));

        Assert.Equal(HttpStatusCode.Redirect, loginPost.StatusCode);
        var loginLoc = loginPost.Headers.Location!;
        Assert.Equal("/dashboard", loginLoc.IsAbsoluteUri ? loginLoc.AbsolutePath : loginLoc.OriginalString);
    }

    [Fact]
    public async Task InvalidConfirmationToken_ShowsErrorStateAndDoesNotSendWelcomeEmail()
    {
        var sendCount = 0;
        await using var factory = new MockedEmailAppFactory((_, _) =>
        {
            sendCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"re_unexpected"}""", Encoding.UTF8, "application/json")
            };
        });
        await factory.EnsureMigratedAsync();

        string userId;
        using (var scope = factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                UserName = "invalidtok@example.test",
                Email = "invalidtok@example.test",
                FullName = "Token Test"
            };
            Assert.True((await userManager.CreateAsync(user, "TokenPass2026")).Succeeded);
            userId = user.Id;
        }

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var badCode = Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes("definitely-not-a-valid-token"));
        var resp = await client.GetAsync($"/Account/ConfirmEmail?userId={Uri.EscapeDataString(userId)}&code={badCode}");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("sb-confirm-icon--error", html);
        Assert.Contains("Request New Verification Email", html);
        Assert.Equal(0, sendCount);
    }

    [Fact]
    public async Task VerificationResend_IsRateLimitedAndUncertainWelcomeDoesNotBlindlyRetry()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        IDbContextFactory<AppDbContext> dbFactory = new SimpleDbContextFactory(dbOptions);

        var user = new ApplicationUser
        {
            Id = "user-rl-1",
            UserName = "rl@example.test",
            Email = "rl@example.test",
            EmailConfirmed = true,
            FullName = "Rate Limit User"
        };

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        var stubEmail = new StubEmailService
        {
            NextVerificationResult = EmailSendResult.Sent("re_v1"),
            NextWelcomeResult = EmailSendResult.Failure(EmailSendStatus.Timeout, "Timed out")
        };

        var notificationService = new EmailNotificationService(
            dbFactory,
            stubEmail,
            NullLogger<EmailNotificationService>.Instance);

        // Rate-limited verification resend
        var firstVerify = await notificationService.SendVerificationEmailAsync(user, "https://localhost/Account/ConfirmEmail?x=1");
        var secondVerify = await notificationService.SendVerificationEmailAsync(user, "https://localhost/Account/ConfirmEmail?x=2");
        Assert.True(firstVerify.Succeeded);
        Assert.Equal(EmailSendStatus.DuplicateSuppressed, secondVerify.Status);
        Assert.Equal(1, stubEmail.VerificationCalls);

        // Uncertain welcome email (Timeout) must NOT blindly resend on next call
        var firstWelcome = await notificationService.SendWelcomeEmailIfNotSentAsync(user, "https://localhost/dashboard");
        var secondWelcome = await notificationService.SendWelcomeEmailIfNotSentAsync(user, "https://localhost/dashboard");
        Assert.Equal(EmailSendStatus.Timeout, firstWelcome.Status);
        Assert.Equal(EmailSendStatus.DuplicateSuppressed, secondWelcome.Status);
        Assert.Equal(1, stubEmail.WelcomeCalls);

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var notif = await db.EmailNotifications.SingleAsync(n => n.UserId == user.Id);
            Assert.Equal(EmailNotificationStatus.Uncertain, notif.Status);
            Assert.Equal("Timeout", notif.LastErrorCode);
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

    private sealed record CapturedResendCall(string To, string Subject, string Html, string Text, string? IdempotencyKey)
    {
        public static CapturedResendCall Parse(HttpRequestMessage req, string body)
        {
            using var doc = JsonDocument.Parse(body);
            req.Headers.TryGetValues("Idempotency-Key", out var idemValues);
            return new CapturedResendCall(
                To: doc.RootElement.GetProperty("to")[0].GetString() ?? string.Empty,
                Subject: doc.RootElement.GetProperty("subject").GetString() ?? string.Empty,
                Html: doc.RootElement.GetProperty("html").GetString() ?? string.Empty,
                Text: doc.RootElement.GetProperty("text").GetString() ?? string.Empty,
                IdempotencyKey: idemValues?.FirstOrDefault());
        }
    }

    private sealed class MockedEmailAppFactory(
        Func<HttpRequestMessage, string, HttpResponseMessage> responder) : WebApplicationFactory<Program>, IAsyncDisposable
    {
        private readonly string _dbFilePath = Path.Combine(Path.GetTempPath(), $"studybuddy-wf-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={_dbFilePath}");
            builder.UseSetting("DevSeed:Enabled", "false");
            builder.UseSetting("Email:Enabled", "true");
            builder.UseSetting("Email:Provider", "Resend");
            builder.UseSetting("Email:ApiKey", "re_mock_workflow_key");
            builder.UseSetting("Email:FromEmail", "onboarding@resend.dev");
            builder.UseSetting("Email:FromName", "Study Buddy");
            builder.UseSetting("Email:TimeoutSeconds", "15");

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new MockResendHttpClientFactory(responder));
            });
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

    private sealed class MockResendHttpClientFactory(
        Func<HttpRequestMessage, string, HttpResponseMessage> responder) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new CallbackHandler(responder), disposeHandler: false)
            {
                BaseAddress = new Uri("https://api.resend.com/")
            };
    }

    private sealed class CallbackHandler(
        Func<HttpRequestMessage, string, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is not null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : string.Empty;
            return responder(request, body);
        }
    }

    private sealed class SimpleDbContextFactory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private sealed class StubEmailService : IEmailService
    {
        public bool IsConfigured => true;
        public bool IsUsingResendTestDomain => true;
        public int VerificationCalls { get; private set; }
        public int WelcomeCalls { get; private set; }
        public EmailSendResult NextVerificationResult { get; set; } = EmailSendResult.Sent("v1");
        public EmailSendResult NextWelcomeResult { get; set; } = EmailSendResult.Sent("w1");

        public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default) =>
            Task.FromResult(EmailSendResult.Sent("ok"));

        public Task<EmailSendResult> SendEmailVerificationAsync(string toEmail, string? fullName, string verificationUrl, string? idempotencyKey = null, CancellationToken cancellationToken = default)
        {
            VerificationCalls++;
            return Task.FromResult(NextVerificationResult);
        }

        public Task<EmailSendResult> SendPasswordResetAsync(string toEmail, string? fullName, string resetUrl, string? idempotencyKey = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(EmailSendResult.Sent("r1"));

        public Task<EmailSendResult> SendWelcomeEmailAsync(string toEmail, string? fullName, string dashboardUrl, string? idempotencyKey = null, CancellationToken cancellationToken = default)
        {
            WelcomeCalls++;
            return Task.FromResult(NextWelcomeResult);
        }

        public Task<EmailSendResult> SendPasswordChangedNotificationAsync(string toEmail, string? fullName, DateTime changedAtUtc, string recoveryUrl, string? idempotencyKey = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(EmailSendResult.Sent("pc1"));

        public Task<EmailSendResult> SendSecurityNotificationAsync(string toEmail, string? fullName, string eventTitle, string eventDetails, DateTime occurredAtUtc, string? idempotencyKey = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(EmailSendResult.Sent("s1"));
    }
}

