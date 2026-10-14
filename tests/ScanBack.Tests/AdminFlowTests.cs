using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ScanBack.Domain;
using ScanBack.Infrastructure;

namespace ScanBack.Tests;

[Collection(SqlWebCollection.Name)]
public sealed class AdminFlowTests(SqlWebFixture fixture)
{
    [Fact]
    public async Task Normal_rider_cannot_access_administration()
    {
        var rider = await fixture.SeedRider();
        using var client = fixture.Client();
        await SqlWebFixture.Login(client, rider.Email);
        var response = await client.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("access-denied", response.Headers.Location?.OriginalString);
        var denial = await client.GetAsync(response.Headers.Location);
        Assert.Equal(HttpStatusCode.Forbidden, denial.StatusCode);
    }

    [Fact]
    public async Task Administrator_role_without_MFA_must_set_up_authenticator_before_administration()
    {
        var rider = await fixture.SeedRider();
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.FindByIdAsync(rider.UserId);
            Assert.True((await users.AddToRoleAsync(user!, "Administrator")).Succeeded);
        }
        using var client = fixture.Client();
        await SqlWebFixture.Login(client, rider.Email);
        var response = await client.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/security", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Administrator_metadata_does_not_disclose_medical_records_GPS_or_access_tokens()
    {
        var rider = await fixture.SeedRider(PublicFields.Name | PublicFields.Allergies);
        var incident = await fixture.SeedIncident(rider);
        await fixture.InDatabase(async db =>
        {
            db.OutboxMessages.Add(new OutboxMessage { RiderProfileId = rider.Profile.Id, IncidentId = incident.IncidentId, ToEmail = rider.Contact.Email, Subject = "Private notification", Body = "Private body marker with invitation " + incident.Token });
            await db.SaveChangesAsync();
        });
        var session = await AdministratorSession();
        using var client = session.Client;
        foreach (var path in new[] { "/admin", "/admin/accounts", "/admin/identifiers", "/admin/incidents", "/admin/audit", "/admin/settings" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(rider.Profile.Allergies!, html);
            Assert.DoesNotContain(rider.Profile.Conditions!, html);
            Assert.DoesNotContain(rider.Profile.Medications!, html);
            Assert.DoesNotContain("37.9838", html);
            Assert.DoesNotContain("23.7275", html);
            Assert.DoesNotContain(rider.Token, html);
            Assert.DoesNotContain(incident.Token, html);
            Assert.DoesNotContain("Private body marker", html);
        }
    }

    [Fact]
    public async Task Administrator_cannot_deactivate_self_and_status_updates_require_antiforgery()
    {
        var session = await AdministratorSession();
        using var client = session.Client;
        var path = "/admin/accounts/" + session.Rider.UserId + "/status";
        var missingCsrf = await client.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string> { ["isActive"] = "false" }));
        Assert.Equal(HttpStatusCode.BadRequest, missingCsrf.StatusCode);
        var csrf = await SqlWebFixture.Antiforgery(client, "/admin/accounts");
        var response = await client.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = csrf, ["isActive"] = "false" }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.True(await fixture.InDatabase(db => db.Users.Where(x => x.Id == session.Rider.UserId).Select(x => x.IsActive).SingleAsync()));
        Assert.True(await fixture.InDatabase(db => db.AuditEvents.AnyAsync(x => x.ActorId == session.Rider.UserId && x.Action == "Admin.SelfDeactivationDenied")));
    }

    [Fact]
    public async Task Invalid_retention_access_and_cooldown_settings_are_not_persisted()
    {
        var session = await AdministratorSession();
        using var client = session.Client;
        var original = await fixture.InDatabase(db => db.PlatformSettings.AsNoTracking().SingleAsync());
        var csrf = await SqlWebFixture.Antiforgery(client, "/admin/settings");
        var response = await client.PostAsync("/admin/settings", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrf,
            ["IncidentRetentionDays"] = "0",
            ["IncidentAccessHours"] = "0",
            ["NotificationCooldownMinutes"] = "0",
            ["NotificationsEnabled"] = "false"
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var saved = await fixture.InDatabase(db => db.PlatformSettings.AsNoTracking().SingleAsync());
        Assert.Equal(original.IncidentRetentionDays, saved.IncidentRetentionDays);
        Assert.Equal(original.IncidentAccessHours, saved.IncidentAccessHours);
        Assert.Equal(original.NotificationCooldownMinutes, saved.NotificationCooldownMinutes);
        Assert.Equal(original.NotificationsEnabled, saved.NotificationsEnabled);
    }

    [Fact]
    public async Task Administrator_account_deactivation_immediately_blocks_existing_rider_sessions_and_public_stickers()
    {
        var rider = await fixture.SeedRider(PublicFields.Name);
        using var riderClient = fixture.Client();
        await SqlWebFixture.Login(riderClient, rider.Email);
        Assert.Equal(HttpStatusCode.OK, (await riderClient.GetAsync("/profile")).StatusCode);
        var session = await AdministratorSession();
        using var admin = session.Client;
        var csrf = await SqlWebFixture.Antiforgery(admin, "/admin/accounts");
        var response = await admin.PostAsync("/admin/accounts/" + rider.UserId + "/status", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrf,
            ["isActive"] = "false"
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.False(await fixture.InDatabase(db => db.Users.Where(x => x.Id == rider.UserId).Select(x => x.IsActive).SingleAsync()));
        Assert.Equal(HttpStatusCode.Redirect, (await riderClient.GetAsync("/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await riderClient.GetAsync("/e/" + rider.Token)).StatusCode);
    }

    private async Task<(HttpClient Client, RiderSeed Rider)> AdministratorSession()
    {
        var rider = await fixture.SeedRider();
        using var scope = fixture.Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = (await users.FindByIdAsync(rider.UserId))!;
        Assert.True((await users.AddToRoleAsync(user, "Administrator")).Succeeded);
        Assert.True((await users.SetTwoFactorEnabledAsync(user, true)).Succeeded);
        var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<ApplicationUser>>();
        var principal = await signIn.CreateUserPrincipalAsync(user);
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim("amr", "mfa"));
        var properties = new AuthenticationProperties { IssuedUtc = DateTimeOffset.UtcNow, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) };
        var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(IdentityConstants.ApplicationScheme);
        var cookie = options.TicketDataFormat.Protect(new AuthenticationTicket(principal, properties, IdentityConstants.ApplicationScheme));
        var client = fixture.Client();
        // This is a seeded, server-signed Identity session for focused admin
        // authorization tests. Registration/login are separately exercised over
        // HTTP; no alternative authentication scheme or policy is installed.
        client.DefaultRequestHeaders.Add("Cookie", options.Cookie.Name + "=" + cookie);
        return (client, rider);
    }
}
