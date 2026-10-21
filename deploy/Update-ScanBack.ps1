#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PackagePath,
    [string] $InstallRoot = 'C:\inetpub\ScanBack',
    [string] $BackupRoot = 'C:\ProgramData\ScanBack\release-backups',
    [string] $ConfigPath = 'C:\ProgramData\ScanBack\config\appsettings.Production.json',
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_.-]{0,50}$')] [string] $SiteName = 'ScanBack',
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_.-]{0,50}$')] [string] $AppPoolName = 'ScanBack'
)
. (Join-Path $PSScriptRoot 'ScanBack.Common.ps1')
Assert-Administrator
Import-Module WebAdministration
$PackagePath = Get-CanonicalPath $PackagePath
$InstallRoot = Get-CanonicalPath $InstallRoot
$BackupRoot = Get-CanonicalPath $BackupRoot
$ConfigPath = Get-CanonicalPath $ConfigPath
$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
$protectedTrees = @((Split-Path $ConfigPath -Parent), (Get-CanonicalPath $config.Encryption.KeyDirectory), (Get-CanonicalPath $config.Storage.PhotoDirectory))
foreach ($protected in $protectedTrees) {
    if ($protected -eq $InstallRoot -or $protected.StartsWith($InstallRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Move configuration, encryption keys and photos outside InstallRoot before updating.' }
    if ($BackupRoot -eq $protected -or $BackupRoot.StartsWith($protected + '\', [StringComparison]::OrdinalIgnoreCase) -or $protected.StartsWith($BackupRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'BackupRoot must be a separate directory tree from configuration, encryption keys and photos.' }
}
if (Test-Path (Join-Path $InstallRoot 'Web\App_Data')) { throw 'Legacy Web/App_Data exists. Preserve its keys/photos outside the webroot, update external configuration and archive/remove only the verified old copy before updating.' }
if ($BackupRoot -eq $InstallRoot -or $BackupRoot.StartsWith($InstallRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or $InstallRoot.StartsWith($BackupRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Backups and installed application must have separate directory trees.' }
if ($PackagePath -eq $InstallRoot -or $PackagePath.StartsWith($InstallRoot + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Stage the new package outside InstallRoot.' }
Assert-Package $PackagePath
if (-not (Test-Path (Join-Path $InstallRoot 'Web\ScanBack.Web.exe'))) { throw 'Existing installation not found.' }
foreach ($folder in @('Web', 'Worker', 'Tools')) { if (-not (Test-Path (Join-Path $InstallRoot $folder) -PathType Container)) { throw 'Updater requires the complete Web/Worker/Tools installation. Use the deployment guide for a legacy web-only host.' } }
if (-not (Test-Path "IIS:\Sites\$SiteName") -or -not (Test-Path "IIS:\AppPools\$AppPoolName") -or -not (Get-Service 'ScanBack.Worker' -ErrorAction SilentlyContinue)) { throw 'IIS pool/site or worker service is missing; repair the installation before updating.' }
$backup = Join-Path $BackupRoot ((Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
Set-PrivateDirectory $BackupRoot
New-Item -ItemType Directory $backup | Out-Null
$offline = Join-Path $InstallRoot 'Web\app_offline.htm'
'ScanBack maintenance is in progress.' | Set-Content $offline -Encoding UTF8
Stop-Website $SiteName
Stop-Website "$SiteName.Redirect"
Set-ItemProperty "IIS:\Sites\$SiteName" serverAutoStart $false
Set-ItemProperty "IIS:\Sites\$SiteName.Redirect" serverAutoStart $false
& sc.exe config 'ScanBack.Worker' start= demand | Out-Null
Assert-NativeSuccess 'Disable automatic worker startup during maintenance'
Stop-Service 'ScanBack.Worker'
(Get-Service 'ScanBack.Worker').WaitForStatus('Stopped', [TimeSpan]::FromSeconds(60))
if ((Get-WebAppPoolState $AppPoolName).Value -ne 'Stopped') { Stop-WebAppPool $AppPoolName }
$poolStopped = $false
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    if ((Get-WebAppPoolState $AppPoolName).Value -eq 'Stopped') { $poolStopped = $true; break }
    Start-Sleep -Seconds 1
}
if (-not $poolStopped) { throw 'Application pool did not stop. Do not replace locked files.' }
foreach ($folder in @('Web', 'Worker', 'Tools')) { Copy-Item (Join-Path $InstallRoot $folder) $backup -Recurse }
try {
    Copy-CodeDirectories $PackagePath $InstallRoot
    'ScanBack maintenance is in progress.' | Set-Content $offline -Encoding UTF8
    Set-PrivateDirectory $InstallRoot -Readers @("IIS APPPOOL\$AppPoolName", 'NT SERVICE\ScanBack.Worker')
} catch {
    # Only restore code; never touch external configuration, photos, keys or SQL data.
    Copy-CodeDirectories $backup $InstallRoot
    'ScanBack maintenance is in progress.' | Set-Content $offline -Encoding UTF8
    Set-PrivateDirectory $InstallRoot -Readers @("IIS APPPOOL\$AppPoolName", 'NT SERVICE\ScanBack.Worker')
    throw
}
Write-Host "Code installed; previous code is at $backup"
Write-Host 'Site and worker remain stopped. Take/verify the coordinated SQL+keys+photos backup, run required migrations/backfill under the operator identity, then run Start-ScanBack.ps1. No automatic database rollback is attempted.'
