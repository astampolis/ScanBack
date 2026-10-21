using System.Security.Claims;
using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ScanBack.Domain;
using ScanBack.Infrastructure;
using ScanBack.Infrastructure.Hosting;
using ScanBack.Web.Models;
using ScanBack.Web.Services;

namespace ScanBack.Web.Controllers;

[Authorize, Route("contacts"), EnableRateLimiting("authentication")]
public sealed class ContactsController(AppDbContext db, TokenService tokens, SiteLinks links, IOptions<PrivacyOptions> privacy) : Controller
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    private Task<RiderProfile?> OwnProfile() => db.RiderProfiles.FirstOrDefaultAsync(x => x.UserId == UserId);
    private Task<EmergencyContact?> OwnContact(Guid id) => db.EmergencyContacts.Include(x => x.RiderProfile).FirstOrDefaultAsync(x => x.Id == id && x.RiderProfile.UserId == UserId);

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var profile = await OwnProfile();
        if (profile is null) return RedirectToAction("Edit", "Profile");
        return View(await db.EmergencyContacts.AsNoTracking().Where(x => x.RiderProfileId == profile.Id).OrderBy(x => x.Name).ToListAsync());
    }
    [HttpGet("create")]
    public IActionResult Create() => View("Edit", new ContactEditModel());

    [HttpPost("create"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(ContactEditModel model)
    {
        var profile = await OwnProfile();
        if (profile is null) return RedirectToAction("Edit", "Profile");
        await using var transaction = await db.Database.BeginTransactionAsync(db.Database.IsSqlServer() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable);
        if (db.Database.IsSqlServer())
            await db.RiderProfiles.FromSqlInterpolated($"SELECT * FROM [RiderProfiles] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {profile.Id}").AsNoTracking().Select(p => p.Id).SingleAsync();
        if (await db.EmergencyContacts.CountAsync(x => x.RiderProfileId == profile.Id) >= 10) ModelState.AddModelError("", "A rider can have up to 10 emergency contacts.");
        ValidateContact(model);
        if (!ModelState.IsValid) return View("Edit", model);
        var contact = new EmergencyContact { RiderProfileId = profile.Id, NotifyEnabled = false };
        Apply(model, contact);
        db.EmergencyContacts.Add(contact);
        if (contact.NotificationsRequested || contact.PublicPhoneRequested) await QueueVerification(contact, profile);
        Audit("EmergencyContactCreated", contact.Id);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        TempData["Success"] = "Contact added. Email notifications and public phone publication require the recipient's separate confirmation.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("edit/{id:guid}")]
    public async Task<IActionResult> Edit(Guid id)
    {
        var contact = await OwnContact(id);
        if (contact is null) return NotFound();
        return View(ToModel(contact));
    }

    [HttpPost("edit/{id:guid}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, ContactEditModel model)
    {
        await using var transaction = await BeginContactUpdate(id);
        var contact = await OwnContact(id);
        if (contact is null) return NotFound();
        model.Id = contact.Id;
        ValidateContact(model);
        if (!ModelState.IsValid) return View(model);
        var emailChanged = !string.Equals(contact.Email, model.Email.Trim(), StringComparison.OrdinalIgnoreCase);
        var phoneChanged = !string.Equals(contact.Phone, model.Phone?.Trim() ?? "", StringComparison.Ordinal)
            || !string.Equals(contact.Name, model.Name.Trim(), StringComparison.Ordinal)
            || !string.Equals(contact.Relationship, model.Relationship.Trim(), StringComparison.Ordinal);
        var publicationRequested = !contact.PublicPhoneRequested && model.PublicPhone;
        var enabledChanged = !contact.NotificationsRequested && model.NotifyEnabled;
        Apply(model, contact);
        if (enabledChanged) contact.NotifyEnabled = false;
        if (emailChanged)
        {
            contact.IsVerified = false;
            contact.NotifyEnabled = false;
            contact.VerificationTokenHash = "";
            contact.PermissionVersion = Guid.NewGuid();
            contact.PublicPhone = false;
            contact.PublicPhoneConsentedAt = null;
            contact.NotificationsConsentedAt = null;
            await RevokeContactAccess(contact.Id);
            await RemovePendingMessages(contact.Id);
        }
        if (phoneChanged || !contact.PublicPhoneRequested)
        {
            contact.PublicPhone = false;
            contact.PublicPhoneConsentedAt = null;
        }
        if (!contact.NotificationsRequested)
        {
            contact.NotificationsConsentedAt = null;
            await RevokeContactAccess(contact.Id);
            await RemovePendingMessages(contact.Id);
        }
        if ((contact.NotificationsRequested || contact.PublicPhoneRequested) && (emailChanged || enabledChanged || (phoneChanged && contact.PublicPhoneRequested) || publicationRequested))
            await QueueVerification(contact, contact.RiderProfile);
        Audit("EmergencyContactUpdated", contact.Id);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        TempData["Success"] = "Emergency contact saved. Unverified email addresses do not receive emergency notifications.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("resend/{id:guid}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Resend(Guid id)
    {
        await using var transaction = await BeginContactUpdate(id);
        var contact = await OwnContact(id);
        if (contact is null) return NotFound();
        if ((!contact.NotificationsRequested || (contact.IsVerified && contact.NotifyEnabled)) && (!contact.PublicPhoneRequested || contact.PublicPhone)) return RedirectToAction(nameof(Index));
        var since = DateTimeOffset.UtcNow.AddMinutes(-5);
        if (await db.OutboxMessages.AnyAsync(x => x.EmergencyContactId == id && x.Kind == "ContactVerification" && x.CreatedAt > since))
        {
            TempData["Success"] = "A verification message was recently queued. Please wait five minutes before sending another.";
            return RedirectToAction(nameof(Index));
        }
        await RemovePendingMessages(id);
        await QueueVerification(contact, contact.RiderProfile);
        Audit("ContactVerificationRequested", contact.Id);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        TempData["Success"] = "A new contact verification email has been queued.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("delete/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var contact = await OwnContact(id);
        return contact is null ? NotFound() : View(ToModel(contact));
    }

    [HttpPost("delete/{id:guid}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(Guid id)
    {
        await using var transaction = await BeginContactUpdate(id);
        var contact = await OwnContact(id);
        if (contact is null) return NotFound();
        await RevokeContactAccess(id, detach: true);
        db.OutboxMessages.RemoveRange(await db.OutboxMessages.Where(x => x.EmergencyContactId == id).ToListAsync());
        db.EmergencyContacts.Remove(contact);
        Audit("EmergencyContactDeleted", id);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        TempData["Success"] = "Emergency contact deleted and their incident access revoked.";
        return RedirectToAction(nameof(Index));
    }

    [AllowAnonymous, HttpGet("verify/{token}")]
    public async Task<IActionResult> Verify(string token)
    {
        var contact = await ContactForVerification(token);
        return View(ToTokenModel(token, contact));
    }

    [AllowAnonymous, HttpPost("verify/{token}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifyConfirmed(string token, ContactTokenModel model)
    {
        await using var transaction = await BeginVerificationUpdate(token);
        var contact = await ContactForVerification(token);
        if (contact is null) return View("Verify", new ContactTokenModel());
        if (model.PolicyVersion != privacy.Value.PolicyVersion)
        {
            ModelState.AddModelError("", "The privacy notice changed. Review the current permissions and confirm your choices again.");
            return View("Verify", ToTokenModel(token, contact));
        }
        // Either permission may be declined independently; unchecked boxes never grant consent.
        contact.IsVerified = true;
        contact.NotifyEnabled = contact.NotificationsRequested && model.AcceptNotifications;
        contact.NotificationsRequested = contact.NotifyEnabled;
        contact.NotificationsConsentedAt = contact.NotifyEnabled && model.AcceptNotifications ? DateTimeOffset.UtcNow : null;
        contact.PublicPhone = contact.PublicPhoneRequested && model.AcceptPublicPhone;
        contact.PublicPhoneRequested = contact.PublicPhone;
        contact.PublicPhoneConsentedAt = contact.PublicPhone ? DateTimeOffset.UtcNow : null;
        contact.ConsentPolicyVersion = privacy.Value.PolicyVersion;
        contact.VerificationTokenHash = "";
        if (!contact.NotifyEnabled)
        {
            await RevokeContactAccess(contact.Id);
            await RemovePendingMessages(contact.Id);
        }
        AuditAnonymous("ContactEmailConfirmed", contact.Id);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return View("Message", "Your chosen emergency contact permissions are confirmed. Published phone numbers can be seen by anyone with the rider's sticker link. Reporting does not notify emergency services.");
    }

    [AllowAnonymous, HttpGet("unsubscribe/{token}")]
    public async Task<IActionResult> Unsubscribe(string token)
    {
        var contact = await ContactForUnsubscribe(token);
        return View(new ContactTokenModel { Token = token, ContactName = contact?.Name ?? "", RiderName = contact?.RiderProfile.FullName ?? "", Valid = contact is not null });
    }

    [AllowAnonymous, HttpPost("unsubscribe/{token}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> UnsubscribeConfirmed(string token)
    {
        var id = UnsubscribeContactId(token);
        if (id is null) return View("Unsubscribe", new ContactTokenModel());
        await using var transaction = await BeginContactUpdate(id.Value);
        var contact = await ContactForUnsubscribe(token);
        if (contact is null) return View("Unsubscribe", new ContactTokenModel());
        contact.NotifyEnabled = false;
        contact.NotificationsRequested = false;
        contact.PublicPhone = false;
        contact.PublicPhoneRequested = false;
        contact.PublicPhoneConsentedAt = null;
        contact.NotificationsConsentedAt = null;
        contact.IsVerified = false;
        contact.VerificationTokenHash = "";
        await RevokeContactAccess(contact.Id);
        await RemovePendingMessages(contact.Id);
        AuditAnonymous("ContactNotificationsDeclined", contact.Id);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return View("Message", "Emergency notifications have been disabled. Your phone number is no longer published on this rider’s emergency page, and your incident invitations have been revoked.");
    }

    private async Task<EmergencyContact?> ContactForVerification(string token)
    {
        if (token.Length is < 32 or > 100) return null;
        var hash = TokenService.Hash(token);
        var now = DateTimeOffset.UtcNow;
        return await db.EmergencyContacts.Include(x => x.RiderProfile).FirstOrDefaultAsync(x => x.VerificationTokenHash == hash && x.VerificationExpiresAt > now && (x.NotificationsRequested || x.PublicPhoneRequested) && db.Users.Any(u => u.Id == x.RiderProfile.UserId && u.IsActive));
    }
    private async Task<EmergencyContact?> ContactForUnsubscribe(string token)
    {
        var id = UnsubscribeContactId(token);
        if (id is null) return null;
        var value = tokens.Unprotect(token).Split(':');
        var version = Guid.Parse(value[2]);
        return await db.EmergencyContacts.Include(x => x.RiderProfile).FirstOrDefaultAsync(x => x.Id == id && x.PermissionVersion == version);
    }
    private Guid? UnsubscribeContactId(string token)
    {
        if (token.Length is < 20 or > 1024) return null;
        try
        {
            var parts = tokens.Unprotect(token).Split(':');
            if (parts.Length != 3 || parts[0] != "unsubscribe" || !Guid.TryParse(parts[1], out var id) || !Guid.TryParse(parts[2], out _)) return null;
            return id;
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or FormatException or ArgumentException) { return null; }
    }
    private async Task QueueVerification(EmergencyContact contact, RiderProfile profile)
    {
        db.OutboxMessages.RemoveRange(await db.OutboxMessages.Where(x => x.EmergencyContactId == contact.Id && x.Kind == "ContactVerification" && x.AcceptedAt == null).ToListAsync());
        var token = TokenService.Generate();
        contact.VerificationTokenHash = TokenService.Hash(token);
        contact.VerificationExpiresAt = DateTimeOffset.UtcNow.AddDays(7);
        var url = links.Absolute("/contacts/verify/" + Uri.EscapeDataString(token));
        var unsubscribe = links.Absolute("/contacts/unsubscribe/" + Uri.EscapeDataString(tokens.Protect($"unsubscribe:{contact.Id}:{contact.PermissionVersion}")));
        db.OutboxMessages.Add(new OutboxMessage
        {
            RiderProfileId = profile.Id, EmergencyContactId = contact.Id, ToEmail = contact.Email,
            Subject = "ScanBack — confirm emergency contact participation", Kind = "ContactVerification",
            Body = $"Someone has invited you to be an emergency contact in ScanBack.\n\nReview and confirm your email and the requested permissions: {url}\n\nThis link expires in seven days. Notifications require your confirmation. Public phone publication requires your separate, explicit permission on the confirmation form. Reports are unverified. ScanBack does not contact emergency dispatch.\n\nDecline or withdraw participation: {unsubscribe}"
        });
        await Task.CompletedTask;
    }
    private async Task RemovePendingMessages(Guid contactId) => db.OutboxMessages.RemoveRange(await db.OutboxMessages.Where(x => x.EmergencyContactId == contactId && x.AcceptedAt == null).ToListAsync());
    private async Task RevokeContactAccess(Guid contactId, bool detach = false)
    {
        foreach (var grant in await db.IncidentAccesses.Where(x => x.EmergencyContactId == contactId).ToListAsync()) { grant.IsRevoked = true; if (detach) grant.EmergencyContactId = null; }
    }
    private void Audit(string action, Guid id) => db.AuditEvents.Add(new AuditEvent { ActorId = UserId, Action = action, EntityId = id.ToString() });
    private void AuditAnonymous(string action, Guid id) => db.AuditEvents.Add(new AuditEvent { Action = action, EntityId = id.ToString() });
    private void ValidateContact(ContactEditModel model)
    {
        if (model.PublicPhone && string.IsNullOrWhiteSpace(model.Phone)) ModelState.AddModelError(nameof(model.Phone), "Enter a phone number before making it public.");
    }
    private static void Apply(ContactEditModel model, EmergencyContact contact)
    {
        contact.Name = model.Name.Trim(); contact.Relationship = model.Relationship.Trim(); contact.Email = model.Email.Trim();
        contact.Phone = model.Phone?.Trim() ?? ""; contact.PublicPhoneRequested = model.PublicPhone; contact.NotificationsRequested = model.NotifyEnabled;
        if (!contact.NotificationsRequested) contact.NotifyEnabled = false;
    }
    private static ContactEditModel ToModel(EmergencyContact x) => new() { Id = x.Id, Name = x.Name, Relationship = x.Relationship, Email = x.Email, Phone = x.Phone, PublicPhone = x.PublicPhoneRequested, NotifyEnabled = x.NotificationsRequested };
    private ContactTokenModel ToTokenModel(string token, EmergencyContact? contact) => new()
    {
        Token = token, ContactName = contact?.Name ?? "", RiderName = contact?.RiderProfile.FullName ?? "", Valid = contact is not null,
        NotificationsRequested = contact?.NotificationsRequested ?? false, PublicPhoneRequested = contact?.PublicPhoneRequested ?? false, Phone = contact?.Phone ?? "", Relationship = contact?.Relationship ?? "", PolicyVersion = privacy.Value.PolicyVersion
    };
    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> BeginContactUpdate(Guid id)
    {
        var transaction = await db.Database.BeginTransactionAsync(db.Database.IsSqlServer() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable);
        if (db.Database.IsSqlServer())
            await db.EmergencyContacts.FromSqlInterpolated($"SELECT * FROM [EmergencyContacts] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id}").AsNoTracking().Select(c => c.Id).SingleOrDefaultAsync();
        return transaction;
    }
    private async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> BeginVerificationUpdate(string token)
    {
        var transaction = await db.Database.BeginTransactionAsync(db.Database.IsSqlServer() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable);
        var hash = TokenService.Hash(token);
        if (db.Database.IsSqlServer())
            await db.EmergencyContacts.FromSqlInterpolated($"SELECT * FROM [EmergencyContacts] WITH (UPDLOCK, HOLDLOCK) WHERE [VerificationTokenHash] = {hash}").AsNoTracking().Select(c => c.Id).SingleOrDefaultAsync();
        return transaction;
    }
}
