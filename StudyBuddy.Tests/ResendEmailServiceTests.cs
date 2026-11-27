using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StudyBuddy.Data;
using StudyBuddy.Models;
using StudyBuddy.Services;
using StudyBuddy.Services.Email;
using Xunit;

namespace StudyBuddy.Tests;

public class ResendEmailServiceTests
{
    private static ResendEmailService CreateService(
        EmailOptions options,
        FakeHttpMessageHandler handler)
    {
        var factory = new StubHttpClientFactory(handler, options.BaseUrl);
        var monitor = new StaticOptionsMonitor<EmailOptions>(options);
        var templateService = new EmailTemplateService(
            monitor,
            Options.Create(new DataProtectionTokenProviderOptions()));
        return new ResendEmailService(factory, monitor, templateService, NullLogger<ResendEmailService>.Instance);
    }

    private static EmailOptions ValidTestOptions(int timeoutSeconds = 15) => new()
    {
        Enabled = true,
        Provider = "Resend",
        ApiKey = "re_mock_only_never_live",
        FromEmail = "onboarding@resend.dev",
        FromName = "Study Buddy",
        TimeoutSeconds = timeoutSeconds,
        BaseUrl = "https://api.resend.com/"
    };

    [Fact]
    public async Task SendAsync_Success_SendsExpectedResendPayloadAndHeaders()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;

        var handler = new FakeHttpMessageHandler(async (req, ct) =>
        {
            capturedRequest = req;
            capturedBody = await req.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"re_msg_12345"}""", Encoding.UTF8, "application/json")
            };
        });

        var service = CreateService(ValidTestOptions(), handler);

        var result = await service.SendAsync(new EmailMessage(
            To: "student@example.test",
            Subject: "Confirm your Study Buddy email",
            HtmlBody: "<p>Hello <b>Student</b></p>",
            TextBody: "Hello Student",
            IdempotencyKey: "idem-key-001",
            Category: "verification"));

        Assert.True(result.Succeeded);
        Assert.Equal(EmailSendStatus.Sent, result.Status);
        Assert.Equal("re_msg_12345", result.MessageId);
        Assert.Equal(200, result.HttpStatusCode);

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("https://api.resend.com/emails", capturedRequest.RequestUri?.ToString());
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization?.Scheme);
        Assert.Equal("re_mock_only_never_live", capturedRequest.Headers.Authorization?.Parameter);
        Assert.True(capturedRequest.Headers.TryGetValues("Idempotency-Key", out var idemValues));
        Assert.Contains("idem-key-001", idemValues);

        Assert.NotNull(capturedBody);
        using var doc = JsonDocument.Parse(capturedBody!);
        Assert.Equal("Study Buddy <onboarding@resend.dev>", doc.RootElement.GetProperty("from").GetString());
        Assert.Equal("student@example.test", doc.RootElement.GetProperty("to")[0].GetString());
        Assert.Equal("Confirm your Study Buddy email", doc.RootElement.GetProperty("subject").GetString());
        Assert.Equal("<p>Hello <b>Student</b></p>", doc.RootElement.GetProperty("html").GetString());
        Assert.Equal("Hello Student", doc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task SendAsync_MissingConfiguration_ReturnsNotConfiguredWithoutHttpCall()
    {
        var called = false;
        var handler = new FakeHttpMessageHandler((_, _) =>
        {
            called = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var options = new EmailOptions { Enabled = false, Provider = "Resend" };
        var service = CreateService(options, handler);

        var result = await service.SendAsync(new EmailMessage(
            "student@example.test", "Subj", "<p>Hi</p>", "Hi"));

        Assert.False(result.Succeeded);
        Assert.Equal(EmailSendStatus.NotConfigured, result.Status);
        Assert.False(called);
    }

    [Fact]
    public async Task SendAsync_RateLimited_ReturnsRateLimitedStatus()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("""{"name":"rate_limit_exceeded","message":"Too many requests"}""", Encoding.UTF8, "application/json")
            }));

        var service = CreateService(ValidTestOptions(), handler);
        var result = await service.SendAsync(new EmailMessage("student@example.test", "Subj", "<p>Hi</p>", "Hi"));

        Assert.False(result.Succeeded);
        Assert.Equal(EmailSendStatus.RateLimited, result.Status);
        Assert.Equal(429, result.HttpStatusCode);
    }

    [Fact]
    public async Task SendAsync_ResendUnverifiedDomainRestriction_ReturnsDomainRestricted()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(
                    """{"name":"validation_error","message":"You can only send testing emails to your own email address. To send emails to other recipients, please verify a domain."}""",
                    Encoding.UTF8,
                    "application/json")
            }));

        var service = CreateService(ValidTestOptions(), handler);
        var result = await service.SendAsync(new EmailMessage("other@example.test", "Subj", "<p>Hi</p>", "Hi"));

        Assert.False(result.Succeeded);
        Assert.Equal(EmailSendStatus.DomainRestricted, result.Status);
        Assert.Equal(403, result.HttpStatusCode);
        Assert.Contains("onboarding@resend.dev", result.ErrorMessage ?? string.Empty);
    }

    [Fact]
    public async Task SendAsync_ProviderError_ReturnsProviderErrorWithoutExposingApiKey()
    {
        var handler = new FakeHttpMessageHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("""{"name":"application_error","message":"Upstream failure"}""", Encoding.UTF8, "application/json")
            }));

        var service = CreateService(ValidTestOptions(), handler);
        var result = await service.SendAsync(new EmailMessage("student@example.test", "Subj", "<p>Hi</p>", "Hi"));

        Assert.False(result.Succeeded);
        Assert.Equal(EmailSendStatus.ProviderError, result.Status);
        Assert.Equal(500, result.HttpStatusCode);
        Assert.DoesNotContain("re_mock_only_never_live", result.ErrorMessage ?? string.Empty);
    }

    [Fact]
    public async Task SendAsync_CallerCancellation_PropagatesOperationCanceledException()
    {
        var handler = new FakeHttpMessageHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var service = CreateService(ValidTestOptions(), handler);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SendAsync(new EmailMessage("student@example.test", "Subj", "<p>Hi</p>", "Hi"), cts.Token));
    }

    [Fact]
    public async Task Templates_VerificationResetWelcomeAndSecurity_IncludeHtmlAndPlainText()
    {
        var bodies = new List<string>();
        var handler = new FakeHttpMessageHandler(async (req, ct) =>
        {
            bodies.Add(await req.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"re_tpl_1"}""", Encoding.UTF8, "application/json")
            };
        });

        var service = CreateService(ValidTestOptions(), handler);

        Assert.True((await service.SendEmailVerificationAsync("a@test.com", "Alex", "https://localhost/Account/ConfirmEmail?userId=1&code=abc")).Succeeded);
        Assert.True((await service.SendPasswordResetAsync("a@test.com", "Alex", "https://localhost/Account/ResetPassword?code=xyz")).Succeeded);
        Assert.True((await service.SendWelcomeEmailAsync("a@test.com", "Alex", "https://localhost/dashboard")).Succeeded);
        Assert.True((await service.SendSecurityNotificationAsync("a@test.com", "Alex", "Password changed", "Your password was updated.", DateTime.UtcNow)).Succeeded);

        Assert.Equal(4, bodies.Count);
        foreach (var json in bodies)
        {
            using var doc = JsonDocument.Parse(json);
            Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("html").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(doc.RootElement.GetProperty("text").GetString()));
        }
    }

    [Fact]
    public async Task IdentitySender_DecodesHtmlUrlForPlainTextAndSuppressesRapidDuplicates()
    {
        var callCount = 0;
        string? capturedText = null;
        string? capturedHtml = null;

        var handler = new FakeHttpMessageHandler(async (req, ct) =>
        {
            callCount++;
            var body = await req.Content!.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            capturedText = doc.RootElement.GetProperty("text").GetString();
            capturedHtml = doc.RootElement.GetProperty("html").GetString();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"id":"re_dup_1"}""", Encoding.UTF8, "application/json")
            };
        });

        var resendService = CreateService(ValidTestOptions(), handler);
        var identitySender = new ConfigurableEmailSender(resendService, NullLogger<ConfigurableEmailSender>.Instance);
        var user = new ApplicationUser { FullName = "Alex", Email = "alex@example.test" };

        const string encodedUrl = "https://localhost/Account/ConfirmEmail?userId=42&amp;code=tok123";
        await identitySender.SendConfirmationLinkAsync(user, "alex@example.test", encodedUrl);
        // Immediate duplicate within 30s cooldown should be suppressed
        await identitySender.SendConfirmationLinkAsync(user, "alex@example.test", encodedUrl);

        Assert.Equal(1, callCount);
        Assert.Contains("userId=42&code=tok123", capturedText ?? string.Empty);
        Assert.Contains("userId=42&amp;code=tok123", capturedHtml ?? string.Empty);
    }

    [Fact]
    public void EmailOptions_ToString_NeverExposesApiKey()
    {
        var opts = ValidTestOptions();
        var text = opts.ToString();

        Assert.DoesNotContain("re_mock_only_never_live", text);
        Assert.Contains("ApiKeyConfigured=True", text);
    }

    [Fact]
    public void LocalUserSecrets_LoadResendConfigurationWithoutExposingApiKey()
    {
        var config = new ConfigurationBuilder()
            .AddUserSecrets<Program>(optional: true)
            .Build();

        var options = config.GetSection(EmailOptions.SectionName).Get<EmailOptions>();
        Assert.NotNull(options);
        Assert.True(options!.Enabled);
        Assert.Equal("Resend", options.Provider, ignoreCase: true);
        Assert.Equal("onboarding@resend.dev", options.FromEmail, ignoreCase: true);
        Assert.False(string.IsNullOrWhiteSpace(options.ApiKey));
        Assert.True(options.IsConfigured);
        Assert.True(options.IsUsingResendTestDomain);
    }

    [Fact]
    public async Task DevEmailEndpoints_RequireAuthInDevAndAreNotMappedInProduction()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"studybuddy-email-test-{Guid.NewGuid():N}.db");
        try
        {
            await using (var prodFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment(Environments.Production);
                b.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={dbPath}");
                b.UseSetting("Email:Enabled", "false");
            }))
            {
                var prodClient = prodFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
                var statusResp = await prodClient.GetAsync("/Account/Dev/EmailStatus");
                Assert.Equal(HttpStatusCode.NotFound, statusResp.StatusCode);
            }

            await using (var devFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            {
                b.UseEnvironment(Environments.Development);
                b.UseSetting("ConnectionStrings:DefaultConnection", $"Data Source={dbPath}");
                b.UseSetting("Email:Enabled", "false");
            }))
            {
                var devClient = devFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
                var unauthResp = await devClient.GetAsync("/Account/Dev/EmailStatus");
                Assert.Equal(HttpStatusCode.Redirect, unauthResp.StatusCode);
                Assert.Contains("/Account/Login", unauthResp.Headers.Location?.ToString() ?? string.Empty);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(dbPath))
            {
                try { File.Delete(dbPath); } catch { }
            }
        }
    }

    private sealed class FakeHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            responder(request, cancellationToken);
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler, string baseUrl) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false)
            {
                BaseAddress = new Uri(baseUrl)
            };
    }

    private sealed class StaticOptionsMonitor<T>(T currentValue) : IOptionsMonitor<T>
    {
        public T CurrentValue => currentValue;
        public T Get(string? name) => currentValue;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

