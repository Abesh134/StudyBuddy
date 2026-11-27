namespace StudyBuddy.Data;

/// <summary>
/// Opt-in development-only seed settings. Credentials must be supplied through
/// User Secrets or development environment variables, never committed to source control
/// or used in production.
/// </summary>
public sealed class DevSeedOptions
{
    public const string SectionName = "DevSeed";

    public bool Enabled { get; set; }

    public DevSeedAccountOptions DemoUserOne { get; set; } = new();

    public DevSeedAccountOptions DemoUserTwo { get; set; } = new();
}

public sealed class DevSeedAccountOptions
{
    public string? Email { get; set; }
    public string? Password { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string StudyTimeZoneId { get; set; } = "UTC";
}

