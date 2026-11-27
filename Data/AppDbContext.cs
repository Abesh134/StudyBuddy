using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using StudyBuddy.Models;

namespace StudyBuddy.Data;

public class AppDbContext : IdentityDbContext<ApplicationUser>
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<Deck> Decks => Set<Deck>();
    public DbSet<Question> Questions => Set<Question>();
    public DbSet<QuestionOption> QuestionOptions => Set<QuestionOption>();
    public DbSet<Attempt> Attempts => Set<Attempt>();
    public DbSet<ReviewState> ReviewStates => Set<ReviewState>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(u => u.FullName)
                .IsRequired()
                .HasMaxLength(150);

            entity.Property(u => u.StudyTimeZoneId)
                .IsRequired()
                .HasMaxLength(100)
                .HasDefaultValue("UTC");

            entity.Property(u => u.CreatedAtUtc)
                .IsRequired();
        });

        builder.Entity<Deck>(entity =>
        {
            entity.ToTable("Decks");

            entity.Property(d => d.UserId)
                .IsRequired()
                .HasMaxLength(450);

            entity.Property(d => d.Title)
                .IsRequired()
                .HasMaxLength(150);

            entity.Property(d => d.NotesText)
                .IsRequired();

            entity.Property(d => d.SourceType)
                .IsRequired();

            entity.Property(d => d.OriginalFileName)
                .HasMaxLength(260);

            entity.Property(d => d.CreatedAtUtc)
                .IsRequired();

            entity.Property(d => d.UpdatedAtUtc)
                .IsRequired();

            entity.HasOne(d => d.Owner)
                .WithMany(u => u.Decks)
                .HasForeignKey(d => d.UserId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(d => d.UserId)
                .HasDatabaseName("IX_Decks_UserId");

            entity.HasIndex(d => new { d.UserId, d.UpdatedAtUtc })
                .HasDatabaseName("IX_Decks_UserId_UpdatedAtUtc");

            entity.HasIndex(d => new { d.UserId, d.CreatedAtUtc })
                .HasDatabaseName("IX_Decks_UserId_CreatedAtUtc");
        });

        builder.Entity<Question>(entity =>
        {
            entity.ToTable("Questions", t =>
            {
                t.HasCheckConstraint(
                    "CK_Questions_CorrectIndex",
                    "[CorrectIndex] >= 0 AND [CorrectIndex] <= 3");
            });

            entity.Property(q => q.QuestionText)
                .IsRequired()
                .HasMaxLength(2000);

            entity.Property(q => q.Explanation)
                .IsRequired()
                .HasMaxLength(4000);

            entity.Property(q => q.Topic)
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(q => q.SourceSentence)
                .HasMaxLength(1000);

            entity.Property(q => q.CreatedAtUtc)
                .IsRequired();

            entity.HasOne(q => q.Deck)
                .WithMany(d => d.Questions)
                .HasForeignKey(q => q.DeckId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(q => new { q.DeckId, q.CreatedAtUtc })
                .HasDatabaseName("IX_Questions_DeckId_CreatedAtUtc");

            entity.HasIndex(q => new { q.DeckId, q.Topic })
                .HasDatabaseName("IX_Questions_DeckId_Topic");
        });

        builder.Entity<QuestionOption>(entity =>
        {
            entity.ToTable("QuestionOptions", t =>
            {
                t.HasCheckConstraint(
                    "CK_QuestionOptions_Index",
                    "[Index] >= 0 AND [Index] <= 3");
            });

            entity.Property(o => o.Text)
                .IsRequired()
                .HasMaxLength(1000);

            entity.HasOne(o => o.Question)
                .WithMany(q => q.Options)
                .HasForeignKey(o => o.QuestionId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(o => new { o.QuestionId, o.Index })
                .IsUnique()
                .HasDatabaseName("UX_QuestionOptions_QuestionId_Index");
        });

        builder.Entity<Attempt>(entity =>
        {
            entity.ToTable("Attempts", t =>
            {
                t.HasCheckConstraint(
                    "CK_Attempts_SelectedIndex",
                    "[SelectedIndex] >= 0 AND [SelectedIndex] <= 3");
                t.HasCheckConstraint(
                    "CK_Attempts_Sm2Quality",
                    "[Sm2Quality] >= 0 AND [Sm2Quality] <= 5");
            });

            entity.Property(a => a.AttemptedAtUtc)
                .IsRequired();

            entity.Property(a => a.SubmissionId)
                .IsRequired()
                .HasMaxLength(64);

            entity.HasOne(a => a.Question)
                .WithMany(q => q.Attempts)
                .HasForeignKey(a => a.QuestionId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(a => a.SubmissionId)
                .IsUnique()
                .HasDatabaseName("UX_Attempts_SubmissionId");

            entity.HasIndex(a => new { a.QuestionId, a.AttemptedAtUtc })
                .HasDatabaseName("IX_Attempts_QuestionId_AttemptedAtUtc");

            entity.HasIndex(a => a.AttemptedAtUtc)
                .HasDatabaseName("IX_Attempts_AttemptedAtUtc");
        });

        builder.Entity<ReviewState>(entity =>
        {
            entity.ToTable("ReviewStates", t =>
            {
                t.HasCheckConstraint(
                    "CK_ReviewStates_RepetitionCount",
                    "[RepetitionCount] >= 0");
                t.HasCheckConstraint(
                    "CK_ReviewStates_IntervalDays",
                    "[IntervalDays] >= 0");
                t.HasCheckConstraint(
                    "CK_ReviewStates_EaseFactor",
                    "[EaseFactor] >= 1.3");
            });

            entity.Property(r => r.EaseFactor)
                .HasDefaultValue(2.5);

            entity.Property(r => r.NextReviewUtc)
                .IsRequired();

            entity.HasOne(r => r.Question)
                .WithOne(q => q.ReviewState)
                .HasForeignKey<ReviewState>(r => r.QuestionId)
                .IsRequired()
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(r => r.QuestionId)
                .IsUnique()
                .HasDatabaseName("UX_ReviewStates_QuestionId");

            entity.HasIndex(r => r.NextReviewUtc)
                .HasDatabaseName("IX_ReviewStates_NextReviewUtc");
        });
    }
}

