using System.Net;
using System.Text;

namespace StudyBuddy.Services.Email.Templates;

/// <summary>
/// Builds email-client-compatible, table-based, inline-CSS HTML emails
/// using Study Buddy brand tokens and system font fallbacks.
/// Never uses JavaScript, CSS animations, video, or unencoded user content.
/// </summary>
internal static class EmailLayoutBuilder
{
    private const string FontStack =
        "-apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, Helvetica, Arial, sans-serif";

    public static string Build(
        string preheader,
        string badgeText,
        string badgeBackground,
        string badgeColor,
        string headline,
        string bodyHtml,
        string? ctaLabel,
        string? ctaUrl,
        string? secondaryNoteHtml,
        string? supportEmail)
    {
        var sb = new StringBuilder(2048);

        sb.Append("<!DOCTYPE html>");
        sb.Append("<html lang=\"en\">");
        sb.Append("<head>");
        sb.Append("<meta charset=\"utf-8\" />");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\" />");
        sb.Append("<title>").Append(WebUtility.HtmlEncode(headline)).Append("</title>");
        sb.Append("</head>");
        sb.Append($"<body style=\"margin:0;padding:0;background-color:#F1F5F9;font-family:{FontStack};color:#172B4D;-webkit-text-size-adjust:100%;\">");

        // Hidden preheader text for inbox preview
        sb.Append("<div style=\"display:none;max-height:0;overflow:hidden;mso-hide:all;font-size:1px;line-height:1px;color:#F1F5F9;\">");
        sb.Append(WebUtility.HtmlEncode(preheader));
        sb.Append("</div>");

        // Outer full-width wrapper table
        sb.Append("<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"background-color:#F1F5F9;width:100%;\">");
        sb.Append("<tr><td align=\"center\" style=\"padding:32px 16px;\">");

        // 600px centered email card
        sb.Append("<table role=\"presentation\" width=\"600\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"width:100%;max-width:600px;background-color:#FFFFFF;border:1px solid #E2E8F0;border-radius:14px;overflow:hidden;\">");

        // Branded Navy Header
        sb.Append("<tr><td style=\"background-color:#172B4D;padding:24px 32px;\">");
        sb.Append("<table role=\"presentation\" width=\"100%\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\"><tr>");
        sb.Append($"<td style=\"font-family:{FontStack};font-size:21px;font-weight:800;color:#FFFFFF;letter-spacing:-0.02em;\">");
        sb.Append("<span style=\"display:inline-block;width:10px;height:10px;border-radius:3px;background-color:#FEF08A;margin-right:10px;vertical-align:middle;\"></span>");
        sb.Append("<span style=\"vertical-align:middle;\">Study Buddy</span>");
        sb.Append("</td>");
        sb.Append($"<td align=\"right\" style=\"font-family:{FontStack};font-size:12px;font-weight:600;color:#DBEAFE;\">Personal Study Workspace</td>");
        sb.Append("</tr></table>");
        sb.Append("</td></tr>");

        // Main Content Body
        sb.Append("<tr><td style=\"padding:32px 32px 28px 32px;\">");

        if (!string.IsNullOrWhiteSpace(badgeText))
        {
            sb.Append("<p style=\"margin:0 0 14px 0;\">");
            sb.Append($"<span style=\"display:inline-block;padding:5px 12px;border-radius:999px;background-color:{badgeBackground};color:{badgeColor};font-family:{FontStack};font-size:12px;font-weight:700;\">");
            sb.Append(WebUtility.HtmlEncode(badgeText));
            sb.Append("</span></p>");
        }

        sb.Append($"<h1 style=\"margin:0 0 16px 0;font-family:{FontStack};font-size:24px;line-height:1.25;font-weight:800;color:#172B4D;\">");
        sb.Append(WebUtility.HtmlEncode(headline));
        sb.Append("</h1>");

        sb.Append(bodyHtml);

        if (!string.IsNullOrWhiteSpace(ctaLabel) && !string.IsNullOrWhiteSpace(ctaUrl))
        {
            var safeUrl = WebUtility.HtmlEncode(ctaUrl);
            var safeLabel = WebUtility.HtmlEncode(ctaLabel);
            sb.Append("<table role=\"presentation\" cellspacing=\"0\" cellpadding=\"0\" border=\"0\" style=\"margin:24px 0;\"><tr>");
            sb.Append("<td align=\"center\" style=\"border-radius:8px;background-color:#2563EB;\">");
            sb.Append($"<a href=\"{safeUrl}\" target=\"_blank\" rel=\"noopener noreferrer\" style=\"display:inline-block;padding:13px 26px;font-family:{FontStack};font-size:15px;font-weight:700;color:#FFFFFF;text-decoration:none;border-radius:8px;background-color:#2563EB;\">");
            sb.Append(safeLabel);
            sb.Append("</a>");
            sb.Append("</td></tr></table>");
        }

        if (!string.IsNullOrWhiteSpace(secondaryNoteHtml))
        {
            sb.Append("<div style=\"margin-top:20px;padding-top:18px;border-top:1px solid #E2E8F0;\">");
            sb.Append(secondaryNoteHtml);
            sb.Append("</div>");
        }

        sb.Append("</td></tr>");

        // Clean Footer
        sb.Append("<tr><td style=\"background-color:#F8FAFC;border-top:1px solid #E2E8F0;padding:20px 32px;\">");
        sb.Append($"<p style=\"margin:0 0 6px 0;font-family:{FontStack};font-size:12px;line-height:1.5;color:#64748B;\">");
        sb.Append("&copy; ").Append(DateTime.UtcNow.Year).Append(" Study Buddy &middot; Private multi-user study workspace.");
        sb.Append("</p>");

        if (!string.IsNullOrWhiteSpace(supportEmail))
        {
            var safeSupport = WebUtility.HtmlEncode(supportEmail.Trim());
            sb.Append($"<p style=\"margin:0;font-family:{FontStack};font-size:12px;line-height:1.5;color:#64748B;\">");
            sb.Append($"Need help? Contact <a href=\"mailto:{safeSupport}\" style=\"color:#2563EB;text-decoration:underline;\">{safeSupport}</a>.");
            sb.Append("</p>");
        }

        sb.Append("</td></tr>");
        sb.Append("</table>");

        sb.Append("</td></tr></table>");
        sb.Append("</body></html>");

        return sb.ToString();
    }
}

