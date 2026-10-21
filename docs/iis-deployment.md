# Deploy ScanBack to Windows Server and IIS

This procedure targets **Windows Server 2022 or 2025 x64, IIS 10, .NET 10 LTS and SQL Server**. The release is framework-dependent and contains a website, one Windows background service and a separate deployment CLI. It uses editable source, Bootstrap and open-source components; no paid UI toolkit is required. The server OS, SQL edition, SMTP account, DNS and certificate services are separate operator responsibilities.

The repository's Linux tests and Windows package CI do not establish that your Windows machine, SQL trust chain, DNS or SMTP provider is configured. Complete the acceptance checks below on the target machine before launch.

## 1. Prepare the server and public domain

Use a supported, patched Windows Server with a dedicated administrative deployment account. Keep RDP/WinRM restricted to the administration network. Publish TCP 443 and TCP 80 (redirect only); restrict SQL to the required local/private interfaces. Allow outbound DNS, verified SMTP TLS and any required certificate revocation endpoints. Do not expose SQL, Mailpit or development ports publicly.

Create your domain's A/AAAA records. Obtain a trusted HTTPS certificate covering the exact host, including its full chain and private key, in **Local Computer → Personal** (`Cert:\LocalMachine\My`). Record its thumbprint. Renew this certificate before expiry. A self-signed TLS certificate is suitable only for an isolated test environment whose clients explicitly trust its issuer.

Install the IIS roles in elevated **Windows PowerShell 5.1**:

```powershell
Install-WindowsFeature Web-Server,Web-Http-Redirect,Web-Mgmt-Console,Web-Scripting-Tools,Web-AppInit
```

Download and install the current **.NET 10 x64 Hosting Bundle** from [Microsoft](https://dotnet.microsoft.com/download/dotnet/10.0) after IIS. The bundle includes the ASP.NET Core Module V2 and required shared runtimes. If IIS was installed afterward, repair the bundle. Restart the server or WAS/W3SVC during an approved maintenance window; this affects other IIS sites. Confirm `dotnet --list-runtimes` contains `Microsoft.NETCore.App 10.0.x` and `Microsoft.AspNetCore.App 10.0.x` and that IIS lists `AspNetCoreModuleV2`. Configure Windows/SChannel to your organization's current TLS policy; disable obsolete protocols after testing other workloads.

## 2. Install SQL Server with trusted encryption

Install and patch a supported SQL Server version, such as SQL Server 2022/2025, using the vendor installer. **Developer edition is for development/testing, not production.** Express can be used only within its licensed capacity and feature limits; select Standard/Enterprise or a managed SQL service when required. Accept licensing terms yourself. Configure Windows authentication where possible and do not run the application as `sa`, `db_owner` or a SQL administrator.

Create a dedicated empty `ScanBack` database with an approved owner, sizing, recovery model, encrypted disk/backup strategy and scheduled backups. Install a SQL TLS certificate issued by a CA trusted by the Windows server, whose DNS name matches the connection's `Server` name. Enable SQL encryption as appropriate. Use `Encrypt=True;TrustServerCertificate=False`; do not work around a certificate error by disabling validation. For a same-host SQL service, a DNS name such as `sql.your-domain` pointing to the server can match its SQL certificate. SQL Server Express named instances may require an explicit TCP port. Confirm connectivity with current `sqlcmd` using integrated authentication, encryption and certificate verification, for example `sqlcmd -S 'sql.your-domain,1433' -E -N -b -Q 'SELECT 1'`.

The same-host default uses **two runtime identities**: `IIS APPPOOL\ScanBack` and `NT SERVICE\ScanBack.Worker`. The installer creates those Windows principals. After installation, run the validating wrapper as a SQL security administrator on that same machine:

```powershell
.\deploy\Set-ScanBackSqlPermissions.ps1 -Server 'sql.your-domain,1433' -DatabaseName ScanBack
```

This grants DML through `scanback_runtime`, with no DDL or security administration. Review `deploy/sql/runtime-permissions.sql`. Existing excessive grants are not removed by this additive script: check both logins' server roles and database role memberships. Both identities must be able to connect/read/write application tables; neither should be able to create/drop/alter tables.

For **remote SQL**, local virtual accounts are not remote identities: they normally authenticate as the web machine account. Use separate dedicated domain identities or gMSAs for the app pool and worker, map them to equivalent DML roles, and configure the pool/service and filesystem/private-key ACLs accordingly. The supplied installer deliberately targets same-host virtual accounts; adapt it for your domain with an administrator. For Azure SQL or SQL authentication, put runtime credentials only in protected configuration/a supported secret manager and keep deployment credentials separate.

Use a **separate migration operator**. For example, create a dedicated Windows login/database user with `db_ddladmin`, `db_datareader`, and `db_datawriter` for this database; this can apply schema migrations and initialize data without server administration. Review privileges for each migration and revoke temporary elevated grants afterward. An integrated runtime connection string automatically uses this operator's Windows identity when the CLI is run under that account. Do not grant DDL to either runtime identity.

## 3. Configure SMTP and privacy details

Choose a transactional mail provider that supports authenticated **STARTTLS on 587**, or implicit TLS on **465**. Confirm the sender/domain and set SPF, DKIM and DMARC according to the provider. If it requires OAuth instead of username/password SMTP authentication, select a compatible SMTP relay or implement that provider's supported authentication; this version uses SMTP username/password authentication. Check the provider's sending quotas, sandbox restrictions, bounce handling, support and data-processing terms. Never disable SMTP server certificate validation.

For 587 set `Mail:UseStartTls=true`; for 465 set it to `false` (the application selects implicit TLS). Set `Enabled=true`, `Host`, `Port`, `FromEmail`, `FromName`, `Username` and `Password`. An authenticated relay may require provider-specific values. Do not use a personal mailbox password if an app password/service credential is available. The application never treats provider acceptance as proof that a person received or read mail.

Provide your legal operator name in `Privacy:ControllerName`, a monitored `Privacy:ContactEmail`, and an approved `Privacy:PolicyVersion`. Review the in-app notices and [security model](security.md) for the actual processing and public-link risks. Decide lawful bases, consent wording, retention and backup expiry; arrange applicable processor agreements, access procedures and a DPIA/legal review before processing real health data. Software alone does not establish GDPR compliance.

## 4. Prepare persistent configuration and encryption

The examples use:

| Location | Purpose | Runtime access |
| --- | --- | --- |
| `C:\inetpub\ScanBack\Web` | IIS files | Read/execute |
| `C:\inetpub\ScanBack\Worker` | Windows service | Read/execute |
| `C:\inetpub\ScanBack\Tools` | Operator-only deployment commands | No runtime use |
| `C:\ProgramData\ScanBack\config\appsettings.Production.json` | SQL, SMTP and operator settings | Read |
| `C:\ProgramData\ScanBack\keys` | Persistent Data Protection key ring | Modify |
| `C:\ProgramData\ScanBack\photos` | Protected uploaded photos | Modify |
| `C:\ProgramData\ScanBack\release-backups` | Previous application binaries | Administrators/SYSTEM |

The DataRoot and InstallRoot must be separate trees. Config, keys and photos must occupy separate subdirectories under DataRoot. The installer restricts their ACLs to Administrators, SYSTEM and the named runtime accounts; it resets inherited/explicit child permissions, so do not use a directory shared with another application. Never place configuration, keys, photos, `.pfx` files or backups under `Web`/`wwwroot`. Do not serve photos directly as static files.

Use a **separate RSA certificate for Data Protection**, not your renewable public TLS certificate. Import it with its private key into `LocalMachine\My`, retain an encrypted offline export and protect the export password separately. Use a certificate with the approved key size/lifetime and key-encipherment use required by your certificate policy. Its thumbprint goes in `Encryption:CertificateThumbprint`. The installer grants private-key read access to the pool and service accounts for supported Windows CNG/CSP RSA providers. Hardware-backed/custom providers need their own supported access configuration.

Alternatively, use `Encryption:CertificatePath` pointing to a PFX **inside the protected external config directory** and `Encryption:CertificatePassword` supplied securely; omit `CertificateThumbprint`. Do not put the PFX in the package or webroot. Restrict its file ACL identically to the config. Certificate and key-ring backups are critical: database ciphertext, private identifier tokens, invitation/outbox payloads and Identity secrets may be unrecoverable without them. Use the same key ring/application identity for Web, Worker and Tools. See [rotation and recovery](operations.md).

In an elevated session, create DataRoot/config with Administrators/SYSTEM access **before** writing real secrets. The helper can do this:

```powershell
. .\deploy\ScanBack.Common.ps1
Set-PrivateDirectory 'C:\ProgramData\ScanBack'
Set-PrivateDirectory 'C:\ProgramData\ScanBack\config'
Copy-Item .\deploy\appsettings.Production.example.json C:\ProgramData\ScanBack\config\appsettings.Production.json
```

Edit that external file locally using a secure editor. Replace every placeholder. `Site:BaseUrl` is the canonical **HTTPS origin**, with no path or query, and `AllowedHosts` is that exact hostname. The production validators reject insecure/incomplete settings. ASP.NET does not load `.env` files automatically. `SCANBACK_CONFIG_FILE` is the absolute external JSON path; environment overrides are supported. The shared JSON must never include `DeploymentConnection` or administrator provisioning credentials: the web and service do not need to read them.

## 5. Build, verify and install the package

On a build machine with PowerShell 7 and the SDK selected by `global.json`:

```powershell
dotnet restore ScanBack.sln --locked-mode
dotnet test ScanBack.sln --no-restore  # Requires the documented disposable SQL test setup.
.\scripts\publish-iis.ps1
```

This produces `artifacts/iis/ScanBack-IIS-win-x64.zip` and a SHA-256 file. Windows CI also publishes that artifact. Transfer it from a trusted build channel; compare `Get-FileHash` with the independently obtained build checksum before extracting. The inner manifest detects incomplete/corrupt files; it is not a substitute for trusting the build source. Extract to an administrator-controlled staging directory, outside InstallRoot/DataRoot. Keep the package and deployment scripts editable and available for recovery.

Run the first-install script using your actual values, elevated Windows PowerShell 5.1:

```powershell
.\deploy\Install-ScanBackIis.ps1 -PackagePath $PWD.Path `
  -HostName 'riders.your-domain' -HttpsCertificateThumbprint 'YOUR-ACTUAL-TLS-THUMBPRINT' `
  -ConfigPath 'C:\ProgramData\ScanBack\config\appsettings.Production.json'
```

The installer validates the package and config, copies Web/Worker/Tools, creates a dedicated 64-bit **No Managed Code** pool with `ApplicationPoolIdentity`, one worker process, no idle shutdown, AlwaysRunning and overlapping recycle disabled. Web production handles HTTP only; the Windows service handles outbox/retention work. Application preload is enabled when `Web-AppInit` is installed. The framework-dependent executable requires the Hosting Bundle.

It creates a hostname/SNI HTTPS binding, a separate port-80 redirect site, and a passwordless `NT SERVICE\ScanBack.Worker` service with restart recovery. It sets `SCANBACK_CONFIG_FILE` through the pool and service environment. The certificate supplied for HTTPS is separate from Data Protection. Keep unrelated sites and existing bindings under operator control; the script refuses an existing ScanBack installation rather than overwriting it.

Both sites and the service remain stopped, the service initially has manual startup, and `app_offline.htm` remains in place until initialization succeeds. Installation is a privileged first-install operation, not a transactional Windows installer: if it fails partway through, keep the sites/service stopped, fix the reported prerequisite, and review/remove only the partially created ScanBack IIS/service objects and empty code installation before rerunning. Never delete DataRoot or its keys to retry installation.

`Web/web.config` uses in-process hosting, disables stdout logs, rejects TRACE, removes `X-Powered-By`/IIS server header and caps request bodies at **3 MiB**; the application's image validation imposes its own limits. HTTP-to-HTTPS uses the separate redirect site. Do not add IIS “Require SSL” to the redirect site. Maintain trusted TLS and use the application security headers. Avoid adding permissive proxy trust or URL rewriting that exposes untrusted forwarded headers.

## 6. Apply schema and initialize with the operator identity

While Web and Worker are stopped, start a secure process **as the dedicated migration operator**, with read/modify access to the external key ring and read access to its certificate/private key and config. The installer only grants runtime/admin access; grant your operator explicit access if it is not a machine administrator. With integrated SQL authentication, the same connection string uses this operator's SQL privileges. Do not run the Windows service as the migration identity.

```powershell
$config = 'C:\ProgramData\ScanBack\config\appsettings.Production.json'
C:\inetpub\ScanBack\Tools\ScanBack.Deployment.exe check-config --config $config
if ($LASTEXITCODE -ne 0) { throw 'Configuration check failed.' }
C:\inetpub\ScanBack\Tools\ScanBack.Deployment.exe migrate --config $config
if ($LASTEXITCODE -ne 0) { throw 'Migration failed.' }
C:\inetpub\ScanBack\Tools\ScanBack.Deployment.exe initialize --config $config
if ($LASTEXITCODE -ne 0) { throw 'Initialization failed.' }
```

Check each exit code; stop on any failure. `check-config` is offline validation; `migrate` applies EF migrations; `initialize` creates required roles/platform settings. Production website/service startup never migrates or seeds the database.

When a distinct SQL deployment connection is unavoidable, inject `ConnectionStrings__DeploymentConnection` **only into this operator process** through your secure environment tooling and remove it afterward. Never save it in shared JSON, IIS config, service environment or a command argument. The CLI uses it in preference to the runtime connection. Runtime hosts reject a privileged connection setting. Do not print or transcript the environment. Migration operations are trusted code and require review/backups.

Create the initial administrator via temporary process-scoped credentials. Avoid passwords in history or command arguments:

```powershell
$credential = Get-Credential -Message 'New ScanBack administrator email and a unique strong password'
try {
    $env:Admin__Email = $credential.UserName
    $env:Admin__Password = $credential.GetNetworkCredential().Password
    C:\inetpub\ScanBack\Tools\ScanBack.Deployment.exe create-admin --config $config
    if ($LASTEXITCODE -ne 0) { throw 'Administrator creation failed.' }
} finally {
    Remove-Item Env:Admin__Email,Env:Admin__Password -ErrorAction SilentlyContinue
    $credential = $null
}
```

No existing account is silently elevated/reset. Create-admin uses the deployment connection/operator context and is not a website startup flag. Remove all temporary provisioning/deployment credentials when done. Enroll TOTP MFA, store recovery codes securely and use a new MFA session before entering `/admin`.

**Upgrading existing ScanBack data:** after the migrations and before starting these new hosts, run `Tools\ScanBack.Deployment.exe backfill-identity-secrets --config $config`. This encrypts legacy authenticator/recovery-token values using the shared Data Protection key ring. Stop all old Web/Worker instances first; old binaries must not write plaintext concurrently. This command is repeatable. Preserve a coordinated pre-upgrade backup and verify an existing user's MFA/recovery behavior with synthetic test data.

## 7. Start and complete acceptance

Back in an elevated server administration session, after clearing provisioning/deployment environment variables:

```powershell
.\deploy\Start-ScanBack.ps1 -HostName 'riders.your-domain'
```

This starts the worker/sites, checks trusted HTTPS `/health` and verifies that the service stays running, then enables automatic startup. DNS must resolve this hostname to the target being tested. It does not bypass TLS validation, send mail or establish worker throughput. A failed readiness check leaves the site in maintenance and stops the worker. Inspect Windows Application/Event Viewer and verify SQL privileges, schema and certificate/private-key access before retrying. `/health/live` checks the web process; `/health` checks database readiness. Treat the separate worker's status and outbox progress as independent signals.

From the operator process, send a **deliberate synthetic** SMTP test to an operator-controlled address:

```powershell
C:\inetpub\ScanBack\Tools\ScanBack.Deployment.exe smtp-test --config $config --to 'your-operations-address@your-domain' --confirm-send
```

Without `--confirm-send` the CLI must refuse to send. Verify actual inbox delivery, spam folder, sender alignment and provider events; CLI success means provider acceptance only. Then use synthetic users to register/confirm, reset a password, enroll MFA, confirm an emergency contact, create a sticker, view its public page, report a test incident and observe worker-delivered contact mail. Test denied GPS, expired/revoked links, consent withdrawal, export and account deletion. Do not call live emergency numbers during acceptance. Restart the server during a controlled test and repeat readiness plus worker delivery to verify automatic recovery.

## 8. Logs and launch review

Public QR/invitation URLs are capabilities. The installer sets both sites' W3C fields to status/timing/method metadata, **omitting URI stem, query, referrer, user agent, user name and client IP**. Inspect a new real IIS log file header after restart; ensure `cs-uri-stem`, `cs-uri-query` and `cs(Referer)` are absent. Do not enable Failed Request Tracing, detailed request logging, stdout logging, APM URL capture or proxy/CDN raw URL logging for production traffic. Configure upstream systems to omit/redact the complete capability path and query too. IIS HTTPERR logs or security products can capture rejected request targets outside site logging; restrict access and retention, review those sinks and redact/disable URL capture where supported. Do not collect a request URL to diagnose a sticker problem.

Configure access-controlled monitoring of readiness, service restarts, SMTP queue age/delivery failures and backups. Review [operations](operations.md), [security](security.md), retention settings, legal notices and provider agreements with the actual operator. Check the public bilingual pages, `robots.txt` and `sitemap.xml` on your real HTTPS domain using [the SEO launch procedure](seo.md); Search Console submission is a manual operator action. Keep private/account/capability routes excluded. Record who owns patching, certificate renewal, incident response, data subject requests and backup restore. Launch only after the real SQL, SMTP, IIS and recovery checks succeed.
