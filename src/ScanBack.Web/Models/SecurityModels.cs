using System.ComponentModel.DataAnnotations;

namespace ScanBack.Web.Models;

public sealed class AuthenticatorSetupModel
{
    public string SharedKey { get; set; } = "";
    public string AuthenticatorUri { get; set; } = "";
    public bool Enabled { get; set; }
    [Required, RegularExpression(@"^[0-9\s-]{6,12}$", ErrorMessage = "Enter the six-digit code from your authenticator.")]
    public string Code { get; set; } = "";
}
