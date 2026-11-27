using System.Net;

namespace StudyBuddy.Services.Email.Templates;

public static class VerificationEmail
{
    public const string SubjectLine = "Verify your Study Buddy account";

    public static RenderedEmail Build(
        string? fullName,
        string verificationUrl,
        TimeSpan tokenLifespan,
        string? supportEmail)
    {
        var greeting = string.IsNullOrWhiteSpace(fullName)
            ? "Hi there,"
            : $"Hi {fullName.Trim()},";

        var expiryDescription = FormatLifespan(tokenLifespan);
        var safeGreeting = WebUtility.HtmlEncode(greeting);
        var safeUrl = WebUtility.HtmlEncode(verificationUrl);
        var safeExpiry = WebUtility.HtmlEncode(expiryDescription);

        var bodyHtml =
            $"<p style=\"margin:0 0 14px 0;font-size:15px;line-height:1.65;color:#172B4D;\">{safeGreeting}</p>" +
            "<p style=\"margin:0 0 14px 0;font-size:15px;line-height:1.65;color:#334155;\">" +
            "Thank you for creating your <strong>Study Buddy</strong> account. Please confirm your email address so we can activate your private study workspace." +
            "</p>" +
            "<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"margin:16px 0 4px 0;background-color:#DBEAFE;border-radius:8px;\">" +
            "<tr><td style=\"padding:12px 16px;font-size:13px;line-height:1.5;color:#172B4D;\">" +
            $"<strong>Link expiration:</strong> For your security, this verification link remains valid for <strong>{safeExpiry}</strong>." +
            "</td></tr></table>";

        var secondaryNoteHtml =
            "<p style=\"margin:0 0 10px 0;font-size:13px;line-height:1.55;color:#64748B;\">" +
            "If the button above does not work, copy and paste this link into your browser:" +
            "</p>" +
            $"<p style=\"margin:0 0 14px 0;font-size:12px;line-height:1.5;word-break:break-all;\"><a href=\"{safeUrl}\" style=\"color:#2563EB;text-decoration:underline;\">{safeUrl}</a></p>" +
            "<p style=\"margin:0;font-size:13px;line-height:1.55;color:#64748B;\">" +
            "<strong>Did not create this account?</strong> You can safely ignore this email. No account will be activated without verification." +
            "</p>";

        var html = EmailLayoutBuilder.Build(
            preheader: "Confirm your email address to activate your Study Buddy account.",
            badgeText: "Email Verification",
            badgeBackground: "#DBEAFE",
            badgeColor: "#1D4ED8",
            headline: "Verify your email address",
            bodyHtml: bodyHtml,
            ctaLabel: "Verify Email Address",
            ctaUrl: verificationUrl,
            secondaryNoteHtml: secondaryNoteHtml,
            supportEmail: supportEmail);

        var text =
            $"{greeting}\n\n" +
            "Thank you for creating your Study Buddy account.\n" +
            "Please confirm your email address to activate your private study workspace:\n\n" +
            $"{verificationUrl}\n\n" +
            $"For your security, this verification link remains valid for {expiryDescription}.\n\n" +
            "Did not create this account? You can safely ignore this email; no account will be activated without verification." +
            (string.IsNullOrWhiteSpace(supportEmail) ? string.Empty : $"\n\nNeed help? Contact {supportEmail.Trim()}.");

        return new RenderedEmail(SubjectLine, html, text);
    }

    private static string FormatLifespan(TimeSpan lifespan)
    {
        if (lifespan.TotalHours >= 1)
        {
            var hours = (int)Math.Round(lifespan.TotalHours);
            return hours == 1 ? "1 hour" : $"{hours} hours";
        }

        var minutes = Math.Max(1, (int)Math.Round(lifespan.TotalMinutes));
        return minutes == 1 ? "1 minute" : $"{minutes} minutes";
    }
}

