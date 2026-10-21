using System.Net;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ScanBack.Domain;

namespace ScanBack.Tests;

[Collection(SqlWebCollection.Name)]
public sealed class SeoTests(SqlWebFixture fixture)
{
    private static readonly string[] Slugs = ["", "how-it-works", "qr-nfc-helmet-stickers", "motorcycle-emergency-guide"];

    [Fact]
    public async Task Compression_is_limited_to_public_editorial_content_and_brand_assets_are_crawlable()
    {
        using var client = fixture.Client();
        client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("gzip");
        var page = await client.GetAsync("/en");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("gzip", page.Content.Headers.ContentEncoding);
        await using var compressed = await page.Content.ReadAsStreamAsync();
        await using var decompressed = new GZipStream(compressed, CompressionMode.Decompress);
        using var reader = new StreamReader(decompressed);
        Assert.Contains("<html lang=\"en\">", await reader.ReadToEndAsync());
        var registration = await client.GetAsync("/account/register");
        Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
        Assert.Empty(registration.Content.Headers.ContentEncoding);
        Assert.Equal("noindex, nofollow", registration.Headers.GetValues("X-Robots-Tag").Single());
        Assert.True(registration.Headers.CacheControl?.NoStore);
        Assert.Contains("__RequestVerificationToken", await registration.Content.ReadAsStringAsync());
        var brand = await client.GetAsync("/images/scanback-social.png");
        Assert.Equal(HttpStatusCode.OK, brand.StatusCode);
        Assert.Equal("index, follow", brand.Headers.GetValues("X-Robots-Tag").Single());
        Assert.True(brand.Headers.CacheControl?.Public);
    }

    [Theory]
    [InlineData("en", "")]
    [InlineData("el", "")]
    [InlineData("en", "how-it-works")]
    [InlineData("el", "how-it-works")]
    [InlineData("en", "qr-nfc-helmet-stickers")]
    [InlineData("el", "qr-nfc-helmet-stickers")]
    [InlineData("en", "motorcycle-emergency-guide")]
    [InlineData("el", "motorcycle-emergency-guide")]
    public async Task Public_pages_have_trusted_canonicals_reciprocal_languages_and_valid_structured_data(string language, string slug)
    {
        using var client = fixture.Client();
        client.DefaultRequestHeaders.Host = "127.0.0.1";
        var opposite = language == "en" ? "el" : "en";
        client.DefaultRequestHeaders.Add("Cookie", $".AspNetCore.Culture=c={opposite}|uic={opposite}");
        var path = PathFor(language, slug);
        var response = await client.GetAsync(path + "?utm_source=untrusted-query-canary");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("index", response.Headers.GetValues("X-Robots-Tag").Single().Split(',').Select(value => value.Trim()));
        Assert.DoesNotContain("noindex", response.Headers.GetValues("X-Robots-Tag").Single(), StringComparison.Ordinal);
        Assert.Equal(language, Assert.Single(response.Content.Headers.ContentLanguage));
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var html = await response.Content.ReadAsStringAsync();
        Assert.Equal(language, Assert.Single(Tags(html, "html"))["lang"]);
        Assert.DoesNotContain("__RequestVerificationToken", html, StringComparison.Ordinal);
        var links = Tags(html, "link").ToList();
        var canonical = Assert.Single(links, link => link.GetValueOrDefault("rel") == "canonical");
        Assert.Equal("http://localhost" + path, canonical["href"]);
        var alternates = links.Where(link => link.GetValueOrDefault("rel") == "alternate" && link.ContainsKey("hreflang"))
            .ToDictionary(link => link["hreflang"], link => link["href"]);
        Assert.Equal(3, alternates.Count);
        Assert.Equal("http://localhost" + PathFor("en", slug), alternates["en"]);
        Assert.Equal("http://localhost" + PathFor("el", slug), alternates["el"]);
        Assert.Equal(alternates["en"], alternates["x-default"]);
        var metadata = Tags(html, "meta").ToList();
        Assert.False(string.IsNullOrWhiteSpace(Assert.Single(metadata, meta => meta.GetValueOrDefault("name") == "description")["content"]));
        Assert.Equal("http://localhost" + path, Assert.Single(metadata, meta => meta.GetValueOrDefault("property") == "og:url")["content"]);
        var structured = Assert.Single(Regex.Matches(html, "<script\\b[^>]*>([\\s\\S]*?)</script>", RegexOptions.IgnoreCase).Cast<Match>(),
            script => Assert.Single(Tags(script.Value, "script")).GetValueOrDefault("type") == "application/ld+json");
        using var json = JsonDocument.Parse(structured.Groups[1].Value);
        Assert.Equal("https://schema.org", json.RootElement.GetProperty("@context").GetString());
        var graph = json.RootElement.GetProperty("@graph").EnumerateArray().ToList();
        Assert.Contains(graph, item => item.GetProperty("@type").GetString() == "Organization");
        Assert.Contains(graph, item => item.GetProperty("@type").GetString() == "WebSite");
        var breadcrumbs = Assert.Single(graph, item => item.GetProperty("@type").GetString() == "BreadcrumbList");
        Assert.All(breadcrumbs.GetProperty("itemListElement").EnumerateArray(), item => Assert.Equal("ListItem", item.GetProperty("@type").GetString()));
        Assert.DoesNotContain("untrusted-query-canary", structured.Groups[1].Value, StringComparison.Ordinal);
        var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, path));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal("index, follow", head.Headers.GetValues("X-Robots-Tag").Single());
    }

    [Fact]
    public async Task Search_discovery_lists_exactly_eight_public_pages_and_excludes_personal_links()
    {
        var rider = await fixture.SeedRider(PublicFields.Name | PublicFields.Allergies);
        using var client = fixture.Client();
        var response = await client.GetAsync("/sitemap.xml");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var xml = await response.Content.ReadAsStringAsync();
        var document = XDocument.Parse(xml);
        XNamespace sitemap = "http://www.sitemaps.org/schemas/sitemap/0.9";
        XNamespace xhtml = "http://www.w3.org/1999/xhtml";
        var entries = document.Root!.Elements(sitemap + "url").ToList();
        var expected = Slugs.SelectMany(slug => new[] { "http://localhost" + PathFor("en", slug), "http://localhost" + PathFor("el", slug) }).Order().ToArray();
        Assert.Equal(expected, entries.Select(entry => entry.Element(sitemap + "loc")!.Value).Order().ToArray());
        foreach (var entry in entries)
        {
            var location = new Uri(entry.Element(sitemap + "loc")!.Value);
            var suffix = location.AbsolutePath.Length > 3 ? location.AbsolutePath[3..] : "";
            var alternates = entry.Elements(xhtml + "link").ToDictionary(link => link.Attribute("hreflang")!.Value, link => link.Attribute("href")!.Value);
            Assert.Equal(3, alternates.Count);
            Assert.Equal("http://localhost/en" + suffix, alternates["en"]);
            Assert.Equal("http://localhost/el" + suffix, alternates["el"]);
            Assert.Equal(alternates["en"], alternates["x-default"]);
        }
        Assert.DoesNotContain(rider.Token, xml, StringComparison.Ordinal);
        Assert.DoesNotContain(rider.UserId, xml, StringComparison.Ordinal);
        var robots = await client.GetStringAsync("/robots.txt");
        Assert.Contains("Sitemap: http://localhost/sitemap.xml", robots, StringComparison.Ordinal);
        foreach (var path in new[] { "/e/", "/incident", "/account", "/profile", "/contacts", "/admin", "/security", "/health" })
            Assert.Contains("Disallow: " + path, robots, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Marketing_never_embeds_authenticated_rider_information_and_private_pages_remain_unindexed()
    {
        var rider = await fixture.SeedRider(PublicFields.Name | PublicFields.Allergies);
        using var owner = fixture.Client();
        await SqlWebFixture.Login(owner, rider.Email);
        var publicPage = await owner.GetAsync("/en");
        Assert.Equal(HttpStatusCode.OK, publicPage.StatusCode);
        var publicHtml = await publicPage.Content.ReadAsStringAsync();
        foreach (var secret in new[] { rider.Email, rider.Token, rider.Profile.FullName, rider.Profile.Allergies! })
            Assert.DoesNotContain(secret, publicHtml, StringComparison.Ordinal);
        Assert.DoesNotContain("__RequestVerificationToken", publicHtml, StringComparison.Ordinal);
        using var anonymous = fixture.Client();
        foreach (var path in new[] { "/e/" + rider.Token, "/account/login", "/privacy" })
        {
            var response = await anonymous.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("noindex, nofollow", response.Headers.GetValues("X-Robots-Tag").Single());
            Assert.True(response.Headers.CacheControl?.NoStore);
            var html = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(Tags(html, "link"), link => link.GetValueOrDefault("rel") == "canonical");
            Assert.DoesNotContain(Tags(html, "script"), script => script.GetValueOrDefault("type") == "application/ld+json");
        }
        var rejected = await anonymous.PostAsync("/en", new StringContent(""));
        Assert.False(rejected.IsSuccessStatusCode);
        Assert.Equal("noindex, nofollow", rejected.Headers.GetValues("X-Robots-Tag").Single());
        Assert.True(rejected.Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData("/", "/en")]
    [InlineData("/how-it-works", "/en/how-it-works")]
    [InlineData("/qr-nfc-helmet-stickers", "/en/qr-nfc-helmet-stickers")]
    [InlineData("/motorcycle-emergency-guide", "/en/motorcycle-emergency-guide")]
    public async Task Legacy_public_URLs_redirect_permanently_to_the_English_canonical(string source, string target)
    {
        using var client = fixture.Client();
        var response = await client.GetAsync(source);
        Assert.Equal(HttpStatusCode.MovedPermanently, response.StatusCode);
        Assert.Equal(target, response.Headers.Location?.OriginalString);
    }

    private static string PathFor(string language, string slug) => "/" + language + (slug.Length == 0 ? "" : "/" + slug);
    private static IEnumerable<Dictionary<string, string>> Tags(string html, string tag) =>
        Regex.Matches(html, "<" + tag + "\\b[^>]*>", RegexOptions.IgnoreCase).Select(match =>
            Regex.Matches(match.Value, "([a-zA-Z_][a-zA-Z0-9_.:-]*)\\s*=\\s*[\"']([^\"']*)[\"']")
                .ToDictionary(attribute => attribute.Groups[1].Value, attribute => WebUtility.HtmlDecode(attribute.Groups[2].Value), StringComparer.OrdinalIgnoreCase));
}
