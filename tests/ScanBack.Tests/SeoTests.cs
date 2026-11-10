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
    private static readonly string[] Slugs = ["", "how-it-works", "qr-nfc-helmet-stickers", "motorcycle-emergency-guide", "frequently-asked-questions"];

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
    [InlineData("en", "frequently-asked-questions")]
    [InlineData("el", "frequently-asked-questions")]
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
        var page = Assert.Single(graph, item => item.GetProperty("@type").GetString() == "WebPage");
        Assert.Equal("http://localhost" + path, page.GetProperty("url").GetString());
        Assert.Equal("http://localhost" + path + "#webpage", page.GetProperty("@id").GetString());
        Assert.Equal(language, page.GetProperty("inLanguage").GetString());
        Assert.Equal("http://localhost/#website", page.GetProperty("isPartOf").GetProperty("@id").GetString());
        if (slug.Length == 0)
        {
            Assert.DoesNotContain(graph, item => item.GetProperty("@type").GetString() == "BreadcrumbList");
        }
        else
        {
            var breadcrumbs = Assert.Single(graph, item => item.GetProperty("@type").GetString() == "BreadcrumbList");
            var items = breadcrumbs.GetProperty("itemListElement").EnumerateArray().ToList();
            Assert.Equal(2, items.Count);
            Assert.Equal(new[] { 1, 2 }, items.Select(item => item.GetProperty("position").GetInt32()));
            Assert.Equal("http://localhost" + path, items[1].GetProperty("item").GetString());
            Assert.Equal(breadcrumbs.GetProperty("@id").GetString(), page.GetProperty("breadcrumb").GetProperty("@id").GetString());
        }
        Assert.DoesNotContain("untrusted-query-canary", structured.Groups[1].Value, StringComparison.Ordinal);
        var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, path));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Equal("index, follow", head.Headers.GetValues("X-Robots-Tag").Single());
    }

    [Fact]
    public async Task Search_discovery_lists_exactly_ten_public_pages_and_excludes_personal_links()
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

    [Theory]
    [InlineData("/EN", "/en")]
    [InlineData("/el/", "/el")]
    [InlineData("/EN/HOW-IT-WORKS/", "/en/how-it-works")]
    [InlineData("/el/FREQUENTLY-ASKED-QUESTIONS", "/el/frequently-asked-questions")]
    public async Task Public_URL_variants_redirect_once_to_canonical_paths(string source, string target)
    {
        using var client = fixture.Client();
        foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head })
        {
            var response = await client.SendAsync(new HttpRequestMessage(method, source + "?utm_source=example"));
            Assert.Equal(HttpStatusCode.PermanentRedirect, response.StatusCode);
            Assert.Equal(target + "?utm_source=example", response.Headers.Location?.OriginalString);
            Assert.Equal("noindex, nofollow", response.Headers.GetValues("X-Robots-Tag").Single());
        }
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(target)).StatusCode);
    }

    [Fact]
    public async Task Editorial_normalization_does_not_rewrite_private_capability_or_account_paths()
    {
        var rider = await fixture.SeedRider(PublicFields.Name);
        using var client = fixture.Client();
        foreach (var path in new[] { "/E/" + rider.Token, "/ACCOUNT/LOGIN" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Null(response.Headers.Location);
            Assert.Equal("noindex, nofollow", response.Headers.GetValues("X-Robots-Tag").Single());
            Assert.True(response.Headers.CacheControl?.NoStore);
        }
    }

    [Fact]
    public async Task Every_editorial_page_has_distinct_metadata_a_single_H1_and_no_unneeded_scripts()
    {
        using var client = fixture.Client();
        var titles = new HashSet<string>();
        var descriptions = new HashSet<string>();
        foreach (var slug in Slugs)
        foreach (var language in new[] { "en", "el" })
        {
            var path = PathFor(language, slug);
            var html = await client.GetStringAsync(path);
            Assert.Single(Tags(html, "h1"));
            Assert.True(titles.Add(WebUtility.HtmlDecode(Assert.Single(Regex.Matches(html, "<title>(.*?)</title>")).Groups[1].Value)));
            Assert.True(descriptions.Add(Assert.Single(Tags(html, "meta"), item => item.GetValueOrDefault("name") == "description")["content"]));
            Assert.DoesNotContain(Tags(html, "script"), script => script.ContainsKey("src"));
            // JSON-LD describes the real site and page, without invented review or product offers.
            Assert.DoesNotContain("aggregateRating", html, StringComparison.Ordinal);
            Assert.DoesNotContain("FAQPage", html, StringComparison.Ordinal);
            if (slug.Length > 0)
            {
                var sections = Tags(html, "section").Where(section => section.GetValueOrDefault("id")?.StartsWith("section-", StringComparison.Ordinal) == true).ToList();
                Assert.NotEmpty(sections);
                var anchors = Tags(html, "a").Select(link => link.GetValueOrDefault("href")).ToList();
                Assert.All(sections, section => Assert.Contains("#" + section["id"], anchors));
            }
        }
        var missing = await client.GetAsync("/en/nonexistent-page");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("noindex, nofollow", missing.Headers.GetValues("X-Robots-Tag").Single());
    }

    private static string PathFor(string language, string slug) => "/" + language + (slug.Length == 0 ? "" : "/" + slug);
    private static IEnumerable<Dictionary<string, string>> Tags(string html, string tag) =>
        Regex.Matches(html, "<" + tag + "\\b[^>]*>", RegexOptions.IgnoreCase).Select(match =>
            Regex.Matches(match.Value, "([a-zA-Z_][a-zA-Z0-9_.:-]*)\\s*=\\s*[\"']([^\"']*)[\"']")
                .ToDictionary(attribute => attribute.Groups[1].Value, attribute => WebUtility.HtmlDecode(attribute.Groups[2].Value), StringComparer.OrdinalIgnoreCase));
}
