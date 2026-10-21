using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ScanBack.Domain;
using ScanBack.Infrastructure;

namespace ScanBack.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SqlWebCollection : ICollectionFixture<SqlWebFixture>
{
    public const string Name = "SQL Server MVC integration";
}

/// <summary>
/// Exercises real SQL Server migrations, Identity, MVC binding, antiforgery and
/// cookies. Deployment is explicit: this fixture migrates/seeds its own database
/// before requests; the web host never initializes schema or runs background jobs.
/// Every run uses and deletes a separate database. A missing SQL connection fails
/// explicitly rather than silently skipping security checks.
/// </summary>
public sealed class SqlWebFixture : IAsyncLifetime
{
    public const string Password = "Test-only!Rider2026";
    public const string PolicyVersion = "2026-10-tests";
    private string _connection = "";
    private readonly string _keyDirectory = Path.Combine(Path.GetTempPath(), "scanback-tests", Guid.NewGuid().ToString("N"));
    private int _clientNumber;
    public TestApplicationFactory Factory { get; private set; } = null!;
    internal string AdministratorConnectionString => _connection;

    public async Task InitializeAsync()
    {
        var supplied = Environment.GetEnvironmentVariable("TestConnectionString");
        var configPath = Environment.GetEnvironmentVariable("ScanBackTestConfig") ?? "/workspace/scanback-runtime/test-config.json";
        if (string.IsNullOrWhiteSpace(supplied) && File.Exists(configPath))
        {
            using var configuration = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
            if (configuration.RootElement.TryGetProperty("TestConnectionString", out var value)) supplied = value.GetString();
        }
        if (string.IsNullOrWhiteSpace(supplied))
            throw new InvalidOperationException("SQL integration tests require TestConnectionString or a ScanBackTestConfig JSON file containing that key. Use a development SQL login allowed to create and delete test databases; never use production credentials.");

        var sql = new SqlConnectionStringBuilder(supplied)
        {
            InitialCatalog = "ScanBackTests_" + Guid.NewGuid().ToString("N"),
            ConnectTimeout = 15
        };
        _connection = sql.ConnectionString;
        Directory.CreateDirectory(_keyDirectory);
        // Minimal-host startup reads its settings before Build. Set these before
        // creating the factory, and restore them immediately after startup.
        var overrides = new Dictionary<string, string?>
        {
            ["ConnectionStrings__DefaultConnection"] = _connection,
            ["Site__BaseUrl"] = "http://localhost",
            ["Encryption__KeyDirectory"] = _keyDirectory,
            ["Encryption__CertificatePath"] = "",
            ["Encryption__CertificateThumbprint"] = "",
            ["Storage__PhotoDirectory"] = Path.Combine(_keyDirectory, "photos"),
            ["AllowedHosts"] = "localhost;127.0.0.1",
            ["Admin__Email"] = "",
            ["Admin__Password"] = "",
            ["Privacy__PolicyVersion"] = PolicyVersion,
            ["Privacy__ControllerName"] = "Integration test controller",
            ["Privacy__ContactEmail"] = "privacy@example.test"
        };
        var original = overrides.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var (key, value) in overrides) Environment.SetEnvironmentVariable(key, value);
            Factory = new TestApplicationFactory();
            using (var scope = Factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.MigrateAsync();
                if (!await db.PlatformSettings.AnyAsync()) db.PlatformSettings.Add(new PlatformSettings());
                await db.SaveChangesAsync();
                var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
                if (!await roles.RoleExistsAsync("Administrator"))
                    Assert.True((await roles.CreateAsync(new IdentityRole("Administrator"))).Succeeded);
            }
            using var client = Client();
            var response = await client.GetAsync("/health");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        finally
        {
            foreach (var (key, value) in original) Environment.SetEnvironmentVariable(key, value);
        }
    }

    public HttpClient Client()
    {
        var client = Factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        var number = Interlocked.Increment(ref _clientNumber);
        client.DefaultRequestHeaders.Add("X-ScanBack-Test-Client", $"10.77.{number / 254}.{number % 254 + 1}");
        return client;
    }

    public async Task<T> InDatabase<T>(Func<AppDbContext, Task<T>> action)
    {
        using var scope = Factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public async Task InDatabase(Func<AppDbContext, Task> action)
    {
        using var scope = Factory.Services.CreateScope();
        await action(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public async Task<RiderSeed> SeedRider(PublicFields publicFields = PublicFields.None, bool healthConsent = true, bool verifiedContact = true)
    {
        using var scope = Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var tokens = scope.ServiceProvider.GetRequiredService<TokenService>();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var user = new ApplicationUser { Email = $"rider-{suffix}@example.test", UserName = $"rider-{suffix}@example.test", EmailConfirmed = true, IsActive = true };
        var created = await users.CreateAsync(user, Password);
        Assert.True(created.Succeeded, string.Join(",", created.Errors.Select(error => error.Code)));
        var profile = new RiderProfile
        {
            UserId = user.Id,
            FullName = "Rider name " + suffix,
            DateOfBirth = new DateOnly(1990, 3, 18),
            BloodType = "A+",
            Allergies = "Allergy marker " + suffix,
            Conditions = "Condition marker " + suffix,
            Medications = "Medication marker " + suffix,
            OtherInformation = "Other health marker " + suffix,
            Instructions = "Instruction marker " + suffix,
            PublicFields = publicFields,
            HealthConsent = healthConsent,
            ConsentAt = healthConsent ? DateTimeOffset.UtcNow : null,
            ConsentPolicyVersion = healthConsent ? PolicyVersion : null
        };
        var contact = new EmergencyContact
        {
            RiderProfileId = profile.Id,
            Name = "Verified contact " + suffix,
            Relationship = "Family",
            Email = $"contact-{suffix}@example.test",
            Phone = "+306900000001",
            NotifyEnabled = true,
            PublicPhone = true,
            PublicPhoneRequested = true,
            PublicPhoneConsentedAt = verifiedContact ? DateTimeOffset.UtcNow : null,
            NotificationsConsentedAt = verifiedContact ? DateTimeOffset.UtcNow : null,
            ConsentPolicyVersion = verifiedContact ? PolicyVersion : null,
            IsVerified = verifiedContact
        };
        var unverified = new EmergencyContact
        {
            RiderProfileId = profile.Id,
            Name = "Unverified contact " + suffix,
            Relationship = "Friend",
            Email = $"unverified-{suffix}@example.test",
            Phone = "+306900000002",
            NotifyEnabled = true,
            PublicPhone = true,
            IsVerified = false
        };
        var token = TokenService.Generate();
        var identifier = new EmergencyIdentifier
        {
            RiderProfileId = profile.Id,
            Label = "Helmet " + suffix,
            TokenHash = TokenService.Hash(token),
            ProtectedToken = tokens.Protect(token)
        };
        db.AddRange(profile, contact, unverified, identifier);
        await db.SaveChangesAsync();
        return new RiderSeed(user.Id, user.Email!, profile, contact, unverified, identifier, token);
    }

    public async Task<IncidentSeed> SeedIncident(RiderSeed rider)
    {
        var token = TokenService.Generate();
        var incident = new Incident
        {
            RiderProfileId = rider.Profile.Id,
            EmergencyIdentifierId = rider.Identifier.Id,
            SubmissionKey = Guid.NewGuid().ToString(),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            Latitude = 37.9838,
            Longitude = 23.7275
        };
        var access = new IncidentAccess
        {
            IncidentId = incident.Id,
            EmergencyContactId = rider.Contact.Id,
            Role = "Contact",
            TokenHash = TokenService.Hash(token),
            ExpiresAt = incident.ExpiresAt
        };
        await InDatabase(async db => { db.AddRange(incident, access); await db.SaveChangesAsync(); });
        return new IncidentSeed(incident.Id, access.Id, token);
    }

    public static async Task<string> Antiforgery(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Hidden(await response.Content.ReadAsStringAsync(), "__RequestVerificationToken");
    }

    public static string Hidden(string html, string name)
    {
        foreach (Match input in Regex.Matches(html, @"<input\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var attributes = Regex.Matches(input.Value, "([a-zA-Z_][a-zA-Z0-9_.:-]*)\\s*=\\s*[\"']([^\"']*)[\"']")
                .ToDictionary(match => match.Groups[1].Value, match => WebUtility.HtmlDecode(match.Groups[2].Value), StringComparer.OrdinalIgnoreCase);
            if (attributes.TryGetValue("name", out var actual) && actual == name && attributes.TryGetValue("value", out var value)) return value;
        }
        throw new InvalidOperationException($"The form did not contain its expected hidden input '{name}'.");
    }

    public static async Task Login(HttpClient client, string email)
    {
        var csrf = await Antiforgery(client, "/account/login");
        var response = await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = csrf,
            ["Email"] = email,
            ["Password"] = Password
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/profile", response.Headers.Location?.OriginalString);
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null) await Factory.DisposeAsync();
        if (!string.IsNullOrWhiteSpace(_connection))
        {
            // The random ScanBackTests_ database is the only database this test
            // fixture is allowed to delete. Never delete the supplied catalog.
            var sql = new SqlConnectionStringBuilder(_connection);
            if (!sql.InitialCatalog.StartsWith("ScanBackTests_", StringComparison.Ordinal)) throw new InvalidOperationException("Unexpected test database name.");
            SqlConnection.ClearAllPools();
            sql.InitialCatalog = "master";
            await using var connection = new SqlConnection(sql.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            var database = new SqlConnectionStringBuilder(_connection).InitialCatalog;
            command.CommandText = $"IF DB_ID(@database) IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END";
            command.Parameters.AddWithValue("@database", database);
            await command.ExecuteNonQueryAsync();
        }
        if (Directory.Exists(_keyDirectory)) Directory.Delete(_keyDirectory, recursive: true);
    }
}

public sealed class TestApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IStartupFilter, TestClientIpStartupFilter>();
            foreach (var worker in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                         && (descriptor.ImplementationType == typeof(NotificationWorker) || descriptor.ImplementationType == typeof(RetentionWorker))).ToArray())
                services.Remove(worker);
        });
    }
}

/// <summary>
/// TestServer has no real remote address. Give each simulated browser its own
/// address so production rate-limit behavior is retained without unrelated
/// integration tests sharing the same anonymous IP quota.
/// </summary>
internal sealed class TestClientIpStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, continuation) =>
        {
            if (IPAddress.TryParse(context.Request.Headers["X-ScanBack-Test-Client"], out var address))
                context.Connection.RemoteIpAddress = address;
            await continuation();
        });
        next(app);
    };
}

public sealed record RiderSeed(string UserId, string Email, RiderProfile Profile, EmergencyContact Contact, EmergencyContact UnverifiedContact, EmergencyIdentifier Identifier, string Token);
public sealed record IncidentSeed(Guid IncidentId, Guid AccessId, string Token);
