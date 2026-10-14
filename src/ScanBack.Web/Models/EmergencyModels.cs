using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

namespace ScanBack.Web.Models;

public sealed record PublicContactView(string Name, string Relationship, string Phone);
public sealed record PublicMedicalField(string Label, string Value);

public sealed class PublicEmergencyProfile
{
    public string DisplayName { get; init; } = "";
    public string IdentifierLabel { get; init; } = "";
    public int? Age { get; init; }
    public string? PhotoUrl { get; init; }
    public List<PublicMedicalField> MedicalFields { get; init; } = [];
    public List<PublicContactView> Contacts { get; init; } = [];
}

public sealed class AccidentReportInput : IValidatableObject
{
    [Required, StringLength(36, MinimumLength = 36)]
    public string SubmissionKey { get; set; } = Guid.NewGuid().ToString();
    [ModelBinder(BinderType = typeof(InvariantCoordinateBinder))]
    public double? Latitude { get; set; }
    [ModelBinder(BinderType = typeof(InvariantCoordinateBinder))]
    public double? Longitude { get; set; }
    [StringLength(300)]
    public string? LocationDescription { get; set; }
    [Required, RegularExpression("^(Unknown|Conscious|Unconscious)$")]
    public string Consciousness { get; set; } = "Unknown";
    [Range(1, 20)]
    public int PeopleInvolved { get; set; } = 1;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!Guid.TryParseExact(SubmissionKey, "D", out _))
            yield return new ValidationResult("The reporting form has expired. Reload this page.", [nameof(SubmissionKey)]);
        if (Latitude.HasValue != Longitude.HasValue)
            yield return new ValidationResult("Both latitude and longitude are required when sharing a location.", [nameof(Latitude), nameof(Longitude)]);
        if (Latitude is double latitude && (!double.IsFinite(latitude) || latitude < -90 || latitude > 90))
            yield return new ValidationResult("The latitude is invalid.", [nameof(Latitude)]);
        if (Longitude is double longitude && (!double.IsFinite(longitude) || longitude < -180 || longitude > 180))
            yield return new ValidationResult("The longitude is invalid.", [nameof(Longitude)]);
    }
}

public sealed class EmergencyPageViewModel
{
    public string Token { get; init; } = "";
    public PublicEmergencyProfile Profile { get; init; } = new();
    public AccidentReportInput Report { get; init; } = new();
    public string? ReportError { get; init; }
}

public sealed class IncidentPageViewModel
{
    public Guid Id { get; init; }
    public PublicEmergencyProfile Profile { get; init; } = new();
    public DateTimeOffset ReportedAt { get; init; }
    public DateTimeOffset ExpiresAt { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    public string? LocationDescription { get; init; }
    public string Consciousness { get; init; } = "Unknown";
    public int PeopleInvolved { get; init; }
    public string Role { get; init; } = "Reporter";
    public int QueuedNotifications { get; init; }
    public int AcceptedNotifications { get; init; }
    public int FailedNotifications { get; init; }
    public string CommunicationScript { get; init; } = "";
    public string? MapsUrl { get; init; }
}

public sealed class IncidentJoinViewModel
{
    public string Token { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public DateTimeOffset ExpiresAt { get; init; }
}

public sealed class EmergencyUnavailableViewModel
{
    public string Message { get; init; } = "This emergency link is unavailable or has expired.";
}

public sealed class StickerCreateInput
{
    [Required, StringLength(60)]
    public string Label { get; set; } = "Helmet sticker";
}

public sealed class StickerViewModel
{
    public Guid Id { get; init; }
    public string Label { get; init; } = "";
    public bool IsActive { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string? EmergencyUrl { get; init; }
}

public sealed class StickersPageViewModel
{
    public List<StickerViewModel> Stickers { get; init; } = [];
    public StickerCreateInput NewSticker { get; init; } = new();
    public string? Error { get; init; }
}
