using System.Globalization;
using System.Xml.Linq;
using Microsoft.AspNetCore.Mvc;
using ScanBack.Web.Services;

namespace ScanBack.Web.Controllers;

public sealed class SearchDiscoveryController(SiteLinks links, IConfiguration configuration) : Controller
{
    [HttpGet("/robots.txt"), HttpHead("/robots.txt")]
    public IActionResult Robots()
    {
        // Blocking a crawler is not authentication. Private responses separately
        // retain noindex/nofollow, no-store and their own authorization controls.
        var text = "User-agent: *\nAllow: /en\nAllow: /el\nAllow: /css/\nAllow: /js/\nAllow: /vendor/\nAllow: /images/\nAllow: /favicon.svg\n"
            + "Disallow: /account\nDisallow: /login\nDisallow: /logout\nDisallow: /forgot-password\nDisallow: /reset-password\nDisallow: /profile\nDisallow: /contacts\nDisallow: /stickers\nDisallow: /admin\nDisallow: /security\nDisallow: /e/\nDisallow: /incident\nDisallow: /i/\nDisallow: /photo\nDisallow: /health\nDisallow: /Home/\nDisallow: /privacy\n"
            + "Sitemap: " + links.Absolute("/sitemap.xml") + "\n";
        return Content(text, "text/plain; charset=utf-8");
    }

    [HttpGet("/sitemap.xml"), HttpHead("/sitemap.xml")]
    public IActionResult Sitemap()
    {
        XNamespace sitemap = "http://www.sitemaps.org/schemas/sitemap/0.9";
        XNamespace xhtml = "http://www.w3.org/1999/xhtml";
        var root = new XElement(sitemap + "urlset", new XAttribute(XNamespace.Xmlns + "xhtml", xhtml));
        // Omit lastmod unless the operator supplies an actual editorial update
        // date. Request time is not a content modification time.
        var hasDate = DateOnly.TryParseExact(configuration["Site:ContentUpdatedAt"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var updated);
        foreach (var slug in PublicMarketingCatalog.Slugs)
        foreach (var language in PublicMarketingCatalog.Languages)
        {
            var page = PublicMarketingCatalog.Page(language, slug, links).Seo;
            var url = new XElement(sitemap + "url", new XElement(sitemap + "loc", page.CanonicalUrl));
            if (hasDate) url.Add(new XElement(sitemap + "lastmod", updated.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
            foreach (var alternate in page.Alternates)
                url.Add(new XElement(xhtml + "link", new XAttribute("rel", "alternate"), new XAttribute("hreflang", alternate.Language), new XAttribute("href", alternate.Url)));
            url.Add(new XElement(xhtml + "link", new XAttribute("rel", "alternate"), new XAttribute("hreflang", "x-default"), new XAttribute("href", page.Alternates[0].Url)));
            root.Add(url);
        }
        return Content(new XDocument(root).ToString(), "application/xml; charset=utf-8");
    }
}
