using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ScanBack.Domain;

namespace ScanBack.Tests;

[Collection(SqlWebCollection.Name)]
public sealed class IncidentChatTests(SqlWebFixture fixture)
{
    private static FormUrlEncodedContent Message(string? csrf, string body, Guid? messageId = null) => new(
        new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrf ?? "",
            ["Body"] = body,
            ["ClientMessageId"] = (messageId ?? Guid.NewGuid()).ToString()
        });

    private async Task<(HttpClient Client, string Csrf)> Join(IncidentSeed incident)
    {
        var client = fixture.Client();
        var csrf = await SqlWebFixture.Antiforgery(client, "/incident/join/" + incident.Token);
        var response = await client.PostAsync("/incident/join/" + incident.Token,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = csrf }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        return (client, await SqlWebFixture.Antiforgery(client, "/incident/" + incident.IncidentId));
    }

    [Fact]
    public async Task Chat_requires_redeemed_incident_session_and_is_isolated_from_other_incidents()
    {
        var rider = await fixture.SeedRider();
        var first = await fixture.SeedIncident(rider);
        var second = await fixture.SeedIncident(rider);
        using var anonymous = fixture.Client();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/incident/{first.IncidentId}/chat")).StatusCode);
        var (client, csrf) = await Join(first);
        using (client)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/incident/{second.IncidentId}/chat")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/incident/{second.IncidentId}/chat", Message(csrf, "private"))).StatusCode);
            var response = await client.GetAsync($"/incident/{first.IncidentId}/chat");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Equal("noindex, nofollow", response.Headers.GetValues("X-Robots-Tag").Single());
        }
    }

    [Fact]
    public async Task Messages_are_ordered_encrypted_idempotent_and_visible_to_other_grants()
    {
        var rider = await fixture.SeedRider();
        var incident = await fixture.SeedIncident(rider);
        var (client, csrf) = await Join(incident);
        using (client)
        {
            var nonce = Guid.NewGuid();
            const string text = "<script>alert('xss')</script>\nSynthetic chat canary";
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/incident/{incident.IncidentId}/chat", Message(csrf, text, nonce))).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/incident/{incident.IncidentId}/chat", Message(csrf, text, nonce))).StatusCode);
            var response = await client.GetFromJsonAsync<JsonElement>($"/incident/{incident.IncidentId}/chat");
            var message = Assert.Single(response.GetProperty("messages").EnumerateArray());
            Assert.Equal(text, message.GetProperty("body").GetString());
            Assert.True(message.GetProperty("isMine").GetBoolean());
            var id = message.GetProperty("id").GetInt64();
            var raw = await fixture.InDatabase(db => db.Database.SqlQuery<string>($"SELECT [Body] AS [Value] FROM [IncidentChatMessages] WHERE [Id] = {id}").SingleAsync());
            Assert.DoesNotContain("Synthetic chat canary", raw, StringComparison.Ordinal);
            Assert.DoesNotContain("<script>", raw, StringComparison.Ordinal);
            var next = await client.GetFromJsonAsync<JsonElement>($"/incident/{incident.IncidentId}/chat?after={id}");
            Assert.Empty(next.GetProperty("messages").EnumerateArray());

            // Another independently redeemed contact grant to the same incident can read history.
            var token = ScanBack.Infrastructure.TokenService.Generate();
            await fixture.InDatabase(async db =>
            {
                db.IncidentAccesses.Add(new IncidentAccess { IncidentId = incident.IncidentId, EmergencyContactId = rider.Contact.Id,
                    Role = "Contact", TokenHash = ScanBack.Infrastructure.TokenService.Hash(token), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) });
                await db.SaveChangesAsync();
            });
            var (other, _) = await Join(new IncidentSeed(incident.IncidentId, Guid.Empty, token));
            using (other)
            {
                var shared = await other.GetFromJsonAsync<JsonElement>($"/incident/{incident.IncidentId}/chat");
                Assert.False(Assert.Single(shared.GetProperty("messages").EnumerateArray()).GetProperty("isMine").GetBoolean());
            }
        }
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("expired")]
    [InlineData("closed")]
    [InlineData("sticker")]
    [InlineData("contact")]
    [InlineData("account")]
    public async Task Read_and_send_stop_immediately_when_access_is_revoked(string reason)
    {
        var rider = await fixture.SeedRider();
        var incident = await fixture.SeedIncident(rider);
        var (client, csrf) = await Join(incident);
        using (client)
        {
            await fixture.InDatabase(async db =>
            {
                if (reason == "grant") (await db.IncidentAccesses.FindAsync(incident.AccessId))!.IsRevoked = true;
                if (reason == "expired") (await db.Incidents.FindAsync(incident.IncidentId))!.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
                if (reason == "closed") (await db.Incidents.FindAsync(incident.IncidentId))!.IsClosed = true;
                if (reason == "sticker") (await db.EmergencyIdentifiers.FindAsync(rider.Identifier.Id))!.IsActive = false;
                if (reason == "contact") (await db.EmergencyContacts.FindAsync(rider.Contact.Id))!.NotifyEnabled = false;
                if (reason == "account") (await db.Users.FindAsync(rider.UserId))!.IsActive = false;
                await db.SaveChangesAsync();
            });
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/incident/{incident.IncidentId}/chat")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/incident/{incident.IncidentId}/chat", Message(csrf, "not stored"))).StatusCode);
            Assert.Equal(0, await fixture.InDatabase(db => db.IncidentChatMessages.CountAsync(x => x.IncidentId == incident.IncidentId)));
        }
    }

    [Fact]
    public async Task Sends_validate_csrf_length_and_blank_messages_and_apply_per_session_rate_limit()
    {
        var rider = await fixture.SeedRider();
        var incident = await fixture.SeedIncident(rider);
        var (client, csrf) = await Join(incident);
        using (client)
        {
            foreach (var content in new[] { Message(null, "missing csrf"), Message(csrf, "  "), Message(csrf, new string('a', 1001)), Message(csrf, "bad nonce", Guid.Empty) })
                Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/incident/{incident.IncidentId}/chat", content)).StatusCode);
            for (var i = 0; i < 20; i++)
                Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/incident/{incident.IncidentId}/chat", Message(csrf, "test " + i))).StatusCode);
            var limited = await client.PostAsync($"/incident/{incident.IncidentId}/chat", Message(csrf, "rate limited"));
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
            Assert.NotNull(limited.Headers.RetryAfter);
        }
    }

    [Fact]
    public async Task Deleting_incident_also_deletes_its_chat_history()
    {
        var incident = await fixture.SeedIncident(await fixture.SeedRider());
        var (client, csrf) = await Join(incident);
        using (client)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/incident/{incident.IncidentId}/chat", Message(csrf, "retention canary"))).StatusCode);
            await fixture.InDatabase(async db => { await db.Incidents.Where(x => x.Id == incident.IncidentId).ExecuteDeleteAsync(); });
            Assert.Equal(0, await fixture.InDatabase(db => db.IncidentChatMessages.CountAsync(x => x.IncidentId == incident.IncidentId)));
        }
    }
}
