using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using StudyBuddy.Components;
using StudyBuddy.Components.Account;
using StudyBuddy.Data;
using StudyBuddy.Models;
using StudyBuddy.Services;
using StudyBuddy.Services.Documents;
using StudyBuddy.Services.Email;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IdentityRedirectManager>();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
builder.Services.AddAuthorization();

var rawConnectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? "Data Source=App_Data/studybuddy.db";

var sqliteBuilder = new SqliteConnectionStringBuilder(rawConnectionString);
if (!string.IsNullOrWhiteSpace(sqliteBuilder.DataSource) &&
    !sqliteBuilder.DataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
{
    var fullDbPath = Path.IsPathRooted(sqliteBuilder.DataSource)
        ? Path.GetFullPath(sqliteBuilder.DataSource)
        : Path.GetFullPath(Path.Combine(builder.Environment.ContentRootPath, sqliteBuilder.DataSource));

    var webRootFullPath = Path.GetFullPath(
        string.IsNullOrWhiteSpace(builder.Environment.WebRootPath)
            ? Path.Combine(builder.Environment.ContentRootPath, "wwwroot")
            : builder.Environment.WebRootPath);

    if (fullDbPath.StartsWith(webRootFullPath, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException(
            $"SQLite database path '{fullDbPath}' must be located outside wwwroot.");
    }

    var dbDirectory = Path.GetDirectoryName(fullDbPath);
    if (!string.IsNullOrWhiteSpace(dbDirectory))
    {
        Directory.CreateDirectory(dbDirectory);
    }

    sqliteBuilder.DataSource = fullDbPath;
}

var resolvedConnectionString = sqliteBuilder.ToString();

// Short-lived study operations use IDbContextFactory<AppDbContext>.
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite(resolvedConnectionString));

// Keep Identity stores compatible with request-scoped account operations.
builder.Services.AddScoped<AppDbContext>(sp =>
    sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

builder.Services.Configure<EmailOptions>(
    builder.Configuration.GetSection(EmailOptions.SectionName));

var emailOptions = builder.Configuration.GetSection(EmailOptions.SectionName).Get<EmailOptions>()
    ?? new EmailOptions();

builder.Services.AddHttpClient(ResendEmailService.HttpClientName, client =>
{
    var baseUrl = Uri.TryCreate(emailOptions.BaseUrl, UriKind.Absolute, out var parsedUri)
        ? parsedUri
        : new Uri("https://api.resend.com/");
    client.BaseAddress = baseUrl;
    client.Timeout = Timeout.InfiniteTimeSpan; // Per-request linked timeout is enforced in ResendEmailService.
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("StudyBuddy/1.0");
});

builder.Services.AddSingleton<IEmailTemplateService, EmailTemplateService>();
builder.Services.AddSingleton<IEmailService, ResendEmailService>();
builder.Services.AddSingleton<IEmailNotificationService, EmailNotificationService>();
builder.Services.AddSingleton<ConfigurableEmailSender>();
builder.Services.AddSingleton<IStudyBuddyEmailSender>(sp =>
    sp.GetRequiredService<ConfigurableEmailSender>());
builder.Services.AddSingleton<IEmailSender<ApplicationUser>>(sp =>
    sp.GetRequiredService<ConfigurableEmailSender>());

var requireConfirmedAccount = !builder.Environment.IsDevelopment() ||
    emailOptions.IsConfigured ||
    builder.Configuration.GetValue<bool>("Identity:RequireConfirmedAccount");

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies(options =>
    {
        options.ApplicationCookie?.Configure(cookie =>
        {
            cookie.LoginPath = "/Account/Login";
            cookie.LogoutPath = "/Account/Logout";
            cookie.AccessDeniedPath = "/Account/AccessDenied";
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SameSite = SameSiteMode.Lax;
            cookie.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;
            cookie.SlidingExpiration = true;
            cookie.ExpireTimeSpan = TimeSpan.FromDays(14);
        });
    });

builder.Services.AddIdentityCore<ApplicationUser>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.SignIn.RequireConfirmedAccount = requireConfirmedAccount;

        options.Password.RequiredLength = 8;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = false;

        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddScoped<IOwnerScopedDeckService, OwnerScopedDeckService>();
builder.Services.AddScoped<IUserStreakService, UserStreakService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.Configure<DeckUploadOptions>(
    builder.Configuration.GetSection(DeckUploadOptions.SectionName));
builder.Services.Configure<DocumentExtractionOptions>(
    builder.Configuration.GetSection(DocumentExtractionOptions.SectionName));

builder.Services.AddSingleton<IOcrService, TesseractCliOcrService>();
builder.Services.AddSingleton<IPdfPageRenderer, PdfToImagePageRenderer>();
builder.Services.AddSingleton<ITextExtractor, PlainTextExtractor>();
builder.Services.AddSingleton<ITextExtractor, PdfTextExtractor>();
builder.Services.AddSingleton<ITextExtractor, WordTextExtractor>();
builder.Services.AddSingleton<ITextExtractor, PowerPointTextExtractor>();
builder.Services.AddSingleton<ITextExtractor, ImageTextExtractor>();
builder.Services.AddSingleton<IDocumentExtractionService, DocumentExtractionService>();
builder.Services.AddSingleton<ISafeMarkdownRenderer, SafeMarkdownRenderer>();

builder.Services.AddScoped<IDeckService, DeckService>();

builder.Services.Configure<DevSeedOptions>(
    builder.Configuration.GetSection(DevSeedOptions.SectionName));
builder.Services.AddScoped<DevDataSeeder>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapAdditionalIdentityEndpoints();
app.MapDevEmailTestingEndpoints(app.Environment);

if (app.Environment.IsDevelopment() &&
    app.Configuration.GetValue<bool>($"{DevSeedOptions.SectionName}:Enabled"))
{
    using var scope = app.Services.CreateScope();
    var seeder = scope.ServiceProvider.GetRequiredService<DevDataSeeder>();
    await seeder.SeedIfEnabledAsync();
}

app.Run();

public partial class Program;
