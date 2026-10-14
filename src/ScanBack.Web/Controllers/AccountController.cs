using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using ScanBack.Domain;
using ScanBack.Infrastructure;
using ScanBack.Web.Models;
using ScanBack.Web.Services;

namespace ScanBack.Web.Controllers;

[Route("account"), EnableRateLimiting("authentication")]
public sealed class AccountController(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn, AppDbContext db, SiteLinks links) : Controller
{
    [HttpGet("register"), AllowAnonymous]
    public IActionResult Register() => View(new RegisterModel());

    [HttpPost("register"), AllowAnonymous, ValidateAntiForgeryToken]
    public async Task<IActionResult> Register(RegisterModel model)
    {
        if (!model.PrivacyAccepted) ModelState.AddModelError(nameof(model.PrivacyAccepted), "Read and accept the privacy notice to create an account.");
        if (!ModelState.IsValid) return View(model);
        var email = model.Email.Trim();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var user = new ApplicationUser { UserName = email, Email = email, IsActive = true };
        var result = await users.CreateAsync(user, model.Password);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors) ModelState.AddModelError("", error.Description);
            return View(model);
        }
        var profile = new RiderProfile { UserId = user.Id, FullName = model.FullName.Trim() };
        db.RiderProfiles.Add(profile);
        db.ConsentRecords.Add(new ConsentRecord { UserId = user.Id, Action = "PrivacyNoticeAccepted" });
        db.AuditEvents.Add(new AuditEvent { ActorId = user.Id, Action = "AccountCreated", EntityId = profile.Id.ToString() });
        await QueueConfirmation(user, profile.Id);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        TempData["Success"] = "Account created. Check your email and confirm your address before signing in.";
        return RedirectToAction(nameof(Login));
    }

    [HttpGet("login"), HttpGet("/login"), AllowAnonymous]
    public IActionResult Login(string? returnUrl = null) => View(new LoginModel { ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null });

    [HttpPost("login"), HttpPost("/login"), AllowAnonymous, ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginModel model)
    {
        if (!ModelState.IsValid) return View(model);
        var user = await users.FindByEmailAsync(model.Email.Trim());
        if (user is null || !user.IsActive)
        {
            ModelState.AddModelError("", "Unable to sign in. Check your details and confirm your email address.");
            return View(model);
        }
        var result = await signIn.PasswordSignInAsync(user, model.Password, model.RememberMe, lockoutOnFailure: true);
        if (result.RequiresTwoFactor) return RedirectToAction(nameof(TwoFactor), new { returnUrl = Url.IsLocalUrl(model.ReturnUrl) ? model.ReturnUrl : null, rememberMe = model.RememberMe });
        if (!result.Succeeded)
        {
            ModelState.AddModelError("", result.IsLockedOut ? "This account is temporarily locked. Try again later." : "Unable to sign in. Check your details and confirm your email address.");
            return View(model);
        }
        return Url.IsLocalUrl(model.ReturnUrl) ? LocalRedirect(model.ReturnUrl!) : RedirectToAction("Index", "Profile");
    }

    [HttpGet("two-factor"), AllowAnonymous]
    public async Task<IActionResult> TwoFactor(string? returnUrl = null, bool rememberMe = false)
    {
        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is not { IsActive: true }) return RedirectToAction(nameof(Login));
        return View(new TwoFactorModel { ReturnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null, RememberMe = rememberMe });
    }

    [HttpPost("two-factor"), AllowAnonymous, ValidateAntiForgeryToken]
    public async Task<IActionResult> TwoFactor(TwoFactorModel model)
    {
        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is not { IsActive: true })
        {
            await signIn.SignOutAsync();
            return RedirectToAction(nameof(Login));
        }
        if (!ModelState.IsValid) return View(model);
        var code = model.Code.Replace(" ", "").Replace("-", "");
        var result = model.UseRecoveryCode
            ? await signIn.TwoFactorRecoveryCodeSignInAsync(model.Code.Replace(" ", ""))
            : await signIn.TwoFactorAuthenticatorSignInAsync(code, model.RememberMe, rememberClient: false);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(nameof(model.Code), result.IsLockedOut ? "This account is temporarily locked. Try again later." : "The verification code is invalid. Try a new code or use a recovery code.");
            return View(model);
        }
        return Url.IsLocalUrl(model.ReturnUrl) ? LocalRedirect(model.ReturnUrl!) : RedirectToAction("Index", "Profile");
    }

    [HttpPost("logout"), HttpPost("/logout"), Authorize, ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await signIn.SignOutAsync();
        return RedirectToAction("Index", "Home");
    }

    [HttpGet("confirm-email"), AllowAnonymous]
    public async Task<IActionResult> ConfirmEmail(string? userId, string? code)
    {
        if (string.IsNullOrWhiteSpace(userId) || userId.Length > 450 || string.IsNullOrWhiteSpace(code) || code.Length > 4096) return View("Message", "This confirmation link is invalid or expired.");
        var user = await users.FindByIdAsync(userId);
        if (user is null || !user.IsActive) return View("Message", "This confirmation link is invalid or expired.");
        try
        {
            var result = await users.ConfirmEmailAsync(user, Decode(code));
            return View("Message", result.Succeeded ? "Your email address is confirmed. You can now sign in." : "This confirmation link is invalid or expired. Request another confirmation email.");
        }
        catch (FormatException) { return View("Message", "This confirmation link is invalid or expired."); }
    }

    [HttpGet("resend-confirmation"), AllowAnonymous]
    public IActionResult ResendConfirmation() => View("Email", new AccountEmailModel());

    [HttpPost("resend-confirmation"), AllowAnonymous, ValidateAntiForgeryToken]
    public async Task<IActionResult> ResendConfirmation(AccountEmailModel model)
    {
        if (!ModelState.IsValid) return View("Email", model);
        var user = await users.FindByEmailAsync(model.Email.Trim());
        if (user is { IsActive: true, EmailConfirmed: false } && !await RecentlyQueued(user.Email!, "Confirmation"))
        {
            var profileId = await db.RiderProfiles.Where(x => x.UserId == user.Id).Select(x => (Guid?)x.Id).FirstOrDefaultAsync();
            await QueueConfirmation(user, profileId);
            await db.SaveChangesAsync();
        }
        return View("Message", "If an account needs email confirmation, a new link will be emailed. Please check your inbox and spam folder.");
    }

    [HttpGet("forgot-password"), HttpGet("/forgot-password"), AllowAnonymous]
    public IActionResult ForgotPassword() => View("Email", new AccountEmailModel());

    [HttpPost("forgot-password"), HttpPost("/forgot-password"), AllowAnonymous, ValidateAntiForgeryToken]
    public async Task<IActionResult> ForgotPassword(AccountEmailModel model)
    {
        if (!ModelState.IsValid) return View("Email", model);
        var user = await users.FindByEmailAsync(model.Email.Trim());
        if (user is { IsActive: true, EmailConfirmed: true } && !await RecentlyQueued(user.Email!, "PasswordReset"))
        {
            var code = Encode(await users.GeneratePasswordResetTokenAsync(user));
            var url = links.Absolute($"/account/reset-password?email={Uri.EscapeDataString(user.Email!)}&code={Uri.EscapeDataString(code)}");
            var profileId = await db.RiderProfiles.Where(x => x.UserId == user.Id).Select(x => (Guid?)x.Id).FirstOrDefaultAsync();
            db.OutboxMessages.Add(new OutboxMessage { ToEmail = user.Email!, RiderProfileId = profileId, Subject = "ScanBack — reset your password", Kind = "PasswordReset", Body = $"A password reset was requested for your ScanBack account.\n\nReset your password: {url}\n\nIf you did not request this, ignore this message." });
            await db.SaveChangesAsync();
        }
        return View("Message", "If a confirmed account matches that address, a password reset link will be emailed.");
    }

    [HttpGet("reset-password"), HttpGet("/reset-password"), AllowAnonymous]
    public IActionResult ResetPassword(string? email, string? code) => View(new ResetPasswordModel { Email = email ?? "", Code = code ?? "" });

    [HttpPost("reset-password"), HttpPost("/reset-password"), AllowAnonymous, ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(ResetPasswordModel model)
    {
        if (!ModelState.IsValid) return View(model);
        var user = await users.FindByEmailAsync(model.Email.Trim());
        if (user is not { IsActive: true, EmailConfirmed: true }) return View("Message", "The link is invalid or expired. Request another password reset.");
        try
        {
            var result = await users.ResetPasswordAsync(user, Decode(model.Code), model.Password);
            if (!result.Succeeded)
            {
                foreach (var error in result.Errors) ModelState.AddModelError("", error.Description);
                return View(model);
            }
        }
        catch (FormatException)
        {
            ModelState.AddModelError("", "The link is invalid or expired. Request another password reset.");
            return View(model);
        }
        db.AuditEvents.Add(new AuditEvent { ActorId = user.Id, Action = "PasswordReset" });
        await db.SaveChangesAsync();
        TempData["Success"] = "Password changed. Sign in with your new password.";
        return RedirectToAction(nameof(Login));
    }

    [HttpGet("access-denied"), AllowAnonymous]
    public IActionResult AccessDenied() { Response.StatusCode = 403; return View("Message", "You do not have permission to access this page."); }

    private Task<bool> RecentlyQueued(string email, string kind)
    {
        var since = DateTimeOffset.UtcNow.AddMinutes(-5);
        return db.OutboxMessages.AnyAsync(x => x.ToEmail == email && x.Kind == kind && x.CreatedAt > since);
    }
    private async Task QueueConfirmation(ApplicationUser user, Guid? profileId)
    {
        var code = Encode(await users.GenerateEmailConfirmationTokenAsync(user));
        var url = links.Absolute($"/account/confirm-email?userId={Uri.EscapeDataString(user.Id)}&code={Uri.EscapeDataString(code)}");
        db.OutboxMessages.Add(new OutboxMessage { ToEmail = user.Email!, RiderProfileId = profileId, Subject = "ScanBack — confirm your email", Kind = "Confirmation", Body = $"Confirm your ScanBack email address to sign in:\n\n{url}\n\nIf you did not create this account, ignore this message." });
    }
    private static string Encode(string value) => WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(value));
    private static string Decode(string value) => Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(value));
}
