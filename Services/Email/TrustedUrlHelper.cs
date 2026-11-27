namespace StudyBuddy.Services.Email;

public static class TrustedUrlHelper
{
    /// <summary>
    /// Resolves a trusted absolute application base URI, preferring an explicitly configured
    /// Email:AppBaseUrl (when valid http/https) and falling back to the current request's base URI.
    /// </summary>
    public static Uri ResolveBaseUri(string? configuredAppBaseUrl, string fallbackBaseUri)
    {
        if (!string.IsNullOrWhiteSpace(configuredAppBaseUrl) &&
            Uri.TryCreate(configuredAppBaseUrl.Trim(), UriKind.Absolute, out var configured) &&
            (configured.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
             configured.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            return configured;
        }

        if (Uri.TryCreate(fallbackBaseUri, UriKind.Absolute, out var fallback) &&
            (fallback.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
             fallback.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)))
        {
            return fallback;
        }

        return new Uri("https://localhost/");
    }

    public static string BuildAbsoluteUrl(string? configuredAppBaseUrl, string fallbackBaseUri, string relativePath)
    {
        var baseUri = ResolveBaseUri(configuredAppBaseUrl, fallbackBaseUri);
        return new Uri(baseUri, relativePath.TrimStart('/')).AbsoluteUri;
    }

    /// <summary>
    /// Masks an email address for display on public confirmation pages (e.g. a***@example.com).
    /// </summary>
    public static string MaskEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return string.Empty;
        }

        var trimmed = email.Trim();
        var atIndex = trimmed.IndexOf('@');
        if (atIndex <= 0 || atIndex == trimmed.Length - 1)
        {
            return "***";
        }

        var local = trimmed[..atIndex];
        var domain = trimmed[(atIndex + 1)..];
        var visiblePrefix = local.Length <= 2 ? local[..1] : local[..2];
        return $"{visiblePrefix}***@{domain}";
    }
}

