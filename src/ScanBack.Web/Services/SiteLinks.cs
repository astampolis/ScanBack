namespace ScanBack.Web.Services;

/// <summary>Builds capability links from trusted configuration, never the HTTP Host header.</summary>
public sealed class SiteLinks(IConfiguration configuration)
{
    public string BaseUrl { get; } = (configuration["Site:BaseUrl"] ?? "http://localhost:5080").TrimEnd('/');
    public string Absolute(string path) => BaseUrl + "/" + path.TrimStart('/');
}
