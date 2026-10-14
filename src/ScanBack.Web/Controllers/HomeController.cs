using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;

namespace ScanBack.Web.Controllers;

public sealed class HomeController : Controller
{
    public IActionResult Index() => View();

    [HttpGet("privacy")]
    public IActionResult Privacy() => View();

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
