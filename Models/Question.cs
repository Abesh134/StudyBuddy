using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace StudyBuddy.Models;

public class Question
{
    public int Id { get; set; }

    [Required]
    public int DeckId { get; set; }

    [ForeignKey(nameof(DeckId))]
    public Deck? Deck { get; set; }

    [Required]
    [MaxLength(2000)]
    public string QuestionText { get; set; } = string.Empty;

    [Range(0, 3)]
    public int CorrectIndex { get; set; }

    [Required]
    [MaxLength(4000)]
    public string Explanation { get; set; } = string.Empty;

    [Required]
    [MaxLength(100)]
    public string Topic { get; set; } = string.Empty;

    [MaxLength(1000)]
    public string? SourceSentence { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<QuestionOption> Options { get; set; } = new List<QuestionOption>();

    public ICollection<Attempt> Attempts { get; set; } = new List<Attempt>();

    public ReviewState? ReviewState { get; set; }
}

