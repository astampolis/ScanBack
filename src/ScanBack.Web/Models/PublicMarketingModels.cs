namespace ScanBack.Web.Models;

public sealed record PublicSeoAlternate(string Language, string Url);

public sealed record PublicSeoMetadata(
    string Language,
    string Title,
    string Description,
    string CanonicalUrl,
    string ImageUrl,
    IReadOnlyList<PublicSeoAlternate> Alternates,
    string StructuredDataJson,
    string Slug)
{
    public string LocalPath(string language) => $"/{language}" + (Slug.Length == 0 ? "" : "/" + Slug);
}

public sealed record MarketingSection(string Heading, IReadOnlyList<string> Paragraphs, IReadOnlyList<string>? Steps = null);

public sealed record PublicMarketingPage(
    PublicSeoMetadata Seo,
    string Heading,
    string Introduction,
    IReadOnlyList<MarketingSection> Sections)
{
    public bool IsGreek => Seo.Language == "el";
}
