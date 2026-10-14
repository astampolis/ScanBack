using System.Security.Claims;
using System.Security.Cryptography;
using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using QRCoder;
using ScanBack.Domain;
using ScanBack.Infrastructure;
using ScanBack.Web.Models;
using ScanBack.Web.Services;

namespace ScanBack.Web.Controllers;

[Authorize]
[Route("stickers")]
public sealed class StickersController(AppDbContext db, TokenService tokens, SiteLinks links) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var profile = await OwnerProfile();
        if (profile is null) return Redirect("/profile");
        return View(await Page(profile.Id));
    }

    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create([Bind(Prefix = "NewSticker")] StickerCreateInput input)
    {
        var profile = await OwnerProfile();
        if (profile is null) return Redirect("/profile");
        if (string.IsNullOrWhiteSpace(input.Label)) ModelState.AddModelError("NewSticker.Label", "Enter a sticker label.");
        if (!ModelState.IsValid) return View("Index", await Page(profile.Id, input));
        await using var transaction = await db.Database.BeginTransactionAsync(db.Database.IsSqlServer() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable);
        if (db.Database.IsSqlServer())
            await db.RiderProfiles.FromSqlInterpolated($"SELECT * FROM [RiderProfiles] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {profile.Id}")
                .AsNoTracking().Select(p => p.Id).SingleAsync();
        if (await db.EmergencyIdentifiers.CountAsync(i => i.RiderProfileId == profile.Id) >= 10)
            return View("Index", await Page(profile.Id, input, "You can have up to 10 stickers. Replace an existing sticker to issue a new link."));
        var token = TokenService.Generate();
        var identifier = new EmergencyIdentifier
        {
            RiderProfileId = profile.Id,
            Label = input.Label.Trim(),
            TokenHash = TokenService.Hash(token),
            ProtectedToken = tokens.Protect(token)
        };
        db.EmergencyIdentifiers.Add(identifier);
        Audit("Identifier.Created", identifier.Id);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        TempData["Success"] = "Emergency sticker created. The QR code and NFC URL open the same emergency page.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:guid}/toggle")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Toggle(Guid id)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(db.Database.IsSqlServer() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable);
        var identifier = await OwnedIdentifierForUpdate(id);
        if (identifier is null) return NotFound();
        identifier.IsActive = !identifier.IsActive;
        identifier.RevokedAt = identifier.IsActive ? null : DateTimeOffset.UtcNow;
        if (!identifier.IsActive) await RevokeIncidentAccess(id);
        Audit(identifier.IsActive ? "Identifier.Activated" : "Identifier.Deactivated", id);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        TempData["Success"] = identifier.IsActive ? "Sticker activated." : "Sticker deactivated. Its emergency page and existing incident links are unavailable.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:guid}/replace")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Replace(Guid id)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(db.Database.IsSqlServer() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable);
        var identifier = await OwnedIdentifierForUpdate(id);
        if (identifier is null) return NotFound();
        var token = TokenService.Generate();
        identifier.TokenHash = TokenService.Hash(token);
        identifier.ProtectedToken = tokens.Protect(token);
        identifier.IsActive = true;
        identifier.RevokedAt = null;
        await RevokeIncidentAccess(id);
        Audit("Identifier.Replaced", id);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        TempData["Success"] = "Sticker replaced. The previous QR code, NFC URL, and incident links are invalid. Print the new QR code and reprogram the NFC sticker.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("{id:guid}/print")]
    public async Task<IActionResult> Print(Guid id)
    {
        var identifier = await OwnedIdentifier(id);
        if (identifier is null) return NotFound();
        var view = ToView(identifier);
        if (view.EmergencyUrl is null) return NotFound();
        return View(view);
    }

    [HttpGet("{id:guid}/qr.svg")]
    public async Task<IActionResult> Svg(Guid id)
    {
        var identifier = await OwnedIdentifier(id);
        if (identifier is null) return NotFound();
        var url = ToView(identifier).EmergencyUrl;
        if (url is null) return NotFound();
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        using var qr = new SvgQRCode(data);
        var svg = qr.GetGraphic(8);
        return File(System.Text.Encoding.UTF8.GetBytes(svg), "image/svg+xml", $"scanback-{id:N}.svg");
    }

    [HttpGet("{id:guid}/qr.png")]
    public async Task<IActionResult> Png(Guid id)
    {
        var identifier = await OwnedIdentifier(id);
        if (identifier is null) return NotFound();
        var url = ToView(identifier).EmergencyUrl;
        if (url is null) return NotFound();
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q);
        using var qr = new PngByteQRCode(data);
        return File(qr.GetGraphic(16), "image/png", $"scanback-{id:N}.png");
    }

    private async Task<RiderProfile?> OwnerProfile()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId is null || !await db.Users.AnyAsync(u => u.Id == userId && u.IsActive)) return null;
        return await db.RiderProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId);
    }

    private async Task<EmergencyIdentifier?> OwnedIdentifier(Guid id)
    {
        var owner = await OwnerProfile();
        return owner is null ? null : await db.EmergencyIdentifiers.FirstOrDefaultAsync(i => i.Id == id && i.RiderProfileId == owner.Id);
    }

    private async Task<EmergencyIdentifier?> OwnedIdentifierForUpdate(Guid id)
    {
        var owner = await OwnerProfile();
        if (owner is null) return null;
        return db.Database.IsSqlServer()
            ? await db.EmergencyIdentifiers.FromSqlInterpolated($"SELECT * FROM [EmergencyIdentifiers] WITH (UPDLOCK, HOLDLOCK) WHERE [Id] = {id} AND [RiderProfileId] = {owner.Id}").SingleOrDefaultAsync()
            : await db.EmergencyIdentifiers.FirstOrDefaultAsync(i => i.Id == id && i.RiderProfileId == owner.Id);
    }

    private async Task<StickersPageViewModel> Page(Guid profileId, StickerCreateInput? input = null, string? error = null)
    {
        var identifiers = await db.EmergencyIdentifiers.AsNoTracking().Where(i => i.RiderProfileId == profileId).OrderByDescending(i => i.CreatedAt).ToListAsync();
        return new StickersPageViewModel { Stickers = identifiers.Select(ToView).ToList(), NewSticker = input ?? new StickerCreateInput(), Error = error };
    }

    private StickerViewModel ToView(EmergencyIdentifier identifier)
    {
        string? url = null;
        try { url = links.Absolute("/e/" + Uri.EscapeDataString(tokens.Unprotect(identifier.ProtectedToken))); }
        catch (Exception exception) when (exception is CryptographicException or FormatException or ArgumentException) { }
        return new StickerViewModel { Id = identifier.Id, Label = identifier.Label, IsActive = identifier.IsActive, CreatedAt = identifier.CreatedAt, EmergencyUrl = url };
    }

    private async Task RevokeIncidentAccess(Guid identifierId)
    {
        // Persist revocation together with the identifier change using one SaveChanges operation.
        var grants = await db.IncidentAccesses.Where(a => a.Incident.EmergencyIdentifierId == identifierId && !a.IsRevoked).ToListAsync();
        foreach (var grant in grants) grant.IsRevoked = true;
        var notices = await db.OutboxMessages.Where(m => m.IncidentId != null && m.AcceptedAt == null && m.Attempts < 5
            && db.Incidents.Any(i => i.Id == m.IncidentId && i.EmergencyIdentifierId == identifierId)).ToListAsync();
        foreach (var notice in notices)
        {
            notice.Attempts = 5;
            notice.LastError = "Incident access revoked";
        }
    }

    private void Audit(string action, Guid id) => db.AuditEvents.Add(new AuditEvent { ActorId = User.FindFirstValue(ClaimTypes.NameIdentifier), Action = action, EntityId = id.ToString() });
}
