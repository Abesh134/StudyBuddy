using System.Net;

namespace StudyBuddy.Services.Email.Templates;

public static class PasswordChangedEmail
{
    public const string SubjectLine = "Your Study Buddy password has been updated";

    public static RenderedEmail Build(
        string? fullName,
        DateTime changedAtUtc,
        string recoveryUrl,
        string? supportEmail)
    {
        var greeting = string.IsNullOrWhiteSpace(fullName)
            ? "Hi there,"
            : $"Hi {fullName.Trim()},";

        var timestamp = changedAtUtc.ToUniversalTime().ToString("MMMM d, yyyy 'at' HH:mm 'UTC'");
        var safeGreeting = WebUtility.HtmlEncode(greeting);
        var safeTimestamp = WebUtility.HtmlEncode(timestamp);
        var safeRecoveryUrl = WebUtility.HtmlEncode(recoveryUrl);

        var bodyHtml =
            $"<p style=\"margin:0 0 14px 0;font-size:15px;line-height:1.65;color:#172B4D;\">{safeGreeting}</p>" +
            "<p style=\"margin:0 0 14px 0;font-size:15px;line-height:1.65;color:#334155;\">" +
            "This is a confirmation that the password for your <strong>Study Buddy</strong> account was successfully updated." +
            "</p>" +
            "<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"margin:16px 0;background-color:#F8FAFC;border:1px solid #E2E8F0;border-radius:8px;\">" +
            "<tr><td style=\"padding:14px 16px;font-size:13px;line-height:1.55;color:#172B4D;\">" +
            $"<strong>Event:</strong> Password updated<br />" +
            $"<strong>Date &amp; time:</strong> {safeTimestamp}" +
            "</td></tr></table>" +
            "<p style=\"margin:0 0 4px 0;font-size:14px;line-height:1.6;color:#334155;\">" +
            "If you made this change, no further action is needed. If you did <strong>not</strong> initiate this password update, recover your account immediately using the button below." +
            "</p>";

        var secondaryNoteHtml =
            "<p style=\"margin:0 0 8px 0;font-size:13px;line-height:1.55;color:#64748B;\">" +
            "Account recovery link:" +
            "</p>" +
            $"<p style=\"margin:0;font-size:12px;line-height:1.5;word-break:break-all;\"><a href=\"{safeRecoveryUrl}\" style=\"color:#2563EB;text-decoration:underline;\">{safeRecoveryUrl}</a></p>";

        var html = EmailLayoutBuilder.Build(
            preheader: "Your Study Buddy account password was updated.",
            badgeText: "Security Notification",
            badgeBackground: "#FEF08A",
            badgeColor: "#713F12",
            headline: "Your password has been updated",
            bodyHtml: bodyHtml,
            ctaLabel: "Recover Your Account",
            ctaUrl: recoveryUrl,
            secondaryNoteHtml: secondaryNoteHtml,
            supportEmail: supportEmail);

        var text =
            $"{greeting}\n\n" +
            "This is a confirmation that the password for your Study Buddy account was successfully updated.\n\n" +
            $"Event: Password updated\n" +
            $"Date & time: {timestamp}\n\n" +
            "If you made this change, no further action is needed.\n" +
            $"If you did not initiate this change, recover your account immediately:\n{recoveryUrl}" +
            (string.IsNullOrWhiteSpace(supportEmail) ? string.Empty : $"\n\nNeed help? Contact {supportEmail.Trim()}.");

        return new RenderedEmail(SubjectLine, html, text);
    }
}

