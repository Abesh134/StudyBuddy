namespace StudyBuddy.Services;

public sealed record EmailMessage(
    string To,
    string Subject,
    string HtmlBody,
    string TextBody,
    string? IdempotencyKey = null,
    string? Category = null);

public enum EmailSendStatus
{
    Sent = 0,
    NotConfigured = 1,
    InvalidRequest = 2,
    RateLimited = 3,
    DomainRestricted = 4,
    Timeout = 5,
    ProviderError = 6,
    NetworkError = 7,
    DuplicateSuppressed = 8
}

public sealed record EmailSendResult(
    bool Succeeded,
    EmailSendStatus Status,
    string? MessageId = null,
    int? HttpStatusCode = null,
    string? ErrorMessage = null)
{
    public static EmailSendResult Sent(string? messageId, int httpStatusCode = 200) =>
        new(true, EmailSendStatus.Sent, messageId, httpStatusCode, null);

    public static EmailSendResult NotConfigured(string message) =>
        new(false, EmailSendStatus.NotConfigured, null, null, message);

    public static EmailSendResult InvalidRequest(string message) =>
        new(false, EmailSendStatus.InvalidRequest, null, null, message);

    public static EmailSendResult DuplicateSuppressed(string message) =>
        new(false, EmailSendStatus.DuplicateSuppressed, null, null, message);

    public static EmailSendResult Failure(
        EmailSendStatus status,
        string errorMessage,
        int? httpStatusCode = null) =>
        new(false, status, null, httpStatusCode, errorMessage);
}

