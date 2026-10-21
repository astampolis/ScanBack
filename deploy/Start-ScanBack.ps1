#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9.-]*[a-zA-Z0-9]$')] [string] $HostName,
    [string] $InstallRoot = 'C:\inetpub\ScanBack',
    [string] $ConfigPath = 'C:\ProgramData\ScanBack\config\appsettings.Production.json',
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_.-]{0,50}$')] [string] $SiteName = 'ScanBack',
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_.-]{0,50}$')] [string] $AppPoolName = 'ScanBack'
)
. (Join-Path $PSScriptRoot 'ScanBack.Common.ps1')
Assert-Administrator
Import-Module WebAdministration
$InstallRoot = Get-CanonicalPath $InstallRoot
$ConfigPath = Get-CanonicalPath $ConfigPath
$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
if ($config.ConnectionStrings.PSObject.Properties.Name -contains 'DeploymentConnection' -and $config.ConnectionStrings.DeploymentConnection) { throw 'Remove privileged DeploymentConnection from the shared runtime JSON before starting.' }
if ($config.Site.BaseUrl.TrimEnd('/') -ne "https://$HostName" -or $config.AllowedHosts -ne $HostName) { throw 'HostName must match the canonical HTTPS origin in the production configuration.' }
if (-not (Get-WebBinding -Name $SiteName -Protocol https | Where-Object bindingInformation -eq "*:443:$HostName")) { throw 'The requested HTTPS host is not bound to this site.' }
& (Join-Path $InstallRoot 'Tools\ScanBack.Deployment.exe') check-config --config $ConfigPath --environment Production
Assert-NativeSuccess 'Production configuration validation'
$offline = Join-Path $InstallRoot 'Web\app_offline.htm'
try {
    Start-Service 'ScanBack.Worker'
    (Get-Service 'ScanBack.Worker').WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
    if (Test-Path $offline) { Remove-Item -LiteralPath $offline -Force }
    if ((Get-WebAppPoolState $AppPoolName).Value -ne 'Started') { Start-WebAppPool $AppPoolName }
    Start-Website $SiteName
    Start-Website "$SiteName.Redirect"
    $healthy = $false
    for ($attempt = 0; $attempt -lt 12; $attempt++) {
        try {
            $response = Invoke-WebRequest -Uri "https://$HostName/health" -UseBasicParsing -TimeoutSec 10
            if ($response.StatusCode -eq 200 -and (($response.Content | ConvertFrom-Json).status -eq 'healthy')) { $healthy = $true; break }
        } catch { }
        Start-Sleep -Seconds 2
    }
    if (-not $healthy) { throw 'HTTPS readiness failed. Check DNS, certificate trust, schema, runtime SQL permissions and Windows Event Viewer. No certificate validation was bypassed.' }
    if ((Get-Service 'ScanBack.Worker').Status -ne 'Running') { throw 'The worker stopped during startup. Inspect Windows Event Viewer.' }
    & sc.exe config 'ScanBack.Worker' start= delayed-auto | Out-Null
    Assert-NativeSuccess 'Enable automatic worker startup'
    Set-ItemProperty "IIS:\Sites\$SiteName" serverAutoStart $true
    Set-ItemProperty "IIS:\Sites\$SiteName.Redirect" serverAutoStart $true
    Write-Host 'HTTPS readiness passed; the worker is running. Complete real SMTP and browser acceptance checks in the deployment guide.'
} catch {
    'ScanBack maintenance is in progress.' | Set-Content $offline -Encoding UTF8
    Stop-Website $SiteName -ErrorAction SilentlyContinue
    Stop-Website "$SiteName.Redirect" -ErrorAction SilentlyContinue
    Stop-Service 'ScanBack.Worker' -ErrorAction SilentlyContinue
    & sc.exe config 'ScanBack.Worker' start= demand | Out-Null
    Set-ItemProperty "IIS:\Sites\$SiteName" serverAutoStart $false
    Set-ItemProperty "IIS:\Sites\$SiteName.Redirect" serverAutoStart $false
    throw
}
