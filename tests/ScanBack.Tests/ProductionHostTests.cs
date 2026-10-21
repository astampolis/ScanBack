using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScanBack.Domain;
using ScanBack.Infrastructure;

namespace ScanBack.Tests;

[Collection(SqlWebCollection.Name)]
public sealed class ProductionHostTests(SqlWebFixture fixture)
{
    [Fact]
    public async Task Production_starts_and_reads_using_a_real_SQL_identity_denied_DDL_without_seeding_or_hosted_workers()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var databaseName = "ScanBackProductionTests_" + suffix;
        var runtimeLogin = "ScanBackRuntime_" + suffix;
        var runtimePassword = "Runtime!" + Guid.NewGuid().ToString("N");
        var directory = Path.Combine(Path.GetTempPath(), "scanback-production-tests", suffix);
        Directory.CreateDirectory(directory);
        var admin = new SqlConnectionStringBuilder(fixture.AdministratorConnectionString) { InitialCatalog = databaseName };
        var master = new SqlConnectionStringBuilder(admin.ConnectionString) { InitialCatalog = "master" };
        var runtime = new SqlConnectionStringBuilder(admin.ConnectionString) { UserID = runtimeLogin, Password = runtimePassword, IntegratedSecurity = false };
        var token = TokenService.Generate();
        try
        {
            await using (var db = AdministratorDatabase(admin.ConnectionString))
            {
                await db.Database.MigrateAsync();
                var user = new ApplicationUser { Email = "production-rider@example.test", UserName = "production-rider@example.test", IsActive = true };
                var profile = new RiderProfile { UserId = user.Id, FullName = "Production readable rider", PublicFields = PublicFields.Name };
                var identifier = new EmergencyIdentifier { RiderProfileId = profile.Id, Label = "Production helmet", TokenHash = TokenService.Hash(token) };
                db.AddRange(user, profile, identifier);
                await db.SaveChangesAsync();
            }
            await Execute(master.ConnectionString, $"CREATE LOGIN [{runtimeLogin}] WITH PASSWORD = '{runtimePassword}', CHECK_POLICY = OFF");
            await Execute(admin.ConnectionString, $"CREATE USER [{runtimeLogin}] FOR LOGIN [{runtimeLogin}]; ALTER ROLE db_datareader ADD MEMBER [{runtimeLogin}]; ALTER ROLE db_datawriter ADD MEMBER [{runtimeLogin}]; DENY CREATE TABLE TO [{runtimeLogin}]; DENY CREATE PROCEDURE TO [{runtimeLogin}]; DENY ALTER ON SCHEMA::dbo TO [{runtimeLogin}]");
            await Assert.ThrowsAsync<SqlException>(() => Execute(runtime.ConnectionString, "CREATE TABLE dbo.ScanBackForbiddenDdl (Id int NOT NULL)"));
            await Assert.ThrowsAsync<SqlException>(() => Execute(runtime.ConnectionString, "ALTER TABLE dbo.RiderProfiles ADD ForbiddenStartupColumn int NULL"));

            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=ScanBack production-host test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            var certificatePath = Path.Combine(directory, "key-protection.pfx");
            await File.WriteAllBytesAsync(certificatePath, certificate.Export(X509ContentType.Pfx, "test-key-protection-password"));
            var overrides = new Dictionary<string, string?>
            {
                ["ConnectionStrings__DefaultConnection"] = runtime.ConnectionString,
                ["ConnectionStrings__DeploymentConnection"] = "", ["Site__BaseUrl"] = "https://app.scanback.test", ["AllowedHosts"] = "app.scanback.test",
                ["Encryption__KeyDirectory"] = Path.Combine(directory, "keys"), ["Storage__PhotoDirectory"] = Path.Combine(directory, "photos"),
                ["Encryption__CertificateThumbprint"] = "", ["Encryption__CertificatePath"] = certificatePath, ["Encryption__CertificatePassword"] = "test-key-protection-password",
                ["Mail__Enabled"] = "true", ["Mail__Host"] = "smtp.scanback.test", ["Mail__Port"] = "587", ["Mail__UseStartTls"] = "true",
                ["Mail__FromEmail"] = "sender@scanback.test", ["Mail__Username"] = "", ["Mail__Password"] = "",
                ["Privacy__ControllerName"] = "Integration test controller", ["Privacy__ContactEmail"] = "privacy@scanback.test", ["Privacy__PolicyVersion"] = SqlWebFixture.PolicyVersion,
                ["Admin__Email"] = "unexpected-admin@scanback.test", ["Admin__Password"] = "ShouldNever!Seed2026"
            };
            var original = overrides.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
            try
            {
                foreach (var (name, value) in overrides) Environment.SetEnvironmentVariable(name, value);
                await using var factory = new ProductionApplicationFactory();
                using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://app.scanback.test"), AllowAutoRedirect = false });
                Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);
                var page = await client.GetAsync("/e/" + token);
                Assert.Equal(HttpStatusCode.OK, page.StatusCode);
                Assert.Contains("Production readable rider", await page.Content.ReadAsStringAsync());
                Assert.DoesNotContain(factory.Services.GetServices<IHostedService>(), worker => worker is NotificationWorker or RetentionWorker);
                await using var db = AdministratorDatabase(admin.ConnectionString);
                Assert.Equal(0, await db.PlatformSettings.CountAsync());
                Assert.Equal(0, await db.Roles.CountAsync());
                Assert.Equal(1, await db.Users.CountAsync());
                Assert.Equal(0, await db.AuditEvents.CountAsync());
            }
            finally { foreach (var (name, value) in original) Environment.SetEnvironmentVariable(name, value); }
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await Execute(master.ConnectionString, $"IF DB_ID('{databaseName}') IS NOT NULL BEGIN ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{databaseName}]; END; IF SUSER_ID('{runtimeLogin}') IS NOT NULL DROP LOGIN [{runtimeLogin}]");
            Directory.Delete(directory, recursive: true);
        }
    }

    private static AppDbContext AdministratorDatabase(string connection) => new(new DbContextOptionsBuilder<AppDbContext>().UseSqlServer(connection).Options, new EphemeralDataProtectionProvider());
    private static async Task Execute(string connection, string sql)
    {
        await using var database = new SqlConnection(connection);
        await database.OpenAsync();
        await using var command = database.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class ProductionApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment(Environments.Production);
    }
}
