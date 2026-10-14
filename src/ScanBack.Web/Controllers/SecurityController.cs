using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using QRCoder;
using ScanBack.Domain;
using ScanBack.Infrastructure;
using ScanBack.Web.Models;

namespace ScanBack.Web.Controllers;

[Authorize, Route("security")]
public sealed class SecurityController(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn, AppDbContext db) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        return View(await Model(user));
    }

    [HttpGet("qr")]
    public async Task<IActionResult> Qr()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        if (user.TwoFactorEnabled) return NotFound();
        var model = await Model(user);
        using var data = QRCodeGenerator.GenerateQrCode(model.AuthenticatorUri, QRCodeGenerator.ECCLevel.Q);
        using var code = new PngByteQRCode(data);
        return File(code.GetGraphic(8), "image/png");
    }

    [HttpPost("enable")]
    public async Task<IActionResult> Enable(AuthenticatorSetupModel input)
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        if (user.TwoFactorEnabled) return RedirectToAction(nameof(Index));
        var code = input.Code?.Replace(" ", "").Replace("-", "") ?? "";
        if (!ModelState.IsValid || !await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, code))
        {
            ModelState.AddModelError(nameof(input.Code), "The authenticator code is invalid. Please try again.");
            return View("Index", await Model(user));
        }
        await users.SetTwoFactorEnabledAsync(user, true);
        var codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))!.ToArray();
        db.AuditEvents.Add(new AuditEvent { ActorId = user.Id, Action = "MfaEnabled", EntityId = user.Id });
        await db.SaveChangesAsync();
        await users.UpdateSecurityStampAsync(user);
        await signIn.SignOutAsync();
        return View("RecoveryCodes", codes);
    }

    private async Task<AuthenticatorSetupModel> Model(ApplicationUser user)
    {
        if (user.TwoFactorEnabled) return new() { Enabled = true };
        var key = await users.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await users.ResetAuthenticatorKeyAsync(user);
            key = await users.GetAuthenticatorKeyAsync(user);
        }
        return new()
        {
            SharedKey = key!,
            AuthenticatorUri = $"otpauth://totp/ScanBack:{Uri.EscapeDataString(user.Email!)}?secret={key}&issuer=ScanBack&digits=6"
        };
    }
}
