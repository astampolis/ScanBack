using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using ScanBack.Infrastructure;
using ScanBack.Infrastructure.Hosting;
using ScanBack.Web.Services;

namespace ScanBack.Web.Hosting;

public static class WebServices
{
    public static WebApplicationBuilder AddScanBackConfiguration(this WebApplicationBuilder builder)
    {
        if (builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"))
            builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true);
        var file = Environment.GetEnvironmentVariable("SCANBACK_CONFIG_FILE");
        if (!string.IsNullOrWhiteSpace(file))
        {
            if (!Path.IsPathFullyQualified(file)) throw new InvalidOperationException("SCANBACK_CONFIG_FILE must be an absolute external JSON path.");
            builder.Configuration.AddJsonFile(file, optional: false, reloadOnChange: false);
        }
        builder.Configuration.AddEnvironmentVariables();
        return builder;
    }

    public static IServiceCollection AddScanBackWeb(this IServiceCollection services, IConfiguration configuration, RuntimeConfiguration runtime)
    {
        services.AddScanBackDataProtection(runtime);
        services.AddScanBackPersistence(configuration, runtime);
        services.AddProcessingServices();
        services.AddIdentity<ApplicationUser, IdentityRole>(IdentityPolicy.Configure).AddEntityFrameworkStores<AppDbContext>().AddDefaultTokenProviders();
        services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromHours(2));
        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = runtime.IsDevelopment ? "ScanBack.Account" : "__Host-ScanBack.Account";
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            options.Cookie.IsEssential = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = runtime.IsDevelopment ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.LoginPath = "/account/login";
            options.AccessDeniedPath = "/account/access-denied";
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = false;
            options.Events.OnValidatePrincipal = async context =>
            {
                await SecurityStampValidator.ValidatePrincipalAsync(context);
                if (context.Principal is null) return;
                var users = context.HttpContext.RequestServices.GetRequiredService<UserManager<ApplicationUser>>();
                var user = await users.GetUserAsync(context.Principal);
                if (user is null || !user.IsActive)
                { context.RejectPrincipal(); await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme); }
            };
        });
        services.Configure<CookieAuthenticationOptions>(IdentityConstants.TwoFactorUserIdScheme, options =>
        {
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = runtime.IsDevelopment ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.ExpireTimeSpan = TimeSpan.FromMinutes(5);
        });
        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = runtime.IsDevelopment ? "ScanBack.Csrf" : "__Host-ScanBack.Csrf";
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = runtime.IsDevelopment ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });
        services.AddScoped<LocalizedValidationFilter>();
        services.AddControllersWithViews(o => { o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()); o.Filters.Add<LocalizedValidationFilter>(); });
        services.AddLocalization();
        services.AddResponseCompression(options => options.EnableForHttps = true);
        services.Configure<RequestLocalizationOptions>(o =>
        {
            o.DefaultRequestCulture = new RequestCulture("en");
            o.SupportedCultures = [new CultureInfo("en"), new CultureInfo("el")];
            o.SupportedUICultures = o.SupportedCultures;
            o.RequestCultureProviders = [new CookieRequestCultureProvider()];
        });
        services.AddSingleton<SiteLinks>();
        services.AddTransient<UiText>();
        services.AddExceptionHandler<SafeExceptionHandler>();
        services.AddHsts(o => { o.MaxAge = TimeSpan.FromDays(365); o.IncludeSubDomains = false; });
        services.AddHttpsRedirection(o => { o.HttpsPort = 443; o.RedirectStatusCode = StatusCodes.Status308PermanentRedirect; });
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("reports", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 6, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.AddPolicy("authentication", context => HttpMethods.IsGet(context.Request.Method)
                ? RateLimitPartition.GetNoLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown")
                : RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 12, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.OnRejected = async (context, ct) =>
            {
                context.HttpContext.Response.ContentType = "text/html; charset=utf-8";
                context.HttpContext.Response.Headers.RetryAfter = "60";
                await context.HttpContext.Response.WriteAsync("<!doctype html><html lang=\"en\"><meta name=\"viewport\" content=\"width=device-width\"><title>Please wait</title><body><h1>Please wait before trying again</h1><p>For an emergency, call immediately.</p><p><a href=\"tel:112\">Call 112</a> · <a href=\"tel:166\">Call 166 — EKAB</a></p></body></html>", ct);
            };
        });
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            foreach (var value in configuration.GetSection("ForwardedHeaders:TrustedProxies").Get<string[]>() ?? [])
            {
                if (!IPAddress.TryParse(value, out var address)) throw new InvalidOperationException("TrustedProxies must contain explicit IP addresses.");
                o.KnownProxies.Add(address);
            }
        });
        if (runtime.IsDevelopment)
        {
            services.AddHostedService<NotificationWorker>();
            services.AddHostedService<RetentionWorker>();
        }
        return services;
    }
}
