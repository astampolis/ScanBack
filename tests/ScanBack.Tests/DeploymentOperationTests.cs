using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ScanBack.Deployment;
using ScanBack.Infrastructure;

namespace ScanBack.Tests;

[Collection(SqlWebCollection.Name)]
public sealed class DeploymentOperationTests(SqlWebFixture fixture)
{
    [Fact]
    public async Task Identity_secret_backfill_handles_multiple_batches_and_can_be_repeated_without_losing_authenticator_keys()
    {
        var rider = await fixture.SeedRider();
        string key;
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(rider.UserId))!;
            Assert.True((await users.ResetAuthenticatorKeyAsync(user)).Succeeded);
            key = (await users.GetAuthenticatorKeyAsync(user))!;
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            for (var index = 0; index < 205; index++)
                db.UserTokens.Add(new IdentityUserToken<string> { UserId = rider.UserId, LoginProvider = "LegacyMigrationTest", Name = "Token" + index.ToString("D3"), Value = "legacy-token-canary" });
            await db.SaveChangesAsync();
            // A pre-upgrade database contains plaintext. Bypass converters only
            // to create that historical input, then exercise the real command.
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AspNetUserTokens SET Value = CASE WHEN Name = 'AuthenticatorKey' THEN {key} ELSE 'legacy-token-canary' END WHERE UserId = {rider.UserId}");
        }
        for (var run = 0; run < 2; run++)
        {
            await DeploymentOperations.BackfillIdentitySecretsAsync(fixture.Factory.Services, CancellationToken.None);
            using var scope = fixture.Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Database.OpenConnectionAsync();
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM AspNetUserTokens WHERE UserId = @userId AND (Value NOT LIKE 'SBID1:%' OR Value LIKE '%legacy-token-canary%')";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@userId"; parameter.Value = rider.UserId;
            command.Parameters.Add(parameter);
            Assert.Equal(0, (int)(await command.ExecuteScalarAsync())!);
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.Equal(key, await users.GetAuthenticatorKeyAsync((await users.FindByIdAsync(rider.UserId))!));
            Assert.Equal("legacy-token-canary", await users.GetAuthenticationTokenAsync((await users.FindByIdAsync(rider.UserId))!, "LegacyMigrationTest", "Token204"));
        }
    }

    [Fact]
    public async Task Administrator_provisioning_cannot_promote_or_reset_an_existing_rider()
    {
        var rider = await fixture.SeedRider();
        var before = await fixture.InDatabase(db => db.Users.AsNoTracking().SingleAsync(x => x.Id == rider.UserId));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Admin:Email"] = rider.Email, ["Admin:Password"] = "Different!Provision2026"
        }).Build();
        await Assert.ThrowsAsync<DeploymentCommandException>(() => DeploymentOperations.CreateAdministratorAsync(fixture.Factory.Services, configuration, CancellationToken.None));
        using var scope = fixture.Factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var after = (await users.FindByIdAsync(rider.UserId))!;
        Assert.Equal(before.PasswordHash, after.PasswordHash);
        Assert.False(await users.IsInRoleAsync(after, "Administrator"));
        Assert.True(await users.CheckPasswordAsync(after, SqlWebFixture.Password));
    }

    [Fact]
    public async Task Repeated_initialization_preserves_existing_operator_settings_and_singleton_records()
    {
        var before = await fixture.InDatabase(db => db.PlatformSettings.AsNoTracking().SingleAsync());
        await fixture.InDatabase(async db => { await db.PlatformSettings.ExecuteUpdateAsync(update => update.SetProperty(x => x.IncidentRetentionDays, 17)); });
        try
        {
            await DeploymentOperations.InitializeAsync(fixture.Factory.Services, CancellationToken.None);
            await DeploymentOperations.InitializeAsync(fixture.Factory.Services, CancellationToken.None);
            Assert.Equal(17, await fixture.InDatabase(db => db.PlatformSettings.Select(x => x.IncidentRetentionDays).SingleAsync()));
            Assert.Equal(1, await fixture.InDatabase(db => db.Roles.CountAsync(x => x.Name == "Administrator")));
        }
        finally
        {
            await fixture.InDatabase(async db => { await db.PlatformSettings.ExecuteUpdateAsync(update => update.SetProperty(x => x.IncidentRetentionDays, before.IncidentRetentionDays)); });
        }
    }
}
