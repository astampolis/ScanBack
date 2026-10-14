using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using ScanBack.Domain;
using ScanBack.Infrastructure;
using ScanBack.Web.Models;

namespace ScanBack.Tests;

public sealed class SecurityUnitTests
{
    [Fact]
    public void New_profiles_keep_health_and_public_fields_private()
    {
        var profile = new RiderProfile();
        Assert.False(profile.HealthConsent);
        Assert.Null(profile.ConsentAt);
        Assert.Equal(PublicFields.None, profile.PublicFields);
        Assert.Null(profile.BloodType);
    }

    [Fact]
    public void Identifier_tokens_use_32_random_bytes_with_URL_safe_encoding()
    {
        var tokens = Enumerable.Range(0, 1_000).Select(_ => TokenService.Generate()).ToArray();
        Assert.Equal(tokens.Length, tokens.Distinct(StringComparer.Ordinal).Count());
        Assert.All(tokens, token =>
        {
            Assert.Equal(43, token.Length);
            Assert.Matches("^[A-Za-z0-9_-]{43}$", token);
            Assert.Equal(32, Microsoft.AspNetCore.WebUtilities.WebEncoders.Base64UrlDecode(token).Length);
        });
    }

    [Fact]
    public void Token_hashes_are_stable_and_do_not_store_the_access_token()
    {
        const string token = "an-example-access-token";
        var hash = TokenService.Hash(token);
        Assert.Equal(TokenService.Hash(token), hash);
        Assert.Equal(64, hash.Length);
        Assert.NotEqual(token, hash);
        Assert.NotEqual(hash, TokenService.Hash(token + "x"));
    }

    [Fact]
    public void Protected_tokens_round_trip_and_reject_tampering_or_an_unrelated_key_ring()
    {
        var service = new TokenService(new EphemeralDataProtectionProvider());
        var token = TokenService.Generate();
        var protectedToken = service.Protect(token);
        Assert.Equal(token, service.Unprotect(protectedToken));
        var tampered = protectedToken.ToCharArray();
        tampered[tampered.Length / 2] = tampered[tampered.Length / 2] == 'A' ? 'B' : 'A';
        Assert.Throws<CryptographicException>(() => service.Unprotect(new string(tampered)));
        var unrelated = new TokenService(new EphemeralDataProtectionProvider());
        Assert.Throws<CryptographicException>(() => unrelated.Unprotect(protectedToken));
    }

    [Theory]
    [InlineData(91, 10)]
    [InlineData(10, -181)]
    [InlineData(double.NaN, 10)]
    [InlineData(double.PositiveInfinity, 10)]
    public void Report_input_rejects_invalid_or_nonfinite_coordinates(double latitude, double longitude)
    {
        var report = new AccidentReportInput { Latitude = latitude, Longitude = longitude };
        var results = new List<ValidationResult>();
        Assert.False(Validator.TryValidateObject(report, new ValidationContext(report), results, validateAllProperties: true));
        Assert.NotEmpty(results);
    }

    [Fact]
    public void Report_input_requires_paired_coordinates_but_allows_permission_denial()
    {
        var paired = new AccidentReportInput { Latitude = 37.98, Longitude = 23.72 };
        var denied = new AccidentReportInput();
        var incomplete = new AccidentReportInput { Latitude = 37.98 };
        Assert.True(Validator.TryValidateObject(paired, new ValidationContext(paired), [], true));
        Assert.True(Validator.TryValidateObject(denied, new ValidationContext(denied), [], true));
        Assert.False(Validator.TryValidateObject(incomplete, new ValidationContext(incomplete), [], true));
    }

    [Theory]
    [InlineData("predictable-identifier")]
    [InlineData("00000000-0000-0000-0000-000000000000x")]
    public void Report_input_requires_a_well_formed_submission_identifier(string submission)
    {
        var report = new AccidentReportInput { SubmissionKey = submission };
        Assert.False(Validator.TryValidateObject(report, new ValidationContext(report), [], true));
    }
}
