using System.Data;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ScanBack.Domain;
using ScanBack.Infrastructure;
using ScanBack.Web.Models;

namespace ScanBack.Web.Controllers;

[Authorize(Roles = "Administrator")]
[Route("admin")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminController(AppDbContext db, UserManager<ApplicationUser> users) : Controller
{
    private const int MaximumNotificationAttempts = 5;
    private const int RowLimit = 100;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var settings = await db.PlatformSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
        // These projections never load medical records, notification bodies, or invitation tokens.
        var model = new AdminDashboardModel
        {
            Accounts = await users.Users.CountAsync(cancellationToken),
            ActiveAccounts = await users.Users.CountAsync(x => x.IsActive, cancellationToken),
            ActiveIdentifiers = await db.EmergencyIdentifiers.CountAsync(x => x.IsActive && x.RevokedAt == null, cancellationToken),
            OpenIncidents = await db.Incidents.CountAsync(x => !x.IsClosed && x.ExpiresAt > now, cancellationToken),
            QueuedNotifications = await db.OutboxMessages.CountAsync(x => x.AcceptedAt == null && x.Attempts < MaximumNotificationAttempts, cancellationToken),
            AcceptedNotifications = await db.OutboxMessages.CountAsync(x => x.AcceptedAt != null, cancellationToken),
            ExhaustedNotifications = await db.OutboxMessages.CountAsync(x => x.AcceptedAt == null && x.Attempts >= MaximumNotificationAttempts, cancellationToken),
            NotificationsEnabled = settings?.NotificationsEnabled ?? true
        };
        return View(model);
    }

    [HttpGet("accounts")]
    public async Task<IActionResult> Accounts(string? search, CancellationToken cancellationToken)
    {
        search = NormalizeSearch(search);
        var query = users.Users.AsNoTracking();
        if (search is not null)
            query = query.Where(x => x.Id.Contains(search) || (x.Email != null && x.Email.Contains(search)));

        var accounts = await query.OrderBy(x => x.Email).ThenBy(x => x.Id).Take(RowLimit)
            .Select(x => new AdminAccountRow
            {
                Id = x.Id, Email = x.Email ?? "", IsActive = x.IsActive, EmailConfirmed = x.EmailConfirmed
            }).ToListAsync(cancellationToken);
        var ids = accounts.Select(x => x.Id).ToArray();
        var administratorIds = await (from membership in db.UserRoles.AsNoTracking()
                                      join role in db.Roles.AsNoTracking() on membership.RoleId equals role.Id
                                      where role.NormalizedName == "ADMINISTRATOR" && ids.Contains(membership.UserId)
                                      select membership.UserId).ToListAsync(cancellationToken);
        var currentId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        foreach (var account in accounts)
        {
            account.IsAdministrator = administratorIds.Contains(account.Id);
            account.IsCurrentAccount = account.Id == currentId;
        }
        return View(new AdminAccountsModel { Search = search, Accounts = accounts });
    }

    [HttpPost("accounts/{id}/status")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetAccountStatus(string id, bool isActive, CancellationToken cancellationToken)
    {
        var account = await users.FindByIdAsync(id);
        if (account is null) return NotFound();

        if (!isActive && account.Id == User.FindFirstValue(ClaimTypes.NameIdentifier))
        {
            await AuditAsync("Admin.SelfDeactivationDenied", account.Id, cancellationToken);
            TempData["AdminError"] = "You cannot deactivate your own administrator account.";
            return RedirectToAction(nameof(Accounts));
        }

        // Keep the last-administrator check and update in one serializable transaction so
        // simultaneous administrative changes cannot deactivate every administrator.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        if (!isActive && account.IsActive && await users.IsInRoleAsync(account, "Administrator"))
        {
            var activeAdministratorCount = await (from membership in db.UserRoles
                                                  join role in db.Roles on membership.RoleId equals role.Id
                                                  join user in users.Users on membership.UserId equals user.Id
                                                  where role.NormalizedName == "ADMINISTRATOR" && user.IsActive
                                                  select user.Id).Distinct().CountAsync(cancellationToken);
            if (activeAdministratorCount <= 1)
            {
                await AuditAsync("Admin.LastAdministratorDeactivationDenied", account.Id, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                TempData["AdminError"] = "The last active administrator cannot be deactivated.";
                return RedirectToAction(nameof(Accounts));
            }
        }

        if (account.IsActive != isActive)
        {
            account.IsActive = isActive;
            // Rotation invalidates existing sessions at the configured security-stamp validation interval.
            // The application's cookie validator also checks the active-account flag on each request.
            var updateResult = await users.UpdateSecurityStampAsync(account);
            if (!updateResult.Succeeded)
            {
                await transaction.RollbackAsync(cancellationToken);
                TempData["AdminError"] = "The account could not be updated. Try again.";
                return RedirectToAction(nameof(Accounts));
            }
            await AuditAsync(isActive ? "Admin.AccountActivated" : "Admin.AccountDeactivated", account.Id, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        TempData["AdminSuccess"] = isActive ? "Account activated." : "Account deactivated.";
        return RedirectToAction(nameof(Accounts));
    }

    [HttpGet("identifiers")]
    public async Task<IActionResult> Identifiers(string? search, CancellationToken cancellationToken)
    {
        search = NormalizeSearch(search);
        var query = db.EmergencyIdentifiers.AsNoTracking();
        if (search is not null)
            query = query.Where(x => x.Label.Contains(search) || x.RiderProfile.UserId.Contains(search));
        var rows = await query.OrderByDescending(x => x.CreatedAt).Take(RowLimit)
            .Select(x => new AdminIdentifierRow
            {
                Id = x.Id, Label = x.Label, AccountId = x.RiderProfile.UserId, IsActive = x.IsActive,
                CreatedAt = x.CreatedAt, RevokedAt = x.RevokedAt
            }).ToListAsync(cancellationToken);
        return View(new AdminIdentifiersModel { Search = search, Identifiers = rows });
    }

    [HttpPost("identifiers/{id:guid}/revoke")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeIdentifier(Guid id, CancellationToken cancellationToken)
    {
        var identifier = await db.EmergencyIdentifiers.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (identifier is null) return NotFound();
        if (identifier.RevokedAt is null)
        {
            identifier.IsActive = false;
            identifier.RevokedAt = DateTimeOffset.UtcNow;
            await AuditAsync("Admin.IdentifierRevoked", id.ToString(), cancellationToken);
        }
        TempData["AdminSuccess"] = "Identifier permanently revoked. The rider can create a replacement.";
        return RedirectToAction(nameof(Identifiers));
    }

    [HttpGet("incidents")]
    public async Task<IActionResult> Incidents(CancellationToken cancellationToken)
    {
        var rows = await db.Incidents.AsNoTracking().OrderByDescending(x => x.ReportedAt).Take(RowLimit)
            .Select(x => new AdminIncidentRow
            {
                Id = x.Id, RiderProfileId = x.RiderProfileId, ReportedAt = x.ReportedAt, ExpiresAt = x.ExpiresAt,
                IsClosed = x.IsClosed, HasLocation = x.Latitude != null && x.Longitude != null,
                QueuedNotifications = db.OutboxMessages.Count(m => m.IncidentId == x.Id && m.AcceptedAt == null && m.Attempts < MaximumNotificationAttempts),
                AcceptedNotifications = db.OutboxMessages.Count(m => m.IncidentId == x.Id && m.AcceptedAt != null),
                ExhaustedNotifications = db.OutboxMessages.Count(m => m.IncidentId == x.Id && m.AcceptedAt == null && m.Attempts >= MaximumNotificationAttempts)
            }).ToListAsync(cancellationToken);
        return View(new AdminIncidentsModel { Incidents = rows });
    }

    [HttpGet("settings")]
    public async Task<IActionResult> Settings(CancellationToken cancellationToken)
    {
        var settings = await db.PlatformSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
        return View(new AdminSettingsModel
        {
            NotificationsEnabled = settings?.NotificationsEnabled ?? true,
            IncidentRetentionDays = settings?.IncidentRetentionDays ?? 30,
            IncidentAccessHours = settings?.IncidentAccessHours ?? 24,
            NotificationCooldownMinutes = settings?.NotificationCooldownMinutes ?? 10
        });
    }

    [HttpPost("settings")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Settings(AdminSettingsModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return View(model);
        var settings = await db.PlatformSettings.SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
        if (settings is null)
        {
            settings = new PlatformSettings();
            db.PlatformSettings.Add(settings);
        }
        settings.NotificationsEnabled = model.NotificationsEnabled;
        settings.IncidentRetentionDays = model.IncidentRetentionDays;
        settings.IncidentAccessHours = model.IncidentAccessHours;
        settings.NotificationCooldownMinutes = model.NotificationCooldownMinutes;
        await AuditAsync("Admin.PlatformSettingsUpdated", "1", cancellationToken);
        TempData["AdminSuccess"] = "Platform settings saved.";
        return RedirectToAction(nameof(Settings));
    }

    [HttpGet("audit")]
    public async Task<IActionResult> Audit(CancellationToken cancellationToken)
    {
        var rows = await db.AuditEvents.AsNoTracking().OrderByDescending(x => x.CreatedAt).Take(RowLimit)
            .Select(x => new AdminAuditRow
            {
                CreatedAt = x.CreatedAt, ActorId = x.ActorId, Action = x.Action, EntityId = x.EntityId
            }).ToListAsync(cancellationToken);
        return View(new AdminAuditModel { Events = rows });
    }

    private async Task AuditAsync(string action, string entityId, CancellationToken cancellationToken)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            ActorId = User.FindFirstValue(ClaimTypes.NameIdentifier), Action = action, EntityId = entityId
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string? NormalizeSearch(string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return null;
        var trimmed = search.Trim();
        return trimmed.Length <= 254 ? trimmed : trimmed[..254];
    }
}
