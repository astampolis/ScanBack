using Microsoft.AspNetCore.Identity;

namespace ScanBack.Infrastructure;

public sealed class ApplicationUser : IdentityUser
{
    public bool IsActive { get; set; } = true;
}
