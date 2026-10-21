using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ScanBack.Infrastructure.Hosting;

/// <summary>Shared fail-closed configuration for the web, worker and deployment hosts.</summary>
public sealed class RuntimeConfiguration
{
    public required string ConnectionString { get; init; }
    public required Uri SiteBaseUri { get; init; }
    public required string KeyDirectory { get; init; }
    public required string PhotoDirectory { get; init; }
    public required MailOptions Mail { get; init; }
    public required PrivacyOptions Privacy { get; init; }
    public bool IsDevelopment { get; init; }
    public string? CertificateThumbprint { get; init; }
    public string? CertificatePath { get; init; }
    public string? CertificatePassword { get; init; }
    public IReadOnlyList<string> DecryptionCertificateThumbprints { get; init; } = [];

    public static RuntimeConfiguration Load(IConfiguration configuration, IHostEnvironment environment, bool isDeployment = false)
    {
        var local = environment.IsDevelopment() || environment.IsEnvironment("Testing");
        var errors = new List<string>();
        var defaultConnection = configuration.GetConnectionString("DefaultConnection") ?? "";
        var deploymentConnection = configuration.GetConnectionString("DeploymentConnection");
        ValidateSql(defaultConnection, local, false, "DefaultConnection", errors);
        if (!string.IsNullOrWhiteSpace(deploymentConnection))
        {
            if (!isDeployment && !local) errors.Add("DeploymentConnection must not be exposed to the web or worker process; supply it only to the deployment operator.");
            if (isDeployment) ValidateSql(deploymentConnection, local, true, "DeploymentConnection", errors);
        }
        var baseUrl = configuration["Site:BaseUrl"] ?? (local ? "http://localhost:5080" : "");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || uri.AbsolutePath != "/")
            errors.Add("Site:BaseUrl must be an absolute HTTP(S) site root without credentials, query, or fragment.");
        if (!local && uri is not null)
        {
            if (uri.Scheme != "https" || uri.IsLoopback) errors.Add("Production Site:BaseUrl must use a public HTTPS hostname.");
            var hosts = (configuration["AllowedHosts"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (hosts.Length != 1 || hosts[0].Contains('*') || !hosts[0].Equals(uri.IdnHost, StringComparison.OrdinalIgnoreCase))
                errors.Add("Production AllowedHosts must contain only the exact Site:BaseUrl hostname.");
            if (Placeholder(baseUrl)) errors.Add("Site:BaseUrl still contains a deployment placeholder.");
        }
        var keys = configuration["Encryption:KeyDirectory"] ?? Path.Combine(environment.ContentRootPath, "App_Data", "keys");
        var photos = configuration["Storage:PhotoDirectory"] ?? Path.Combine(environment.ContentRootPath, "App_Data", "photos");
        if (!local)
        {
            ValidateExternalPath(keys, environment.ContentRootPath, "Encryption:KeyDirectory", errors);
            ValidateExternalPath(photos, environment.ContentRootPath, "Storage:PhotoDirectory", errors);
            var file = configuration["SCANBACK_CONFIG_FILE"] ?? Environment.GetEnvironmentVariable("SCANBACK_CONFIG_FILE");
            if (!string.IsNullOrEmpty(file)) ValidateExternalPath(file, environment.ContentRootPath, "SCANBACK_CONFIG_FILE", errors);
        }
        MailOptions mail;
        try { mail = configuration.GetSection("Mail").Get<MailOptions>() ?? new(); }
        catch (InvalidOperationException) { throw new InvalidOperationException("Mail configuration has invalid field types. Check Port and the Enabled/UseStartTls switches."); }
        if (!local)
        {
            if (!mail.Enabled || string.IsNullOrWhiteSpace(mail.Host) || IsLoopbackHost(mail.Host) || Placeholder(mail.Host))
                errors.Add("Production Mail must be enabled and use your configured SMTP provider, not the development inbox.");
            if (mail.Port is < 1 or > 65535 || (!mail.UseStartTls && mail.Port != 465))
                errors.Add("Production SMTP must use verified STARTTLS or implicit TLS on port 465.");
            if (!ValidEmail(mail.FromEmail) || Placeholder(mail.FromEmail)) errors.Add("Mail:FromEmail must be a verified sender address.");
            if (string.IsNullOrWhiteSpace(mail.Username) != string.IsNullOrWhiteSpace(mail.Password)) errors.Add("SMTP username and password must be supplied together, or omitted for a trusted TLS relay.");
            if (Placeholder(mail.Username) || Placeholder(mail.Password)) errors.Add("SMTP credentials still contain deployment placeholders.");
        }
        var privacy = configuration.GetSection("Privacy").Get<PrivacyOptions>() ?? new();
        if (!local)
        {
            if (string.IsNullOrWhiteSpace(privacy.ControllerName) || privacy.ControllerName.Length > 200 || Placeholder(privacy.ControllerName)) errors.Add("Privacy:ControllerName must identify the data controller (maximum 200 characters).");
            if (!ValidEmail(privacy.ContactEmail) || privacy.ContactEmail.Length > 254 || Placeholder(privacy.ContactEmail)) errors.Add("Privacy:ContactEmail must identify your privacy contact (maximum 254 characters).");
            if (string.IsNullOrWhiteSpace(privacy.PolicyVersion) || privacy.PolicyVersion.Length > 64 || Placeholder(privacy.PolicyVersion)) errors.Add("Privacy:PolicyVersion must identify the reviewed notice/consent version (maximum 64 characters).");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(privacy.ControllerName)) privacy.ControllerName = "ScanBack development";
            if (string.IsNullOrWhiteSpace(privacy.ContactEmail)) privacy.ContactEmail = "privacy@scanback.test";
            if (string.IsNullOrWhiteSpace(privacy.PolicyVersion)) privacy.PolicyVersion = "development-v2";
        }
        if (string.IsNullOrWhiteSpace(privacy.HealthConsentTextPublic) || privacy.HealthConsentTextPublic.Length > 4000) errors.Add("Privacy:HealthConsentTextPublic must contain the reviewed consent text (maximum 4000 characters).");
        var thumbprint = configuration["Encryption:CertificateThumbprint"]?.Replace(" ", "");
        var certificatePath = configuration["Encryption:CertificatePath"];
        var oldThumbprints = configuration.GetSection("Encryption:DecryptionCertificateThumbprints").Get<string[]>() ?? [];
        oldThumbprints = oldThumbprints.Select(value => value.Replace(" ", "")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (oldThumbprints.Any(value => value.Length != 40 || !value.All(Uri.IsHexDigit))) errors.Add("DecryptionCertificateThumbprints must contain complete certificate-store thumbprints.");
        if (!local)
        {
            if (string.IsNullOrWhiteSpace(thumbprint) && string.IsNullOrWhiteSpace(certificatePath))
                errors.Add("Production requires Encryption:CertificateThumbprint or Encryption:CertificatePath to protect persisted keys.");
            if (!string.IsNullOrWhiteSpace(thumbprint) && (thumbprint.Length != 40 || !thumbprint.All(Uri.IsHexDigit)))
                errors.Add("Encryption:CertificateThumbprint must be a SHA-1 certificate-store thumbprint.");
            if (!string.IsNullOrWhiteSpace(certificatePath)) ValidateExternalPath(certificatePath, environment.ContentRootPath, "Encryption:CertificatePath", errors);
        }
        if (errors.Count > 0) throw new InvalidOperationException("ScanBack configuration is invalid: " + string.Join(" ", errors));
        return new()
        {
            ConnectionString = isDeployment && !string.IsNullOrWhiteSpace(deploymentConnection) ? deploymentConnection : defaultConnection,
            SiteBaseUri = uri!, KeyDirectory = Path.GetFullPath(keys), PhotoDirectory = Path.GetFullPath(photos),
            IsDevelopment = local, Mail = mail, Privacy = privacy, CertificateThumbprint = thumbprint,
            CertificatePath = certificatePath, CertificatePassword = configuration["Encryption:CertificatePassword"], DecryptionCertificateThumbprints = oldThumbprints
        };
    }

    private static void ValidateSql(string value, bool local, bool deployment, string name, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(value)) { errors.Add($"ConnectionStrings:{name} is required."); return; }
        try
        {
            var sql = new SqlConnectionStringBuilder(value);
            if (string.IsNullOrWhiteSpace(sql.DataSource) || string.IsNullOrWhiteSpace(sql.InitialCatalog)) errors.Add($"{name} must name a SQL server and database.");
            if (!local)
            {
                if (sql.TrustServerCertificate || !sql.Encrypt) errors.Add($"{name} must require encrypted SQL connections with certificate validation.");
                if (!deployment && sql.UserID.Equals("sa", StringComparison.OrdinalIgnoreCase)) errors.Add($"{name} must use a least-privilege application identity, not sa.");
                if (Placeholder(sql.Password) || Placeholder(sql.DataSource)) errors.Add($"{name} still contains deployment placeholders.");
                if (sql.PersistSecurityInfo) errors.Add($"{name} must disable Persist Security Info.");
            }
        }
        catch (ArgumentException) { errors.Add($"{name} is not a valid SQL Server connection string."); }
    }

    private static void ValidateExternalPath(string value, string root, string name, List<string> errors)
    {
        try
        {
            if (!Path.IsPathFullyQualified(value)) { errors.Add($"{name} must be an absolute path outside the application installation."); return; }
            var candidate = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
            var installRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
            if (candidate.Equals(installRoot, StringComparison.OrdinalIgnoreCase) || candidate.StartsWith(installRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                errors.Add($"{name} must be outside the application installation.");
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { errors.Add($"{name} is not a valid external path."); }
    }
    private static bool Placeholder(string? text) => !string.IsNullOrWhiteSpace(text) && new[] { "SET_SECURELY", "CHANGE_ME", "REPLACE_", ".invalid", "your-domain", "your-provider", "your-sql", "your-company" }.Any(s => text.Contains(s, StringComparison.OrdinalIgnoreCase));
    private static bool ValidEmail(string? value) => !string.IsNullOrWhiteSpace(value) && MailAddress.TryCreate(value, out var address) && address.Address.Equals(value, StringComparison.Ordinal) && !value.Contains('\r') && !value.Contains('\n');
    private static bool IsLoopbackHost(string value) => value.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(value, out var address) && IPAddress.IsLoopback(address);
}

public static class RuntimeServiceRegistration
{
    public static IServiceCollection AddScanBackPersistence(this IServiceCollection services, IConfiguration configuration, RuntimeConfiguration runtime, bool isDeployment = false)
    {
        services.AddSingleton(runtime);
        services.AddDbContext<AppDbContext>(o => o.UseSqlServer(runtime.ConnectionString));
        services.AddSingleton<TokenService>();
        services.Configure<MailOptions>(configuration.GetSection("Mail"));
        services.AddOptions<PrivacyOptions>().Configure(o =>
        {
            o.ControllerName = runtime.Privacy.ControllerName; o.ContactEmail = runtime.Privacy.ContactEmail;
            o.PolicyVersion = runtime.Privacy.PolicyVersion; o.HealthConsentTextPublic = runtime.Privacy.HealthConsentTextPublic;
        });
        if (!runtime.IsDevelopment && !isDeployment) services.AddHostedService<DatabasePrivilegeGuard>();
        return services;
    }

    /// <summary>Read-only certificate access probe; no directories or key files are created.</summary>
    public static void ValidateKeyProtection(RuntimeConfiguration runtime)
    {
        using var certificate = ResolveCertificate(runtime);
        foreach (var thumbprint in runtime.DecryptionCertificateThumbprints)
            using (var old = ResolveOldCertificate(thumbprint)) { }
    }

    public static IServiceCollection AddScanBackDataProtection(this IServiceCollection services, RuntimeConfiguration runtime)
    {
        var certificate = ResolveCertificate(runtime);
        Directory.CreateDirectory(runtime.KeyDirectory);
        Directory.CreateDirectory(runtime.PhotoDirectory);
        var protection = services.AddDataProtection().SetApplicationName("ScanBack").PersistKeysToFileSystem(new DirectoryInfo(runtime.KeyDirectory));
        if (certificate is not null)
        {
            protection.ProtectKeysWithCertificate(certificate);
            services.AddSingleton(certificate);
        }
        var decryptionCertificates = runtime.DecryptionCertificateThumbprints.Select(ResolveOldCertificate).ToArray();
        if (decryptionCertificates.Length > 0)
        {
            protection.UnprotectKeysWithAnyCertificate(decryptionCertificates);
            services.AddSingleton(new PreviousCertificates(decryptionCertificates));
        }
        return services;
    }

    private sealed class PreviousCertificates(X509Certificate2[] certificates) : IDisposable
    {
        public void Dispose() { foreach (var certificate in certificates) certificate.Dispose(); }
    }

    private static X509Certificate2 ResolveOldCertificate(string thumbprint)
    {
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Previous store certificates require Windows.");
        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            var certificate = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false).OfType<X509Certificate2>().SingleOrDefault();
            if (certificate is null || !certificate.HasPrivateKey) throw new InvalidOperationException("A previous key-protection certificate with its private key is missing.");
            using var key = certificate.GetRSAPrivateKey();
            if (key is null || key.KeySize < 2048) { certificate.Dispose(); throw new InvalidOperationException("A previous key-protection certificate is not accessible."); }
            return certificate;
        }
        catch (CryptographicException) { throw new InvalidOperationException("A previous key-protection certificate is not accessible."); }
    }

    private static X509Certificate2? ResolveCertificate(RuntimeConfiguration runtime)
    {
        X509Certificate2? certificate = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(runtime.CertificateThumbprint))
            {
                if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Certificate-store protection requires Windows; use a protected PFX path for other hosts.");
                using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
                store.Open(OpenFlags.ReadOnly);
                certificate = store.Certificates.Find(X509FindType.FindByThumbprint, runtime.CertificateThumbprint, validOnly: false).OfType<X509Certificate2>().SingleOrDefault();
                if (certificate is null) throw new InvalidOperationException("The configured key-protection certificate is not installed in LocalMachine/My.");
            }
            else if (!string.IsNullOrWhiteSpace(runtime.CertificatePath))
                certificate = X509CertificateLoader.LoadPkcs12FromFile(runtime.CertificatePath, runtime.CertificatePassword, X509KeyStorageFlags.EphemeralKeySet);
            if (certificate is not null)
            {
                if (!certificate.HasPrivateKey || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow || certificate.NotBefore.ToUniversalTime() > DateTime.UtcNow)
                    throw new InvalidOperationException("The key-protection certificate must be current and have an accessible private key.");
                using var key = certificate.GetRSAPrivateKey();
                if (key is null || key.KeySize < 2048) throw new InvalidOperationException("The key-protection certificate requires an accessible RSA private key of at least 2048 bits.");
            }
            else if (!runtime.IsDevelopment) throw new InvalidOperationException("Production key protection has not been configured.");
            return certificate;
        }
        catch (Exception error) when (error is CryptographicException or IOException or UnauthorizedAccessException)
        {
            certificate?.Dispose();
            throw new InvalidOperationException("The configured key-protection certificate could not be loaded. Check file/store access, validity and secure configuration.");
        }
        catch
        {
            certificate?.Dispose();
            throw;
        }
    }

}

public static class IdentityPolicy
{
    public static void Configure(IdentityOptions options)
    {
        options.SignIn.RequireConfirmedEmail = true;
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = options.Password.RequireUppercase = options.Password.RequireLowercase = options.Password.RequireNonAlphanumeric = true;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
        options.Lockout.MaxFailedAccessAttempts = 5;
    }
}
