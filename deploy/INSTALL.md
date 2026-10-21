# ScanBack IIS package

Target: Windows Server 2022/2025 x64, IIS 10, .NET 10 Hosting Bundle, SQL Server and a transactional SMTP provider. This is a framework-dependent package; it contains no Windows or SQL licenses and no production credentials.

Read [the complete deployment procedure](docs/iis-deployment.md) before running scripts. The package contains `Web`, `Worker`, `Tools`, and `deploy`. Edit a copy of `deploy/appsettings.Production.example.json` outside this package, protect it, and supply your real domain, certificates, SQL server and mail provider. The installer never invents them.

In an elevated **Windows PowerShell 5.1** session, after installing the prerequisites:

```powershell
.\deploy\Install-ScanBackIis.ps1 -PackagePath $PWD.Path -HostName 'YOUR-DNS-NAME' `
  -HttpsCertificateThumbprint 'YOUR-TLS-CERTIFICATE-THUMBPRINT' `
  -ConfigPath 'C:\ProgramData\ScanBack\config\appsettings.Production.json'
```

Installation leaves the site and worker stopped. Grant SQL permissions; run `Tools\ScanBack.Deployment.exe check-config`, `migrate`, `initialize`, and `create-admin` under the documented operator identity. Clear temporary administrative/deployment environment variables, then run `deploy\Start-ScanBack.ps1 -HostName 'YOUR-DNS-NAME'` and test registration and contact mail. See [operations](docs/operations.md) for updates, monitoring, backup, certificate rotation and rollback.

The example is deliberately invalid until you enter real settings. Do not put secrets into source control, the Web directory, transcripts, command arguments, tickets or shell history. Review the provided scripts before running them with administrative permissions.
