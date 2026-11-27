using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using StudyBuddy.Services.Email;

namespace StudyBuddy.Services;

/// <summary>
/// Server-side Resend REST API email delivery service using IHttpClientFactory.
/// Never logs or exposes the configured API key.
/// </summary>
public sealed class ResendEmailService : IEmailService
{
    public const string HttpClientName = "Resend";

    private static readonly EmailAddressAttribute EmailValidator = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<EmailOptions> _optionsMonitor;
    private readonly IEmailTemplateService _templateService;
    private readonly ILogger<ResendEmailService> _logger;

    public ResendEmailService(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<EmailOptions> optionsMonitor,
        IEmailTemplateService templateService,
        ILogger<ResendEmailService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _optionsMonitor = optionsMonitor;
        _templateService = templateService;
        _logger = logger;
    }

    public bool IsConfigured => _optionsMonitor.CurrentValue.IsConfigured;

    public bool IsUsingResendTestDomain => _optionsMonitor.CurrentValue.IsUsingResendTestDomain;

    public async Task<EmailSendResult> SendAsync(
        EmailMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var options = _optionsMonitor.CurrentValue;
        if (!options.IsConfigured)
        {
            _logger.LogWarning(
                "Email delivery skipped for category '{Category}' because Resend email settings are not enabled or incomplete.",
                message.Category ?? "general");
            return EmailSendResult.NotConfigured(
                "Email delivery is not configured or enabled on this server.");
        }

        var recipient = message.To?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(recipient) || !EmailValidator.IsValid(recipient))
        {
            return EmailSendResult.InvalidRequest("A valid recipient email address is required.");
        }

        if (string.IsNullOrWhiteSpace(message.Subject))
        {
            return EmailSendResult.InvalidRequest("Email subject is required.");
        }

        if (string.IsNullOrWhiteSpace(message.HtmlBody) && string.IsNullOrWhiteSpace(message.TextBody))
        {
            return EmailSendResult.InvalidRequest("Email content (HTML or plain text) is required.");
        }

        var payload = new ResendSendRequest(
            From: options.FormattedFrom,
            To: new[] { recipient },
            Subject: message.Subject.Trim(),
            Html: string.IsNullOrWhiteSpace(message.HtmlBody) ? null : message.HtmlBody,
            Text: string.IsNullOrWhiteSpace(message.TextBody) ? null : message.TextBody);

        var json = JsonSerializer.Serialize(payload, JsonOptions);

        using var request = new HttpRequestMessage(HttpMethod.Post, "emails")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey!.Trim());

        if (!string.IsNullOrWhiteSpace(message.IdempotencyKey))
        {
            var safeKey = message.IdempotencyKey.Trim();
            if (safeKey.Length > 256)
            {
                safeKey = safeKey[..256];
            }

            request.Headers.TryAddWithoutValidation("Idempotency-Key", safeKey);
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        if (client.BaseAddress is null && Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var baseUri))
        {
            client.BaseAddress = baseUri;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(options.EffectiveTimeout);

        try
        {
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token);

            var responseBody = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            var statusCode = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                string? messageId = null;
                try
                {
                    var okPayload = JsonSerializer.Deserialize<ResendSendResponse>(responseBody, JsonOptions);
                    messageId = okPayload?.Id;
                }
                catch (JsonException)
                {
                    // Successful HTTP status even if response JSON differs.
                }

                _logger.LogInformation(
                    "Resend accepted email (category={Category}, status={StatusCode}, messageId={MessageId}).",
                    message.Category ?? "general",
                    statusCode,
                    messageId ?? "unknown");

                return EmailSendResult.Sent(messageId, statusCode);
            }

            var providerError = TryExtractProviderError(responseBody);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                _logger.LogWarning(
                    "Resend rate-limited email delivery (category={Category}, status={StatusCode}).",
                    message.Category ?? "general",
                    statusCode);

                return EmailSendResult.Failure(
                    EmailSendStatus.RateLimited,
                    "Email delivery is temporarily rate-limited. Please wait a moment and try again.",
                    statusCode);
            }

            if (IsDomainRestrictionError(response.StatusCode, providerError, options.IsUsingResendTestDomain))
            {
                _logger.LogWarning(
                    "Resend rejected recipient due to unverified-domain restriction (category={Category}, status={StatusCode}).",
                    message.Category ?? "general",
                    statusCode);

                return EmailSendResult.Failure(
                    EmailSendStatus.DomainRestricted,
                    "Resend test sender (onboarding@resend.dev) can only deliver to your verified Resend account email address until a custom sending domain is verified.",
                    statusCode);
            }

            _logger.LogError(
                "Resend API returned HTTP {StatusCode} for category '{Category}': {ProviderError}",
                statusCode,
                message.Category ?? "general",
                providerError ?? "No error details returned.");

            return EmailSendResult.Failure(
                EmailSendStatus.ProviderError,
                "Email delivery failed at the email provider. Please try again later.",
                statusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Resend API request timed out after {TimeoutSeconds}s for category '{Category}'.",
                options.EffectiveTimeout.TotalSeconds,
                message.Category ?? "general");

            return EmailSendResult.Failure(
                EmailSendStatus.Timeout,
                "The email delivery request timed out. Please try again.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(
                ex,
                "Network error while calling Resend API for category '{Category}'.",
                message.Category ?? "general");

            return EmailSendResult.Failure(
                EmailSendStatus.NetworkError,
                "Unable to reach the email delivery service due to a network error.");
        }
    }

    public Task<EmailSendResult> SendEmailVerificationAsync(
        string toEmail,
        string? fullName,
        string verificationUrl,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        var rendered = _templateService.RenderVerificationEmail(fullName, verificationUrl);
        return SendAsync(
            new EmailMessage(
                To: toEmail,
                Subject: rendered.Subject,
                HtmlBody: rendered.HtmlBody,
                TextBody: rendered.TextBody,
                IdempotencyKey: idempotencyKey,
                Category: "verification"),
            cancellationToken);
    }

    public Task<EmailSendResult> SendPasswordResetAsync(
        string toEmail,
        string? fullName,
        string resetUrl,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        var rendered = _templateService.RenderPasswordResetEmail(fullName, resetUrl);
        return SendAsync(
            new EmailMessage(
                To: toEmail,
                Subject: rendered.Subject,
                HtmlBody: rendered.HtmlBody,
                TextBody: rendered.TextBody,
                IdempotencyKey: idempotencyKey,
                Category: "password-reset"),
            cancellationToken);
    }

    public Task<EmailSendResult> SendWelcomeEmailAsync(
        string toEmail,
        string? fullName,
        string dashboardUrl,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        var rendered = _templateService.RenderWelcomeEmail(fullName, dashboardUrl);
        return SendAsync(
            new EmailMessage(
                To: toEmail,
                Subject: rendered.Subject,
                HtmlBody: rendered.HtmlBody,
                TextBody: rendered.TextBody,
                IdempotencyKey: idempotencyKey,
                Category: "welcome"),
            cancellationToken);
    }

    public Task<EmailSendResult> SendPasswordChangedNotificationAsync(
        string toEmail,
        string? fullName,
        DateTime changedAtUtc,
        string recoveryUrl,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        var rendered = _templateService.RenderPasswordChangedEmail(fullName, changedAtUtc, recoveryUrl);
        return SendAsync(
            new EmailMessage(
                To: toEmail,
                Subject: rendered.Subject,
                HtmlBody: rendered.HtmlBody,
                TextBody: rendered.TextBody,
                IdempotencyKey: idempotencyKey,
                Category: "password-changed"),
            cancellationToken);
    }

    public Task<EmailSendResult> SendSecurityNotificationAsync(
        string toEmail,
        string? fullName,
        string eventTitle,
        string eventDetails,
        DateTime occurredAtUtc,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default)
    {
        var rendered = _templateService.RenderSecurityNotificationEmail(fullName, eventTitle, eventDetails, occurredAtUtc);
        return SendAsync(
            new EmailMessage(
                To: toEmail,
                Subject: rendered.Subject,
                HtmlBody: rendered.HtmlBody,
                TextBody: rendered.TextBody,
                IdempotencyKey: idempotencyKey,
                Category: "security-notification"),
            cancellationToken);
    }

    private static string? TryExtractProviderError(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<ResendErrorResponse>(responseBody, JsonOptions);
            if (!string.IsNullOrWhiteSpace(parsed?.Message))
            {
                return parsed.Message;
            }
        }
        catch (JsonException)
        {
            // Ignore malformed error body.
        }

        return null;
    }

    private static bool IsDomainRestrictionError(
        HttpStatusCode statusCode,
        string? providerError,
        bool isUsingTestDomain)
    {
        if (statusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.UnprocessableEntity))
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(providerError))
        {
            return isUsingTestDomain && statusCode == HttpStatusCode.Forbidden;
        }

        return providerError.Contains("testing emails", StringComparison.OrdinalIgnoreCase) ||
               providerError.Contains("verify a domain", StringComparison.OrdinalIgnoreCase) ||
               providerError.Contains("domain is not verified", StringComparison.OrdinalIgnoreCase) ||
               providerError.Contains("only send testing emails to your own email", StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ResendSendRequest(
        [property: JsonPropertyName("from")] string From,
        [property: JsonPropertyName("to")] string[] To,
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("html")] string? Html,
        [property: JsonPropertyName("text")] string? Text);

    private sealed record ResendSendResponse(
        [property: JsonPropertyName("id")] string? Id);

    private sealed record ResendErrorResponse(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("message")] string? Message);
}

