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
using StudyBuddy.Data;
using StudyBuddy.Models;
using StudyBuddy.Services;
using StudyBuddy.Services.Email;
using StudyBuddy.Services.Email.Templates;
using Xunit;

namespace StudyBuddy.Tests;

public class PasswordRecoveryTests
{
    private const string ExpectedGenericForgotMessage =
        "If an account exists with this email address, you will receive instructions to reset your password.";

    [Fact]
    public void PasswordResetEmailTemplate_IncludesBrandingGreetingExpiryAndEncodesHtml()
    {
        var rendered = PasswordResetEmail.Build(
            fullName: "Sam <script>x</script>",
            resetUrl: "https://studybuddy.example/Account/ResetPassword?code=abc123",
            tokenLifespan: TimeSpan.FromHours(24),
            supportEmail: null);

        Assert.Equal("Reset your Study Buddy password", rendered.Subject);
        Assert.Contains("Study Buddy", rendered.HtmlBody);
        Assert.Contains("Reset Password", rendered.HtmlBody);
        Assert.Contains("24 hours", rendered.HtmlBody);
        Assert.Contains("Did not request a password reset?", rendered.HtmlBody);
        Assert.Contains("&lt;script&gt;", rendered.HtmlBody);
        Assert.DoesNotContain("<script>", rendered.HtmlBody);
        Assert.DoesNotContain("@keyframes", rendered.HtmlBody);
        Assert.Contains("https://studybuddy.example/Account/ResetPassword?code=abc123", rendered.TextBody);
    }

    [Fact]
    public void PasswordChangedEmailTemplate_IncludesTimestampRecoveryLinkAndNoSensitiveData()
    {
        var changedUtc = new DateTime(2026, 11, 28, 14, 30, 0, DateTimeKind.Utc);
        var rendered = PasswordChangedEmail.Build(
            fullName: "Sam Patel",
            changedAtUtc: changedUtc,
            recoveryUrl: "https://studybuddy.example/Account/ForgotPassword",
            supportEmail: "support@studybuddy.example");

        Assert.Equal("Your Study Buddy password has been updated", rendered.Subject);
        Assert.Contains("Hi Sam Patel,", rendered.HtmlBody);
        Assert.Contains("November 28, 2026 at 14:30 UTC", rendered.HtmlBody);
        Assert.Contains("Recover Your Account", rendered.HtmlBody);
        Assert.Contains("https://studybuddy.example/Account/ForgotPassword", rendered.HtmlBody);
        Assert.Contains("mailto:support@studybuddy.example", rendered.HtmlBody);
        Assert.Contains("November 28, 2026 at 14:30 UTC", rendered.TextBody);
        Assert.Contains("https://studybuddy.example/Account/ForgotPassword", rendered.TextBody);
    }

    [Fact]
    public async Task ForgotPassword_RegisteredVsUnknownEmail_ReturnIdenticalGenericResponse()
    {
        var sentEmails = new List<CapturedEmail>();
        await using var factory = new MockedRecoveryAppFactory((req, body) =>
        {
            sentEmails.Add(CapturedEmail.Parse(req, body));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"id":"re_fp_{{sentEmails.Count}}"}""", Encoding.UTF8, "application/json")
            };
        });
        await factory.EnsureMigratedAsync();
        await factory.CreateConfirmedUserAsync("sam@example.test", "Sam Patel", "InitialPass2026");

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        // 1. Unknown email -> redirects to ForgotPasswordConfirmation, sends 0 emails
        var getUnknown = await client.GetAsync("/Account/ForgotPassword");
        var tokUnknown = ExtractAntiforgeryToken(await getUnknown.Content.ReadAsStringAsync());
        var postUnknown = await client.PostAsync("/Account/ForgotPassword", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = tokUnknown,
            ["_handler"] = "forgot-password",
            ["Input.Email"] = "nobody@example.test"
        }));

        Assert.Equal(HttpStatusCode.Redirect, postUnknown.StatusCode);
        Assert.Equal("/Account/ForgotPasswordConfirmation", GetPath(postUnknown.Headers.Location));
        Assert.Empty(sentEmails);

        var confirmUnknownHtml = await (await client.GetAsync("/Account/ForgotPasswordConfirmation")).Content.ReadAsStringAsync();
        Assert.Contains(ExpectedGenericForgotMessage, confirmUnknownHtml);

        // 2. Registered confirmed email -> identical redirect & generic page, sends 1 reset email
        var getKnown = await client.GetAsync("/Account/ForgotPassword");
        var tokKnown = ExtractAntiforgeryToken(await getKnown.Content.ReadAsStringAsync());
        var postKnown = await client.PostAsync("/Account/ForgotPassword", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = tokKnown,
            ["_handler"] = "forgot-password",
            ["Input.Email"] = "sam@example.test"
        }));

        Assert.Equal(HttpStatusCode.Redirect, postKnown.StatusCode);
        Assert.Equal("/Account/ForgotPasswordConfirmation", GetPath(postKnown.Headers.Location));

        var confirmKnownHtml = await (await client.GetAsync("/Account/ForgotPasswordConfirmation")).Content.ReadAsStringAsync();
        Assert.Contains(ExpectedGenericForgotMessage, confirmKnownHtml);

        Assert.Single(sentEmails);
        Assert.Equal("sam@example.test", sentEmails[0].To);
        Assert.Equal(PasswordResetEmail.SubjectLine, sentEmails[0].Subject);
    }

    [Fact]
    public async Task ForgotPassword_RateLimitsPerAccountAndPerIp()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var dbOptions = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        IDbContextFactory<AppDbContext> dbFactory = new SimpleDbContextFactory(dbOptions);
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync();
        }

        var stubEmail = new CountingEmailService();
        var svc = new EmailNotificationService(dbFactory, stubEmail, NullLogger<EmailNotificationService>.Instance);

        var user1 = new ApplicationUser { Id = "u1", Email = "u1@example.test", EmailConfirmed = true, FullName = "U1" };

        // Per-account cooldown blocks immediate second call
        var first = await svc.SendPasswordResetEmailAsync(user1, "https://localhost/Account/ResetPassword?code=1", "10.0.0.1");
        var second = await svc.SendPasswordResetEmailAsync(user1, "https://localhost/Account/ResetPassword?code=2", "10.0.0.2");
        Assert.True(first.Succeeded);
        Assert.Equal(EmailSendStatus.DuplicateSuppressed, second.Status);

        // Per-IP window (5 requests per 5 min) blocks 6th distinct account from same IP
        for (var i = 2; i <= 5; i++)
        {
            var u = new ApplicationUser { Id = $"u{i}", Email = $"u{i}@example.test", EmailConfirmed = true, FullName = $"U{i}" };
            var res = await svc.SendPasswordResetEmailAsync(u, $"https://localhost/Account/ResetPassword?code={i}", "10.0.0.1");
            Assert.True(res.Succeeded);
        }

        var user6 = new ApplicationUser { Id = "u6", Email = "u6@example.test", EmailConfirmed = true, FullName = "U6" };
        var rateLimited = await svc.SendPasswordResetEmailAsync(user6, "https://localhost/Account/ResetPassword?code=6", "10.0.0.1");
        Assert.False(rateLimited.Succeeded);
        Assert.Equal(EmailSendStatus.RateLimited, rateLimited.Status);
        Assert.Equal(5, stubEmail.ResetCalls);
    }

    [Fact]
    public async Task EndToEnd_ResetPassword_ValidatesWeakMismatchReuseRotatesStampNotifiesAndUpdatesLogin()
    {
        var sentEmails = new List<CapturedEmail>();
        await using var factory = new MockedRecoveryAppFactory((req, body) =>
        {
            sentEmails.Add(CapturedEmail.Parse(req, body));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"id":"re_rst_{{sentEmails.Count}}"}""", Encoding.UTF8, "application/json")
            };
        });
        await factory.EnsureMigratedAsync();
        var user = await factory.CreateConfirmedUserAsync("alex@example.test", "Alex Rivera", "OldPassword2026");
        var originalStamp = user.SecurityStamp;

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        // 1. Trigger ForgotPassword
        var fpGet = await client.GetAsync("/Account/ForgotPassword");
        var fpTok = ExtractAntiforgeryToken(await fpGet.Content.ReadAsStringAsync());
        await client.PostAsync("/Account/ForgotPassword", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = fpTok,
            ["_handler"] = "forgot-password",
            ["Input.Email"] = "alex@example.test"
        }));

        Assert.Single(sentEmails);
        var resetUrlMatch = Regex.Match(sentEmails[0].Text, @"https?://[^\s]+/Account/ResetPassword\?code=[^\s]+");
        Assert.True(resetUrlMatch.Success);
        var resetPathAndQuery = new Uri(resetUrlMatch.Value).PathAndQuery;

        // 2. Weak password rejected (no uppercase/digit), 0 security emails sent
        var resetGet1 = await client.GetAsync(resetPathAndQuery);
        var resetHtml1 = await resetGet1.Content.ReadAsStringAsync();
        var weakPost = await client.PostAsync(resetPathAndQuery, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ExtractAntiforgeryToken(resetHtml1),
            ["_handler"] = "reset-password",
            ["Input.Code"] = ExtractHiddenCode(resetHtml1),
            ["Input.Email"] = "alex@example.test",
            ["Input.Password"] = "weakpassword",
            ["Input.ConfirmPassword"] = "weakpassword"
        }));
        Assert.Equal(HttpStatusCode.OK, weakPost.StatusCode);
        Assert.Single(sentEmails);

        // 3. Mismatched confirm password rejected
        var resetGet2 = await client.GetAsync(resetPathAndQuery);
        var resetHtml2 = await resetGet2.Content.ReadAsStringAsync();
        var mismatchPost = await client.PostAsync(resetPathAndQuery, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ExtractAntiforgeryToken(resetHtml2),
            ["_handler"] = "reset-password",
            ["Input.Code"] = ExtractHiddenCode(resetHtml2),
            ["Input.Email"] = "alex@example.test",
            ["Input.Password"] = "NewStrongPass2026",
            ["Input.ConfirmPassword"] = "DifferentPass2026"
        }));
        Assert.Equal(HttpStatusCode.OK, mismatchPost.StatusCode);
        Assert.Contains("do not match", await mismatchPost.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Single(sentEmails);

        // 4. Valid password reset -> redirects to confirmation, rotates SecurityStamp, sends security email
        var resetGet3 = await client.GetAsync(resetPathAndQuery);
        var resetHtml3 = await resetGet3.Content.ReadAsStringAsync();
        var hiddenCode = ExtractHiddenCode(resetHtml3);
        var validPost = await client.PostAsync(resetPathAndQuery, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ExtractAntiforgeryToken(resetHtml3),
            ["_handler"] = "reset-password",
            ["Input.Code"] = hiddenCode,
            ["Input.Email"] = "alex@example.test",
            ["Input.Password"] = "NewStrongPass2026",
            ["Input.ConfirmPassword"] = "NewStrongPass2026"
        }));

        Assert.Equal(HttpStatusCode.Redirect, validPost.StatusCode);
        Assert.Equal("/Account/ResetPasswordConfirmation", GetPath(validPost.Headers.Location));

        var confirmHtml = await (await client.GetAsync("/Account/ResetPasswordConfirmation")).Content.ReadAsStringAsync();
        Assert.Contains("sb-confirm-icon--success", confirmHtml);

        // Security email sent automatically
        Assert.Equal(2, sentEmails.Count);
        Assert.Equal(PasswordChangedEmail.SubjectLine, sentEmails[1].Subject);
        Assert.DoesNotContain("NewStrongPass2026", sentEmails[1].Html);
        Assert.DoesNotContain("NewStrongPass2026", sentEmails[1].Text);

        // SecurityStamp rotated (invalidating existing sessions on revalidation)
        using (var scope = factory.Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var updated = await userManager.FindByEmailAsync("alex@example.test");
            Assert.NotEqual(originalStamp, updated!.SecurityStamp);
        }

        // 5. Previously used reset token is rejected
        var resetGet4 = await client.GetAsync(resetPathAndQuery);
        var resetHtml4 = await resetGet4.Content.ReadAsStringAsync();
        var reusePost = await client.PostAsync(resetPathAndQuery, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ExtractAntiforgeryToken(resetHtml4),
            ["_handler"] = "reset-password",
            ["Input.Code"] = ExtractHiddenCode(resetHtml4),
            ["Input.Email"] = "alex@example.test",
            ["Input.Password"] = "AnotherStrong2026",
            ["Input.ConfirmPassword"] = "AnotherStrong2026"
        }));
        Assert.Equal(HttpStatusCode.OK, reusePost.StatusCode);
        Assert.Contains("invalid, expired, or has already been used", await reusePost.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);

        // 6. Old password rejected, new password succeeds
        var loginOldGet = await client.GetAsync("/Account/Login");
        var loginOldPost = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ExtractAntiforgeryToken(await loginOldGet.Content.ReadAsStringAsync()),
            ["_handler"] = "login",
            ["Input.Email"] = "alex@example.test",
            ["Input.Password"] = "OldPassword2026",
            ["Input.RememberMe"] = "false"
        }));
        Assert.Equal(HttpStatusCode.OK, loginOldPost.StatusCode);
        Assert.Contains("Invalid login attempt", await loginOldPost.Content.ReadAsStringAsync());

        var loginNewGet = await client.GetAsync("/Account/Login");
        var loginNewPost = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ExtractAntiforgeryToken(await loginNewGet.Content.ReadAsStringAsync()),
            ["_handler"] = "login",
            ["Input.Email"] = "alex@example.test",
            ["Input.Password"] = "NewStrongPass2026",
            ["Input.RememberMe"] = "false"
        }));
        Assert.Equal(HttpStatusCode.Redirect, loginNewPost.StatusCode);
        Assert.Equal("/dashboard", GetPath(loginNewPost.Headers.Location));
    }

    [Fact]
    public async Task ResetPassword_MissingToken_ShowsInvalidLinkState()
    {
        await using var factory = new MockedRecoveryAppFactory((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        await factory.EnsureMigratedAsync();

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var resp = await client.GetAsync("/Account/ResetPassword");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Contains("sb-confirm-icon--error", html);
        Assert.Contains("Request New Reset Link", html);
    }

    [Fact]
    public async Task AuthenticatedChangePassword_SendsSecurityEmailAndSurvivesProviderFailure()
    {
        var failNext = true;
        var sentEmails = new List<CapturedEmail>();
        await using var factory = new MockedRecoveryAppFactory((req, body) =>
        {
            sentEmails.Add(CapturedEmail.Parse(req, body));
            if (failNext)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("""{"message":"Temporary failure"}""", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"re_chg_ok"}""", Encoding.UTF8, "application/json")
            };
        });
        await factory.EnsureMigratedAsync();
        await factory.CreateConfirmedUserAsync("priya@example.test", "Priya Nair", "FirstPass2026");

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        // Sign in
        var loginGet = await client.GetAsync("/Account/Login");
        var loginPost = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ExtractAntiforgeryToken(await loginGet.Content.ReadAsStringAsync()),
            ["_handler"] = "login",
            ["Input.Email"] = "priya@example.test",
            ["Input.Password"] = "FirstPass2026",
            ["Input.RememberMe"] = "false"
        }));
        Assert.Equal(HttpStatusCode.Redirect, loginPost.StatusCode);

        // Wrong current password -> rejected, 0 emails sent
        var chgGet1 = await client.GetAsync("/Account/Manage/ChangePassword");
        var chgBad = await client.PostAsync("/Account/Manage/ChangePassword", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ExtractAntiforgeryToken(await chgGet1.Content.ReadAsStringAsync()),
            ["_handler"] = "change-password",
            ["Input.OldPassword"] = "WrongCurrent2026",
            ["Input.NewPassword"] = "SecondPass2026",
            ["Input.ConfirmPassword"] = "SecondPass2026"
        }));
        Assert.Equal(HttpStatusCode.OK, chgBad.StatusCode);
        Assert.Empty(sentEmails);

        // Valid change password even when Resend returns 500 -> password still updates!
        var chgGet2 = await client.GetAsync("/Account/Manage/ChangePassword");
        var chgOk = await client.PostAsync("/Account/Manage/ChangePassword", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = ExtractAntiforgeryToken(await chgGet2.Content.ReadAsStringAsync()),
            ["_handler"] = "change-password",
            ["Input.OldPassword"] = "FirstPass2026",
            ["Input.NewPassword"] = "SecondPass2026",
            ["Input.ConfirmPassword"] = "SecondPass2026"
        }));
        Assert.Equal(HttpStatusCode.Redirect, chgOk.StatusCode);
        Assert.Single(sentEmails);
        Assert.Equal(PasswordChangedEmail.SubjectLine, sentEmails[0].Subject);

        // Verify password actually updated in Identity store
        using var scope = factory.Services.CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var updatedUser = await userManager.FindByEmailAsync("priya@example.test");
        Assert.True(await userManager.CheckPasswordAsync(updatedUser!, "SecondPass2026"));
        Assert.False(await userManager.CheckPasswordAsync(updatedUser!, "FirstPass2026"));
    }

    private static string GetPath(Uri? uri)
    {
        Assert.NotNull(uri);
        return uri!.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString;
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

    private static string ExtractHiddenCode(string html)
    {
        var match = Regex.Match(
            html,
            @"name=""Input\.Code""[^>]*value=""([^""]+)""",
            RegexOptions.IgnoreCase);
        Assert.True(match.Success, "Expected hidden Input.Code field in ResetPassword form.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    private sealed record CapturedEmail(string To, string Subject, string Html, string Text)
    {
        public static CapturedEmail Parse(HttpRequestMessage req, string body)
        {
            using var doc = JsonDocument.Parse(body);
            return new CapturedEmail(
                To: doc.RootElement.GetProperty("to")[0].GetString() ?? string.Empty,
                Subject: doc.RootElement.GetProperty("subject").GetString() ?? string.Empty,
                Html: doc.RootElement.GetProperty("html").GetString() ?? string.Empty,
                Text: doc.RootElement.GetProperty("text").GetString() ?? string.Empty);
        }
    }

    private sealed class MockedRecoveryAppFactory(
        Func<HttpRequestMessage, string, HttpResponseMessage> responder) : WebApplicationFactory<Program>, IAsyncDisposable
    {
        private readonly string _dbFilePath = Path.Combine(Path.GetTempPath(), $"studybuddy-rec-{Guid.NewGuid():N}.db");

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={_dbFilePath}");
            builder.UseSetting("DevSeed:Enabled", "false");
            builder.UseSetting("Email:Enabled", "true");
            builder.UseSetting("Email:Provider", "Resend");
            builder.UseSetting("Email:ApiKey", "re_mock_recovery_key");
            builder.UseSetting("Email:FromEmail", "onboarding@resend.dev");
            builder.UseSetting("Email:FromName", "Study Buddy");
            builder.UseSetting("Email:TimeoutSeconds", "15");

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IHttpClientFactory>();
                services.AddSingleton<IHttpClientFactory>(new MockHttpClientFactory(responder));
            });
        }

        public async Task EnsureMigratedAsync()
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.MigrateAsync();
        }

        public async Task<ApplicationUser> CreateConfirmedUserAsync(string email, string fullName, string password)
        {
            using var scope = Services.CreateScope();
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser
            {
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                FullName = fullName
            };
            Assert.True((await userManager.CreateAsync(user, password)).Succeeded);
            return user;
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

    private sealed class MockHttpClientFactory(
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

    private sealed class CountingEmailService : IEmailService
    {
        public bool IsConfigured => true;
        public bool IsUsingResendTestDomain => true;
        public int ResetCalls { get; private set; }

        public Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken = default) =>
            Task.FromResult(EmailSendResult.Sent("ok"));

        public Task<EmailSendResult> SendEmailVerificationAsync(string toEmail, string? fullName, string verificationUrl, string? idempotencyKey = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(EmailSendResult.Sent("v1"));

        public Task<EmailSendResult> SendPasswordResetAsync(string toEmail, string? fullName, string resetUrl, string? idempotencyKey = null, CancellationToken cancellationToken = default)
        {
            ResetCalls++;
            return Task.FromResult(EmailSendResult.Sent($"r{ResetCalls}"));
        }

        public Task<EmailSendResult> SendWelcomeEmailAsync(string toEmail, string? fullName, string dashboardUrl, string? idempotencyKey = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(EmailSendResult.Sent("w1"));

        public Task<EmailSendResult> SendPasswordChangedNotificationAsync(string toEmail, string? fullName, DateTime changedAtUtc, string recoveryUrl, string? idempotencyKey = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(EmailSendResult.Sent("pc1"));

        public Task<EmailSendResult> SendSecurityNotificationAsync(string toEmail, string? fullName, string eventTitle, string eventDetails, DateTime occurredAtUtc, string? idempotencyKey = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(EmailSendResult.Sent("s1"));
    }
}

