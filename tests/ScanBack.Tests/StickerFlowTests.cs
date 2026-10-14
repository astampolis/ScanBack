using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ScanBack.Domain;
using ScanBack.Infrastructure;

namespace ScanBack.Tests;

[Collection(SqlWebCollection.Name)]
public sealed class StickerFlowTests(SqlWebFixture fixture)
{
    [Fact]
    public async Task Owner_can_download_printable_QR_and_NFC_URL_but_other_riders_cannot()
    {
        var rider = await fixture.SeedRider();
        var other = await fixture.SeedRider();
        using var owner = fixture.Client();
        using var stranger = fixture.Client();
        await SqlWebFixture.Login(owner, rider.Email);
        await SqlWebFixture.Login(stranger, other.Email);
        var prefix = "/stickers/" + rider.Identifier.Id;
        var svg = await owner.GetAsync(prefix + "/qr.svg");
        Assert.Equal(HttpStatusCode.OK, svg.StatusCode);
        Assert.Equal("image/svg+xml", svg.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<svg", await svg.Content.ReadAsStringAsync());
        var png = await owner.GetAsync(prefix + "/qr.png");
        Assert.Equal(HttpStatusCode.OK, png.StatusCode);
        Assert.Equal("image/png", png.Content.Headers.ContentType?.MediaType);
        var bytes = await png.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }));
        var print = await owner.GetAsync(prefix + "/print");
        Assert.Equal(HttpStatusCode.OK, print.StatusCode);
        var html = await print.Content.ReadAsStringAsync();
        Assert.Contains("http://localhost/e/" + rider.Token, html);
        Assert.DoesNotContain(rider.Profile.Allergies!, html);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(prefix + "/qr.svg")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(prefix + "/qr.png")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(prefix + "/print")).StatusCode);
    }

    [Fact]
    public async Task Replacement_invalidates_the_previous_URL_and_all_existing_incident_invitations()
    {
        var rider = await fixture.SeedRider(PublicFields.Name);
        var incident = await fixture.SeedIncident(rider);
        using var owner = fixture.Client();
        using var anonymous = fixture.Client();
        await SqlWebFixture.Login(owner, rider.Email);
        var csrf = await SqlWebFixture.Antiforgery(owner, "/stickers");
        var response = await owner.PostAsync("/stickers/" + rider.Identifier.Id + "/replace", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = csrf }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/e/" + rider.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/incident/join/" + incident.Token)).StatusCode);
        var protectedToken = await fixture.InDatabase(db => db.EmergencyIdentifiers.Where(x => x.Id == rider.Identifier.Id).Select(x => x.ProtectedToken).SingleAsync());
        using var scope = fixture.Factory.Services.CreateScope();
        var replacement = scope.ServiceProvider.GetRequiredService<TokenService>().Unprotect(protectedToken);
        Assert.NotEqual(rider.Token, replacement);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/e/" + replacement)).StatusCode);
        Assert.True(await fixture.InDatabase(db => db.IncidentAccesses.Where(x => x.Id == incident.AccessId).Select(x => x.IsRevoked).SingleAsync()));
    }

    [Fact]
    public async Task Deactivation_can_be_reversed_without_restoring_old_incident_grants()
    {
        var rider = await fixture.SeedRider();
        var incident = await fixture.SeedIncident(rider);
        using var owner = fixture.Client();
        using var anonymous = fixture.Client();
        await SqlWebFixture.Login(owner, rider.Email);
        var csrf = await SqlWebFixture.Antiforgery(owner, "/stickers");
        var toggle = "/stickers/" + rider.Identifier.Id + "/toggle";
        var form = new Dictionary<string, string> { ["__RequestVerificationToken"] = csrf };
        Assert.Equal(HttpStatusCode.Redirect, (await owner.PostAsync(toggle, new FormUrlEncodedContent(form))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/e/" + rider.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await owner.PostAsync(toggle, new FormUrlEncodedContent(form))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/e/" + rider.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync("/incident/join/" + incident.Token)).StatusCode);
    }
}
