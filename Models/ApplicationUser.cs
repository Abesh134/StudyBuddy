using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace StudyBuddy.Models;

public class ApplicationUser : IdentityUser
{
    [Required]
    [MaxLength(150)]
    public string FullName { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    [Required]
    [MaxLength(100)]
    public string StudyTimeZoneId { get; set; } = "UTC";

    public ICollection<Deck> Decks { get; set; } = new List<Deck>();
}

