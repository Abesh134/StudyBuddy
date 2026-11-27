using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using StudyBuddy.Models;
using StudyBuddy.Services;

namespace StudyBuddy.Components.Account;

internal static class IdentityComponentsEndpointRouteBuilderExtensions
{
    private static readonly TimeSpan DevTestCooldown = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, DateTimeOffset> DevTestCooldowns = new(StringComparer.Ordinal);

    public static IEndpointConventionBuilder MapAdditionalIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var accountGroup = endpoints.MapGroup("/Account");

        accountGroup.MapPost("/Logout", async (
            ClaimsPrincipal user,
            [FromServices] SignInManager<ApplicationUser> signInManager,
            [FromForm] string? returnUrl) =>
        {
            await signInManager.SignOutAsync();
            var safeReturnUrl = IdentityRedirectManager.IsLocalUrl(returnUrl) ? returnUrl! : "/";
            return TypedResults.LocalRedirect(safeReturnUrl);
        });

        return accountGroup;
    }

    /// <summary>
    /// Maps secure, authenticated, antiforgery-protected Development-only endpoints
    /// for inspecting sanitized email status and manually testing Resend delivery.
    /// Never mapped outside Development and never exposes ApiKey.
    /// </summary>
    public static void MapDevEmailTestingEndpoints(this IEndpointRouteBuilder endpoints, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (!environment.IsDevelopment())
        {
            return;
        }

        var devGroup = endpoints.MapGroup("/Account/Dev").RequireAuthorization();

        devGroup.MapGet("/EmailStatus", (
            [FromServices] IOptionsMonitor<EmailOptions> optionsMonitor) =>
        {
            var opts = optionsMonitor.CurrentValue;
            return Results.Ok(new
            {
                opts.Enabled,
                opts.Provider,
                opts.FromEmail,
                opts.FromName,
                TimeoutSeconds = (int)opts.EffectiveTimeout.TotalSeconds,
                opts.IsConfigured,
                opts.IsUsingResendTestDomain
            });
        });

        devGroup.MapPost("/TestEmail", async (
            ClaimsPrincipal user,
            [FromForm] string? toEmail,
            [FromServices] IEmailService emailService,
            CancellationToken cancellationToken) =>
        {
            var userId = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Results.Unauthorized();
            }

            var recipient = string.IsNullOrWhiteSpace(toEmail)
                ? user.FindFirstValue(ClaimTypes.Email)
                : toEmail.Trim();

            if (string.IsNullOrWhiteSpace(recipient))
            {
                return Results.BadRequest(new { succeeded = false, errorMessage = "A recipient email address is required." });
            }

            var now = DateTimeOffset.UtcNow;
            if (DevTestCooldowns.TryGetValue(userId, out var lastSent) && (now - lastSent) < DevTestCooldown)
            {
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            }

            DevTestCooldowns[userId] = now;

            var idempotencyKey = $"sb-dev-test-{userId}-{now.ToUnixTimeSeconds() / 30}";
            var result = await emailService.SendAsync(
                new EmailMessage(
                    To: recipient,
                    Subject: "Study Buddy — Development Test Email",
                    HtmlBody: "<p>This is a development-only test email from <strong>Study Buddy</strong> via Resend.</p>",
                    TextBody: "This is a development-only test email from Study Buddy via Resend.",
                    IdempotencyKey: idempotencyKey,
                    Category: "dev-test"),
                cancellationToken);

            if (!result.Succeeded)
            {
                DevTestCooldowns.TryRemove(userId, out _);
            }

            return Results.Ok(new
            {
                result.Succeeded,
                Status = result.Status.ToString(),
                result.MessageId,
                result.HttpStatusCode,
                result.ErrorMessage
            });
        });
    }
}
