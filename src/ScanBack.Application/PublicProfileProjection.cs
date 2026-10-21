using ScanBack.Domain;

namespace ScanBack.Application;

public sealed record PublicContact(string Name, string Relationship, string Phone);
public sealed record PublicMedicalField(string Label, string Value);

/// <summary>An allowlisted snapshot with no account ID, private email, birth date, or raw token.</summary>
public sealed record PublicEmergencyProfile(
    string DisplayName,
    string IdentifierLabel,
    int? Age,
    bool CanShowPhoto,
    IReadOnlyList<PublicMedicalField> MedicalFields,
    IReadOnlyList<PublicContact> Contacts);

public static class ConsentPolicy
{
    public const string CurrentPolicyVersion = "2026-10-v1";
    public const PublicFields HealthFields = PublicFields.BloodType | PublicFields.Allergies
        | PublicFields.Conditions | PublicFields.Medications | PublicFields.OtherInformation | PublicFields.Instructions;

    public static bool MayPublishHealth(RiderProfile profile, PublicFields field, string? requiredPolicyVersion = null) =>
        profile.HealthConsent && (field & HealthFields) != PublicFields.None && profile.PublicFields.HasFlag(field)
        && (requiredPolicyVersion is null || profile.ConsentPolicyVersion == requiredPolicyVersion);

    public static PublicFields WithoutHealth(PublicFields fields) => fields & ~HealthFields;
}

/// <summary>Every public emergency and incident page uses this consent-aware projection.</summary>
public static class PublicProfileProjection
{
    public static PublicEmergencyProfile Project(EmergencyIdentifier identifier, DateOnly today, string? requiredPolicyVersion = null)
    {
        ArgumentNullException.ThrowIfNull(identifier);
        var profile = identifier.RiderProfile;
        var fields = profile.PublicFields;
        var medical = new List<PublicMedicalField>();
        Add(PublicFields.BloodType, "Blood type", profile.BloodType);
        Add(PublicFields.Allergies, "Allergies", profile.Allergies);
        Add(PublicFields.Conditions, "Medical conditions", profile.Conditions);
        Add(PublicFields.Medications, "Medications", profile.Medications);
        Add(PublicFields.OtherInformation, "Other medical information", profile.OtherInformation);
        Add(PublicFields.Instructions, "Instructions for responders", profile.Instructions);

        int? age = null;
        if (fields.HasFlag(PublicFields.Age) && profile.DateOfBirth is DateOnly birth && birth <= today)
        {
            var years = today.Year - birth.Year;
            if (today < birth.AddYears(years)) years--;
            if (years is >= 0 and <= 125) age = years;
        }
        var label = string.IsNullOrWhiteSpace(identifier.Label)
            ? "Rider " + identifier.Id.ToString("N")[..8] : identifier.Label;
        var contacts = fields.HasFlag(PublicFields.Contacts)
            ? profile.Contacts.Where(contact => contact.PublicPhone && contact.PublicPhoneConsentedAt is not null
                    && (requiredPolicyVersion is null || contact.ConsentPolicyVersion == requiredPolicyVersion)
                    && contact.IsVerified && !string.IsNullOrWhiteSpace(contact.Phone))
                .Select(contact => new PublicContact(contact.Name, contact.Relationship, contact.Phone)).ToList()
            : [];
        return new PublicEmergencyProfile(
            fields.HasFlag(PublicFields.Name) && !string.IsNullOrWhiteSpace(profile.FullName) ? profile.FullName : label,
            label, age, fields.HasFlag(PublicFields.Photo) && profile.PhotoFileName is not null,
            medical.AsReadOnly(), contacts.AsReadOnly());

        void Add(PublicFields field, string labelText, string? value)
        {
            if (ConsentPolicy.MayPublishHealth(profile, field, requiredPolicyVersion) && !string.IsNullOrWhiteSpace(value))
                medical.Add(new PublicMedicalField(labelText, value));
        }
    }
}
