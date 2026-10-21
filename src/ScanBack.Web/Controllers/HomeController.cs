using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ScanBack.Infrastructure;
using ScanBack.Infrastructure.Hosting;

namespace ScanBack.Web.Controllers;

public sealed class HomeController(AppDbContext db, IOptions<PrivacyOptions> privacy) : Controller
{
    public IActionResult Index() => RedirectPermanent("/en");

    [HttpGet("privacy")]
    public async Task<IActionResult> Privacy()
    {
        var settings = await db.PlatformSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1);
        ViewData["IncidentAccessHours"] = settings?.IncidentAccessHours ?? 24;
        ViewData["IncidentRetentionDays"] = settings?.IncidentRetentionDays ?? 30;
        return View(privacy.Value);
    }

    [IgnoreAntiforgeryToken]
    public IActionResult Error() => View();

    [HttpGet]
    public IActionResult Language(string culture = "en", string? returnUrl = null)
    {
        var selected = culture == "el" ? "el" : "en";
        Response.Cookies.Append(CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(selected)),
            new CookieOptions
            {
                Expires = DateTimeOffset.UtcNow.AddYears(1),
                HttpOnly = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                IsEssential = true
            });
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
    }
}
