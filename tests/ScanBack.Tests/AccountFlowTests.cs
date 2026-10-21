using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ScanBack.Domain;
using ScanBack.Infrastructure;
using SkiaSharp;

namespace ScanBack.Tests;

[Collection(SqlWebCollection.Name)]
public sealed class AccountFlowTests(SqlWebFixture fixture)
{
    [Fact]
    public async Task Registration_requires_email_confirmation_before_login_and_starts_with_private_health()
    {
        using var client = fixture.Client();
        var email = "registration-" + Guid.NewGuid().ToString("N") + "@example.test";
        var csrf = await SqlWebFixture.Antiforgery(client, "/account/register");
        var register = await client.PostAsync("/account/register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrf,
            ["Email"] = email,
            ["Password"] = SqlWebFixture.Password,
            ["ConfirmPassword"] = SqlWebFixture.Password,
            ["FullName"] = "New rider",
            ["PrivacyAccepted"] = "true",
            ["privacyPolicyVersion"] = SqlWebFixture.PolicyVersion
        }));
        Assert.Equal(HttpStatusCode.Redirect, register.StatusCode);
        var user = await fixture.InDatabase(db => db.Users.AsNoTracking().SingleAsync(x => x.Email == email));
        Assert.False(user.EmailConfirmed);
        var profile = await fixture.InDatabase(db => db.RiderProfiles.AsNoTracking().SingleAsync(x => x.UserId == user.Id));
        Assert.Equal(PublicFields.None, profile.PublicFields);
        Assert.False(profile.HealthConsent);
        Assert.Equal(1, await fixture.InDatabase(db => db.ConsentRecords.CountAsync(x => x.UserId == user.Id && x.Action == "PrivacyNoticeAccepted")));

        csrf = await SqlWebFixture.Antiforgery(client, "/account/login");
        var unconfirmedLogin = await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrf,
            ["Email"] = email,
            ["Password"] = SqlWebFixture.Password
        }));
        Assert.Equal(HttpStatusCode.OK, unconfirmedLogin.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/profile")).StatusCode);

        var confirmation = await fixture.InDatabase(db => db.OutboxMessages.AsNoTracking().SingleAsync(x => x.ToEmail == email && x.Kind == "Confirmation"));
        var link = Regex.Match(confirmation.Body, @"http://localhost/account/confirm-email\?[^\s]+").Value;
        Assert.NotEmpty(link);
        var preview = await client.GetAsync(link);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.False(await fixture.InDatabase(db => db.Users.Where(x => x.Id == user.Id).Select(x => x.EmailConfirmed).SingleAsync()));
        var previewHtml = await preview.Content.ReadAsStringAsync();
        var confirmed = await client.PostAsync("/account/confirm-email", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = SqlWebFixture.Hidden(previewHtml, "__RequestVerificationToken"),
            ["userId"] = SqlWebFixture.Hidden(previewHtml, "userId"),
            ["code"] = SqlWebFixture.Hidden(previewHtml, "code")
        }));
        Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
        Assert.True(await fixture.InDatabase(db => db.Users.Where(x => x.Id == user.Id).Select(x => x.EmailConfirmed).SingleAsync()));
        await SqlWebFixture.Login(client, email);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/profile")).StatusCode);
    }

    [Fact]
    public async Task Changed_privacy_notice_requires_review_before_registration_or_health_consent_can_be_saved()
    {
        using var newcomer = fixture.Client();
        var email = "stale-notice-" + Guid.NewGuid().ToString("N") + "@example.test";
        var csrf = await SqlWebFixture.Antiforgery(newcomer, "/account/register");
        var registration = await newcomer.PostAsync("/account/register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrf, ["Email"] = email,
            ["Password"] = SqlWebFixture.Password, ["ConfirmPassword"] = SqlWebFixture.Password,
            ["FullName"] = "Stale notice", ["PrivacyAccepted"] = "true", ["privacyPolicyVersion"] = "superseded-notice"
        }));
        Assert.Equal(HttpStatusCode.OK, registration.StatusCode);
        Assert.False(await fixture.InDatabase(db => db.Users.AnyAsync(x => x.Email == email)));
        var rider = await fixture.SeedRider(healthConsent: false);
        using var owner = fixture.Client();
        await SqlWebFixture.Login(owner, rider.Email);
        csrf = await SqlWebFixture.Antiforgery(owner, "/profile/edit");
        var edit = await owner.PostAsync("/profile/edit", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrf, ["FullName"] = rider.Profile.FullName,
            ["HealthConsent"] = "true", ["Allergies"] = "Consent must be current", ["consentPolicyVersion"] = "superseded-notice"
        }));
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.False(await fixture.InDatabase(db => db.RiderProfiles.Where(x => x.Id == rider.Profile.Id).Select(x => x.HealthConsent).SingleAsync()));
        Assert.False(await fixture.InDatabase(db => db.ConsentRecords.AnyAsync(x => x.UserId == rider.UserId && x.Action == "HealthConsentGranted")));
    }

    [Fact]
    public async Task Rider_cannot_read_or_edit_another_riders_contact_or_export()
    {
        var owner = await fixture.SeedRider();
        var victim = await fixture.SeedRider();
        using var client = fixture.Client();
        await SqlWebFixture.Login(client, owner.Email);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/contacts/edit/" + victim.Contact.Id)).StatusCode);
        var csrf = await SqlWebFixture.Antiforgery(client, "/contacts/create");
        var edit = await client.PostAsync("/contacts/edit/" + victim.Contact.Id, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrf,
            ["Name"] = "Unauthorized change",
            ["Relationship"] = "Friend",
            ["Email"] = "attacker@example.test"
        }));
        Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode);
        Assert.Equal(victim.Contact.Name, await fixture.InDatabase(db => db.EmergencyContacts.Where(x => x.Id == victim.Contact.Id).Select(x => x.Name).SingleAsync()));

        var export = await client.GetAsync("/profile/export?userId=" + victim.UserId);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        var data = await export.Content.ReadAsStringAsync();
        Assert.Contains(owner.Email, data);
        Assert.DoesNotContain(victim.Email, data);
        Assert.DoesNotContain(victim.Profile.FullName, data);
        Assert.DoesNotContain(owner.Token, data);
        Assert.DoesNotContain("TokenHash", data);
        Assert.DoesNotContain("ProtectedToken", data);
        Assert.DoesNotContain("PasswordHash", data);
        Assert.Equal("no-store", export.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Profile_edit_ignores_supplied_foreign_owner_identifiers()
    {
        var owner = await fixture.SeedRider();
        var victim = await fixture.SeedRider();
        using var client = fixture.Client();
        await SqlWebFixture.Login(client, owner.Email);
        var csrf = await SqlWebFixture.Antiforgery(client, "/profile/edit");
        var response = await client.PostAsync("/profile/edit", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrf,
            ["FullName"] = "Own profile updated",
            ["UserId"] = victim.UserId,
            ["Id"] = victim.Profile.Id.ToString(),
            ["HealthConsent"] = "true",
            ["consentPolicyVersion"] = SqlWebFixture.PolicyVersion
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("Own profile updated", await fixture.InDatabase(db => db.RiderProfiles.Where(x => x.UserId == owner.UserId).Select(x => x.FullName).SingleAsync()));
        Assert.Equal(victim.Profile.FullName, await fixture.InDatabase(db => db.RiderProfiles.Where(x => x.UserId == victim.UserId).Select(x => x.FullName).SingleAsync()));
    }

    [Fact]
    public async Task Health_information_cannot_be_saved_without_explicit_consent()
    {
        var rider = await fixture.SeedRider(healthConsent: false);
        using var client = fixture.Client();
        await SqlWebFixture.Login(client, rider.Email);
        var csrf = await SqlWebFixture.Antiforgery(client, "/profile/edit");
        var response = await client.PostAsync("/profile/edit", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrf,
            ["FullName"] = "Disallowed edit",
            ["Allergies"] = "New sensitive information",
            ["HealthConsent"] = "false",
            ["VisibleFields"] = "Allergies"
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(rider.Profile.FullName, await fixture.InDatabase(db => db.RiderProfiles.Where(x => x.Id == rider.Profile.Id).Select(x => x.FullName).SingleAsync()));
    }

    [Fact]
    public async Task Consent_withdrawal_deletes_health_fields_and_their_public_visibility()
    {
        var rider = await fixture.SeedRider(PublicFields.Name | PublicFields.Allergies | PublicFields.Medications);
        using var client = fixture.Client();
        await SqlWebFixture.Login(client, rider.Email);
        var csrf = await SqlWebFixture.Antiforgery(client, "/profile/edit");
        var response = await client.PostAsync("/profile/withdraw-consent", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = csrf }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var profile = await fixture.InDatabase(db => db.RiderProfiles.AsNoTracking().SingleAsync(x => x.Id == rider.Profile.Id));
        Assert.False(profile.HealthConsent);
        Assert.Null(profile.ConsentAt);
        Assert.Null(profile.BloodType);
        Assert.Null(profile.Allergies);
        Assert.Null(profile.Conditions);
        Assert.Null(profile.Medications);
        Assert.Null(profile.OtherInformation);
        Assert.Null(profile.Instructions);
        Assert.Equal(PublicFields.Name, profile.PublicFields);
        Assert.True(await fixture.InDatabase(db => db.ConsentRecords.AnyAsync(x => x.UserId == rider.UserId && x.Action == "HealthConsentWithdrawn")));
        var html = await client.GetStringAsync("/e/" + rider.Token);
        Assert.DoesNotContain(rider.Profile.Allergies!, html);
        Assert.Contains(rider.Profile.FullName, html);
    }

    [Fact]
    public async Task Contact_email_verification_requires_POST_and_email_changes_revoke_old_access()
    {
        var rider = await fixture.SeedRider();
        using var client = fixture.Client();
        await SqlWebFixture.Login(client, rider.Email);
        var csrf = await SqlWebFixture.Antiforgery(client, "/contacts/create");
        var email = "new-contact-" + Guid.NewGuid().ToString("N") + "@example.test";
        var response = await client.PostAsync("/contacts/create", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrf,
            ["Name"] = "New contact",
            ["Relationship"] = "Family",
            ["Email"] = email,
            ["Phone"] = "+306900000003",
            ["NotifyEnabled"] = "true",
            ["PublicPhone"] = "true"
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var contact = await fixture.InDatabase(db => db.EmergencyContacts.AsNoTracking().SingleAsync(x => x.Email == email));
        Assert.False(contact.IsVerified);
        var verification = await fixture.InDatabase(db => db.OutboxMessages.AsNoTracking().SingleAsync(x => x.EmergencyContactId == contact.Id && x.Kind == "ContactVerification"));
        var link = Regex.Match(verification.Body, @"http://localhost/contacts/verify/[^\s]+").Value;
        using var contactClient = fixture.Client();
        var confirmationCsrf = await SqlWebFixture.Antiforgery(contactClient, link);
        Assert.False(await fixture.InDatabase(db => db.EmergencyContacts.Where(x => x.Id == contact.Id).Select(x => x.IsVerified).SingleAsync()));
        response = await contactClient.PostAsync(link, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = confirmationCsrf, ["PolicyVersion"] = SqlWebFixture.PolicyVersion,
            ["AcceptNotifications"] = "true"
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await fixture.InDatabase(db => db.EmergencyContacts.Where(x => x.Id == contact.Id).Select(x => x.IsVerified).SingleAsync()));

        var incident = await fixture.SeedIncident(rider);
        csrf = await SqlWebFixture.Antiforgery(client, "/contacts/edit/" + rider.Contact.Id);
        response = await client.PostAsync("/contacts/edit/" + rider.Contact.Id, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrf,
            ["Name"] = rider.Contact.Name,
            ["Relationship"] = rider.Contact.Relationship,
            ["Email"] = "changed-" + Guid.NewGuid().ToString("N") + "@example.test",
            ["Phone"] = rider.Contact.Phone,
            ["NotifyEnabled"] = "true",
            ["PublicPhone"] = "true"
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.False(await fixture.InDatabase(db => db.EmergencyContacts.Where(x => x.Id == rider.Contact.Id).Select(x => x.IsVerified).SingleAsync()));
        Assert.True(await fixture.InDatabase(db => db.IncidentAccesses.Where(x => x.Id == incident.AccessId).Select(x => x.IsRevoked).SingleAsync()));
        Assert.Equal(HttpStatusCode.NotFound, (await contactClient.GetAsync("/incident/join/" + incident.Token)).StatusCode);
    }

    [Fact]
    public async Task Account_deletion_removes_related_personal_data_without_affecting_other_riders()
    {
        var rider = await fixture.SeedRider();
        var other = await fixture.SeedRider();
        var incident = await fixture.SeedIncident(rider);
        await fixture.InDatabase(async db =>
        {
            db.OutboxMessages.Add(new OutboxMessage { RiderProfileId = rider.Profile.Id, EmergencyContactId = rider.Contact.Id, IncidentId = incident.IncidentId, ToEmail = rider.Contact.Email, Subject = "Test notification", Body = "Sensitive invitation body" });
            db.ConsentRecords.Add(new ConsentRecord { UserId = rider.UserId });
            await db.SaveChangesAsync();
        });
        using var client = fixture.Client();
        await SqlWebFixture.Login(client, rider.Email);
        var csrf = await SqlWebFixture.Antiforgery(client, "/profile/delete");
        var response = await client.PostAsync("/profile/delete", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrf,
            ["Password"] = SqlWebFixture.Password,
            ["ConfirmDeletion"] = "true"
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.False(await fixture.InDatabase(db => db.Users.AnyAsync(x => x.Id == rider.UserId)));
        Assert.False(await fixture.InDatabase(db => db.RiderProfiles.AnyAsync(x => x.Id == rider.Profile.Id)));
        Assert.False(await fixture.InDatabase(db => db.EmergencyContacts.AnyAsync(x => x.RiderProfileId == rider.Profile.Id)));
        Assert.False(await fixture.InDatabase(db => db.EmergencyIdentifiers.AnyAsync(x => x.RiderProfileId == rider.Profile.Id)));
        Assert.False(await fixture.InDatabase(db => db.Incidents.AnyAsync(x => x.RiderProfileId == rider.Profile.Id)));
        Assert.False(await fixture.InDatabase(db => db.IncidentAccesses.AnyAsync(x => x.IncidentId == incident.IncidentId)));
        Assert.False(await fixture.InDatabase(db => db.OutboxMessages.AnyAsync(x => x.RiderProfileId == rider.Profile.Id)));
        Assert.False(await fixture.InDatabase(db => db.ConsentRecords.AnyAsync(x => x.UserId == rider.UserId)));
        Assert.True(await fixture.InDatabase(db => db.Users.AnyAsync(x => x.Id == other.UserId)));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/e/" + rider.Token)).StatusCode);
    }

    [Fact]
    public async Task Medical_values_are_encrypted_in_SQL_Server_and_round_trip_through_EF()
    {
        var rider = await fixture.SeedRider();
        var stored = await fixture.InDatabase(async db =>
        {
            await db.Database.OpenConnectionAsync();
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT Allergies FROM RiderProfiles WHERE Id = @id";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@id";
            parameter.Value = rider.Profile.Id;
            command.Parameters.Add(parameter);
            return (string)(await command.ExecuteScalarAsync())!;
        });
        Assert.DoesNotContain(rider.Profile.Allergies!, stored);
        Assert.StartsWith("CfDJ", stored);
        Assert.Equal(rider.Profile.Allergies, await fixture.InDatabase(db => db.RiderProfiles.Where(x => x.Id == rider.Profile.Id).Select(x => x.Allergies).SingleAsync()));
    }

    [Fact]
    public async Task Rider_photo_upload_is_private_until_selected_for_public_visibility_and_can_be_removed()
    {
        var rider = await fixture.SeedRider();
        using var owner = fixture.Client();
        using var anonymous = fixture.Client();
        await SqlWebFixture.Login(owner, rider.Email);
        var csrf = await SqlWebFixture.Antiforgery(owner, "/profile/edit");
        using var bitmap = new SKBitmap(2, 2);
        bitmap.Erase(SKColors.Blue);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 90);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(csrf), "__RequestVerificationToken" },
            { new StringContent(rider.Profile.FullName), "FullName" },
            { new StringContent("true"), "HealthConsent" },
            { new StringContent(SqlWebFixture.PolicyVersion), "consentPolicyVersion" },
            { new ByteArrayContent(data.ToArray()), "Photo", "camera.png" }
        };
        Assert.Equal(HttpStatusCode.Redirect, (await owner.PostAsync("/profile/edit", form)).StatusCode);
        var filename = await fixture.InDatabase(db => db.RiderProfiles.Where(x => x.Id == rider.Profile.Id).Select(x => x.PhotoFileName).SingleAsync());
        Assert.NotNull(filename);
        Assert.DoesNotContain("camera", filename);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/profile/photo")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/profile/photo/" + rider.Token)).StatusCode);

        csrf = await SqlWebFixture.Antiforgery(owner, "/profile/edit");
        var visibility = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrf,
            ["FullName"] = rider.Profile.FullName,
            ["HealthConsent"] = "true",
            ["consentPolicyVersion"] = SqlWebFixture.PolicyVersion,
            ["VisibleFields"] = "Photo"
        };
        Assert.Equal(HttpStatusCode.Redirect, (await owner.PostAsync("/profile/edit", new FormUrlEncodedContent(visibility))).StatusCode);
        var publicPhoto = await anonymous.GetAsync("/profile/photo/" + rider.Token);
        Assert.Equal(HttpStatusCode.OK, publicPhoto.StatusCode);
        Assert.Equal("image/png", publicPhoto.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", publicPhoto.Headers.CacheControl?.ToString());

        visibility.Remove("VisibleFields");
        visibility["RemovePhoto"] = "true";
        Assert.Equal(HttpStatusCode.Redirect, (await owner.PostAsync("/profile/edit", new FormUrlEncodedContent(visibility))).StatusCode);
        Assert.Null(await fixture.InDatabase(db => db.RiderProfiles.Where(x => x.Id == rider.Profile.Id).Select(x => x.PhotoFileName).SingleAsync()));
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/profile/photo/" + rider.Token)).StatusCode);
    }
}
