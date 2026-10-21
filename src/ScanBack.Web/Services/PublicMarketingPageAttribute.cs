namespace ScanBack.Web.Services;

/// <summary>Only reviewed, static marketing endpoints may opt into search indexing.</summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class PublicMarketingPageAttribute : Attribute;
