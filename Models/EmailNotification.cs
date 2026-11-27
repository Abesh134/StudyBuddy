using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace StudyBuddy.Models;

public enum EmailNotificationStatus
{
    Pending = 0,
    Accepted = 1,
    Failed = 2,
    Uncertain = 3
}

/// <summary>
/// Durable tracking record for transactional notifications (such as one-time welcome emails).
/// Never stores verification tokens, password reset tokens, or sensitive email bodies.
/// </summary>
public class EmailNotification
{
    public int Id { get; set; }

    [Required]
    [MaxLength(450)]
    public string UserId { get; set; } = string.Empty;

    [ForeignKey(nameof(UserId))]
    public ApplicationUser? User { get; set; }

    [Required]
    [MaxLength(64)]
    public string NotificationType { get; set; } = string.Empty;

    public EmailNotificationStatus Status { get; set; } = EmailNotificationStatus.Pending;

    [MaxLength(128)]
    public string? ProviderMessageId { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? AcceptedAtUtc { get; set; }

    [Range(0, int.MaxValue)]
    public int AttemptCount { get; set; }

    [MaxLength(64)]
    public string? LastErrorCode { get; set; }
}

