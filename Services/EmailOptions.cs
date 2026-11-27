namespace StudyBuddy.Services;

public sealed class EmailOptions
{
    public const string SectionName = "Email";

    public bool Enabled { get; set; }

    public string Provider { get; set; } = "Resend";

    public string? ApiKey { get; set; }

    public string? FromEmail { get; set; }

    public string FromName { get; set; } = "Study Buddy";

    public int TimeoutSeconds { get; set; } = 15;

    public string BaseUrl { get; set; } = "https://api.resend.com/";

    /// <summary>
    /// Optional trusted public application base URL (e.g. https://studybuddy.example.com)
    /// used when generating links in transactional emails.
    /// </summary>
    public string? AppBaseUrl { get; set; }

    /// <summary>
    /// Optional support contact email address. Included in email footers only when configured.
    /// </summary>
    public string? SupportEmail { get; set; }

    public bool IsConfigured =>
        Enabled &&
        string.Equals(Provider?.Trim(), "Resend", StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(FromEmail);

    public bool IsUsingResendTestDomain =>
        !string.IsNullOrWhiteSpace(FromEmail) &&
        FromEmail.Trim().EndsWith("@resend.dev", StringComparison.OrdinalIgnoreCase);

    public TimeSpan EffectiveTimeout =>
        TimeSpan.FromSeconds(Math.Clamp(TimeoutSeconds <= 0 ? 15 : TimeoutSeconds, 5, 60));

    public string FormattedFrom =>
        string.IsNullOrWhiteSpace(FromName)
            ? (FromEmail?.Trim() ?? string.Empty)
            : $"{FromName.Trim()} <{FromEmail?.Trim()}>";

    /// <summary>
    /// Redacts sensitive credentials so accidental logging never exposes ApiKey.
    /// </summary>
    public override string ToString() =>
        $"EmailOptions(Enabled={Enabled}, Provider={Provider}, FromEmail={FromEmail}, FromName={FromName}, TimeoutSeconds={TimeoutSeconds}, ApiKeyConfigured={!string.IsNullOrWhiteSpace(ApiKey)})";
}
