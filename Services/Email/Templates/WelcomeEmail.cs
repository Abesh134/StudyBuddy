using System.Net;

namespace StudyBuddy.Services.Email.Templates;

public static class WelcomeEmail
{
    public const string SubjectLine = "Welcome to Study Buddy! Your learning journey starts here.";

    public static RenderedEmail Build(
        string? fullName,
        string dashboardUrl,
        string? supportEmail)
    {
        var greeting = string.IsNullOrWhiteSpace(fullName)
            ? "Hi there,"
            : $"Hi {fullName.Trim()},";

        var safeGreeting = WebUtility.HtmlEncode(greeting);

        var bodyHtml =
            $"<p style=\"margin:0 0 14px 0;font-size:15px;line-height:1.65;color:#172B4D;\">{safeGreeting}</p>" +
            "<p style=\"margin:0 0 14px 0;font-size:15px;line-height:1.65;color:#334155;\">" +
            "Your email address is confirmed and your <strong>Study Buddy</strong> account is now ready. " +
            "You can sign in anytime to access your private, personal study workspace." +
            "</p>" +
            "<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"margin:18px 0;background-color:#F8FAFC;border:1px solid #E2E8F0;border-radius:10px;\">" +
            "<tr><td style=\"padding:18px 20px;\">" +
            "<p style=\"margin:0 0 12px 0;font-size:14px;font-weight:800;color:#172B4D;\">Inside your Study Buddy workspace</p>" +
            "<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"font-size:14px;line-height:1.55;color:#334155;\">" +
            "<tr><td style=\"padding:0 0 8px 0;\"><strong style=\"color:#15803D;\">&#10003; Private Study Workspace (Ready now):</strong> Your personal dashboard, account settings, and isolated deck space are live and ready to explore.</td></tr>" +
            "<tr><td style=\"padding:0 0 8px 0;\"><strong style=\"color:#2563EB;\">&bull; AI-Powered Quiz Generation (Upcoming):</strong> Turn pasted notes or uploaded text files into structured four-option MCQs with explanations.</td></tr>" +
            "<tr><td style=\"padding:0 0 8px 0;\"><strong style=\"color:#2563EB;\">&bull; Split-View Study &amp; Source Highlighting (Upcoming):</strong> Practice questions alongside your original notes with <span style=\"background-color:#FEF08A;color:#172B4D;padding:1px 4px;border-radius:3px;\">highlighted source sentences</span>.</td></tr>" +
            "<tr><td style=\"padding:0 0 8px 0;\"><strong style=\"color:#2563EB;\">&bull; SM-2 Spaced Repetition (Upcoming):</strong> Adaptive review intervals designed to resurface concepts right before you forget them.</td></tr>" +
            "<tr><td style=\"padding:0;\"><strong style=\"color:#2563EB;\">&bull; Personal Learning Analytics (Upcoming):</strong> Track real accuracy, topic mastery, and daily study streaks in your own timezone.</td></tr>" +
            "</table>" +
            "</td></tr></table>";

        var secondaryNoteHtml =
            "<p style=\"margin:0;font-size:13px;line-height:1.55;color:#64748B;\">" +
            "All decks, notes, and learning records in Study Buddy remain private to your account." +
            "</p>";

        var html = EmailLayoutBuilder.Build(
            preheader: "Your Study Buddy account is confirmed and your personal study workspace is ready.",
            badgeText: "Account Verified",
            badgeBackground: "#DCFCE7",
            badgeColor: "#15803D",
            headline: "Welcome to Study Buddy!",
            bodyHtml: bodyHtml,
            ctaLabel: "Start Learning",
            ctaUrl: dashboardUrl,
            secondaryNoteHtml: secondaryNoteHtml,
            supportEmail: supportEmail);

        var text =
            $"{greeting}\n\n" +
            "Welcome to Study Buddy! Your email address is confirmed and your account is ready.\n\n" +
            "Inside your Study Buddy workspace:\n" +
            "- Private Study Workspace (Ready now): Access your personal dashboard, profile, and settings.\n" +
            "- AI-Powered Quiz Generation (Upcoming): Turn your notes into structured multiple-choice quizzes.\n" +
            "- Split-View Study & Source Highlighting (Upcoming): Review notes side-by-side with highlighted source sentences.\n" +
            "- SM-2 Spaced Repetition (Upcoming): Adaptive review scheduling for long-term retention.\n" +
            "- Personal Learning Analytics (Upcoming): Track accuracy, topic mastery, and daily study streaks.\n\n" +
            $"Start Learning: {dashboardUrl}\n\n" +
            "All decks and learning records in Study Buddy remain private to your account." +
            (string.IsNullOrWhiteSpace(supportEmail) ? string.Empty : $"\n\nNeed help? Contact {supportEmail.Trim()}.");

        return new RenderedEmail(SubjectLine, html, text);
    }
}

