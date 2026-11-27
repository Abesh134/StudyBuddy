using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace StudyBuddy.Models;

/// <summary>
/// A private study deck owned by a single user. All child entities
/// (questions, options, attempts, and review states) inherit ownership from this deck.
/// </summary>
public class Deck
{
    public int Id { get; set; }

    [Required]
    [MaxLength(450)]
    public string UserId { get; set; } = string.Empty;

    [ForeignKey(nameof(UserId))]
    public ApplicationUser? Owner { get; set; }

    [Required]
    [MaxLength(150)]
    public string Title { get; set; } = string.Empty;

    [Required]
    public string NotesText { get; set; } = string.Empty;

    public NoteSourceType SourceType { get; set; } = NoteSourceType.PastedText;

    [MaxLength(260)]
    public string? OriginalFileName { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public ICollection<Question> Questions { get; set; } = new List<Question>();
}

