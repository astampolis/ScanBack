using System.ComponentModel.DataAnnotations;
using ScanBack.Domain;

namespace ScanBack.Web.Models;

public sealed class RegisterModel
{
    [Required, EmailAddress, StringLength(254)] public string Email { get; set; } = "";
    [Required, StringLength(128, MinimumLength = 12), DataType(DataType.Password)] public string Password { get; set; } = "";
    [Required, Compare(nameof(Password)), DataType(DataType.Password)] public string ConfirmPassword { get; set; } = "";
    [Required, StringLength(180)] public string FullName { get; set; } = "";
    public bool PrivacyAccepted { get; set; }
}
public sealed class LoginModel
{
    [Required, EmailAddress, StringLength(254)] public string Email { get; set; } = "";
    [Required, StringLength(128), DataType(DataType.Password)] public string Password { get; set; } = "";
    public bool RememberMe { get; set; }
    public string? ReturnUrl { get; set; }
}
public sealed class AccountEmailModel
{
    [Required, EmailAddress, StringLength(254)] public string Email { get; set; } = "";
}
public sealed class ResetPasswordModel
{
    [Required, EmailAddress, StringLength(254)] public string Email { get; set; } = "";
    [Required, StringLength(4096)] public string Code { get; set; } = "";
    [Required, StringLength(128, MinimumLength = 12), DataType(DataType.Password)] public string Password { get; set; } = "";
    [Required, Compare(nameof(Password)), DataType(DataType.Password)] public string ConfirmPassword { get; set; } = "";
}
public sealed class ProfileEditModel
{
    [Required, StringLength(180)] public string FullName { get; set; } = "";
    [DataType(DataType.Date)] public DateOnly? DateOfBirth { get; set; }
    [StringLength(8)] public string? BloodType { get; set; }
    [StringLength(2000)] public string? Allergies { get; set; }
    [StringLength(2000)] public string? Conditions { get; set; }
    [StringLength(2000)] public string? Medications { get; set; }
    [StringLength(2000)] public string? OtherInformation { get; set; }
    [StringLength(2000)] public string? Instructions { get; set; }
    public bool HealthConsent { get; set; }
    public List<PublicFields> VisibleFields { get; set; } = [];
    public IFormFile? Photo { get; set; }
    public bool RemovePhoto { get; set; }
    public bool HasPhoto { get; set; }
}
public sealed class ProfileDashboardModel
{
    public RiderProfile Profile { get; set; } = null!;
    public int ContactCount { get; set; }
    public int VerifiedContactCount { get; set; }
    public int IncidentCount { get; set; }
    public IReadOnlyList<EmergencyIdentifier> Identifiers { get; set; } = [];
    public string? PreviewUrl { get; set; }
}
public sealed class ContactEditModel
{
    public Guid? Id { get; set; }
    [Required, StringLength(160)] public string Name { get; set; } = "";
    [Required, StringLength(80)] public string Relationship { get; set; } = "";
    [Required, EmailAddress, StringLength(254)] public string Email { get; set; } = "";
    [Phone, StringLength(40)] public string? Phone { get; set; }
    public bool PublicPhone { get; set; }
    public bool NotifyEnabled { get; set; } = true;
}
public sealed class ContactTokenModel
{
    public string Token { get; set; } = "";
    public string ContactName { get; set; } = "";
    public string Relationship { get; set; } = "";
    public string RiderName { get; set; } = "";
    public bool Valid { get; set; }
    public bool NotificationsRequested { get; set; }
    public bool PublicPhoneRequested { get; set; }
    public string Phone { get; set; } = "";
    public bool AcceptNotifications { get; set; }
    public bool AcceptPublicPhone { get; set; }
    public string PolicyVersion { get; set; } = "";
}
public sealed class DeleteAccountModel
{
    [Required, DataType(DataType.Password), StringLength(128)] public string Password { get; set; } = "";
    public bool ConfirmDeletion { get; set; }
}

public sealed class TwoFactorModel
{
    [Required, StringLength(100)] public string Code { get; set; } = "";
    public bool UseRecoveryCode { get; set; }
    public bool RememberMe { get; set; }
    public string? ReturnUrl { get; set; }
}
