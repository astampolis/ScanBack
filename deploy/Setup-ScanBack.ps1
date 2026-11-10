#Requires -Version 5.1
#Requires -RunAsAdministrator
<#
.SYNOPSIS
Guided first installation on a Windows Server with local SQL Server and IIS.
.DESCRIPTION
Run from an extracted IIS package. Configure and Install run as server administrator;
Initialize runs as a SQL deployment operator; Start runs as server administrator.
All is convenient when the current administrator also has SQL provisioning rights.
Existing configuration is preserved. Never use this script for an upgrade.
#>
[CmdletBinding()]
param(
    [ValidateSet('Configure', 'Install', 'Initialize', 'Start', 'All')]
    [string] $Phase = 'Configure',
    [string] $PackagePath = (Split-Path $PSScriptRoot -Parent),
    [string] $HttpsCertificateThumbprint,
    [string] $ConfigPath = 'C:\ProgramData\ScanBack\config\appsettings.Production.json'
)
. (Join-Path $PSScriptRoot 'ScanBack.Common.ps1')
Assert-Administrator
$PackagePath = Get-CanonicalPath $PackagePath
$ConfigPath = Get-CanonicalPath $ConfigPath
Assert-ChildPath $ConfigPath 'C:\ProgramData\ScanBack\config'
Assert-Package $PackagePath
foreach ($name in @('ConnectionStrings__DeploymentConnection', 'Admin__Email', 'Admin__Password')) {
    if ([Environment]::GetEnvironmentVariable($name)) { throw "Clear process variable $name before using guided setup." }
}
function Read-Required([string] $Prompt) {
    do { $value = Read-Host $Prompt } while ([string]::IsNullOrWhiteSpace($value))
    return $value.Trim()
}
if ($Phase -in @('Configure', 'All')) {
    if (Test-Path -LiteralPath $ConfigPath) {
        Write-Host 'Using existing external configuration; no secrets or certificates were replaced.'
    } else {
        $config = Get-Content (Join-Path $PSScriptRoot 'appsettings.Production.example.json') -Raw | ConvertFrom-Json
        $hostName = Read-Required 'Public DNS hostname (without https://)'
        if ($hostName -notmatch '^[A-Za-z0-9](?:[A-Za-z0-9.-]*[A-Za-z0-9])$') { throw 'Invalid hostname.' }
        $config.Site.BaseUrl = "https://$hostName"
        $config.AllowedHosts = $hostName
        $server = Read-Required 'Local SQL Server certificate DNS name and TCP port (e.g. sql.company.gr,1433)'
        # Build rather than concatenate: a semicolon in an answer cannot inject SQL options.
        $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
        $builder.DataSource = $server
        $builder.InitialCatalog = 'ScanBack'
        $builder.IntegratedSecurity = $true
        $builder.Encrypt = $true
        $builder.TrustServerCertificate = $false
        $builder.MultipleActiveResultSets = $true
        $config.ConnectionStrings.DefaultConnection = $builder.ConnectionString
        $config.Mail.Host = Read-Required 'SMTP hostname'
        $port = Read-Required 'SMTP port (587 STARTTLS or 465 TLS)'
        if ($port -notin @('587', '465')) { throw 'Choose port 587 or 465.' }
        $config.Mail.Port = [int]$port
        $config.Mail.UseStartTls = $port -eq '587'
        $config.Mail.FromEmail = Read-Required 'Verified sender email'
        $credential = Get-Credential -Message 'SMTP service username and password'
        if ($null -eq $credential) { throw 'SMTP credentials cancelled.' }
        try {
            $config.Mail.Username = $credential.UserName
            $config.Mail.Password = $credential.GetNetworkCredential().Password
        } finally { $credential = $null }
        $config.Privacy.ControllerName = Read-Required 'Legal operator name'
        $config.Privacy.ContactEmail = Read-Required 'Privacy contact email'
        $config.Privacy.PolicyVersion = Read-Required 'Reviewed privacy policy version'
        Set-PrivateDirectory 'C:\ProgramData\ScanBack'
        Set-PrivateDirectory (Split-Path $ConfigPath -Parent)
        # Local wrapping certificate, distinct from public HTTPS. Back up this private key.
        $cert = New-SelfSignedCertificate -Subject 'CN=ScanBack Data Protection' `
            -CertStoreLocation 'Cert:\LocalMachine\My' -KeyAlgorithm RSA -KeyLength 3072 `
            -KeyExportPolicy Exportable -KeyUsage KeyEncipherment,DataEncipherment `
            -NotAfter (Get-Date).AddYears(5)
        $config.Encryption.CertificateThumbprint = $cert.Thumbprint
        $config | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ConfigPath -Encoding UTF8
        $config.Mail.Password = $null
        Write-Host 'Protected configuration written. Back up the Data Protection certificate private key and key ring as described in docs/operations.md.'
    }
}
$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$hostName = $config.AllowedHosts
$tool = Join-Path $PackagePath 'Tools\ScanBack.Deployment.exe'
& $tool check-config --config $ConfigPath --environment Production
Assert-NativeSuccess 'Production configuration validation'
if ($Phase -in @('Install', 'All')) {
    if (-not $HttpsCertificateThumbprint) { $HttpsCertificateThumbprint = Read-Required 'Existing public HTTPS certificate thumbprint (LocalMachine/My)' }
    & (Join-Path $PSScriptRoot 'Install-ScanBackIis.ps1') -PackagePath $PackagePath `
        -HostName $hostName -HttpsCertificateThumbprint $HttpsCertificateThumbprint -ConfigPath $ConfigPath
}
if ($Phase -in @('Initialize', 'All')) {
    Get-Command sqlcmd -ErrorAction Stop | Out-Null
    Import-Module WebAdministration
    if ((Get-Service 'ScanBack.Worker').Status -ne 'Stopped' -or
        (Get-Website -Name ScanBack).State -ne 'Stopped') { throw 'Stop ScanBack website and worker before initialization.' }
    $builder = New-Object System.Data.SqlClient.SqlConnectionStringBuilder($config.ConnectionStrings.DefaultConnection)
    if (-not $builder.IntegratedSecurity -or $builder.InitialCatalog -ne 'ScanBack') { throw 'Guided setup requires Windows authentication and database ScanBack.' }
    # Current operator needs SQL CREATE DATABASE/login permissions. Runtime accounts never get them.
    & sqlcmd -S $builder.DataSource -E -N -b -Q "IF DB_ID(N'ScanBack') IS NULL CREATE DATABASE [ScanBack];"
    Assert-NativeSuccess 'Create database'
    & (Join-Path $PSScriptRoot 'Set-ScanBackSqlPermissions.ps1') -Server $builder.DataSource
    foreach ($command in @('migrate', 'initialize')) {
        & $tool $command --config $ConfigPath --environment Production
        Assert-NativeSuccess $command
    }
    $credential = Get-Credential -Message 'NEW ScanBack administrator email and strong password (existing users are not reset)'
    if ($null -eq $credential) { throw 'Administrator provisioning cancelled; rerun Initialize.' }
    try {
        $env:Admin__Email = $credential.UserName
        $env:Admin__Password = $credential.GetNetworkCredential().Password
        & $tool create-admin --config $ConfigPath --environment Production
        Assert-NativeSuccess 'Create administrator'
    } finally {
        Remove-Item Env:Admin__Email,Env:Admin__Password -ErrorAction SilentlyContinue
        $credential = $null
    }
}
if ($Phase -in @('Start', 'All')) {
    & (Join-Path $PSScriptRoot 'Start-ScanBack.ps1') -HostName $hostName -ConfigPath $ConfigPath
}
Write-Host "Phase $Phase completed. See docs/windows-quickstart.el.md for next steps."
