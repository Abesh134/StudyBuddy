using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace StudyBuddy.Models;

[Index(nameof(QuestionId), IsUnique = true)]
public class ReviewState
{
    public int Id { get; set; }

    [Required]
    public int QuestionId { get; set; }

    [ForeignKey(nameof(QuestionId))]
    public Question? Question { get; set; }

    [Range(0, int.MaxValue)]
    public int RepetitionCount { get; set; }

    [Range(0, int.MaxValue)]
    public int IntervalDays { get; set; }

    [Range(1.3, 5.0)]
    public double EaseFactor { get; set; } = 2.5;

    public DateTime NextReviewUtc { get; set; } = DateTime.UtcNow;

    public DateTime? LastReviewedAtUtc { get; set; }
}

