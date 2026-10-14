namespace ScanBack.Domain;

[Flags]
public enum PublicFields
{
    None = 0, Name = 1, Photo = 2, Age = 4, BloodType = 8, Allergies = 16,
    Conditions = 32, Medications = 64, OtherInformation = 128, Instructions = 256, Contacts = 512
}

public sealed class RiderProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "";
    public string FullName { get; set; } = "";
    public DateOnly? DateOfBirth { get; set; }
    public string? PhotoFileName { get; set; }
    public string? BloodType { get; set; }
    public string? Allergies { get; set; }
    public string? Conditions { get; set; }
    public string? Medications { get; set; }
    public string? OtherInformation { get; set; }
    public string? Instructions { get; set; }
    public PublicFields PublicFields { get; set; } = PublicFields.None;
    public bool HealthConsent { get; set; }
    public DateTimeOffset? ConsentAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<EmergencyContact> Contacts { get; set; } = [];
    public List<EmergencyIdentifier> Identifiers { get; set; } = [];
}

public sealed class EmergencyContact
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RiderProfileId { get; set; }
    public RiderProfile RiderProfile { get; set; } = null!;
    public string Name { get; set; } = "";
    public string Relationship { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public bool PublicPhone { get; set; }
    public bool NotifyEnabled { get; set; } = true;
    public bool IsVerified { get; set; }
    public string VerificationTokenHash { get; set; } = "";
    public DateTimeOffset VerificationExpiresAt { get; set; }
}

public sealed class EmergencyIdentifier
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RiderProfileId { get; set; }
    public RiderProfile RiderProfile { get; set; } = null!;
    public string Label { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string ProtectedToken { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }
}

public sealed class Incident
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RiderProfileId { get; set; }
    public RiderProfile RiderProfile { get; set; } = null!;
    public Guid EmergencyIdentifierId { get; set; }
    public EmergencyIdentifier EmergencyIdentifier { get; set; } = null!;
    public DateTimeOffset ReportedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? LocationDescription { get; set; }
    public string Consciousness { get; set; } = "Unknown";
    public int PeopleInvolved { get; set; } = 1;
    public string SubmissionKey { get; set; } = "";
    public bool IsClosed { get; set; }
    public List<IncidentAccess> AccessGrants { get; set; } = [];
}

public sealed class IncidentAccess
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IncidentId { get; set; }
    public Incident Incident { get; set; } = null!;
    public Guid? EmergencyContactId { get; set; }
    public string Role { get; set; } = "Reporter";
    public string TokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public bool IsRevoked { get; set; }
}

public sealed class OutboxMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? IncidentId { get; set; }
    public Guid? RiderProfileId { get; set; }
    public Guid? EmergencyContactId { get; set; }
    public string ToEmail { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public string Kind { get; set; } = "Incident";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? AcceptedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

public sealed class ConsentRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = "";
    public string PolicyVersion { get; set; } = "2026-10-v1";
    public string Action { get; set; } = "Granted";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AuditEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string? ActorId { get; set; }
    public string Action { get; set; } = "";
    public string? EntityId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class PlatformSettings
{
    public int Id { get; set; } = 1;
    public int IncidentRetentionDays { get; set; } = 30;
    public int IncidentAccessHours { get; set; } = 24;
    public int NotificationCooldownMinutes { get; set; } = 10;
    public bool NotificationsEnabled { get; set; } = true;
}
