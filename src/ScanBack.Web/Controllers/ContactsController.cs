using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ScanBack.Domain;
using ScanBack.Infrastructure;
using ScanBack.Web.Models;
using ScanBack.Web.Services;

namespace ScanBack.Web.Controllers;

[Authorize, Route("contacts")]
public sealed class ContactsController(AppDbContext db, TokenService tokens, SiteLinks links) : Controller
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
        if (await db.EmergencyContacts.CountAsync(x => x.RiderProfileId == profile.Id) >= 10) ModelState.AddModelError("", "A rider can have up to 10 emergency contacts.");
        ValidateContact(model);
        if (!ModelState.IsValid) return View("Edit", model);
        var contact = new EmergencyContact { RiderProfileId = profile.Id };
        Apply(model, contact);
        db.EmergencyContacts.Add(contact);
        if (contact.NotifyEnabled) await QueueVerification(contact, profile);
        Audit("EmergencyContactCreated", contact.Id);
        await db.SaveChangesAsync();
        TempData["Success"] = contact.NotifyEnabled ? "Contact added. They must confirm their email address before receiving emergency notifications." : "Contact added. Emergency notifications are disabled.";
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
        var contact = await OwnContact(id);
        if (contact is null) return NotFound();
        model.Id = contact.Id;
        ValidateContact(model);
        if (!ModelState.IsValid) return View(model);
        var emailChanged = !string.Equals(contact.Email, model.Email.Trim(), StringComparison.OrdinalIgnoreCase);
        var enabledChanged = !contact.NotifyEnabled && model.NotifyEnabled;
        Apply(model, contact);
        if (emailChanged)
        {
            contact.IsVerified = false;
            contact.VerificationTokenHash = "";
            await RevokeContactAccess(contact.Id);
            await RemovePendingMessages(contact.Id);
        }
        if (!contact.NotifyEnabled)
        {
            await RevokeContactAccess(contact.Id);
            await RemovePendingMessages(contact.Id);
        }
        else if (!contact.IsVerified && (emailChanged || enabledChanged)) await QueueVerification(contact, contact.RiderProfile);
        Audit("EmergencyContactUpdated", contact.Id);
        await db.SaveChangesAsync();
        TempData["Success"] = "Emergency contact saved. Unverified email addresses do not receive emergency notifications.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("resend/{id:guid}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Resend(Guid id)
    {
        var contact = await OwnContact(id);
        if (contact is null) return NotFound();
        if (contact.IsVerified || !contact.NotifyEnabled) return RedirectToAction(nameof(Index));
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
        var contact = await OwnContact(id);
        if (contact is null) return NotFound();
        await RevokeContactAccess(id, detach: true);
        db.OutboxMessages.RemoveRange(await db.OutboxMessages.Where(x => x.EmergencyContactId == id).ToListAsync());
        db.EmergencyContacts.Remove(contact);
        Audit("EmergencyContactDeleted", id);
        await db.SaveChangesAsync();
        TempData["Success"] = "Emergency contact deleted and their incident access revoked.";
        return RedirectToAction(nameof(Index));
    }

    [AllowAnonymous, HttpGet("verify/{token}")]
    public async Task<IActionResult> Verify(string token)
    {
        var contact = await ContactForVerification(token);
        return View(new ContactTokenModel { Token = token, ContactName = contact?.Name ?? "", RiderName = contact?.RiderProfile.FullName ?? "", Valid = contact is not null });
    }

    [AllowAnonymous, HttpPost("verify/{token}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifyConfirmed(string token)
    {
        var contact = await ContactForVerification(token);
        if (contact is null) return View("Verify", new ContactTokenModel());
        contact.IsVerified = true;
        contact.VerificationTokenHash = "";
        AuditAnonymous("ContactEmailConfirmed", contact.Id);
        await db.SaveChangesAsync();
        return View("Message", "Your email address is confirmed. You may receive messages when someone reports a possible accident for this rider. Reporting does not notify emergency services.");
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
        var contact = await ContactForUnsubscribe(token);
        if (contact is null) return View("Unsubscribe", new ContactTokenModel());
        contact.NotifyEnabled = false;
        contact.PublicPhone = false;
        contact.IsVerified = false;
        contact.VerificationTokenHash = "";
        await RevokeContactAccess(contact.Id);
        await RemovePendingMessages(contact.Id);
        AuditAnonymous("ContactNotificationsDeclined", contact.Id);
        await db.SaveChangesAsync();
        return View("Message", "Emergency notifications have been disabled. Your phone number is no longer published on this rider’s emergency page, and your incident invitations have been revoked.");
    }

    private async Task<EmergencyContact?> ContactForVerification(string token)
    {
        if (token.Length is < 32 or > 100) return null;
        var hash = TokenService.Hash(token);
        var now = DateTimeOffset.UtcNow;
        return await db.EmergencyContacts.Include(x => x.RiderProfile).FirstOrDefaultAsync(x => x.VerificationTokenHash == hash && x.VerificationExpiresAt > now && x.NotifyEnabled && db.Users.Any(u => u.Id == x.RiderProfile.UserId && u.IsActive));
    }
    private async Task<EmergencyContact?> ContactForUnsubscribe(string token)
    {
        if (token.Length is < 20 or > 1024) return null;
        try
        {
            var value = tokens.Unprotect(token);
            if (!value.StartsWith("unsubscribe:", StringComparison.Ordinal) || !Guid.TryParse(value[12..], out var id)) return null;
            return await db.EmergencyContacts.Include(x => x.RiderProfile).FirstOrDefaultAsync(x => x.Id == id);
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or FormatException or ArgumentException) { return null; }
    }
    private async Task QueueVerification(EmergencyContact contact, RiderProfile profile)
    {
        var token = TokenService.Generate();
        contact.VerificationTokenHash = TokenService.Hash(token);
        contact.VerificationExpiresAt = DateTimeOffset.UtcNow.AddDays(7);
        var url = links.Absolute("/contacts/verify/" + Uri.EscapeDataString(token));
        var unsubscribe = links.Absolute("/contacts/unsubscribe/" + Uri.EscapeDataString(tokens.Protect("unsubscribe:" + contact.Id)));
        db.OutboxMessages.Add(new OutboxMessage
        {
            RiderProfileId = profile.Id, EmergencyContactId = contact.Id, ToEmail = contact.Email,
            Subject = "ScanBack — confirm emergency contact participation", Kind = "ContactVerification",
            Body = $"{profile.FullName} has added you as an emergency contact in ScanBack.\n\nConfirm your email and participation: {url}\n\nThis link expires in seven days. You will receive notifications only after confirming. Reports describe possible accidents and are not independently verified. ScanBack does not contact emergency dispatch.\n\nIf you do not want to participate, decline here: {unsubscribe}"
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
        contact.Phone = model.Phone?.Trim() ?? ""; contact.PublicPhone = model.PublicPhone; contact.NotifyEnabled = model.NotifyEnabled;
    }
    private static ContactEditModel ToModel(EmergencyContact x) => new() { Id = x.Id, Name = x.Name, Relationship = x.Relationship, Email = x.Email, Phone = x.Phone, PublicPhone = x.PublicPhone, NotifyEnabled = x.NotifyEnabled };
}
