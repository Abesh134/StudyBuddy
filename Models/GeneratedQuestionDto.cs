using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace StudyBuddy.Models;

public class GeneratedQuestionDto : IValidatableObject
{
    [JsonPropertyName("question")]
    [JsonRequired]
    [Required(AllowEmptyStrings = false)]
    [MaxLength(2000)]
    public string Question { get; set; } = string.Empty;

    [JsonPropertyName("options")]
    [JsonRequired]
    [Required]
    [MinLength(4)]
    [MaxLength(4)]
    public List<string> Options { get; set; } = new();

    /// <summary>
    /// Nullable with JsonRequired and Required so a missing or null JSON value
    /// never silently defaults to 0.
    /// </summary>
    [JsonPropertyName("correctIndex")]
    [JsonRequired]
    [Required]
    [Range(0, 3)]
    public int? CorrectIndex { get; set; }

    [JsonPropertyName("explanation")]
    [JsonRequired]
    [Required(AllowEmptyStrings = false)]
    [MaxLength(4000)]
    public string Explanation { get; set; } = string.Empty;

    [JsonPropertyName("topic")]
    [JsonRequired]
    [Required(AllowEmptyStrings = false)]
    [MaxLength(100)]
    public string Topic { get; set; } = string.Empty;

    [JsonPropertyName("sourceSentence")]
    [MaxLength(1000)]
    public string? SourceSentence { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(Question))
        {
            yield return new ValidationResult(
                "Question text is required.",
                new[] { nameof(Question) });
        }

        if (Options is null || Options.Count != 4)
        {
            yield return new ValidationResult(
                "Exactly 4 answer options are required.",
                new[] { nameof(Options) });
        }
        else if (Options.Any(string.IsNullOrWhiteSpace))
        {
            yield return new ValidationResult(
                "All 4 answer options must be non-empty.",
                new[] { nameof(Options) });
        }

        if (!CorrectIndex.HasValue || CorrectIndex.Value < 0 || CorrectIndex.Value > 3)
        {
            yield return new ValidationResult(
                "CorrectIndex must be explicitly provided and between 0 and 3.",
                new[] { nameof(CorrectIndex) });
        }

        if (string.IsNullOrWhiteSpace(Explanation))
        {
            yield return new ValidationResult(
                "Explanation is required.",
                new[] { nameof(Explanation) });
        }

        if (string.IsNullOrWhiteSpace(Topic))
        {
            yield return new ValidationResult(
                "Topic is required.",
                new[] { nameof(Topic) });
        }
    }
}

