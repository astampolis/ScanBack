using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ScanBack.Infrastructure;

namespace ScanBack.Tests;

[Collection(SqlWebCollection.Name)]
public sealed class MfaFlowTests(SqlWebFixture fixture)
{
    [Fact]
    public async Task Real_authenticator_enrollment_accepts_only_the_code_then_requires_OTP_for_admin_login()
    {
        var rider = await fixture.SeedRider();
        using var client = fixture.Client();
        var enrollment = await Enroll(client, rider, administrator: true);
        var challenge = await PasswordStep(client, rider.Email, "/admin");
        var csrf = await SqlWebFixture.Antiforgery(client, challenge);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/admin")).StatusCode);

        var acceptedNearbyCodes = Enumerable.Range(-4, 9).Select(offset => Totp(enrollment.Key, DateTimeOffset.UtcNow.AddSeconds(offset * 30))).ToHashSet();
        var invalid = Enumerable.Range(0, 100).Select(number => number.ToString("D6", CultureInfo.InvariantCulture)).First(code => !acceptedNearbyCodes.Contains(code));
        var rejected = await client.PostAsync("/account/two-factor", VerifyForm(csrf, invalid, false, "/admin"));
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/admin")).StatusCode);

        var verified = await client.PostAsync("/account/two-factor", VerifyForm(csrf, Totp(enrollment.Key, DateTimeOffset.UtcNow), false, "/admin"));
        Assert.Equal(HttpStatusCode.Redirect, verified.StatusCode);
        Assert.Equal("/admin", verified.Headers.Location?.OriginalString);
        // The production admin middleware requires both enabled MFA and a
        // server-issued amr=mfa claim. This asserts the real OTP flow sets it.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/security/qr")).StatusCode);
    }

    [Fact]
    public async Task Recovery_code_sign_in_succeeds_once_and_cannot_reuse_the_consumed_code()
    {
        var rider = await fixture.SeedRider();
        using var client = fixture.Client();
        var enrollment = await Enroll(client, rider, administrator: false);
        var challenge = await PasswordStep(client, rider.Email, "/profile");
        var csrf = await SqlWebFixture.Antiforgery(client, challenge);
        var response = await client.PostAsync("/account/two-factor", VerifyForm(csrf, enrollment.RecoveryCodes[0], true, "/profile"));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/profile")).StatusCode);

        csrf = await SqlWebFixture.Antiforgery(client, "/profile");
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostAsync("/account/logout", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = csrf }))).StatusCode);
        challenge = await PasswordStep(client, rider.Email, "/profile");
        csrf = await SqlWebFixture.Antiforgery(client, challenge);
        var reuse = await client.PostAsync("/account/two-factor", VerifyForm(csrf, enrollment.RecoveryCodes[0], true, "/profile"));
        Assert.Equal(HttpStatusCode.OK, reuse.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/profile")).StatusCode);

        response = await client.PostAsync("/account/two-factor", VerifyForm(csrf, enrollment.RecoveryCodes[1], true, "/profile"));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/profile")).StatusCode);
    }

    private async Task<(string Key, string[] RecoveryCodes)> Enroll(HttpClient client, RiderSeed rider, bool administrator)
    {
        if (administrator)
        {
            using var scope = fixture.Factory.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True((await users.AddToRoleAsync((await users.FindByIdAsync(rider.UserId))!, "Administrator")).Succeeded);
        }
        await SqlWebFixture.Login(client, rider.Email);
        var csrf = await SqlWebFixture.Antiforgery(client, "/security");
        var qr = await client.GetAsync("/security/qr");
        Assert.Equal(HttpStatusCode.OK, qr.StatusCode);
        Assert.Equal("image/png", qr.Content.Headers.ContentType?.MediaType);
        string key;
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            key = (await users.GetAuthenticatorKeyAsync((await users.FindByIdAsync(rider.UserId))!))!;
        }
        Assert.False(string.IsNullOrWhiteSpace(key));
        // The rendered form contains Code and antiforgery only. Display-only
        // SharedKey/AuthenticatorUri values must not be implicitly required.
        var response = await client.PostAsync("/security/enable", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrf,
            ["Code"] = Totp(key, DateTimeOffset.UtcNow)
        }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(await fixture.InDatabase(db => db.Users.Where(user => user.Id == rider.UserId).Select(user => user.TwoFactorEnabled).SingleAsync()));
        var html = await response.Content.ReadAsStringAsync();
        var codes = Regex.Matches(html, @"<li><code>([^<]+)</code></li>").Select(match => WebUtility.HtmlDecode(match.Groups[1].Value)).ToArray();
        Assert.Equal(10, codes.Length);
        var storedTokens = await fixture.InDatabase(async db =>
        {
            await db.Database.OpenConnectionAsync();
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT Value FROM AspNetUserTokens WHERE UserId = @userId";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@userId";
            parameter.Value = rider.UserId;
            command.Parameters.Add(parameter);
            await using var rows = await command.ExecuteReaderAsync();
            var values = new List<string>();
            while (await rows.ReadAsync()) values.Add(rows.GetString(0));
            return values;
        });
        Assert.NotEmpty(storedTokens);
        Assert.All(storedTokens, stored =>
        {
            Assert.StartsWith("SBID1:", stored);
            Assert.DoesNotContain(key, stored, StringComparison.Ordinal);
            Assert.All(codes, code => Assert.DoesNotContain(code, stored, StringComparison.Ordinal));
        });
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/profile")).StatusCode);
        return (key, codes);
    }

    private static async Task<string> PasswordStep(HttpClient client, string email, string returnUrl)
    {
        var csrf = await SqlWebFixture.Antiforgery(client, "/account/login");
        var response = await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrf,
            ["Email"] = email,
            ["Password"] = SqlWebFixture.Password,
            ["ReturnUrl"] = returnUrl
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.OriginalString;
        Assert.StartsWith("/account/two-factor", location);
        return location!;
    }

    private static FormUrlEncodedContent VerifyForm(string csrf, string code, bool recovery, string returnUrl) => new(new Dictionary<string, string>
    {
        ["__RequestVerificationToken"] = csrf,
        ["Code"] = code,
        ["UseRecoveryCode"] = recovery ? "true" : "false",
        ["ReturnUrl"] = returnUrl
    });

    private static string Totp(string base32Key, DateTimeOffset now)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>();
        var buffer = 0;
        var bits = 0;
        foreach (var character in base32Key.ToUpperInvariant().Where(character => character != '=' && !char.IsWhiteSpace(character)))
        {
            var value = alphabet.IndexOf(character);
            if (value < 0) throw new InvalidOperationException("The authenticator key was not valid Base32.");
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits < 8) continue;
            bits -= 8;
            bytes.Add((byte)(buffer >> bits));
        }
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, now.ToUnixTimeSeconds() / 30);
        var digest = HMACSHA1.HashData(bytes.ToArray(), counter);
        var offset = digest[^1] & 0x0f;
        var value31 = BinaryPrimitives.ReadInt32BigEndian(digest.AsSpan(offset, 4)) & 0x7fffffff;
        return (value31 % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }
}
