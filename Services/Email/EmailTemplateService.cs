using System.ComponentModel.DataAnnotations;
using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using StudyBuddy.Services.Email.Templates;

namespace StudyBuddy.Services.Email;

public sealed record RenderedEmail(string Subject, string HtmlBody, string TextBody);

public interface IEmailTemplateService
{
    RenderedEmail RenderVerificationEmail(string? fullName, string verificationUrl);
    RenderedEmail RenderWelcomeEmail(string? fullName, string dashboardUrl);
    RenderedEmail RenderPasswordResetEmail(string? fullName, string resetUrl);
    RenderedEmail RenderPasswordChangedEmail(string? fullName, DateTime changedAtUtc, string recoveryUrl);
    RenderedEmail RenderSecurityNotificationEmail(string? fullName, string eventTitle, string eventDetails, DateTime occurredAtUtc);
}

public sealed class EmailTemplateService : IEmailTemplateService
{
    private static readonly EmailAddressAttribute EmailValidator = new();

    private readonly IOptionsMonitor<EmailOptions> _emailOptions;
    private readonly IOptions<DataProtectionTokenProviderOptions> _tokenOptions;

    public EmailTemplateService(
        IOptionsMonitor<EmailOptions> emailOptions,
        IOptions<DataProtectionTokenProviderOptions> tokenOptions)
    {
        _emailOptions = emailOptions;
        _tokenOptions = tokenOptions;
    }

    public RenderedEmail RenderVerificationEmail(string? fullName, string verificationUrl) =>
        VerificationEmail.Build(fullName, verificationUrl, GetTokenLifespan(), GetValidSupportEmail());

    public RenderedEmail RenderWelcomeEmail(string? fullName, string dashboardUrl) =>
        WelcomeEmail.Build(fullName, dashboardUrl, GetValidSupportEmail());

    public RenderedEmail RenderPasswordResetEmail(string? fullName, string resetUrl) =>
        PasswordResetEmail.Build(fullName, resetUrl, GetTokenLifespan(), GetValidSupportEmail());

    public RenderedEmail RenderPasswordChangedEmail(string? fullName, DateTime changedAtUtc, string recoveryUrl) =>
        PasswordChangedEmail.Build(fullName, changedAtUtc, recoveryUrl, GetValidSupportEmail());

    public RenderedEmail RenderSecurityNotificationEmail(
        string? fullName,
        string eventTitle,
        string eventDetails,
        DateTime occurredAtUtc)
    {
        var greeting = string.IsNullOrWhiteSpace(fullName) ? "Hi there," : $"Hi {fullName.Trim()},";
        var timestamp = occurredAtUtc.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'");
        var safeGreeting = WebUtility.HtmlEncode(greeting);
        var safeDetails = WebUtility.HtmlEncode(eventDetails);

        var bodyHtml =
            $"<p style=\"margin:0 0 14px 0;font-size:15px;line-height:1.65;color:#172B4D;\">{safeGreeting}</p>" +
            $"<p style=\"margin:0 0 14px 0;font-size:15px;line-height:1.65;color:#334155;\">{safeDetails}</p>" +
            $"<p style=\"margin:0;font-size:13px;color:#64748B;\"><strong>Recorded time:</strong> {timestamp}</p>";

        var secondaryNoteHtml =
            "<p style=\"margin:0;font-size:13px;line-height:1.55;color:#64748B;\">" +
            "If you did not perform this action, change your password immediately." +
            "</p>";

        var html = EmailLayoutBuilder.Build(
            preheader: $"Study Buddy security notice: {eventTitle}",
            badgeText: "Security Notice",
            badgeBackground: "#FEF08A",
            badgeColor: "#713F12",
            headline: eventTitle,
            bodyHtml: bodyHtml,
            ctaLabel: null,
            ctaUrl: null,
            secondaryNoteHtml: secondaryNoteHtml,
            supportEmail: GetValidSupportEmail());

        var text =
            $"{greeting}\n\n" +
            $"Study Buddy Security Notice: {eventTitle}\n" +
            $"{eventDetails}\n" +
            $"Recorded time: {timestamp}\n\n" +
            "If you did not perform this action, change your password immediately.";

        return new RenderedEmail($"Study Buddy security notice: {eventTitle}", html, text);
    }

    private TimeSpan GetTokenLifespan()
    {
        var lifespan = _tokenOptions.Value.TokenLifespan;
        return lifespan > TimeSpan.Zero ? lifespan : TimeSpan.FromHours(24);
    }

    private string? GetValidSupportEmail()
    {
        var candidate = _emailOptions.CurrentValue.SupportEmail?.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        return EmailValidator.IsValid(candidate) ? candidate : null;
    }
}
