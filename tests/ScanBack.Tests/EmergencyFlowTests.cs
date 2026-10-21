using System.Net;
using Microsoft.EntityFrameworkCore;
using ScanBack.Domain;

namespace ScanBack.Tests;

[Collection(SqlWebCollection.Name)]
public sealed class EmergencyFlowTests(SqlWebFixture fixture)
{
    [Fact]
    public async Task Anonymous_emergency_page_exposes_only_selected_fields_and_verified_public_phone_contacts()
    {
        var rider = await fixture.SeedRider(PublicFields.Name | PublicFields.Allergies | PublicFields.Contacts);
        using var client = fixture.Client();
        var response = await client.GetAsync("/e/" + rider.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains(rider.Profile.FullName, html);
        Assert.Contains(rider.Profile.Allergies!, html);
        Assert.Contains(rider.Contact.Name, html);
        Assert.Contains("tel:112", html);
        Assert.Contains("tel:166", html);
        Assert.DoesNotContain(rider.Profile.Conditions!, html);
        Assert.DoesNotContain(rider.Profile.Medications!, html);
        Assert.DoesNotContain(rider.Profile.OtherInformation!, html);
        Assert.DoesNotContain(rider.Profile.Instructions!, html);
        Assert.DoesNotContain(rider.Contact.Email, html);
        Assert.DoesNotContain(rider.UnverifiedContact.Name, html);
        Assert.DoesNotContain("1990-03-18", html);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal(0, await fixture.InDatabase(db => db.Incidents.CountAsync(x => x.RiderProfileId == rider.Profile.Id)));
        Assert.Equal(0, await fixture.InDatabase(db => db.OutboxMessages.CountAsync(x => x.RiderProfileId == rider.Profile.Id)));
    }

    [Fact]
    public async Task Public_visibility_flags_cannot_publish_health_without_processing_consent()
    {
        var rider = await fixture.SeedRider(PublicFields.Allergies | PublicFields.Conditions | PublicFields.Medications, healthConsent: false);
        using var client = fixture.Client();
        var response = await client.GetAsync("/e/" + rider.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(rider.Profile.FullName, html);
        Assert.DoesNotContain(rider.Profile.Allergies!, html);
        Assert.DoesNotContain(rider.Profile.Conditions!, html);
        Assert.DoesNotContain(rider.Profile.Medications!, html);
        Assert.Contains(rider.Identifier.Label, html);
    }

    [Fact]
    public async Task Invalid_and_revoked_stickers_have_no_emergency_profile()
    {
        var rider = await fixture.SeedRider(PublicFields.Name);
        await fixture.InDatabase(async db =>
        {
            var sticker = await db.EmergencyIdentifiers.FindAsync(rider.Identifier.Id);
            sticker!.IsActive = false;
            sticker.RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        });
        using var client = fixture.Client();
        foreach (var token in new[] { rider.Token, "guessable-123", ScanBack.Infrastructure.TokenService.Generate() })
        {
            var response = await client.GetAsync("/e/" + token);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.DoesNotContain(rider.Profile.FullName, await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task Missing_antiforgery_token_cannot_create_an_incident_or_notification()
    {
        var rider = await fixture.SeedRider();
        using var client = fixture.Client();
        var response = await client.PostAsync("/e/" + rider.Token + "/report", ReportContent(Guid.NewGuid().ToString(), null));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await fixture.InDatabase(db => db.Incidents.CountAsync(x => x.RiderProfileId == rider.Profile.Id)));
        Assert.Equal(0, await fixture.InDatabase(db => db.OutboxMessages.CountAsync(x => x.RiderProfileId == rider.Profile.Id)));
    }

    [Fact]
    public async Task Greek_locale_GPS_report_is_durable_idempotent_and_notifies_only_confirmed_contacts()
    {
        var rider = await fixture.SeedRider(PublicFields.Name | PublicFields.Allergies);
        using var client = fixture.Client();
        client.DefaultRequestHeaders.Add("Cookie", ".AspNetCore.Culture=c=el|uic=el");
        var csrf = await SqlWebFixture.Antiforgery(client, "/e/" + rider.Token);
        var submission = Guid.NewGuid().ToString();
        var response = await client.PostAsync("/e/" + rider.Token + "/report", ReportContent(submission, csrf, "37.983800", "23.727500"));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/incident/", response.Headers.Location?.OriginalString);
        var incident = await fixture.InDatabase(db => db.Incidents.AsNoTracking().SingleAsync(x => x.RiderProfileId == rider.Profile.Id));
        Assert.Equal(37.9838, incident.Latitude);
        Assert.Equal(23.7275, incident.Longitude);
        Assert.Equal(submission, incident.SubmissionKey);
        var emails = await fixture.InDatabase(db => db.OutboxMessages.AsNoTracking().Where(x => x.IncidentId == incident.Id).ToListAsync());
        var email = Assert.Single(emails);
        Assert.Equal(rider.Contact.Email, email.ToEmail);
        Assert.Contains("unverified", email.Body);
        Assert.Contains("/incident/join/", email.Body);
        Assert.DoesNotContain(rider.Profile.Medications!, email.Body);
        Assert.Null(email.AcceptedAt);
        Assert.Equal(2, await fixture.InDatabase(db => db.IncidentAccesses.CountAsync(x => x.IncidentId == incident.Id)));

        var repeated = await client.PostAsync("/e/" + rider.Token + "/report", ReportContent(submission, csrf, "37.983800", "23.727500"));
        Assert.Equal(HttpStatusCode.Redirect, repeated.StatusCode);
        Assert.Equal(response.Headers.Location, repeated.Headers.Location);
        Assert.Equal(1, await fixture.InDatabase(db => db.Incidents.CountAsync(x => x.RiderProfileId == rider.Profile.Id)));
        Assert.Equal(1, await fixture.InDatabase(db => db.OutboxMessages.CountAsync(x => x.IncidentId == incident.Id)));

        var page = await client.GetAsync(response.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("https://www.google.com/maps/search/", html);
        Assert.DoesNotContain(rider.Profile.Conditions!, html);
        Assert.DoesNotContain(rider.Profile.Medications!, html);
        using var stranger = fixture.Client();
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(response.Headers.Location)).StatusCode);
    }

    [Fact]
    public async Task Denied_GPS_and_unverified_contacts_do_not_prevent_a_possible_accident_report()
    {
        var rider = await fixture.SeedRider(verifiedContact: false);
        using var client = fixture.Client();
        var csrf = await SqlWebFixture.Antiforgery(client, "/e/" + rider.Token);
        var response = await client.PostAsync("/e/" + rider.Token + "/report", ReportContent(Guid.NewGuid().ToString(), csrf));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var incident = await fixture.InDatabase(db => db.Incidents.AsNoTracking().SingleAsync(x => x.RiderProfileId == rider.Profile.Id));
        Assert.Null(incident.Latitude);
        Assert.Null(incident.Longitude);
        Assert.Equal(0, await fixture.InDatabase(db => db.OutboxMessages.CountAsync(x => x.IncidentId == incident.Id)));
        Assert.Equal(1, await fixture.InDatabase(db => db.IncidentAccesses.CountAsync(x => x.IncidentId == incident.Id)));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(response.Headers.Location)).StatusCode);
    }

    [Fact]
    public async Task Out_of_range_coordinates_do_not_create_an_incident()
    {
        var rider = await fixture.SeedRider();
        using var client = fixture.Client();
        var csrf = await SqlWebFixture.Antiforgery(client, "/e/" + rider.Token);
        var response = await client.PostAsync("/e/" + rider.Token + "/report", ReportContent(Guid.NewGuid().ToString(), csrf, "91", "23"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, await fixture.InDatabase(db => db.Incidents.CountAsync(x => x.RiderProfileId == rider.Profile.Id)));
        Assert.Equal(0, await fixture.InDatabase(db => db.OutboxMessages.CountAsync(x => x.RiderProfileId == rider.Profile.Id)));
    }

    [Fact]
    public async Task Notification_cooldown_blocks_a_second_independent_report()
    {
        var rider = await fixture.SeedRider();
        await fixture.SeedIncident(rider);
        using var client = fixture.Client();
        var csrf = await SqlWebFixture.Antiforgery(client, "/e/" + rider.Token);
        var response = await client.PostAsync("/e/" + rider.Token + "/report", ReportContent(Guid.NewGuid().ToString(), csrf));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(1, await fixture.InDatabase(db => db.Incidents.CountAsync(x => x.RiderProfileId == rider.Profile.Id)));
        Assert.Equal(0, await fixture.InDatabase(db => db.OutboxMessages.CountAsync(x => x.RiderProfileId == rider.Profile.Id)));
    }

    [Fact]
    public async Task Concurrent_reports_for_one_sticker_create_one_incident_and_one_notification_set()
    {
        var rider = await fixture.SeedRider();
        using var first = fixture.Client();
        using var second = fixture.Client();
        var firstCsrf = await SqlWebFixture.Antiforgery(first, "/e/" + rider.Token);
        var secondCsrf = await SqlWebFixture.Antiforgery(second, "/e/" + rider.Token);
        var responses = await Task.WhenAll(
            first.PostAsync("/e/" + rider.Token + "/report", ReportContent(Guid.NewGuid().ToString(), firstCsrf)),
            second.PostAsync("/e/" + rider.Token + "/report", ReportContent(Guid.NewGuid().ToString(), secondCsrf)));
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Redirect);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal(1, await fixture.InDatabase(db => db.Incidents.CountAsync(x => x.RiderProfileId == rider.Profile.Id)));
        Assert.Equal(1, await fixture.InDatabase(db => db.OutboxMessages.CountAsync(x => x.RiderProfileId == rider.Profile.Id)));
    }

    [Fact]
    public async Task Report_rate_limit_preserves_emergency_call_links()
    {
        var rider = await fixture.SeedRider();
        using var client = fixture.Client();
        for (var request = 0; request < 6; request++)
        {
            var rejectedByCsrf = await client.PostAsync("/e/" + rider.Token + "/report", ReportContent(Guid.NewGuid().ToString(), null));
            Assert.Equal(HttpStatusCode.BadRequest, rejectedByCsrf.StatusCode);
        }
        var limited = await client.PostAsync("/e/" + rider.Token + "/report", ReportContent(Guid.NewGuid().ToString(), null));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(60, limited.Headers.RetryAfter?.Delta?.TotalSeconds);
        var html = await limited.Content.ReadAsStringAsync();
        Assert.Contains("tel:112", html);
        Assert.Contains("tel:166", html);
        Assert.Equal(0, await fixture.InDatabase(db => db.Incidents.CountAsync(x => x.RiderProfileId == rider.Profile.Id)));
    }

    [Fact]
    public async Task Contact_invitation_requires_explicit_redemption_then_restricts_access_to_its_incident()
    {
        var rider = await fixture.SeedRider(PublicFields.Name | PublicFields.Allergies);
        var incident = await fixture.SeedIncident(rider);
        var other = await fixture.SeedIncident(rider);
        using var client = fixture.Client();
        var csrf = await SqlWebFixture.Antiforgery(client, "/incident/join/" + incident.Token);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/incident/" + incident.IncidentId)).StatusCode);
        var redeem = await client.PostAsync("/incident/join/" + incident.Token, new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = csrf }));
        Assert.Equal(HttpStatusCode.Redirect, redeem.StatusCode);
        Assert.Equal("/incident/" + incident.IncidentId, redeem.Headers.Location?.OriginalString);
        Assert.DoesNotContain(incident.Token, redeem.Headers.Location!.OriginalString);
        var page = await client.GetAsync(redeem.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.DoesNotContain(rider.Profile.Conditions!, await page.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/incident/" + other.IncidentId)).StatusCode);
        var consumed = await fixture.InDatabase(db => db.IncidentAccesses.AsNoTracking().SingleAsync(x => x.Id == incident.AccessId));
        Assert.NotNull(consumed.InvitationConsumedAt);
        Assert.NotNull(consumed.SessionTokenHash);
        Assert.NotEqual(consumed.TokenHash, consumed.SessionTokenHash);
        using var replay = fixture.Client();
        Assert.Equal(HttpStatusCode.NotFound, (await replay.GetAsync("/incident/join/" + incident.Token)).StatusCode);
        var replayCsrf = await SqlWebFixture.Antiforgery(replay, "/e/" + rider.Token);
        Assert.Equal(HttpStatusCode.NotFound, (await replay.PostAsync("/incident/join/" + incident.Token, new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = replayCsrf }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await replay.GetAsync("/incident/" + incident.IncidentId)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/incident/" + incident.IncidentId)).StatusCode);
    }

    [Theory]
    [InlineData("grant-expired")]
    [InlineData("incident-expired")]
    [InlineData("grant-revoked")]
    [InlineData("incident-closed")]
    [InlineData("sticker-revoked")]
    [InlineData("contact-disabled")]
    [InlineData("rider-disabled")]
    public async Task Expired_or_revoked_incident_access_is_refused(string reason)
    {
        var rider = await fixture.SeedRider(PublicFields.Name);
        var incident = await fixture.SeedIncident(rider);
        using var client = fixture.Client();
        var csrf = await SqlWebFixture.Antiforgery(client, "/incident/join/" + incident.Token);
        var redeem = await client.PostAsync("/incident/join/" + incident.Token, new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = csrf }));
        Assert.Equal(HttpStatusCode.Redirect, redeem.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/incident/" + incident.IncidentId)).StatusCode);
        await fixture.InDatabase(async db =>
        {
            switch (reason)
            {
                case "grant-expired": (await db.IncidentAccesses.FindAsync(incident.AccessId))!.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); break;
                case "incident-expired": (await db.Incidents.FindAsync(incident.IncidentId))!.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); break;
                case "grant-revoked": (await db.IncidentAccesses.FindAsync(incident.AccessId))!.IsRevoked = true; break;
                case "incident-closed": (await db.Incidents.FindAsync(incident.IncidentId))!.IsClosed = true; break;
                case "sticker-revoked": (await db.EmergencyIdentifiers.FindAsync(rider.Identifier.Id))!.IsActive = false; break;
                case "contact-disabled": (await db.EmergencyContacts.FindAsync(rider.Contact.Id))!.NotifyEnabled = false; break;
                case "rider-disabled": (await db.Users.FindAsync(rider.UserId))!.IsActive = false; break;
            }
            await db.SaveChangesAsync();
        });
        var response = await client.GetAsync("/incident/join/" + incident.Token);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(rider.Profile.FullName, await response.Content.ReadAsStringAsync());
        var existingSession = await client.GetAsync("/incident/" + incident.IncidentId);
        Assert.Equal(HttpStatusCode.NotFound, existingSession.StatusCode);
        Assert.DoesNotContain(rider.Profile.FullName, await existingSession.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Concurrent_invitation_redemption_grants_exactly_one_browser_a_session()
    {
        var rider = await fixture.SeedRider();
        var incident = await fixture.SeedIncident(rider);
        using var first = fixture.Client();
        using var second = fixture.Client();
        var path = "/incident/join/" + incident.Token;
        var firstCsrf = await SqlWebFixture.Antiforgery(first, path);
        var secondCsrf = await SqlWebFixture.Antiforgery(second, path);
        var responses = await Task.WhenAll(
            first.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = firstCsrf })),
            second.PostAsync(path, new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = secondCsrf })));
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Redirect);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.NotFound);
        var pages = await Task.WhenAll(first.GetAsync("/incident/" + incident.IncidentId), second.GetAsync("/incident/" + incident.IncidentId));
        Assert.Single(pages, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(pages, response => response.StatusCode == HttpStatusCode.NotFound);
    }

    private static FormUrlEncodedContent ReportContent(string submission, string? csrf, string? latitude = null, string? longitude = null)
    {
        var fields = new Dictionary<string, string>
        {
            ["Report.SubmissionKey"] = submission,
            ["Report.Consciousness"] = "Unknown",
            ["Report.PeopleInvolved"] = "1"
        };
        if (csrf is not null) fields["__RequestVerificationToken"] = csrf;
        if (latitude is not null) fields["Report.Latitude"] = latitude;
        if (longitude is not null) fields["Report.Longitude"] = longitude;
        return new FormUrlEncodedContent(fields);
    }
}
