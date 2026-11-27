using System.Net;

namespace StudyBuddy.Services.Email.Templates;

public static class PasswordResetEmail
{
    public const string SubjectLine = "Reset your Study Buddy password";

    public static RenderedEmail Build(
        string? fullName,
        string resetUrl,
        TimeSpan tokenLifespan,
        string? supportEmail)
    {
        var greeting = string.IsNullOrWhiteSpace(fullName)
            ? "Hi there,"
            : $"Hi {fullName.Trim()},";

        var expiryDescription = FormatLifespan(tokenLifespan);
        var safeGreeting = WebUtility.HtmlEncode(greeting);
        var safeUrl = WebUtility.HtmlEncode(resetUrl);
        var safeExpiry = WebUtility.HtmlEncode(expiryDescription);

        var bodyHtml =
            $"<p style=\"margin:0 0 14px 0;font-size:15px;line-height:1.65;color:#172B4D;\">{safeGreeting}</p>" +
            "<p style=\"margin:0 0 14px 0;font-size:15px;line-height:1.65;color:#334155;\">" +
            "We received a request to reset the password for your <strong>Study Buddy</strong> account. " +
            "Select the button below to choose a new password." +
            "</p>" +
            "<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"margin:16px 0 4px 0;background-color:#DBEAFE;border-radius:8px;\">" +
            "<tr><td style=\"padding:12px 16px;font-size:13px;line-height:1.5;color:#172B4D;\">" +
            $"<strong>Link expiration:</strong> For your security, this password reset link expires in <strong>{safeExpiry}</strong> and can only be used once." +
            "</td></tr></table>";

        var secondaryNoteHtml =
            "<p style=\"margin:0 0 10px 0;font-size:13px;line-height:1.55;color:#64748B;\">" +
            "If the button above does not work, copy and paste this link into your browser:" +
            "</p>" +
            $"<p style=\"margin:0 0 14px 0;font-size:12px;line-height:1.5;word-break:break-all;\"><a href=\"{safeUrl}\" style=\"color:#2563EB;text-decoration:underline;\">{safeUrl}</a></p>" +
            "<p style=\"margin:0;font-size:13px;line-height:1.55;color:#64748B;\">" +
            "<strong>Did not request a password reset?</strong> You can safely ignore this email. Your current password will remain unchanged." +
            "</p>";

        var html = EmailLayoutBuilder.Build(
            preheader: "Use this link to reset your Study Buddy account password.",
            badgeText: "Password Reset",
            badgeBackground: "#DBEAFE",
            badgeColor: "#1D4ED8",
            headline: "Reset your password",
            bodyHtml: bodyHtml,
            ctaLabel: "Reset Password",
            ctaUrl: resetUrl,
            secondaryNoteHtml: secondaryNoteHtml,
            supportEmail: supportEmail);

        var text =
            $"{greeting}\n\n" +
            "We received a request to reset the password for your Study Buddy account.\n" +
            $"Choose a new password by visiting:\n\n{resetUrl}\n\n" +
            $"For your security, this password reset link expires in {expiryDescription} and can only be used once.\n\n" +
            "Did not request a password reset? You can safely ignore this email. Your password will remain unchanged." +
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

