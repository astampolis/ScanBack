using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ScanBack.Domain;
using ScanBack.Infrastructure;
using ScanBack.Web.Services;

var builder = WebApplication.CreateBuilder(args.Where(a => a is not "--migrate" and not "--provision-admin").ToArray());
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true).AddEnvironmentVariables();
var connection = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connection))
    throw new InvalidOperationException("Set ConnectionStrings__DefaultConnection. See README.md or run scripts/dev-up.sh.");
var baseUrl = builder.Configuration["Site:BaseUrl"] ?? "http://localhost:5080";
if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var siteUri) || siteUri.Scheme is not ("http" or "https"))
    throw new InvalidOperationException("Site:BaseUrl must be an absolute HTTP(S) URL.");
var development = builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing");
if (!development)
{
    if (siteUri.Scheme != "https") throw new InvalidOperationException("Production Site:BaseUrl must use HTTPS.");
    var sql = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connection);
    if (sql.TrustServerCertificate || !sql.Encrypt)
        throw new InvalidOperationException("Production SQL connections must validate the TLS certificate and require encryption.");
    if (builder.Configuration["AllowedHosts"] is null or "*")
        throw new InvalidOperationException("Set AllowedHosts to your production hostname.");
}
var keys = builder.Configuration["Encryption:KeyDirectory"] ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys");
Directory.CreateDirectory(keys);
var protection = builder.Services.AddDataProtection().SetApplicationName("ScanBack").PersistKeysToFileSystem(new DirectoryInfo(keys));
var certPath = builder.Configuration["Encryption:CertificatePath"];
if (!string.IsNullOrWhiteSpace(certPath))
    protection.ProtectKeysWithCertificate(X509CertificateLoader.LoadPkcs12FromFile(certPath, builder.Configuration["Encryption:CertificatePassword"]));
else if (!development)
    throw new InvalidOperationException("Production requires Encryption:CertificatePath to protect persisted data-protection keys.");

builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlServer(connection));
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.SignIn.RequireConfirmedEmail = true;
    options.User.RequireUniqueEmail = true;
    options.Password.RequiredLength = 12;
    options.Password.RequireDigit = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireNonAlphanumeric = true;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    options.Lockout.MaxFailedAccessAttempts = 5;
}).AddEntityFrameworkStores<AppDbContext>().AddDefaultTokenProviders();
builder.Services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(2));
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "ScanBack.Account";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = development ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.LoginPath = "/account/login";
    options.AccessDeniedPath = "/account/access-denied";
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.Events.OnValidatePrincipal = async context =>
    {
        await SecurityStampValidator.ValidatePrincipalAsync(context);
        if (context.Principal is null) return;
        var users = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.GetUserAsync(context.Principal);
        if (user is null || !user.IsActive)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        }
    };
});
builder.Services.AddScoped<LocalizedValidationFilter>();
builder.Services.AddControllersWithViews(o =>
{
    o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
    o.Filters.Add<LocalizedValidationFilter>();
});
builder.Services.AddLocalization();
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture("en");
    options.SupportedCultures = [new CultureInfo("en"), new CultureInfo("el")];
    options.SupportedUICultures = options.SupportedCultures;
    options.RequestCultureProviders = [new CookieRequestCultureProvider()];
});
builder.Services.AddSingleton<SiteLinks>();
builder.Services.AddTransient<UiText>();
builder.Services.AddSingleton<TokenService>();
builder.Services.Configure<MailOptions>(builder.Configuration.GetSection("Mail"));
builder.Services.AddScoped<ScanBack.Infrastructure.IEmailSender, SmtpEmailSender>();
builder.Services.AddHostedService<NotificationWorker>();
builder.Services.AddHostedService<RetentionWorker>();
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("reports", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 6, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("authentication", context => HttpMethods.IsGet(context.Request.Method)
        ? RateLimitPartition.GetNoLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown")
        : RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 12, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "text/html; charset=utf-8";
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await context.HttpContext.Response.WriteAsync("<!doctype html><html lang=\"en\"><meta name=\"viewport\" content=\"width=device-width\"><title>Please wait</title><body><h1>Please wait before trying again</h1><p>For an emergency, call immediately.</p><p><a href=\"tel:112\">Call 112</a> · <a href=\"tel:166\">Call 166 — EKAB</a></p></body></html>", cancellationToken);
    };
});
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    foreach (var value in builder.Configuration.GetSection("ForwardedHeaders:TrustedProxies").Get<string[]>() ?? [])
        if (IPAddress.TryParse(value, out var address)) o.KnownProxies.Add(address);
});
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 3 * 1024 * 1024);

var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (development || args.Contains("--migrate")) await db.Database.MigrateAsync();
    if (!await db.PlatformSettings.AnyAsync())
    {
        db.PlatformSettings.Add(new PlatformSettings());
        await db.SaveChangesAsync();
    }
    var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    if (!await roles.RoleExistsAsync("Administrator")) await roles.CreateAsync(new IdentityRole("Administrator"));
    var adminEmail = builder.Configuration["Admin:Email"];
    var adminPassword = builder.Configuration["Admin:Password"];
    if ((development || args.Contains("--provision-admin")) && !string.IsNullOrWhiteSpace(adminEmail) && !string.IsNullOrWhiteSpace(adminPassword))
    {
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var admin = await users.FindByEmailAsync(adminEmail);
        if (admin is null)
        {
            admin = new ApplicationUser { UserName = adminEmail, Email = adminEmail, EmailConfirmed = true };
            var result = await users.CreateAsync(admin, adminPassword);
            if (!result.Succeeded) throw new InvalidOperationException("Administrator provisioning failed: " + string.Join("; ", result.Errors.Select(e => e.Code)));
            await users.AddToRoleAsync(admin, "Administrator");
            db.AuditEvents.Add(new AuditEvent { ActorId = admin.Id, Action = "AdministratorProvisioned", EntityId = admin.Id });
            await db.SaveChangesAsync();
        }
    }
}
if (args.Contains("--migrate") || args.Contains("--provision-admin")) return;

app.UseForwardedHeaders();
app.UseExceptionHandler("/home/error");
if (!development) { app.UseHsts(); app.UseHttpsRedirection(); }
app.Use(async (context, next) =>
{
    var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
    context.Items["CspNonce"] = nonce;
    context.Response.Headers.ContentSecurityPolicy = $"default-src 'self'; script-src 'self' 'nonce-{nonce}'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "geolocation=(self), camera=(), microphone=()";
    context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
    context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseStaticFiles();
app.UseRouting();
app.UseRequestLocalization();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/admin") && context.User.IsInRole("Administrator"))
    {
        var users = context.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.GetUserAsync(context.User);
        if (user is null || !user.TwoFactorEnabled)
        { context.Response.Redirect("/security"); return; }
        if (!context.User.Claims.Any(c => c.Type == "amr" && c.Value == "mfa"))
        {
            await context.SignOutAsync(IdentityConstants.ApplicationScheme);
            context.Response.Redirect("/account/login?returnUrl=%2Fadmin");
            return;
        }
    }
    await next();
});
app.MapGet("/health", async (AppDbContext db) =>
    await db.Database.CanConnectAsync() ? Results.Ok(new { status = "healthy" }) : Results.StatusCode(503));
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
await app.RunAsync();

public partial class Program;
