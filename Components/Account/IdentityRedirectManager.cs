using Microsoft.AspNetCore.Components;

namespace StudyBuddy.Components.Account;

internal sealed class IdentityRedirectManager(NavigationManager navigationManager)
{
    public const string StatusCookieName = "StudyBuddy.Identity.StatusMessage";

    private static readonly CookieBuilder StatusCookieBuilder = new()
    {
        SameSite = SameSiteMode.Strict,
        HttpOnly = true,
        IsEssential = true,
        MaxAge = TimeSpan.FromSeconds(10)
    };

    public string CurrentPath => navigationManager.ToAbsoluteUri(navigationManager.Uri).GetLeftPart(UriPartial.Path);

    public string GetSafeLocalReturnUrl(string? returnUrl, string defaultUrl = "/dashboard")
    {
        if (IsLocalUrl(returnUrl))
        {
            return returnUrl!;
        }

        return defaultUrl;
    }

    public static bool IsLocalUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        if (url[0] == '/')
        {
            if (url.Length == 1)
            {
                return true;
            }

            if (url[1] != '/' && url[1] != '\\')
            {
                return true;
            }

            return false;
        }

        return false;
    }

    public void RedirectTo(string? uri)
    {
        uri ??= "/";
        if (!IsLocalUrl(uri))
        {
            uri = "/";
        }

        navigationManager.NavigateTo(uri, forceLoad: false);
    }

    public void RedirectTo(string uri, Dictionary<string, object?> queryParameters)
    {
        var uriWithoutQuery = navigationManager.ToAbsoluteUri(uri).GetLeftPart(UriPartial.Path);
        var newUri = navigationManager.GetUriWithQueryParameters(uriWithoutQuery, queryParameters);
        var relative = "/" + navigationManager.ToBaseRelativePath(newUri);
        RedirectTo(relative);
    }

    public void RedirectToWithStatus(string uri, string message, HttpContext context)
    {
        context.Response.Cookies.Append(StatusCookieName, message, StatusCookieBuilder.Build(context));
        RedirectTo(uri);
    }

    public void RedirectToCurrentPage() => RedirectTo("/" + navigationManager.ToBaseRelativePath(CurrentPath));

    public void RedirectToCurrentPageWithStatus(string message, HttpContext context) =>
        RedirectToWithStatus("/" + navigationManager.ToBaseRelativePath(CurrentPath), message, context);
}

