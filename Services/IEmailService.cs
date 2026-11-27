namespace StudyBuddy.Services;

public interface IEmailService
{
    bool IsConfigured { get; }
    bool IsUsingResendTestDomain { get; }

    Task<EmailSendResult> SendAsync(
        EmailMessage message,
        CancellationToken cancellationToken = default);

    Task<EmailSendResult> SendEmailVerificationAsync(
        string toEmail,
        string? fullName,
        string verificationUrl,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default);

    Task<EmailSendResult> SendPasswordResetAsync(
        string toEmail,
        string? fullName,
        string resetUrl,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default);

    Task<EmailSendResult> SendWelcomeEmailAsync(
        string toEmail,
        string? fullName,
        string dashboardUrl,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default);

    Task<EmailSendResult> SendPasswordChangedNotificationAsync(
        string toEmail,
        string? fullName,
        DateTime changedAtUtc,
        string recoveryUrl,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default);

    Task<EmailSendResult> SendSecurityNotificationAsync(
        string toEmail,
        string? fullName,
        string eventTitle,
        string eventDetails,
        DateTime occurredAtUtc,
        string? idempotencyKey = null,
        CancellationToken cancellationToken = default);
}

