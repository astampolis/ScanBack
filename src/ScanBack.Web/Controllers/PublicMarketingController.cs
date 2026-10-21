using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using ScanBack.Web.Services;

namespace ScanBack.Web.Controllers;

public sealed class PublicMarketingController(SiteLinks links) : Controller
{
    [HttpGet("/en"), HttpHead("/en")]
    [HttpGet("/el"), HttpHead("/el")]
    [PublicMarketingPage]
    public IActionResult Index() => Page("");

    [HttpGet("/en/how-it-works"), HttpHead("/en/how-it-works")]
    [HttpGet("/el/how-it-works"), HttpHead("/el/how-it-works")]
    [PublicMarketingPage]
    public IActionResult HowItWorks() => Page("how-it-works");

    [HttpGet("/en/qr-nfc-helmet-stickers"), HttpHead("/en/qr-nfc-helmet-stickers")]
    [HttpGet("/el/qr-nfc-helmet-stickers"), HttpHead("/el/qr-nfc-helmet-stickers")]
    [PublicMarketingPage]
    public IActionResult Stickers() => Page("qr-nfc-helmet-stickers");

    [HttpGet("/en/motorcycle-emergency-guide"), HttpHead("/en/motorcycle-emergency-guide")]
    [HttpGet("/el/motorcycle-emergency-guide"), HttpHead("/el/motorcycle-emergency-guide")]
    [PublicMarketingPage]
    public IActionResult EmergencyGuide() => Page("motorcycle-emergency-guide");

    [HttpGet("/how-it-works"), HttpHead("/how-it-works")]
    public IActionResult LegacyHowItWorks() => RedirectPermanent("/en/how-it-works");

    [HttpGet("/qr-nfc-helmet-stickers"), HttpHead("/qr-nfc-helmet-stickers")]
    public IActionResult LegacyStickers() => RedirectPermanent("/en/qr-nfc-helmet-stickers");

    [HttpGet("/motorcycle-emergency-guide"), HttpHead("/motorcycle-emergency-guide")]
    public IActionResult LegacyEmergencyGuide() => RedirectPermanent("/en/motorcycle-emergency-guide");

    private IActionResult Page(string slug)
    {
        // A public URL always renders its own language, independent of cookies.
        var language = Request.Path.StartsWithSegments("/el", StringComparison.OrdinalIgnoreCase) ? "el" : "en";
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        Response.Headers.ContentLanguage = language;
        var page = PublicMarketingCatalog.Page(language, slug, links);
        ViewData["Seo"] = page.Seo;
        ViewData["Title"] = page.Seo.Title;
        return View(slug.Length == 0 ? "Index" : "Article", page);
    }
}
