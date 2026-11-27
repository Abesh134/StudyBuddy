using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace StudyBuddy.Models;

public class QuestionOption
{
    public int Id { get; set; }

    [Required]
    public int QuestionId { get; set; }

    [ForeignKey(nameof(QuestionId))]
    public Question? Question { get; set; }

    [Range(0, 3)]
    public int Index { get; set; }

    [Required]
    [MaxLength(1000)]
    public string Text { get; set; } = string.Empty;
}

