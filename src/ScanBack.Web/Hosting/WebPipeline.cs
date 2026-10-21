using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using ScanBack.Infrastructure;
using ScanBack.Infrastructure.Hosting;
using ScanBack.Web.Services;

namespace ScanBack.Web.Hosting;

public static class WebPipeline
{
    public static WebApplication UseScanBackWeb(this WebApplication app, RuntimeConfiguration runtime)
    {
        app.UseForwardedHeaders();
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
            context.Response.OnStarting(() =>
            {
                if ((HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
                    && context.Response.StatusCode == StatusCodes.Status200OK
                    && context.GetEndpoint()?.Metadata.GetMetadata<PublicMarketingPageAttribute>() is not null)
                {
                    context.Response.Headers["X-Robots-Tag"] = "index, follow";
                    context.Response.Headers.CacheControl = "private, max-age=0, must-revalidate";
                }
                return Task.CompletedTask;
            });
            await next();
        });
        app.UseExceptionHandler(_ => { });
        if (!runtime.IsDevelopment) { app.UseHsts(); app.UseHttpsRedirection(); }
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = context =>
            {
                // Only files beneath wwwroot use this middleware; rider photos are authorized controller actions.
                context.Context.Response.Headers.CacheControl = context.Context.Request.Query.ContainsKey("v")
                    ? "public, max-age=31536000, immutable" : "public, max-age=86400";
                context.Context.Response.Headers["X-Robots-Tag"] = "index, follow";
            }
        });
        app.UseRouting();
        // Public editorial HTML contains no account data, tokens or antiforgery
        // secrets. Keep response compression outside all sensitive workflows.
        app.UseWhen(context => context.GetEndpoint()?.Metadata.GetMetadata<PublicMarketingPageAttribute>() is not null,
            publicPages => publicPages.UseResponseCompression());
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
                if (user is null || !user.TwoFactorEnabled) { context.Response.Redirect("/security"); return; }
                if (!context.User.Claims.Any(c => c.Type == "amr" && c.Value == "mfa"))
                {
                    await context.SignOutAsync(IdentityConstants.ApplicationScheme);
                    context.Response.Redirect("/account/login?returnUrl=%2Fadmin");
                    return;
                }
            }
            await next();
        });
        app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
        app.MapGet("/health", async (AppDbContext db, CancellationToken ct) =>
        {
            try
            {
                await db.PlatformSettings.AsNoTracking().Select(x => x.Id).Take(1).ToListAsync(ct);
                return Results.Ok(new { status = "healthy" });
            }
            catch (Exception) when (!ct.IsCancellationRequested) { return Results.StatusCode(StatusCodes.Status503ServiceUnavailable); }
        });
        app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
        return app;
    }
}
