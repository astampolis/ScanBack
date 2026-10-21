namespace ScanBack.Infrastructure.Hosting;

public sealed class PrivacyOptions
{
    public string ControllerName { get; set; } = "";
    public string ContactEmail { get; set; } = "";
    public string PolicyVersion { get; set; } = "";
    public string HealthConsentTextPublic { get; set; } = "I explicitly consent to ScanBack storing my health information for emergency identification. Only the fields I select will be publicly accessible to anyone who scans, photographs, or receives my sticker URL. I can withdraw consent and delete this information at any time.";
}
