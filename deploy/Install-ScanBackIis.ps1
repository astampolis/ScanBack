#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PackagePath,
    [Parameter(Mandatory)] [ValidatePattern('^(?=.{1,253}$)[a-zA-Z0-9](?:[a-zA-Z0-9.-]*[a-zA-Z0-9])?$')] [string] $HostName,
    [Parameter(Mandatory)] [ValidatePattern('^[a-fA-F0-9 ]{40,80}$')] [string] $HttpsCertificateThumbprint,
    [Parameter(Mandatory)] [string] $ConfigPath,
    [string] $InstallRoot = 'C:\inetpub\ScanBack',
    [string] $DataRoot = 'C:\ProgramData\ScanBack',
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_.-]{0,50}$')] [string] $SiteName = 'ScanBack',
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_.-]{0,50}$')] [string] $AppPoolName = 'ScanBack',
    [switch] $InstallWindowsFeatures
)
. (Join-Path $PSScriptRoot 'ScanBack.Common.ps1')
Assert-Administrator
$PackagePath = Get-CanonicalPath $PackagePath
$InstallRoot = Get-CanonicalPath $InstallRoot
$DataRoot = Get-CanonicalPath $DataRoot
$ConfigPath = Get-CanonicalPath $ConfigPath
Assert-ChildPath $ConfigPath $DataRoot
if ($DataRoot.StartsWith($InstallRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or $InstallRoot.StartsWith($DataRoot + '\', [StringComparison]::OrdinalIgnoreCase) -or $DataRoot -eq $InstallRoot) { throw 'InstallRoot and DataRoot must be separate directory trees.' }
if ($InstallRoot -eq [IO.Path]::GetPathRoot($InstallRoot).TrimEnd('\') -or $DataRoot -eq [IO.Path]::GetPathRoot($DataRoot).TrimEnd('\')) { throw 'Choose dedicated application directories, never a drive root.' }
Assert-Package $PackagePath
if (-not (Test-Path $ConfigPath -PathType Leaf)) { throw 'Create the external production configuration first; see docs/iis-deployment.md.' }
$config = Get-Content -LiteralPath $ConfigPath -Raw | ConvertFrom-Json
if ($config.ConnectionStrings.PSObject.Properties.Name -contains 'DeploymentConnection' -and $config.ConnectionStrings.DeploymentConnection) { throw 'Do not store DeploymentConnection in shared runtime JSON; inject it only into the migration operator process.' }
if ($config.PSObject.Properties.Name -contains 'Admin' -and ($config.Admin.PSObject.Properties.Name -contains 'Password' -or $config.Admin.PSObject.Properties.Name -contains 'Email')) { throw 'Do not store administrator provisioning credentials in shared runtime JSON.' }
if ($config.Site.BaseUrl.TrimEnd('/') -ne "https://$HostName" -or $config.AllowedHosts -ne $HostName) { throw 'Site:BaseUrl and AllowedHosts must exactly match the HTTPS host supplied.' }
$keys = Get-CanonicalPath $config.Encryption.KeyDirectory
$photos = Get-CanonicalPath $config.Storage.PhotoDirectory
Assert-ChildPath $keys $DataRoot
Assert-ChildPath $photos $DataRoot
$configDirectory = Split-Path $ConfigPath -Parent
foreach ($pair in @(@($keys, $photos), @($keys, $configDirectory), @($photos, $configDirectory))) {
    if ($pair[0] -eq $pair[1] -or $pair[0].StartsWith($pair[1] + '\', [StringComparison]::OrdinalIgnoreCase) -or $pair[1].StartsWith($pair[0] + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Config, keys and photos must occupy separate directory trees.' }
}
if ($InstallWindowsFeatures) {
    Import-Module ServerManager
    $result = Install-WindowsFeature Web-Server,Web-Http-Redirect,Web-Mgmt-Console,Web-Scripting-Tools,Web-AppInit
    if (-not $result.Success) { throw 'IIS Windows feature installation failed.' }
    if ($result.RestartNeeded -eq 'Yes') { throw 'Restart Windows, install/repair the .NET 10 Hosting Bundle, then rerun.' }
}
Import-Module WebAdministration
foreach ($feature in @('Web-Server', 'Web-Http-Redirect', 'Web-Scripting-Tools')) {
    if (-not (Get-WindowsFeature $feature).Installed) { throw "Install the $feature Windows feature first (or use -InstallWindowsFeatures)." }
}
if (-not (Get-WebGlobalModule | Where-Object Name -eq 'AspNetCoreModuleV2')) { throw 'Install or repair the x64 .NET 10 Hosting Bundle after installing IIS, then restart WAS/W3SVC.' }
if ((Test-Path "IIS:\Sites\$SiteName") -or (Test-Path "IIS:\Sites\$SiteName.Redirect") -or (Test-Path "IIS:\AppPools\$AppPoolName") -or (Get-Service 'ScanBack.Worker' -ErrorAction SilentlyContinue)) { throw 'This is a first-install script. Use Update-ScanBack.ps1 for an existing installation.' }
if (Get-WebBinding | Where-Object { $parts = $_.bindingInformation.Split(':'); $parts[-1] -eq $HostName -and $parts[-2] -in @('80', '443') }) { throw 'This hostname already has an IIS binding on port 80/443; review existing sites before installing.' }
if ((Test-Path $InstallRoot) -and (Get-ChildItem $InstallRoot -Force | Select-Object -First 1)) { throw 'InstallRoot must be empty for a first installation.' }
$tlsThumbprint = $HttpsCertificateThumbprint.Replace(' ', '').ToUpperInvariant()
$tlsCertificate = Get-Item "Cert:\LocalMachine\My\$tlsThumbprint"
if (-not $tlsCertificate.HasPrivateKey -or $tlsCertificate.NotAfter -le (Get-Date) -or $tlsCertificate.NotBefore -gt (Get-Date)) { throw 'Install a currently valid TLS certificate with its private key in LocalMachine/My.' }
$dataThumbprint = $null
if ($config.Encryption.PSObject.Properties.Name -contains 'CertificateThumbprint' -and $config.Encryption.CertificateThumbprint) {
    $dataThumbprint = $config.Encryption.CertificateThumbprint.Replace(' ', '').ToUpperInvariant()
    if ($dataThumbprint -eq $tlsThumbprint) { throw 'Use separate HTTPS and Data Protection certificates, with independent renewal/backup.' }
} elseif ($config.Encryption.PSObject.Properties.Name -contains 'CertificatePath' -and $config.Encryption.CertificatePath) {
    $pfxPath = Get-CanonicalPath $config.Encryption.CertificatePath
    Assert-ChildPath $pfxPath $configDirectory
} else { throw 'Configure Encryption:CertificateThumbprint or Encryption:CertificatePath.' }
& (Join-Path $PackagePath 'Tools\ScanBack.Deployment.exe') check-config --config $ConfigPath --environment Production
Assert-NativeSuccess 'Production configuration validation'
New-Item -ItemType Directory $InstallRoot -Force | Out-Null
Copy-CodeDirectories $PackagePath $InstallRoot
New-WebAppPool -Name $AppPoolName | Out-Null
Set-ItemProperty "IIS:\AppPools\$AppPoolName" managedRuntimeVersion ''
Set-ItemProperty "IIS:\AppPools\$AppPoolName" enable32BitAppOnWin64 $false
Set-ItemProperty "IIS:\AppPools\$AppPoolName" startMode AlwaysRunning
Set-ItemProperty "IIS:\AppPools\$AppPoolName" processModel.identityType ApplicationPoolIdentity
Set-ItemProperty "IIS:\AppPools\$AppPoolName" processModel.idleTimeout ([TimeSpan]::Zero)
Set-ItemProperty "IIS:\AppPools\$AppPoolName" processModel.maxProcesses 1
Set-ItemProperty "IIS:\AppPools\$AppPoolName" recycling.disallowOverlappingRotation $true
Set-ItemProperty "IIS:\AppPools\$AppPoolName" recycling.periodicRestart.time ([TimeSpan]::Zero)
Stop-WebAppPool $AppPoolName
foreach ($entry in @{ SCANBACK_CONFIG_FILE = $ConfigPath; ASPNETCORE_ENVIRONMENT = 'Production'; DOTNET_ENVIRONMENT = 'Production' }.GetEnumerator()) {
    Add-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter "system.applicationHost/applicationPools/add[@name='$AppPoolName']/environmentVariables" -Name '.' -Value @{ name = $entry.Key; value = $entry.Value }
}
& sc.exe create 'ScanBack.Worker' binPath= ('"' + (Join-Path $InstallRoot 'Worker\ScanBack.Worker.exe') + '"') start= demand obj= 'NT SERVICE\ScanBack.Worker' DisplayName= 'ScanBack background worker'
Assert-NativeSuccess 'Create worker service'
& sc.exe description 'ScanBack.Worker' 'ScanBack notification outbox and retention processor. Restricted virtual service account.' | Out-Null
Assert-NativeSuccess 'Set worker service description'
if (-not [Diagnostics.EventLog]::SourceExists('ScanBack.Worker')) { New-EventLog -LogName Application -Source 'ScanBack.Worker' }
& sc.exe failure 'ScanBack.Worker' reset= 86400 actions= restart/10000/restart/30000/restart/60000 | Out-Null
Assert-NativeSuccess 'Configure worker recovery'
New-ItemProperty 'HKLM:\SYSTEM\CurrentControlSet\Services\ScanBack.Worker' -Name Environment -PropertyType MultiString -Value @('DOTNET_ENVIRONMENT=Production', "SCANBACK_CONFIG_FILE=$ConfigPath") -Force | Out-Null
$accounts = @("IIS APPPOOL\$AppPoolName", 'NT SERVICE\ScanBack.Worker')
Set-PrivateDirectory $DataRoot
Set-PrivateDirectory $configDirectory -Readers $accounts
Set-PrivateDirectory $keys -Writers $accounts
Set-PrivateDirectory $photos -Writers $accounts
Set-PrivateDirectory $InstallRoot -Readers $accounts
if ($dataThumbprint) { Grant-CertificateRead $dataThumbprint $accounts }
if ($config.Encryption.PSObject.Properties.Name -contains 'DecryptionCertificateThumbprints') {
    foreach ($oldThumbprint in $config.Encryption.DecryptionCertificateThumbprints) {
        Grant-CertificateRead $oldThumbprint.Replace(' ', '') $accounts -AllowExpired
    }
}
$webRoot = Join-Path $InstallRoot 'Web'
'ScanBack deployment is in progress.' | Set-Content (Join-Path $webRoot 'app_offline.htm') -Encoding UTF8
New-Website -Name $SiteName -PhysicalPath $webRoot -ApplicationPool $AppPoolName -Port 443 -HostHeader $HostName -Ssl | Out-Null
Stop-Website $SiteName
Set-WebBinding -Name $SiteName -BindingInformation "*:443:$HostName" -PropertyName sslFlags -Value 1
(Get-WebBinding -Name $SiteName -Protocol https).AddSslCertificate($tlsThumbprint, 'My')
Set-ItemProperty "IIS:\Sites\$SiteName" serverAutoStart $false
Set-ItemProperty "IIS:\Sites\$SiteName" logFile.logExtFileFlags 'Date,Time,Method,HttpStatus,HttpSubStatus,Win32Status,TimeTaken,ServerPort,ProtocolVersion'
Set-ItemProperty "IIS:\Sites\$SiteName" logFile.logFormat W3C
Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $SiteName -Filter 'system.webServer/security/authentication/anonymousAuthentication' -Name userName -Value ''
if ((Get-WindowsFeature Web-AppInit).Installed) {
    Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter "system.applicationHost/sites/site[@name='$SiteName']/application[@path='/']" -Name preloadEnabled -Value $true
}
$redirectName = "$SiteName.Redirect"
$redirectRoot = Join-Path $InstallRoot 'Redirect'
New-Item -ItemType Directory $redirectRoot -Force | Out-Null
$escapedHost = [Security.SecurityElement]::Escape($HostName)
"<configuration><system.webServer><httpRedirect enabled=`"true`" destination=`"https://$escapedHost/`" exactDestination=`"false`" childOnly=`"false`" httpResponseStatus=`"Permanent`" /></system.webServer></configuration>" | Set-Content (Join-Path $redirectRoot 'web.config') -Encoding UTF8
New-Website -Name $redirectName -PhysicalPath $redirectRoot -ApplicationPool $AppPoolName -Port 80 -HostHeader $HostName | Out-Null
Stop-Website $redirectName
Set-ItemProperty "IIS:\Sites\$redirectName" serverAutoStart $false
Set-ItemProperty "IIS:\Sites\$redirectName" logFile.logExtFileFlags 'Date,Time,Method,HttpStatus,HttpSubStatus,Win32Status,TimeTaken,ServerPort,ProtocolVersion'
Set-WebConfigurationProperty -PSPath 'MACHINE/WEBROOT/APPHOST' -Location $redirectName -Filter 'system.webServer/security/authentication/anonymousAuthentication' -Name userName -Value ''
Write-Host 'Installed, with both sites and the worker stopped. Run SQL provisioning, migrations, initialization and administrator creation under the operator identity, then Start-ScanBack.ps1.'
