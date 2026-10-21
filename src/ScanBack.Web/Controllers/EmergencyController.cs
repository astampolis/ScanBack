using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ScanBack.Domain;
using ScanBack.Infrastructure;
using ScanBack.Web.Models;
using ScanBack.Web.Services;

namespace ScanBack.Web.Controllers;

[AllowAnonymous]
public sealed class EmergencyController(AppDbContext db, TokenService tokens, SiteLinks links, UiText text) : Controller
{
    [HttpGet("/e/{token}")]
    public async Task<IActionResult> Index(string token)
    {
        var identifier = await FindIdentifier(token);
        if (identifier is null) return Unavailable();
        return View(new EmergencyPageViewModel { Token = token, Profile = ProjectPublic(identifier, token) });
    }

    [HttpPost("/e/{token}/report")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("reports")]
    public async Task<IActionResult> Report(string token, [Bind(Prefix = "Report")] AccidentReportInput report)
    {
        var identifier = await FindIdentifier(token);
        if (identifier is null) return Unavailable();
        if (!ModelState.IsValid)
            return View("Index", new EmergencyPageViewModel { Token = token, Profile = ProjectPublic(identifier, token), Report = report });

        // Serialize reporting decisions per sticker with their durable notification writes.
        // The update lock avoids the shared-lock upgrade deadlock of two simultaneous reports.
        await using var transaction = await db.Database.BeginTransactionAsync(db.Database.IsSqlServer() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable);
        if (db.Database.IsSqlServer())
        {
            await db.EmergencyIdentifiers
                .FromSqlInterpolated($"SELECT * FROM [EmergencyIdentifiers] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {identifier.Id}")
                .AsNoTracking().Select(i => i.Id).SingleOrDefaultAsync();
        }
        identifier = await FindIdentifier(token);
        if (identifier is null) return Unavailable();
        var previous = await db.Incidents.AsNoTracking().FirstOrDefaultAsync(i => i.SubmissionKey == report.SubmissionKey);
        if (previous is not null)
        {
            if (previous.EmergencyIdentifierId == identifier.Id && await GetAccess(previous.Id) is not null)
                return Redirect($"/incident/{previous.Id}");
            return ReportError(identifier, token, report, "This report has already been submitted. Use your incident link, or reload this page.");
        }
        var settings = await db.PlatformSettings.AsNoTracking().FirstOrDefaultAsync() ?? new PlatformSettings();
        var now = DateTimeOffset.UtcNow;
        var cooldown = now.AddMinutes(-Math.Clamp(settings.NotificationCooldownMinutes, 1, 1440));
        if (await db.Incidents.AnyAsync(i => i.EmergencyIdentifierId == identifier.Id && i.ReportedAt > cooldown))
            return ReportError(identifier, token, report, "A possible accident was recently reported for this sticker. New contact notifications are temporarily paused. Call emergency services if help is needed.");

        var incident = new Incident
        {
            RiderProfileId = identifier.RiderProfileId,
            EmergencyIdentifierId = identifier.Id,
            ExpiresAt = now.AddHours(Math.Clamp(settings.IncidentAccessHours, 1, 168)),
            Latitude = report.Latitude,
            Longitude = report.Longitude,
            LocationDescription = report.LocationDescription?.Trim(),
            Consciousness = report.Consciousness,
            PeopleInvolved = report.PeopleInvolved,
            SubmissionKey = report.SubmissionKey,
            ReportedAt = now
        };
        var reporterToken = TokenService.Generate();
        var reporterAccess = new IncidentAccess
        {
            IncidentId = incident.Id,
            TokenHash = TokenService.Hash(reporterToken),
            SessionTokenHash = TokenService.Hash(reporterToken),
            InvitationConsumedAt = now,
            ExpiresAt = incident.ExpiresAt,
            Role = "Reporter"
        };
        db.Incidents.Add(incident);
        db.IncidentAccesses.Add(reporterAccess);
        if (settings.NotificationsEnabled)
        {
            foreach (var contact in identifier.RiderProfile.Contacts.Where(c => c.IsVerified && c.NotifyEnabled))
            {
                var contactToken = TokenService.Generate();
                db.IncidentAccesses.Add(new IncidentAccess
                {
                    IncidentId = incident.Id,
                    EmergencyContactId = contact.Id,
                    TokenHash = TokenService.Hash(contactToken),
                    ExpiresAt = incident.ExpiresAt,
                    Role = "Contact"
                });
                var invitation = links.Absolute($"/incident/join/{contactToken}");
                var unsubscribe = links.Absolute($"/contacts/unsubscribe/{Uri.EscapeDataString(tokens.Protect($"unsubscribe:{contact.Id}:{contact.PermissionVersion}"))}");
                var greekTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Athens");
                var reportTime = TimeZoneInfo.ConvertTime(now, greekTimeZone);
                var expiryTime = TimeZoneInfo.ConvertTime(incident.ExpiresAt, greekTimeZone);
                db.OutboxMessages.Add(new OutboxMessage
                {
                    IncidentId = incident.Id,
                    RiderProfileId = identifier.RiderProfileId,
                    EmergencyContactId = contact.Id,
                    ToEmail = contact.Email,
                    Subject = "ScanBack: possible accident reported / Αναφορά πιθανού ατυχήματος",
                    Body = $"Someone accessed a ScanBack emergency sticker and reported a possible accident at {reportTime:yyyy-MM-dd HH:mm} (Europe/Athens).\n\nThis report is unverified. ScanBack has not contacted emergency services. If assistance is needed, telephone 112 or 166 in Greece.\n\nView the rider and reported location through your single-use private invitation:\n{invitation}\n\nThis invitation expires at {expiryTime:yyyy-MM-dd HH:mm} (Europe/Athens). Do not forward it.\n\nΚάποιος ανέφερε πιθανό ατύχημα. Η αναφορά δεν έχει επιβεβαιωθεί. Το ScanBack δεν έχει ειδοποιήσει τις υπηρεσίες έκτακτης ανάγκης. Για βοήθεια στην Ελλάδα καλέστε 112 ή 166.\n\nStop receiving notifications / Διακοπή ειδοποιήσεων:\n{unsubscribe}"
                });
            }
        }
        db.AuditEvents.Add(new AuditEvent { Action = "Incident.Reported", EntityId = incident.Id.ToString() });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        SetAccessCookie(reporterAccess, reporterToken);
        return Redirect($"/incident/{incident.Id}");
    }

    [HttpGet("/incident/join/{token}")]
    public async Task<IActionResult> Join(string token)
    {
        var access = await FindInvitation(token);
        if (access is null) return Unavailable();
        return View(new IncidentJoinViewModel
        {
            Token = token,
            DisplayName = ProjectPublic(access.Incident.EmergencyIdentifier, "").DisplayName,
            ExpiresAt = access.ExpiresAt
        });
    }

    [HttpPost("/incident/join/{token}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Redeem(string token)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(db.Database.IsSqlServer() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable);
        var access = await FindInvitation(token);
        if (access is null) return Unavailable();
        var sessionToken = TokenService.Generate();
        var now = DateTimeOffset.UtcNow;
        // A successful redemption replaces the emailed capability with a different cookie
        // secret. An atomic conditional update makes replay and simultaneous redemption fail.
        var updated = await db.IncidentAccesses.Where(a => a.Id == access.Id && !a.IsRevoked && a.InvitationConsumedAt == null && a.ExpiresAt > now)
            .ExecuteUpdateAsync(set => set.SetProperty(a => a.InvitationConsumedAt, now).SetProperty(a => a.SessionTokenHash, TokenService.Hash(sessionToken)));
        if (updated != 1) return Unavailable();
        db.AuditEvents.Add(new AuditEvent { Action = "Incident.InvitationRedeemed", EntityId = access.IncidentId.ToString() });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        SetAccessCookie(access, sessionToken);
        return Redirect($"/incident/{access.IncidentId}");
    }

    [HttpGet("/incident/{id:guid}")]
    public async Task<IActionResult> Incident(Guid id)
    {
        var access = await GetAccess(id);
        if (access is null) return Unavailable();
        var incident = access.Incident;
        var profile = ProjectPublic(incident.EmergencyIdentifier, "");
        var notices = await db.OutboxMessages.AsNoTracking().Where(m => m.IncidentId == id).Select(m => new { m.AcceptedAt, m.Attempts }).ToListAsync();
        db.AuditEvents.Add(new AuditEvent { Action = "Incident.Viewed." + access.Role, EntityId = id.ToString() });
        await db.SaveChangesAsync();
        var maps = incident.Latitude is double lat && incident.Longitude is double lng
            ? "https://www.google.com/maps/search/?api=1&query=" + Uri.EscapeDataString(FormattableString.Invariant($"{lat:F6},{lng:F6}"))
            : null;
        return View(new IncidentPageViewModel
        {
            Id = id,
            Profile = profile,
            ReportedAt = incident.ReportedAt,
            ExpiresAt = incident.ExpiresAt,
            Latitude = incident.Latitude,
            Longitude = incident.Longitude,
            LocationDescription = incident.LocationDescription,
            Consciousness = incident.Consciousness,
            PeopleInvolved = incident.PeopleInvolved,
            Role = access.Role,
            AcceptedNotifications = notices.Count(m => m.AcceptedAt is not null),
            FailedNotifications = notices.Count(m => m.AcceptedAt is null && m.Attempts >= 5),
            QueuedNotifications = notices.Count(m => m.AcceptedAt is null && m.Attempts < 5),
            MapsUrl = maps,
            CommunicationScript = DispatcherScript(incident, profile)
        });
    }

    private async Task<EmergencyIdentifier?> FindIdentifier(string token)
    {
        if (!ValidToken(token)) return null;
        var hash = TokenService.Hash(token);
        var identifier = await db.EmergencyIdentifiers.AsNoTracking()
            .Include(i => i.RiderProfile).ThenInclude(p => p.Contacts)
            .FirstOrDefaultAsync(i => i.TokenHash == hash && i.IsActive && !i.IsAdminBlocked && i.RevokedAt == null);
        if (identifier is null || !await db.Users.AnyAsync(u => u.Id == identifier.RiderProfile.UserId && u.IsActive)) return null;
        return identifier;
    }

    private async Task<IncidentAccess?> FindInvitation(string token)
    {
        if (!ValidToken(token)) return null;
        var hash = TokenService.Hash(token);
        var access = await AccessQuery().FirstOrDefaultAsync(a => a.TokenHash == hash && a.Role == "Contact" && a.InvitationConsumedAt == null);
        return await AccessIsValid(access) ? access : null;
    }

    private IQueryable<IncidentAccess> AccessQuery() => db.IncidentAccesses.AsNoTracking()
        .Include(a => a.Incident).ThenInclude(i => i.EmergencyIdentifier).ThenInclude(i => i.RiderProfile).ThenInclude(p => p.Contacts);

    private async Task<IncidentAccess?> GetAccess(Guid incidentId)
    {
        if (!Request.Cookies.TryGetValue(CookieName(incidentId), out var protectedValue)) return null;
        string value;
        try { value = tokens.Unprotect(protectedValue); }
        catch (Exception exception) when (exception is CryptographicException or FormatException or ArgumentException) { return null; }
        var parts = value.Split('|');
        if (parts.Length != 3 || parts[0] != "incident" || !Guid.TryParse(parts[1], out var grantId) || !ValidToken(parts[2])) return null;
        var hash = TokenService.Hash(parts[2]);
        var access = await AccessQuery().FirstOrDefaultAsync(a => a.Id == grantId && a.IncidentId == incidentId
            && (a.SessionTokenHash == hash || (a.Role == "Reporter" && a.SessionTokenHash == null && a.TokenHash == hash)));
        return await AccessIsValid(access) ? access : null;
    }

    private async Task<bool> AccessIsValid(IncidentAccess? access)
    {
        if (access is null || access.IsRevoked || access.ExpiresAt <= DateTimeOffset.UtcNow) return false;
        var incident = access.Incident;
        if (incident.RiderProfileId != incident.EmergencyIdentifier.RiderProfileId) return false;
        if (incident.IsClosed || incident.ExpiresAt <= DateTimeOffset.UtcNow || !incident.EmergencyIdentifier.IsActive || incident.EmergencyIdentifier.IsAdminBlocked || incident.EmergencyIdentifier.RevokedAt is not null) return false;
        if (!await db.Users.AnyAsync(u => u.Id == incident.EmergencyIdentifier.RiderProfile.UserId && u.IsActive)) return false;
        return access.Role == "Reporter" || (access.Role == "Contact" && access.EmergencyContactId is Guid contactId
            && await db.EmergencyContacts.AnyAsync(c => c.Id == contactId && c.RiderProfileId == incident.RiderProfileId && c.IsVerified && c.NotifyEnabled));
    }

    private void SetAccessCookie(IncidentAccess access, string token) => Response.Cookies.Append(CookieName(access.IncidentId), tokens.Protect($"incident|{access.Id}|{token}"), new CookieOptions
    {
        HttpOnly = true,
        Secure = Request.IsHttps,
        SameSite = SameSiteMode.Lax,
        IsEssential = true,
        Expires = access.ExpiresAt,
        // The reporter must also be recognized on an idempotent /e/.../report retry.
        // Access remains incident-scoped by the protected cookie name, grant, and database checks.
        Path = "/"
    });

    private static string CookieName(Guid id) => "ScanBack.Incident." + id.ToString("N");
    private static bool ValidToken(string token) => token.Length is >= 40 and <= 64 && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private PublicEmergencyProfile ProjectPublic(EmergencyIdentifier identifier, string token)
    {
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Europe/Athens")).DateTime);
        var projected = ScanBack.Application.PublicProfileProjection.Project(identifier, today);
        return new PublicEmergencyProfile
        {
            DisplayName = projected.DisplayName,
            IdentifierLabel = projected.IdentifierLabel,
            Age = projected.Age,
            PhotoUrl = projected.CanShowPhoto && token.Length > 0 ? "/profile/photo/" + Uri.EscapeDataString(token) : null,
            MedicalFields = projected.MedicalFields.Select(f => new PublicMedicalField(f.Label, f.Value)).ToList(),
            Contacts = projected.Contacts.Select(c => new PublicContactView(c.Name, c.Relationship, c.Phone)).ToList()
        };
    }

    private string DispatcherScript(Incident incident, PublicEmergencyProfile profile)
    {
        var location = incident.Latitude is double latitude && incident.Longitude is double longitude
            ? FormattableString.Invariant($"{latitude:F6}, {longitude:F6}")
            : text["The exact location is not available; describe landmarks to the dispatcher."];
        if (!string.IsNullOrWhiteSpace(incident.LocationDescription)) location = location.TrimEnd('.') + ". " + incident.LocationDescription;
        var awareness = incident.Consciousness switch
        {
            "Conscious" => text["The bystander reports that the rider appears conscious."],
            "Unconscious" => text["The bystander reports that the rider appears unconscious."],
            _ => text["The rider's consciousness has not been established."]
        };
        var medical = profile.MedicalFields.Count > 0
            ? " " + text["Rider-supplied emergency information:"] + " " + string.Join("; ", profile.MedicalFields.Select(f => text[f.Label] + ": " + f.Value)) + "."
            : "";
        return text["Hello, I am reporting a possible motorcycle accident."] + " " + text["Reported location:"] + " " + location.TrimEnd('.') + ". "
            + text["Reported number of people involved:"] + " " + incident.PeopleInvolved.ToString(CultureInfo.InvariantCulture) + ". " + awareness + medical + " "
            + text["Please send emergency assistance and tell me what to do next."];
    }

    private IActionResult ReportError(EmergencyIdentifier identifier, string token, AccidentReportInput report, string message)
    {
        Response.StatusCode = StatusCodes.Status409Conflict;
        return View("Index", new EmergencyPageViewModel { Token = token, Profile = ProjectPublic(identifier, token), Report = report, ReportError = message });
    }

    private IActionResult Unavailable()
    {
        Response.StatusCode = StatusCodes.Status404NotFound;
        return View("Unavailable", new EmergencyUnavailableViewModel());
    }
}
