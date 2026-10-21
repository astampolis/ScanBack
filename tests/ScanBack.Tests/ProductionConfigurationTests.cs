using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using ScanBack.Infrastructure.Hosting;

namespace ScanBack.Tests;

public sealed class ProductionConfigurationTests
{
    [Theory]
    [InlineData("Site:BaseUrl", "http://app.scanback.test")]
    [InlineData("Site:BaseUrl", "https://user:secret-canary@app.scanback.test")]
    [InlineData("Site:BaseUrl", "https://app.scanback.test/path")]
    [InlineData("Site:BaseUrl", "https://app.scanback.test/?private=secret-canary")]
    [InlineData("Site:BaseUrl", "https://localhost")]
    [InlineData("AllowedHosts", "*")]
    [InlineData("AllowedHosts", "*.scanback.test")]
    [InlineData("AllowedHosts", "app.scanback.test;*")]
    [InlineData("AllowedHosts", "other.scanback.test")]
    [InlineData("Mail:Enabled", "false")]
    [InlineData("Mail:Host", "localhost")]
    [InlineData("Mail:Host", "127.0.0.1")]
    [InlineData("Mail:Host", "your-provider.test")]
    [InlineData("Mail:UseStartTls", "false")]
    [InlineData("Mail:Port", "0")]
    [InlineData("Mail:Port", "65536")]
    [InlineData("Mail:Port", "private-secret-canary")]
    [InlineData("Mail:Enabled", "private-secret-canary")]
    [InlineData("Mail:FromEmail", "Name <sender@scanback.test>")]
    [InlineData("Mail:FromEmail", "sender@scanback.test\r\nBcc:secret-canary@scanback.test")]
    [InlineData("Mail:Username", "secret-canary")]
    [InlineData("Mail:Password", "secret-canary")]
    [InlineData("Encryption:KeyDirectory", "relative-secret-canary")]
    [InlineData("Encryption:KeyDirectory", "/tmp/scanback-configuration-tests/app/keys")]
    [InlineData("Storage:PhotoDirectory", "/tmp/scanback-configuration-tests/app/wwwroot/photos")]
    [InlineData("Encryption:CertificatePath", "/tmp/scanback-configuration-tests/app/key-secret-canary.pfx")]
    [InlineData("Encryption:CertificateThumbprint", "secret-canary")]
    [InlineData("Privacy:ControllerName", "")]
    [InlineData("Privacy:ContactEmail", "bad-private-secret-canary")]
    [InlineData("Privacy:PolicyVersion", "")]
    [InlineData("Privacy:HealthConsentTextPublic", "")]
    [InlineData("ConnectionStrings:DefaultConnection", "Server=db.scanback.test;Database=ScanBack;User ID=app;Password=secret-canary;Encrypt=false;TrustServerCertificate=false")]
    [InlineData("ConnectionStrings:DefaultConnection", "Server=db.scanback.test;Database=ScanBack;User ID=app;Password=secret-canary;Encrypt=true;TrustServerCertificate=true")]
    [InlineData("ConnectionStrings:DefaultConnection", "Server=db.scanback.test;Database=ScanBack;User ID=sa;Password=secret-canary;Encrypt=true;TrustServerCertificate=false")]
    [InlineData("ConnectionStrings:DefaultConnection", "Server=db.scanback.test;Database=ScanBack;User ID=app;Password=secret-canary;Encrypt=true;Persist Security Info=true")]
    [InlineData("ConnectionStrings:DefaultConnection", "PrivateSecretCanary=secret-canary")]
    public void Production_rejects_insecure_or_incomplete_configuration_without_disclosing_values(string key, string value)
    {
        var settings = Settings();
        settings[key] = value;
        var failure = Assert.Throws<InvalidOperationException>(() => RuntimeConfiguration.Load(Configuration(settings), Environment()));
        Assert.DoesNotContain("secret-canary", failure.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Oversized_consent_wording_is_rejected_before_SQL_persistence()
    {
        var settings = Settings();
        settings["Privacy:HealthConsentTextPublic"] = new string('x', 4001);
        Assert.Throws<InvalidOperationException>(() => RuntimeConfiguration.Load(Configuration(settings), Environment()));
    }

    [Fact]
    public void Production_runtime_cannot_receive_deployment_credentials_but_operator_can_use_them()
    {
        var settings = Settings();
        var operatorConnection = settings["ConnectionStrings:DefaultConnection"]!.Replace("User ID=app", "User ID=deployment", StringComparison.Ordinal);
        settings["ConnectionStrings:DeploymentConnection"] = operatorConnection;
        var failure = Assert.Throws<InvalidOperationException>(() => RuntimeConfiguration.Load(Configuration(settings), Environment()));
        Assert.DoesNotContain("Password=", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(operatorConnection, RuntimeConfiguration.Load(Configuration(settings), Environment(), isDeployment: true).ConnectionString);
    }

    [Fact]
    public void Production_accepts_exact_HTTPS_host_external_storage_and_verified_implicit_SMTP_TLS()
    {
        var settings = Settings();
        settings["Mail:UseStartTls"] = "false";
        settings["Mail:Port"] = "465";
        var runtime = RuntimeConfiguration.Load(Configuration(settings), Environment());
        Assert.False(runtime.IsDevelopment);
        Assert.Equal("app.scanback.test", runtime.SiteBaseUri.Host);
        Assert.Equal(465, runtime.Mail.Port);
    }

    [Fact]
    public void Key_protection_requires_current_RSA_certificate_with_private_key_and_redacts_load_errors()
    {
        var directory = Path.Combine(Path.GetTempPath(), "scanback-certificate-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=ScanBack test key protection", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var current = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            var path = Path.Combine(directory, "private-secret-canary.pfx");
            File.WriteAllBytes(path, current.Export(X509ContentType.Pfx, "test-certificate-password"));
            RuntimeConfiguration Runtime(string? password) => new()
            {
                ConnectionString = Settings()["ConnectionStrings:DefaultConnection"]!, SiteBaseUri = new Uri("https://app.scanback.test"),
                KeyDirectory = Path.Combine(directory, "keys"), PhotoDirectory = Path.Combine(directory, "photos"),
                Mail = new(), Privacy = new(), CertificatePath = path, CertificatePassword = password
            };
            RuntimeServiceRegistration.ValidateKeyProtection(Runtime("test-certificate-password"));
            Assert.False(Directory.Exists(Path.Combine(directory, "keys")));
            Assert.False(Directory.Exists(Path.Combine(directory, "photos")));
            using (var services = new ServiceCollection().AddScanBackDataProtection(Runtime("test-certificate-password")).BuildServiceProvider()) { }
            var badPassword = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddScanBackDataProtection(Runtime("private-password-canary")));
            Assert.DoesNotContain("canary", badPassword.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(directory, badPassword.Message, StringComparison.Ordinal);
            using var expired = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(-1));
            File.WriteAllBytes(path, expired.Export(X509ContentType.Pfx, "test-certificate-password"));
            Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddScanBackDataProtection(Runtime("test-certificate-password")));
            using var weakKey = RSA.Create(1024);
            var weakRequest = new CertificateRequest("CN=Weak test certificate", weakKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var weakCertificate = weakRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            File.WriteAllBytes(path, weakCertificate.Export(X509ContentType.Pfx, "test-certificate-password"));
            Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddScanBackDataProtection(Runtime("test-certificate-password")));
            File.WriteAllBytes(path, current.Export(X509ContentType.Cert));
            Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddScanBackDataProtection(Runtime(null)));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    internal static Dictionary<string, string?> Settings() => new()
    {
        ["ConnectionStrings:DefaultConnection"] = "Server=db.scanback.test;Database=ScanBack;User ID=app;Password=test-password;Encrypt=true;TrustServerCertificate=false",
        ["Site:BaseUrl"] = "https://app.scanback.test", ["AllowedHosts"] = "app.scanback.test",
        ["Encryption:KeyDirectory"] = "/tmp/scanback-configuration-tests/keys", ["Storage:PhotoDirectory"] = "/tmp/scanback-configuration-tests/photos",
        ["Encryption:CertificateThumbprint"] = new string('A', 40),
        ["Mail:Enabled"] = "true", ["Mail:Host"] = "smtp.scanback.test", ["Mail:Port"] = "587",
        ["Mail:UseStartTls"] = "true", ["Mail:FromEmail"] = "sender@scanback.test",
        ["Privacy:ControllerName"] = "Test controller", ["Privacy:ContactEmail"] = "privacy@scanback.test", ["Privacy:PolicyVersion"] = "2026-10-tests"
    };

    internal static IConfiguration Configuration(Dictionary<string, string?> settings) => new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    internal static IHostEnvironment Environment() => new TestHostEnvironment();
    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "ScanBack.Web";
        public string ContentRootPath { get; set; } = "/tmp/scanback-configuration-tests/app";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
