using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using ScanBack.Domain;
using ScanBack.Infrastructure;

namespace ScanBack.Web.Services;

/// <summary>One access policy for the incident page and every chat read/write.</summary>
public sealed class IncidentSession(AppDbContext db, TokenService tokens)
{
    private IQueryable<IncidentAccess> AccessQuery() => db.IncidentAccesses.AsNoTracking()
        .Include(a => a.Incident).ThenInclude(i => i.EmergencyIdentifier).ThenInclude(i => i.RiderProfile).ThenInclude(p => p.Contacts);

    public async Task<IncidentAccess?> GetAsync(HttpContext context, Guid incidentId)
    {
        if (!context.Request.Cookies.TryGetValue("ScanBack.Incident." + incidentId.ToString("N"), out var protectedValue)) return null;
        string value;
        try { value = tokens.Unprotect(protectedValue); }
        catch (Exception exception) when (exception is CryptographicException or FormatException or ArgumentException) { return null; }
        var parts = value.Split('|');
        if (parts.Length != 3 || parts[0] != "incident" || !Guid.TryParse(parts[1], out var grantId) || !ValidToken(parts[2])) return null;
        var hash = TokenService.Hash(parts[2]);
        var access = await AccessQuery().FirstOrDefaultAsync(a => a.Id == grantId && a.IncidentId == incidentId
            && (a.SessionTokenHash == hash || (a.Role == "Reporter" && a.SessionTokenHash == null && a.TokenHash == hash)));
        return await IsValidAsync(access) ? access : null;
    }

    public async Task<bool> IsValidAsync(IncidentAccess? access)
    {
        if (access is null || access.IsRevoked || access.ExpiresAt <= DateTimeOffset.UtcNow) return false;
        var incident = access.Incident;
        if (incident.RiderProfileId != incident.EmergencyIdentifier.RiderProfileId) return false;
        if (incident.IsClosed || incident.ExpiresAt <= DateTimeOffset.UtcNow || !incident.EmergencyIdentifier.IsActive || incident.EmergencyIdentifier.IsAdminBlocked || incident.EmergencyIdentifier.RevokedAt is not null) return false;
        if (!await db.Users.AnyAsync(u => u.Id == incident.EmergencyIdentifier.RiderProfile.UserId && u.IsActive)) return false;
        return access.Role == "Reporter" || (access.Role == "Contact" && access.EmergencyContactId is Guid contactId
            && await db.EmergencyContacts.AnyAsync(c => c.Id == contactId && c.RiderProfileId == incident.RiderProfileId && c.IsVerified && c.NotifyEnabled));
    }

    private static bool ValidToken(string token) => token.Length is >= 40 and <= 64 && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
