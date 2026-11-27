using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using StudyBuddy.Models;

namespace StudyBuddy.Services;

public interface IStudyBuddyEmailSender : IEmailSender<ApplicationUser>
{
    bool IsConfigured { get; }
    bool IsUsingResendTestDomain { get; }
}

/// <summary>
/// Integrates ASP.NET Core Identity email workflows with IEmailService (Resend),
/// adding short-window duplicate submission protection and safe HTML/plain-text handling.
/// </summary>
public sealed class ConfigurableEmailSender : IStudyBuddyEmailSender
{
    private static readonly TimeSpan DuplicateCooldown = TimeSpan.FromSeconds(30);

    private readonly IEmailService _emailService;
    private readonly ILogger<ConfigurableEmailSender> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentSends = new(StringComparer.OrdinalIgnoreCase);

    public ConfigurableEmailSender(
        IEmailService emailService,
        ILogger<ConfigurableEmailSender> logger)
    {
        _emailService = emailService;
        _logger = logger;
    }

    public bool IsConfigured => _emailService.IsConfigured;

    public bool IsUsingResendTestDomain => _emailService.IsUsingResendTestDomain;

    public async Task SendConfirmationLinkAsync(
        ApplicationUser user,
        string email,
        string confirmationLink)
    {
        var rawUrl = WebUtility.HtmlDecode(confirmationLink);
        var key = BuildCooldownKey("confirm", email);
        if (!TryAcquireSendWindow(key))
        {
            _logger.LogInformation("Duplicate confirmation email suppressed within cooldown window.");
            return;
        }

        var idempotencyKey = BuildIdempotencyKey("confirm", email, rawUrl);
        var result = await _emailService.SendEmailVerificationAsync(
            email,
            user.FullName,
            rawUrl,
            idempotencyKey);

        if (!result.Succeeded)
        {
            ReleaseSendWindow(key);
        }
    }

    public async Task SendPasswordResetLinkAsync(
        ApplicationUser user,
        string email,
        string resetLink)
    {
        var rawUrl = WebUtility.HtmlDecode(resetLink);
        var key = BuildCooldownKey("reset-link", email);
        if (!TryAcquireSendWindow(key))
        {
            _logger.LogInformation("Duplicate password reset email suppressed within cooldown window.");
            return;
        }

        var idempotencyKey = BuildIdempotencyKey("reset-link", email, rawUrl);
        var result = await _emailService.SendPasswordResetAsync(
            email,
            user.FullName,
            rawUrl,
            idempotencyKey);

        if (!result.Succeeded)
        {
            ReleaseSendWindow(key);
        }
    }

    public async Task SendPasswordResetCodeAsync(
        ApplicationUser user,
        string email,
        string resetCode)
    {
        var key = BuildCooldownKey("reset-code", email);
        if (!TryAcquireSendWindow(key))
        {
            _logger.LogInformation("Duplicate password reset code email suppressed within cooldown window.");
            return;
        }

        var safeCode = WebUtility.HtmlEncode(resetCode);
        var result = await _emailService.SendAsync(new EmailMessage(
            To: email,
            Subject: "Your Study Buddy password reset code",
            HtmlBody: $"<p>Your Study Buddy password reset code is: <strong>{safeCode}</strong></p>",
            TextBody: $"Your Study Buddy password reset code is: {resetCode}",
            IdempotencyKey: BuildIdempotencyKey("reset-code", email, resetCode),
            Category: "password-reset"));

        if (!result.Succeeded)
        {
            ReleaseSendWindow(key);
        }
    }

    private static string BuildCooldownKey(string category, string email) =>
        $"{category}:{email.Trim().ToLowerInvariant()}";

    private static string BuildIdempotencyKey(string category, string email, string payload)
    {
        var bucket = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{category}|{email.Trim().ToLowerInvariant()}|{payload}|{bucket}"));
        return $"sb-{category}-{Convert.ToHexString(hashBytes)[..24].ToLowerInvariant()}";
    }

    private bool TryAcquireSendWindow(string key)
    {
        var now = DateTimeOffset.UtcNow;
        if (_recentSends.TryGetValue(key, out var lastSent) && (now - lastSent) < DuplicateCooldown)
        {
            return false;
        }

        _recentSends[key] = now;
        return true;
    }

    private void ReleaseSendWindow(string key) => _recentSends.TryRemove(key, out _);
}
