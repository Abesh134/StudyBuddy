using System.Net;
using System.Net.Mail;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using StudyBuddy.Models;

namespace StudyBuddy.Services;

public interface IStudyBuddyEmailSender : IEmailSender<ApplicationUser>
{
    bool IsConfigured { get; }
}

/// <summary>
/// Configurable SMTP email sender. When no SMTP provider is configured,
/// it does not claim an email was sent and never logs raw tokens in production.
/// </summary>
public sealed class ConfigurableEmailSender : IStudyBuddyEmailSender
{
    private readonly IOptions<EmailOptions> _options;
    private readonly ILogger<ConfigurableEmailSender> _logger;

    public ConfigurableEmailSender(
        IOptions<EmailOptions> options,
        ILogger<ConfigurableEmailSender> logger)
    {
        _options = options;
        _logger = logger;
    }

    public bool IsConfigured => _options.Value.IsConfigured;

    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink) =>
        SendAsync(
            email,
            "Confirm your Study Buddy email",
            $"Please confirm your Study Buddy account by <a href='{confirmationLink}'>clicking here</a>.");

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink) =>
        SendAsync(
            email,
            "Reset your Study Buddy password",
            $"Reset your Study Buddy password by <a href='{resetLink}'>clicking here</a>.");

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode) =>
        SendAsync(
            email,
            "Reset your Study Buddy password",
            $"Your Study Buddy password reset code is: <strong>{WebUtility.HtmlEncode(resetCode)}</strong>");

    private async Task SendAsync(string toEmail, string subject, string htmlBody)
    {
        var cfg = _options.Value;
        if (!cfg.IsConfigured)
        {
            _logger.LogWarning(
                "Email provider is not configured. Email '{Subject}' was NOT sent.",
                subject);
            return;
        }

        using var message = new MailMessage
        {
            From = new MailAddress(cfg.FromAddress!, cfg.FromName),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true
        };
        message.To.Add(toEmail);

        using var client = new SmtpClient(cfg.SmtpHost!, cfg.SmtpPort)
        {
            EnableSsl = cfg.UseSsl
        };

        if (!string.IsNullOrWhiteSpace(cfg.SmtpUsername))
        {
            client.Credentials = new NetworkCredential(cfg.SmtpUsername, cfg.SmtpPassword);
        }

        await client.SendMailAsync(message);
    }
}

