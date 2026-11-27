using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using StudyBuddy.Data;
using StudyBuddy.Models;

namespace StudyBuddy.Services.Email;

public interface IEmailNotificationService
{
    Task<EmailSendResult> SendVerificationEmailAsync(
        ApplicationUser user,
        string confirmationUrl,
        CancellationToken cancellationToken = default);

    Task<EmailSendResult> SendWelcomeEmailIfNotSentAsync(
        ApplicationUser user,
        string dashboardUrl,
        CancellationToken cancellationToken = default);

    Task<EmailSendResult> SendPasswordResetEmailAsync(
        ApplicationUser user,
        string resetUrl,
        string? clientIp,
        CancellationToken cancellationToken = default);

    Task<EmailSendResult> SendPasswordChangedNotificationAsync(
        ApplicationUser user,
        DateTime changedAtUtc,
        string recoveryUrl,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Orchestrates transactional verification, password reset, durable one-time welcome emails,
/// and password security notifications with rate limiting, idempotency, and non-blocking failure handling.
/// Never stores tokens, passwords, or full sensitive email bodies in database records.
/// </summary>
public sealed class EmailNotificationService : IEmailNotificationService
{
    public const string WelcomeNotificationType = "Welcome";
    public const string PasswordChangedNotificationPrefix = "PasswordChanged:";
    private const int MaxWelcomeAttempts = 3;
    private const int MaxResetRequestsPerIpWindow = 5;
    private static readonly TimeSpan VerificationResendCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ResetAccountCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ResetIpWindow = TimeSpan.FromMinutes(5);

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IEmailService _emailService;
    private readonly ILogger<EmailNotificationService> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _verificationCooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _resetAccountCooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _resetIpWindows = new(StringComparer.OrdinalIgnoreCase);

    public EmailNotificationService(
        IDbContextFactory<AppDbContext> dbFactory,
        IEmailService emailService,
        ILogger<EmailNotificationService> logger)
    {
        _dbFactory = dbFactory;
        _emailService = emailService;
        _logger = logger;
    }

    public async Task<EmailSendResult> SendVerificationEmailAsync(
        ApplicationUser user,
        string confirmationUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        var email = user.Email?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(email))
        {
            return EmailSendResult.InvalidRequest("User email address is missing.");
        }

        if (!_emailService.IsConfigured)
        {
            return EmailSendResult.NotConfigured("Email delivery is not configured on this server.");
        }

        var now = DateTimeOffset.UtcNow;
        if (_verificationCooldowns.TryGetValue(email, out var lastSent) &&
            (now - lastSent) < VerificationResendCooldown)
        {
            return EmailSendResult.DuplicateSuppressed(
                "A verification email was recently requested. Please wait 30 seconds before trying again.");
        }

        _verificationCooldowns[email] = now;

        var idempotencyKey = BuildIdempotencyKey("verify", user.Id, now.ToUnixTimeSeconds() / 30);
        var result = await _emailService.SendEmailVerificationAsync(
            email,
            user.FullName,
            confirmationUrl,
            idempotencyKey,
            cancellationToken);

        if (!result.Succeeded)
        {
            _verificationCooldowns.TryRemove(email, out _);
        }

        return result;
    }

    public async Task<EmailSendResult> SendPasswordResetEmailAsync(
        ApplicationUser user,
        string resetUrl,
        string? clientIp,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        var email = user.Email?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(email))
        {
            return EmailSendResult.InvalidRequest("User email address is missing.");
        }

        if (!_emailService.IsConfigured)
        {
            return EmailSendResult.NotConfigured("Email delivery is not configured on this server.");
        }

        var now = DateTimeOffset.UtcNow;

        if (!TryAcquireIpResetSlot(clientIp, now))
        {
            _logger.LogWarning("Password reset request rate-limited by IP window.");
            return EmailSendResult.Failure(
                EmailSendStatus.RateLimited,
                "Too many password reset requests. Please wait before trying again.");
        }

        if (_resetAccountCooldowns.TryGetValue(email, out var lastAccountRequest) &&
            (now - lastAccountRequest) < ResetAccountCooldown)
        {
            _logger.LogInformation("Password reset request suppressed within per-account cooldown.");
            return EmailSendResult.DuplicateSuppressed(
                "A password reset email was recently requested.");
        }

        _resetAccountCooldowns[email] = now;

        var idempotencyKey = BuildIdempotencyKey("reset", user.Id, now.ToUnixTimeSeconds() / 30);
        var result = await _emailService.SendPasswordResetAsync(
            email,
            user.FullName,
            resetUrl,
            idempotencyKey,
            cancellationToken);

        if (!result.Succeeded)
        {
            _resetAccountCooldowns.TryRemove(email, out _);
        }

        return result;
    }

    public async Task<EmailSendResult> SendWelcomeEmailIfNotSentAsync(
        ApplicationUser user,
        string dashboardUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (!user.EmailConfirmed || string.IsNullOrWhiteSpace(user.Email))
        {
            return EmailSendResult.InvalidRequest("Welcome emails are sent only to confirmed accounts.");
        }

        if (!_emailService.IsConfigured)
        {
            return EmailSendResult.NotConfigured("Email delivery is not configured on this server.");
        }

        int notificationId;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var existing = await db.EmailNotifications
                .FirstOrDefaultAsync(
                    n => n.UserId == user.Id && n.NotificationType == WelcomeNotificationType,
                    cancellationToken);

            if (existing is null)
            {
                var created = new EmailNotification
                {
                    UserId = user.Id,
                    NotificationType = WelcomeNotificationType,
                    Status = EmailNotificationStatus.Pending,
                    AttemptCount = 1,
                    CreatedAtUtc = DateTime.UtcNow
                };

                db.EmailNotifications.Add(created);
                await db.SaveChangesAsync(cancellationToken);
                notificationId = created.Id;
            }
            else if (existing.Status is EmailNotificationStatus.Accepted or EmailNotificationStatus.Uncertain or EmailNotificationStatus.Pending)
            {
                return EmailSendResult.DuplicateSuppressed("Welcome email has already been handled for this account.");
            }
            else if (existing.AttemptCount >= MaxWelcomeAttempts)
            {
                return EmailSendResult.DuplicateSuppressed("Maximum welcome email attempts reached for this account.");
            }
            else
            {
                existing.Status = EmailNotificationStatus.Pending;
                existing.AttemptCount += 1;
                await db.SaveChangesAsync(cancellationToken);
                notificationId = existing.Id;
            }
        }
        catch (DbUpdateException)
        {
            return EmailSendResult.DuplicateSuppressed("Welcome email already claimed concurrently.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to claim durable welcome email record for user {UserId}.", user.Id);
            return EmailSendResult.Failure(EmailSendStatus.ProviderError, "Unable to record welcome email state.");
        }

        EmailSendResult sendResult;
        try
        {
            var idempotencyKey = $"sb-welcome-{user.Id}";
            sendResult = await _emailService.SendWelcomeEmailAsync(
                user.Email.Trim(),
                user.FullName,
                dashboardUrl,
                idempotencyKey,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Unexpected error sending welcome email for user {UserId}.", user.Id);
            sendResult = EmailSendResult.Failure(EmailSendStatus.NetworkError, "Unexpected delivery error.");
        }

        await PersistNotificationOutcomeAsync(notificationId, user.Id, sendResult, cancellationToken);
        return sendResult;
    }

    public async Task<EmailSendResult> SendPasswordChangedNotificationAsync(
        ApplicationUser user,
        DateTime changedAtUtc,
        string recoveryUrl,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        if (!user.EmailConfirmed || string.IsNullOrWhiteSpace(user.Email))
        {
            return EmailSendResult.InvalidRequest("Password security notifications are sent only to verified email addresses.");
        }

        if (!_emailService.IsConfigured)
        {
            return EmailSendResult.NotConfigured("Email delivery is not configured on this server.");
        }

        var utcTime = changedAtUtc.Kind == DateTimeKind.Utc
            ? changedAtUtc
            : changedAtUtc.ToUniversalTime();
        var notificationType = $"{PasswordChangedNotificationPrefix}{utcTime:yyyyMMddHHmm}";

        int? notificationId = null;
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var existing = await db.EmailNotifications
                .AsNoTracking()
                .FirstOrDefaultAsync(
                    n => n.UserId == user.Id && n.NotificationType == notificationType,
                    cancellationToken);

            if (existing is not null && existing.Status is EmailNotificationStatus.Accepted or EmailNotificationStatus.Uncertain or EmailNotificationStatus.Pending)
            {
                return EmailSendResult.DuplicateSuppressed("Password change notification already recorded for this event.");
            }

            if (existing is null)
            {
                var created = new EmailNotification
                {
                    UserId = user.Id,
                    NotificationType = notificationType,
                    Status = EmailNotificationStatus.Pending,
                    AttemptCount = 1,
                    CreatedAtUtc = DateTime.UtcNow
                };

                db.EmailNotifications.Add(created);
                await db.SaveChangesAsync(cancellationToken);
                notificationId = created.Id;
            }
        }
        catch (DbUpdateException)
        {
            return EmailSendResult.DuplicateSuppressed("Password change notification already claimed concurrently.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Failed to record password change notification state for user {UserId}.", user.Id);
        }

        EmailSendResult sendResult;
        try
        {
            var idempotencyKey = $"sb-pwdchg-{user.Id}-{utcTime:yyyyMMddHHmm}";
            sendResult = await _emailService.SendPasswordChangedNotificationAsync(
                user.Email.Trim(),
                user.FullName,
                utcTime,
                recoveryUrl,
                idempotencyKey,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unexpected error sending password change notification for user {UserId}.", user.Id);
            sendResult = EmailSendResult.Failure(EmailSendStatus.NetworkError, "Unexpected delivery error.");
        }

        if (notificationId.HasValue)
        {
            await PersistNotificationOutcomeAsync(notificationId.Value, user.Id, sendResult, cancellationToken);
        }

        return sendResult;
    }

    private async Task PersistNotificationOutcomeAsync(
        int notificationId,
        string userId,
        EmailSendResult sendResult,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
            var record = await db.EmailNotifications.FirstOrDefaultAsync(n => n.Id == notificationId, cancellationToken);
            if (record is null)
            {
                return;
            }

            if (sendResult.Succeeded)
            {
                record.Status = EmailNotificationStatus.Accepted;
                record.ProviderMessageId = sendResult.MessageId;
                record.AcceptedAtUtc = DateTime.UtcNow;
                record.LastErrorCode = null;
            }
            else if (sendResult.Status is EmailSendStatus.Timeout or EmailSendStatus.NetworkError)
            {
                record.Status = EmailNotificationStatus.Uncertain;
                record.LastErrorCode = sendResult.Status.ToString();
            }
            else
            {
                record.Status = EmailNotificationStatus.Failed;
                record.LastErrorCode = sendResult.Status.ToString();
            }

            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to persist notification outcome for user {UserId}.", userId);
        }
    }

    private bool TryAcquireIpResetSlot(string? clientIp, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(clientIp))
        {
            return true;
        }

        var key = clientIp.Trim();
        var queue = _resetIpWindows.GetOrAdd(key, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && (now - queue.Peek()) >= ResetIpWindow)
            {
                queue.Dequeue();
            }

            if (queue.Count >= MaxResetRequestsPerIpWindow)
            {
                return false;
            }

            queue.Enqueue(now);
            return true;
        }
    }

    private static string BuildIdempotencyKey(string category, string userId, long timeBucket)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{category}|{userId}|{timeBucket}"));
        return $"sb-{category}-{Convert.ToHexString(hash)[..24].ToLowerInvariant()}";
    }
}
