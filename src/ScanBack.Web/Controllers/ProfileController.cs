using System.Data;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ScanBack.Domain;
using ScanBack.Infrastructure;
using ScanBack.Infrastructure.Hosting;
using ScanBack.Web.Models;
using ScanBack.Web.Services;
using SkiaSharp;

namespace ScanBack.Web.Controllers;

[Authorize, Route("profile")]
public sealed class ProfileController(AppDbContext db, UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn, TokenService tokens, IWebHostEnvironment environment, IConfiguration configuration, IOptions<PrivacyOptions> privacy, UiText text) : Controller
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    private string PhotoDirectory => Path.GetFullPath(configuration["Storage:PhotoDirectory"] ?? Path.Combine(environment.ContentRootPath, "App_Data", "photos"), environment.ContentRootPath);
    private const PublicFields HealthFields = PublicFields.BloodType | PublicFields.Allergies | PublicFields.Conditions | PublicFields.Medications | PublicFields.OtherInformation | PublicFields.Instructions;
    private static readonly string[] BloodTypes = ["A+", "A-", "B+", "B-", "AB+", "AB-", "O+", "O-"];

    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var profile = await db.RiderProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == UserId);
        if (profile is null) return RedirectToAction(nameof(Edit));
        var identifiers = await db.EmergencyIdentifiers.AsNoTracking().Where(x => x.RiderProfileId == profile.Id).OrderByDescending(x => x.CreatedAt).ToListAsync();
        var first = identifiers.FirstOrDefault(x => x.IsActive && !x.IsAdminBlocked && x.RevokedAt == null);
        string? preview = null;
        if (first is not null)
        {
            try { preview = "/e/" + Uri.EscapeDataString(tokens.Unprotect(first.ProtectedToken)); }
            catch (System.Security.Cryptography.CryptographicException) { }
        }
        return View(new ProfileDashboardModel
        {
            Profile = profile, Identifiers = identifiers, PreviewUrl = preview,
            ContactCount = await db.EmergencyContacts.CountAsync(x => x.RiderProfileId == profile.Id),
            VerifiedContactCount = await db.EmergencyContacts.CountAsync(x => x.RiderProfileId == profile.Id && x.IsVerified && x.NotifyEnabled),
            IncidentCount = await db.Incidents.CountAsync(x => x.RiderProfileId == profile.Id)
        });
    }

    [HttpGet("edit")]
    public async Task<IActionResult> Edit()
    {
        var profile = await db.RiderProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == UserId);
        var model = profile is null ? new ProfileEditModel() : ToModel(profile);
        // A changed notice needs a new affirmative choice; an old checkbox cannot grant consent to new wording.
        if (profile is not null && profile.ConsentPolicyVersion != privacy.Value.PolicyVersion) model.HealthConsent = false;
        return View(model);
    }

    [HttpPost("edit"), ValidateAntiForgeryToken, RequestSizeLimit(2_200_000)]
    public async Task<IActionResult> Edit(ProfileEditModel model, string? consentPolicyVersion = null)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(db.Database.IsSqlServer() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable);
        var profile = await OwnProfileForUpdate();
        model.HasPhoto = profile?.PhotoFileName is not null;
        if (model.HealthConsent && consentPolicyVersion != privacy.Value.PolicyVersion)
        {
            ModelState.Remove(nameof(model.HealthConsent));
            model.HealthConsent = false;
            ModelState.AddModelError(nameof(model.HealthConsent), "The privacy notice changed. Review the current notice and confirm your health-data choice again.");
        }
        if (model.DateOfBirth > DateOnly.FromDateTime(DateTime.UtcNow) || model.DateOfBirth < DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-150)))
            ModelState.AddModelError(nameof(model.DateOfBirth), "Enter a valid date of birth in the past.");
        model.BloodType = Clean(model.BloodType);
        if (model.BloodType is not null && !BloodTypes.Contains(model.BloodType)) ModelState.AddModelError(nameof(model.BloodType), "Select a known blood type or leave it unknown.");
        if (model.VisibleFields.Any(x => x == PublicFields.None || !Enum.IsDefined(x))) ModelState.AddModelError(nameof(model.VisibleFields), "Choose valid visibility options.");
        var selected = model.VisibleFields.Aggregate(PublicFields.None, (fields, field) => fields | field);
        var hasHealth = new[] { model.BloodType, model.Allergies, model.Conditions, model.Medications, model.OtherInformation, model.Instructions }.Any(x => !string.IsNullOrWhiteSpace(x));
        if (!model.HealthConsent && (hasHealth || (selected & HealthFields) != 0)) ModelState.AddModelError(nameof(model.HealthConsent), "Health information needs your explicit consent. Remove the information or use Withdraw health consent to delete it.");
        if (model.VisibleFields.Contains(PublicFields.Photo) && model.RemovePhoto) ModelState.AddModelError(nameof(model.RemovePhoto), "Remove public photo visibility when deleting your photo.");
        byte[]? photoBytes = null;
        string? photoExtension = null;
        if (model.Photo is { Length: > 0 })
        {
            if (model.Photo.Length > 2 * 1024 * 1024) ModelState.AddModelError(nameof(model.Photo), "Choose a JPEG or PNG image up to 2 MB.");
            else
            {
                using var memory = new MemoryStream();
                await model.Photo.CopyToAsync(memory);
                var canonical = CanonicalPhoto(memory.ToArray());
                if (canonical is null) ModelState.AddModelError(nameof(model.Photo), "Choose a valid JPEG or PNG up to 2 MB, with dimensions no larger than 4096 pixels and 16 megapixels.");
                else { photoBytes = canonical.Value.Bytes; photoExtension = canonical.Value.Extension; }
            }
        }
        if ((selected & PublicFields.Photo) != 0 && !model.HasPhoto && photoExtension is null) ModelState.AddModelError(nameof(model.VisibleFields), "Upload a photo before making it public.");
        if (!ModelState.IsValid) return View(model);
        if (profile is null)
        {
            profile = new RiderProfile { UserId = UserId };
            db.RiderProfiles.Add(profile);
        }
        var oldPhoto = profile.PhotoFileName;
        if (photoBytes is not null && photoExtension is not null)
        {
            Directory.CreateDirectory(PhotoDirectory);
            profile.PhotoFileName = Guid.NewGuid().ToString("N") + photoExtension;
            await System.IO.File.WriteAllBytesAsync(Path.Combine(PhotoDirectory, profile.PhotoFileName), photoBytes);
        }
        else if (model.RemovePhoto) profile.PhotoFileName = null;
        if (profile.HealthConsent != model.HealthConsent || (model.HealthConsent && profile.ConsentPolicyVersion != privacy.Value.PolicyVersion))
        {
            db.ConsentRecords.Add(new ConsentRecord { UserId = UserId, PolicyVersion = privacy.Value.PolicyVersion, ConsentText = text[privacy.Value.HealthConsentTextPublic], Action = model.HealthConsent ? "HealthConsentGranted" : "HealthConsentWithdrawn" });
            profile.ConsentAt = model.HealthConsent ? DateTimeOffset.UtcNow : null;
            profile.ConsentPolicyVersion = model.HealthConsent ? privacy.Value.PolicyVersion : null;
        }
        profile.FullName = model.FullName.Trim();
        profile.DateOfBirth = model.DateOfBirth;
        profile.BloodType = model.BloodType;
        profile.Allergies = Clean(model.Allergies);
        profile.Conditions = Clean(model.Conditions);
        profile.Medications = Clean(model.Medications);
        profile.OtherInformation = Clean(model.OtherInformation);
        profile.Instructions = Clean(model.Instructions);
        profile.HealthConsent = model.HealthConsent;
        profile.PublicFields = selected;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        Audit("ProfileUpdated", profile.Id);
        try
        {
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            if (profile.PhotoFileName != oldPhoto) DeletePhoto(profile.PhotoFileName);
            throw;
        }
        if (oldPhoto != profile.PhotoFileName) DeletePhoto(oldPhoto);
        TempData["Success"] = "Your emergency profile has been saved.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("withdraw-consent"), ValidateAntiForgeryToken]
    public async Task<IActionResult> WithdrawConsent()
    {
        await using var transaction = await db.Database.BeginTransactionAsync(db.Database.IsSqlServer() ? IsolationLevel.ReadCommitted : IsolationLevel.Serializable);
        var profile = await OwnProfileForUpdate();
        if (profile is null) return NotFound();
        profile.BloodType = profile.Allergies = profile.Conditions = profile.Medications = profile.OtherInformation = profile.Instructions = null;
        profile.HealthConsent = false;
        profile.ConsentAt = null;
        profile.ConsentPolicyVersion = null;
        profile.PublicFields &= ~HealthFields;
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        db.ConsentRecords.Add(new ConsentRecord { UserId = UserId, PolicyVersion = privacy.Value.PolicyVersion, ConsentText = text[privacy.Value.HealthConsentTextPublic], Action = "HealthConsentWithdrawn" });
        Audit("HealthConsentWithdrawn", profile.Id);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        TempData["Success"] = "Health consent withdrawn. Your health fields and their public visibility have been removed.";
        return RedirectToAction(nameof(Edit));
    }

    [HttpGet("photo")]
    public async Task<IActionResult> OwnPhoto()
    {
        var filename = await db.RiderProfiles.Where(x => x.UserId == UserId).Select(x => x.PhotoFileName).FirstOrDefaultAsync();
        return PhotoResult(filename);
    }

    [AllowAnonymous, HttpGet("photo/{token}")]
    public async Task<IActionResult> PublicPhoto(string token)
    {
        if (token.Length is < 32 or > 100) return NotFound();
        var hash = TokenService.Hash(token);
        var profile = await db.EmergencyIdentifiers.AsNoTracking().Where(x => x.TokenHash == hash && x.IsActive && !x.IsAdminBlocked && x.RevokedAt == null)
            .Select(x => new { x.RiderProfile.UserId, x.RiderProfile.PublicFields, x.RiderProfile.PhotoFileName }).FirstOrDefaultAsync();
        if (profile is null || (profile.PublicFields & PublicFields.Photo) == 0 || !await db.Users.AnyAsync(x => x.Id == profile.UserId && x.IsActive)) return NotFound();
        return PhotoResult(profile.PhotoFileName);
    }

    [HttpGet("export")]
    public async Task<IActionResult> Export()
    {
        var user = await users.GetUserAsync(User);
        var profile = await db.RiderProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == UserId);
        if (user is null) return NotFound();
        var profileId = profile?.Id;
        var contacts = await db.EmergencyContacts.AsNoTracking().Where(x => x.RiderProfileId == profileId).Select(x => new { x.Id, x.Name, x.Relationship, x.Email, x.Phone, x.PublicPhone, x.PublicPhoneRequested, x.PublicPhoneConsentedAt, x.ConsentPolicyVersion, x.NotificationsRequested, x.NotifyEnabled, x.NotificationsConsentedAt, x.IsVerified, x.VerificationExpiresAt }).ToListAsync();
        var identifiers = await db.EmergencyIdentifiers.AsNoTracking().Where(x => x.RiderProfileId == profileId).Select(x => new { x.Id, x.Label, x.IsActive, x.IsAdminBlocked, x.CreatedAt, x.RevokedAt }).ToListAsync();
        var incidents = await db.Incidents.AsNoTracking().Where(x => x.RiderProfileId == profileId).Select(x => new { x.Id, x.ReportedAt, x.ExpiresAt, x.Latitude, x.Longitude, x.LocationDescription, x.Consciousness, x.PeopleInvolved, x.IsClosed }).ToListAsync();
        var consents = await db.ConsentRecords.AsNoTracking().Where(x => x.UserId == UserId).Select(x => new { x.PolicyVersion, x.ConsentText, x.Action, x.CreatedAt }).ToListAsync();
        var accesses = await db.IncidentAccesses.AsNoTracking().Where(x => x.Incident.RiderProfileId == profileId)
            .Select(x => new { x.Id, x.IncidentId, x.EmergencyContactId, x.Role, x.ExpiresAt, x.IsRevoked, x.InvitationConsumedAt }).ToListAsync();
        var notifications = await db.OutboxMessages.AsNoTracking().Where(x => (profileId != null && x.RiderProfileId == profileId) || (x.IncidentId != null && db.Incidents.Any(i => i.Id == x.IncidentId && i.RiderProfileId == profileId)) || (x.EmergencyContactId != null && db.EmergencyContacts.Any(c => c.Id == x.EmergencyContactId && c.RiderProfileId == profileId)))
            .Select(x => new { x.Id, x.RiderProfileId, x.IncidentId, x.EmergencyContactId, x.ToEmail, x.Subject, x.Kind, x.CreatedAt, x.NextAttemptAt, x.AcceptedAt, x.Attempts, x.LastError }).ToListAsync();
        var audit = await db.AuditEvents.AsNoTracking().Where(x => x.ActorId == UserId).OrderBy(x => x.CreatedAt)
            .Select(x => new { x.Action, x.EntityId, x.CreatedAt }).ToListAsync();
        string? photo = null;
        if (SafePhotoPath(profile?.PhotoFileName) is { } photoPath && System.IO.File.Exists(photoPath)) photo = Convert.ToBase64String(await System.IO.File.ReadAllBytesAsync(photoPath));
        var data = new
        {
            ExportedAt = DateTimeOffset.UtcNow, Account = new { user.Id, user.UserName, user.Email, user.EmailConfirmed, user.PhoneNumber, user.PhoneNumberConfirmed, user.TwoFactorEnabled, user.LockoutEnabled, user.LockoutEnd, user.AccessFailedCount, user.IsActive },
            Profile = profile is null ? null : new { profile.Id, profile.FullName, profile.DateOfBirth, profile.BloodType, profile.Allergies, profile.Conditions, profile.Medications, profile.OtherInformation, profile.Instructions, PublicFields = profile.PublicFields.ToString(), profile.HealthConsent, profile.ConsentAt, profile.ConsentPolicyVersion, profile.UpdatedAt },
            Photo = new { FileName = profile?.PhotoFileName, Base64 = photo }, Contacts = contacts, Stickers = identifiers, Incidents = incidents, IncidentAccess = accesses, Notifications = notifications, ConsentRecords = consents, Audit = audit
        };
        db.AuditEvents.Add(new AuditEvent { ActorId = UserId, Action = "PersonalDataExported", EntityId = profileId?.ToString() });
        await db.SaveChangesAsync();
        Response.Headers.CacheControl = "no-store";
        return File(JsonSerializer.SerializeToUtf8Bytes(data, new JsonSerializerOptions { WriteIndented = true }), "application/json", "scanback-personal-data.json");
    }

    [HttpGet("delete")]
    public IActionResult Delete() => View(new DeleteAccountModel());

    [HttpPost("delete"), ValidateAntiForgeryToken, EnableRateLimiting("authentication")]
    public async Task<IActionResult> Delete(DeleteAccountModel model)
    {
        if (!model.ConfirmDeletion) ModelState.AddModelError(nameof(model.ConfirmDeletion), "Confirm permanent account deletion.");
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        if (!ModelState.IsValid) return View(model);
        if (!await users.CheckPasswordAsync(user, model.Password))
        {
            ModelState.AddModelError(nameof(model.Password), "The password is incorrect.");
            return View(model);
        }
        // Keep the count and deletion under the same serializable transaction as administrator status changes.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        if (await users.IsInRoleAsync(user, "Administrator"))
        {
            var remainingAdministrators = await (from membership in db.UserRoles
                                                 join role in db.Roles on membership.RoleId equals role.Id
                                                 join administrator in db.Users on membership.UserId equals administrator.Id
                                                 where role.NormalizedName == "ADMINISTRATOR" && administrator.IsActive && administrator.Id != user.Id
                                                 select administrator.Id).Distinct().CountAsync();
            if (remainingAdministrators == 0)
            {
                ModelState.AddModelError("", "Create another active administrator before deleting the last administrator account.");
                return View(model);
            }
        }
        var profile = await db.RiderProfiles.FirstOrDefaultAsync(x => x.UserId == user.Id);
        var photo = profile?.PhotoFileName;
        var profileId = profile?.Id;
        var profileIdText = profileId?.ToString();
        var contactIds = profile is null ? new List<Guid>() : await db.EmergencyContacts.Where(x => x.RiderProfileId == profile.Id).Select(x => x.Id).ToListAsync();
        var incidentIds = profile is null ? new List<Guid>() : await db.Incidents.Where(x => x.RiderProfileId == profile.Id).Select(x => x.Id).ToListAsync();
        var identifierIds = profile is null ? new List<Guid>() : await db.EmergencyIdentifiers.Where(x => x.RiderProfileId == profile.Id).Select(x => x.Id).ToListAsync();
        var grantIds = await db.IncidentAccesses.Where(x => incidentIds.Contains(x.IncidentId) || (x.EmergencyContactId.HasValue && contactIds.Contains(x.EmergencyContactId.Value))).Select(x => x.Id).ToListAsync();
        var ownedEntityIds = contactIds.Concat(incidentIds).Concat(identifierIds).Concat(grantIds).Select(x => x.ToString()).ToList();
        ownedEntityIds.Add(user.Id);
        if (profileIdText is not null) ownedEntityIds.Add(profileIdText);
        var outbox = await db.OutboxMessages.Where(x => (profileId.HasValue && x.RiderProfileId == profileId) || (x.EmergencyContactId.HasValue && contactIds.Contains(x.EmergencyContactId.Value)) || (x.IncidentId.HasValue && incidentIds.Contains(x.IncidentId.Value)) || ((x.Kind == "Confirmation" || x.Kind == "PasswordReset") && x.ToEmail == user.Email)).ToListAsync();
        db.OutboxMessages.RemoveRange(outbox);
        db.IncidentAccesses.RemoveRange(await db.IncidentAccesses.Where(x => grantIds.Contains(x.Id)).ToListAsync());
        await db.SaveChangesAsync();
        db.Incidents.RemoveRange(await db.Incidents.Where(x => incidentIds.Contains(x.Id)).ToListAsync());
        await db.SaveChangesAsync();
        if (profile is not null)
        {
            db.EmergencyContacts.RemoveRange(await db.EmergencyContacts.Where(x => x.RiderProfileId == profile.Id).ToListAsync());
            db.EmergencyIdentifiers.RemoveRange(await db.EmergencyIdentifiers.Where(x => x.RiderProfileId == profile.Id).ToListAsync());
            db.RiderProfiles.Remove(profile);
        }
        db.ConsentRecords.RemoveRange(await db.ConsentRecords.Where(x => x.UserId == user.Id).ToListAsync());
        foreach (var entry in await db.AuditEvents.Where(x => x.ActorId == user.Id || (x.EntityId != null && ownedEntityIds.Contains(x.EntityId))).ToListAsync()) { entry.ActorId = null; entry.EntityId = null; }
        db.AuditEvents.Add(new AuditEvent { Action = "AccountDeleted" });
        await db.SaveChangesAsync();
        var result = await users.DeleteAsync(user);
        if (!result.Succeeded)
        {
            await transaction.RollbackAsync();
            ModelState.AddModelError("", "Account deletion could not be completed. Please try again.");
            return View(model);
        }
        await transaction.CommitAsync();
        DeletePhoto(photo);
        await signIn.SignOutAsync();
        TempData["Success"] = "Your account and its active personal data have been deleted.";
        return RedirectToAction("Index", "Home");
    }

    private Task<RiderProfile?> OwnProfileForUpdate() => db.Database.IsSqlServer()
        ? db.RiderProfiles.FromSqlInterpolated($"SELECT * FROM [RiderProfiles] WITH (UPDLOCK, HOLDLOCK) WHERE [UserId] = {UserId}").SingleOrDefaultAsync()
        : db.RiderProfiles.FirstOrDefaultAsync(x => x.UserId == UserId);

    private IActionResult PhotoResult(string? filename)
    {
        var path = SafePhotoPath(filename);
        if (path is null || !System.IO.File.Exists(path)) return NotFound();
        Response.Headers.CacheControl = "no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return PhysicalFile(path, filename!.EndsWith(".png", StringComparison.Ordinal) ? "image/png" : "image/jpeg");
    }
    private string? SafePhotoPath(string? filename) => filename is null || filename != Path.GetFileName(filename) || !(filename.EndsWith(".jpg", StringComparison.Ordinal) || filename.EndsWith(".png", StringComparison.Ordinal)) ? null : Path.Combine(PhotoDirectory, filename);
    private void DeletePhoto(string? filename) { var path = SafePhotoPath(filename); if (path is not null && System.IO.File.Exists(path)) System.IO.File.Delete(path); }
    private void Audit(string action, Guid profileId) => db.AuditEvents.Add(new AuditEvent { ActorId = UserId, Action = action, EntityId = profileId.ToString() });
    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    // Decode before writing, bound allocation, apply orientation, and reencode to strip EXIF/GPS metadata.
    private static (byte[] Bytes, string Extension)? CanonicalPhoto(byte[] bytes)
    {
        using var encoded = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(encoded);
        if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png)) return null;
        var width = codec.Info.Width;
        var height = codec.Info.Height;
        if (width is < 1 or > 4096 || height is < 1 or > 4096 || (long)width * height > 16_000_000) return null;
        using var colorSpace = SKColorSpace.CreateSrgb();
        var info = new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul, colorSpace);
        using var decoded = new SKBitmap(info);
        if (codec.GetPixels(info, decoded.GetPixels()) != SKCodecResult.Success) return null;
        var origin = (int)codec.EncodedOrigin;
        if (origin is < 1 or > 8) return null;
        var transpose = origin >= 5;
        using var oriented = new SKBitmap(new SKImageInfo(transpose ? height : width, transpose ? width : height, SKColorType.Rgba8888, SKAlphaType.Premul, colorSpace));
        using (var canvas = new SKCanvas(oriented))
        {
            canvas.Clear(SKColors.Transparent);
            switch (origin)
            {
                case 2: canvas.Translate(width, 0); canvas.Scale(-1, 1); break;
                case 3: canvas.Translate(width, height); canvas.RotateDegrees(180); break;
                case 4: canvas.Translate(0, height); canvas.Scale(1, -1); break;
                case 5: canvas.RotateDegrees(90); canvas.Scale(1, -1); break;
                case 6: canvas.Translate(height, 0); canvas.RotateDegrees(90); break;
                case 7: canvas.Translate(height, width); canvas.RotateDegrees(90); canvas.Scale(-1, 1); break;
                case 8: canvas.Translate(0, width); canvas.RotateDegrees(270); break;
            }
            canvas.DrawBitmap(decoded, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None));
        }
        using var image = SKImage.FromBitmap(oriented);
        using var canonical = image.Encode(codec.EncodedFormat, 90);
        if (canonical is null || canonical.Size > 2 * 1024 * 1024) return null;
        return (canonical.ToArray(), codec.EncodedFormat == SKEncodedImageFormat.Png ? ".png" : ".jpg");
    }
    private static ProfileEditModel ToModel(RiderProfile p) => new()
    {
        FullName = p.FullName, DateOfBirth = p.DateOfBirth, BloodType = p.BloodType, Allergies = p.Allergies, Conditions = p.Conditions,
        Medications = p.Medications, OtherInformation = p.OtherInformation, Instructions = p.Instructions, HealthConsent = p.HealthConsent,
        HasPhoto = p.PhotoFileName is not null, VisibleFields = Enum.GetValues<PublicFields>().Where(x => x != PublicFields.None && p.PublicFields.HasFlag(x)).ToList()
    };
}
