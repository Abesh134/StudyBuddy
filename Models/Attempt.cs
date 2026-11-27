using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace StudyBuddy.Models;

public class Attempt
{
    public int Id { get; set; }

    [Required]
    public int QuestionId { get; set; }

    [ForeignKey(nameof(QuestionId))]
    public Question? Question { get; set; }

    [Range(0, 3)]
    public int SelectedIndex { get; set; }

    public bool IsCorrect { get; set; }

    [Range(0, 5)]
    public int Sm2Quality { get; set; }

    public DateTime AttemptedAtUtc { get; set; } = DateTime.UtcNow;

    [Required]
    [MaxLength(64)]
    public string SubmissionId { get; set; } = string.Empty;
}

