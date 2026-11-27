using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using StudyBuddy.Models;
using Xunit;

namespace StudyBuddy.Tests;

public class ModelValidationTests
{
    private static List<ValidationResult> ValidateModel(object model)
    {
        var results = new List<ValidationResult>();
        var context = new ValidationContext(model);
        Validator.TryValidateObject(model, context, results, validateAllProperties: true);
        return results;
    }

    [Fact]
    public void Entities_InitializeCollectionsAndDefaults()
    {
        var user = new ApplicationUser();
        Assert.NotNull(user.Decks);
        Assert.Equal("UTC", user.StudyTimeZoneId);
        Assert.Equal(DateTimeKind.Utc, user.CreatedAtUtc.Kind);

        var deck = new Deck();
        Assert.NotNull(deck.Questions);
        Assert.Equal(NoteSourceType.PastedText, deck.SourceType);
        Assert.Equal(DateTimeKind.Utc, deck.CreatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, deck.UpdatedAtUtc.Kind);

        var question = new Question();
        Assert.NotNull(question.Options);
        Assert.NotNull(question.Attempts);
        Assert.Null(question.SourceSentence);
        Assert.Equal(DateTimeKind.Utc, question.CreatedAtUtc.Kind);

        var reviewState = new ReviewState();
        Assert.Equal(2.5, reviewState.EaseFactor);
        Assert.Equal(0, reviewState.RepetitionCount);
        Assert.Equal(0, reviewState.IntervalDays);
        Assert.Null(reviewState.LastReviewedAtUtc);
        Assert.Equal(DateTimeKind.Utc, reviewState.NextReviewUtc.Kind);
    }

    [Fact]
    public void Deck_EnforcesRequiredUserIdAndTitleMaxLength150()
    {
        var invalidDeck = new Deck
        {
            UserId = string.Empty,
            Title = new string('a', 151),
            NotesText = "Some notes"
        };

        var errors = ValidateModel(invalidDeck);
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(Deck.UserId)));
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(Deck.Title)));

        var validDeck = new Deck
        {
            UserId = "user-123",
            Title = new string('a', 150),
            NotesText = "Some notes",
            SourceType = NoteSourceType.UploadedFile,
            OriginalFileName = "lecture1.txt"
        };

        Assert.Empty(ValidateModel(validDeck));
    }

    [Fact]
    public void Question_EnforcesTopicMax100AndCorrectIndexRange()
    {
        var invalidQuestion = new Question
        {
            DeckId = 1,
            QuestionText = "What is HTTP?",
            CorrectIndex = 4,
            Explanation = "Hypertext Transfer Protocol.",
            Topic = new string('t', 101)
        };

        var errors = ValidateModel(invalidQuestion);
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(Question.CorrectIndex)));
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(Question.Topic)));

        var validQuestion = new Question
        {
            DeckId = 1,
            QuestionText = "What is HTTP?",
            CorrectIndex = 0,
            Explanation = "Hypertext Transfer Protocol.",
            Topic = new string('t', 100),
            SourceSentence = null
        };

        Assert.Empty(ValidateModel(validQuestion));
    }

    [Fact]
    public void Attempt_EnforcesRangesAndSubmissionId()
    {
        var invalidAttempt = new Attempt
        {
            QuestionId = 1,
            SelectedIndex = 5,
            Sm2Quality = 6,
            SubmissionId = string.Empty
        };

        var errors = ValidateModel(invalidAttempt);
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(Attempt.SelectedIndex)));
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(Attempt.Sm2Quality)));
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(Attempt.SubmissionId)));

        var validAttempt = new Attempt
        {
            QuestionId = 1,
            SelectedIndex = 2,
            IsCorrect = true,
            Sm2Quality = 5,
            SubmissionId = Guid.NewGuid().ToString("N")
        };

        Assert.Empty(ValidateModel(validAttempt));
    }

    [Fact]
    public void GeneratedQuestionDto_MissingCorrectIndex_ThrowsJsonException()
    {
        const string jsonMissingCorrectIndex = """
        {
          "question": "What is the powerhouse of the cell?",
          "options": ["Nucleus", "Mitochondria", "Ribosome", "Golgi apparatus"],
          "explanation": "Mitochondria generate ATP.",
          "topic": "Cell Biology",
          "sourceSentence": "Mitochondria are the powerhouse of the cell."
        }
        """;

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<GeneratedQuestionDto>(jsonMissingCorrectIndex));
    }

    [Fact]
    public void GeneratedQuestionDto_NullCorrectIndex_DoesNotBecomeZeroAndFailsValidation()
    {
        const string jsonNullCorrectIndex = """
        {
          "question": "What is the powerhouse of the cell?",
          "options": ["Nucleus", "Mitochondria", "Ribosome", "Golgi apparatus"],
          "correctIndex": null,
          "explanation": "Mitochondria generate ATP.",
          "topic": "Cell Biology"
        }
        """;

        var dto = JsonSerializer.Deserialize<GeneratedQuestionDto>(jsonNullCorrectIndex);
        Assert.NotNull(dto);
        Assert.Null(dto!.CorrectIndex);

        var errors = ValidateModel(dto);
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(GeneratedQuestionDto.CorrectIndex)));
    }

    [Fact]
    public void GeneratedQuestionDto_ValidJsonWithZeroIndexAndNullSourceSentence_PassesValidation()
    {
        const string validJson = """
        {
          "question": "Which protocol is stateless?",
          "options": ["HTTP", "TCP", "FTP Control", "SSH"],
          "correctIndex": 0,
          "explanation": "HTTP does not retain session state between requests by default.",
          "topic": "Networking",
          "sourceSentence": null
        }
        """;

        var dto = JsonSerializer.Deserialize<GeneratedQuestionDto>(validJson);
        Assert.NotNull(dto);
        Assert.Equal(0, dto!.CorrectIndex);
        Assert.Null(dto.SourceSentence);
        Assert.Equal(4, dto.Options.Count);
        Assert.Empty(ValidateModel(dto));
    }

    [Fact]
    public void GeneratedQuestionDto_SerializesExactJsonFieldNames()
    {
        var dto = new GeneratedQuestionDto
        {
            Question = "Q?",
            Options = new List<string> { "A", "B", "C", "D" },
            CorrectIndex = 1,
            Explanation = "Because B.",
            Topic = "Basics",
            SourceSentence = "B is correct."
        };

        var json = JsonSerializer.Serialize(dto);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("question", out _));
        Assert.True(root.TryGetProperty("options", out _));
        Assert.True(root.TryGetProperty("correctIndex", out _));
        Assert.True(root.TryGetProperty("explanation", out _));
        Assert.True(root.TryGetProperty("topic", out _));
        Assert.True(root.TryGetProperty("sourceSentence", out _));
    }
}

