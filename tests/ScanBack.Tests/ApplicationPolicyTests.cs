using System.Xml.Linq;
using ScanBack.Application;
using ScanBack.Domain;

namespace ScanBack.Tests;

public sealed class ApplicationPolicyTests
{
    [Fact]
    public void Public_projection_combines_field_visibility_health_consent_and_contact_permission()
    {
        var profile = new RiderProfile
        {
            FullName = "Visible rider", DateOfBirth = new DateOnly(2000, 10, 22),
            Allergies = "Private allergy", Conditions = "Allowed condition", HealthConsent = true,
            PublicFields = PublicFields.Name | PublicFields.Age | PublicFields.Allergies | PublicFields.Contacts
        };
        profile.Contacts.AddRange([
            new EmergencyContact { Name = "Consenting contact", Phone = "+306900000001", PublicPhone = true, IsVerified = true, PublicPhoneConsentedAt = DateTimeOffset.UtcNow },
            new EmergencyContact { Name = "No phone consent", Phone = "+306900000002", PublicPhone = true, IsVerified = true },
            new EmergencyContact { Name = "Unverified contact", Phone = "+306900000003", PublicPhone = true, PublicPhoneConsentedAt = DateTimeOffset.UtcNow }
        ]);
        var identifier = new EmergencyIdentifier { Label = "Helmet", RiderProfile = profile };
        var projected = PublicProfileProjection.Project(identifier, new DateOnly(2026, 10, 21));
        Assert.Equal("Visible rider", projected.DisplayName);
        Assert.Equal(25, projected.Age);
        Assert.Equal("Private allergy", Assert.Single(projected.MedicalFields).Value);
        Assert.Equal("Consenting contact", Assert.Single(projected.Contacts).Name);
        profile.HealthConsent = false;
        Assert.Empty(PublicProfileProjection.Project(identifier, new DateOnly(2026, 10, 22)).MedicalFields);
        Assert.Equal(26, PublicProfileProjection.Project(identifier, new DateOnly(2026, 10, 22)).Age);
    }

    [Fact]
    public void A_new_notice_version_withholds_health_and_public_phone_until_current_permission_is_recorded()
    {
        var profile = new RiderProfile
        {
            HealthConsent = true, ConsentPolicyVersion = "old-version", Allergies = "Historical consent",
            PublicFields = PublicFields.Allergies | PublicFields.Contacts
        };
        var contact = new EmergencyContact { Name = "Historical contact", Phone = "+306900000001", PublicPhone = true, IsVerified = true, PublicPhoneConsentedAt = DateTimeOffset.UtcNow, ConsentPolicyVersion = "old-version" };
        profile.Contacts.Add(contact);
        var identifier = new EmergencyIdentifier { Label = "Helmet", RiderProfile = profile };
        var withheld = PublicProfileProjection.Project(identifier, new DateOnly(2026, 10, 21), "current-version");
        Assert.Empty(withheld.MedicalFields);
        Assert.Empty(withheld.Contacts);
        profile.ConsentPolicyVersion = "current-version";
        contact.ConsentPolicyVersion = "current-version";
        var renewed = PublicProfileProjection.Project(identifier, new DateOnly(2026, 10, 21), "current-version");
        Assert.Single(renewed.MedicalFields);
        Assert.Single(renewed.Contacts);
    }

    [Fact]
    public void Withdrawal_removes_all_health_flags_while_preserving_other_explicit_visibility()
    {
        var selected = PublicFields.Name | PublicFields.Photo | PublicFields.Age | PublicFields.Contacts | ConsentPolicy.HealthFields;
        Assert.Equal(PublicFields.Name | PublicFields.Photo | PublicFields.Age | PublicFields.Contacts, ConsentPolicy.WithoutHealth(selected));
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(3, 8)]
    [InlineData(5, 32)]
    [InlineData(500, 32)]
    public void Retry_backoff_is_bounded_and_preserves_the_reference_time(int attempts, int minutes)
    {
        var now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(now.AddMinutes(minutes), NotificationPolicy.RetryAt(now, attempts));
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData(0, 1)]
    [InlineData(7, 7)]
    [InlineData(400, 365)]
    public void Retention_cutoff_clamps_configuration_to_supported_limits(int? configured, int days)
    {
        var now = new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(now.AddDays(-days), RetentionPolicy.Cutoff(now, configured));
    }

    [Fact]
    public void Domain_and_application_projects_enforce_dependency_direction()
    {
        var root = FindRepositoryRoot();
        var domain = XDocument.Load(Path.Combine(root, "src", "ScanBack.Domain", "ScanBack.Domain.csproj"));
        Assert.Empty(domain.Descendants("ProjectReference"));
        Assert.Empty(domain.Descendants("PackageReference"));
        Assert.Empty(domain.Descendants("FrameworkReference"));
        var application = XDocument.Load(Path.Combine(root, "src", "ScanBack.Application", "ScanBack.Application.csproj"));
        Assert.Equal("../ScanBack.Domain/ScanBack.Domain.csproj", Assert.Single(application.Descendants("ProjectReference")).Attribute("Include")?.Value);
        Assert.Empty(application.Descendants("PackageReference"));
        Assert.Empty(application.Descendants("FrameworkReference"));
        var infrastructure = XDocument.Load(Path.Combine(root, "src", "ScanBack.Infrastructure", "ScanBack.Infrastructure.csproj"));
        Assert.DoesNotContain(infrastructure.Descendants("ProjectReference"), reference => reference.Attribute("Include")!.Value.Contains("ScanBack.Web", StringComparison.Ordinal));
        foreach (var host in new[] { "ScanBack.Worker", "ScanBack.Deployment" })
        {
            var project = XDocument.Load(Path.Combine(root, "src", host, host + ".csproj"));
            Assert.Contains(project.Descendants("ProjectReference"), reference => reference.Attribute("Include")!.Value.Contains("ScanBack.Infrastructure", StringComparison.Ordinal));
            Assert.DoesNotContain(project.Descendants("ProjectReference"), reference => reference.Attribute("Include")!.Value.Contains("ScanBack.Web", StringComparison.Ordinal));
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (Directory.Exists(Path.Combine(directory.FullName, "src", "ScanBack.Domain"))) return directory.FullName;
        throw new InvalidOperationException("Could not locate ScanBack source projects for architecture verification.");
    }
}
