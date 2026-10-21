# Shared by the Windows PowerShell 5.1 deployment scripts.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
function Assert-Administrator {
    if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run this script in an elevated Windows PowerShell session.'
    }
}
function Assert-NativeSuccess([string] $Action) {
    if ($LASTEXITCODE -ne 0) { throw "$Action failed (exit $LASTEXITCODE)." }
}
function Get-CanonicalPath([string] $Path) {
    if ($Path -notmatch '^[A-Za-z]:[\\/]') { throw 'Use an absolute local drive path, such as C:\ProgramData\ScanBack.' }
    return [IO.Path]::GetFullPath($Path).TrimEnd('\')
}
function Assert-ChildPath([string] $Path, [string] $Parent) {
    if (-not $Path.StartsWith($Parent.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Data paths must be separate subdirectories of DataRoot.' }
}
function Set-PrivateDirectory([string] $Path, [string[]] $Readers = @(), [string[]] $Writers = @()) {
    $Path = Get-CanonicalPath $Path
    $volumeRoot = [IO.Path]::GetPathRoot($Path).TrimEnd('\')
    if ($Path -eq $volumeRoot) { throw 'Refusing to change ACLs on a drive root; choose a dedicated application directory.' }
    foreach ($systemPath in @($env:windir, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramData, $env:USERPROFILE)) {
        if ($systemPath -and $Path.Equals($systemPath.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing to change ACLs on a system/profile root.' }
    }
    if ($env:windir -and $Path.StartsWith($env:windir.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Use an application data directory outside Windows.' }
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544')) {
        $identity = New-Object Security.Principal.SecurityIdentifier($sid)
        $rule = New-Object Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    foreach ($account in $Readers) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($account, 'ReadAndExecute', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    foreach ($account in $Writers) {
        $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($account, 'Modify', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
    # Reset children to these inherited ACLs, removing stale explicit access.
    if (Get-ChildItem -LiteralPath $Path -Force | Select-Object -First 1) {
        & icacls.exe (Join-Path $Path '*') /reset /T /Q | Out-Null
        Assert-NativeSuccess 'Reset child ACLs'
    }
}
function Assert-Package([string] $PackagePath) {
    $manifest = Get-Content (Join-Path $PackagePath 'manifest.json') -Raw | ConvertFrom-Json
    if ($manifest.Format -ne 1 -or $manifest.Runtime -ne 'win-x64' -or -not $manifest.FrameworkDependent) { throw 'Unsupported package manifest.' }
    foreach ($file in $manifest.Files) {
        $fullPath = [IO.Path]::GetFullPath((Join-Path $PackagePath $file.Path))
        Assert-ChildPath $fullPath ([IO.Path]::GetFullPath($PackagePath).TrimEnd('\'))
        if ((Get-FileHash -LiteralPath $fullPath -Algorithm SHA256).Hash -ne $file.Sha256) { throw "Package checksum failed: $($file.Path)" }
    }
    $expected = @($manifest.Files | ForEach-Object { $_.Path.Replace('/', '\') })
    foreach ($actual in Get-ChildItem -LiteralPath $PackagePath -File -Recurse) {
        $relative = $actual.FullName.Substring($PackagePath.TrimEnd('\').Length + 1)
        if ($relative -ne 'manifest.json' -and $expected -notcontains $relative) { throw "Package contains an unlisted file: $relative" }
    }
    foreach ($required in @('Web\ScanBack.Web.exe', 'Web\web.config', 'Worker\ScanBack.Worker.exe', 'Tools\ScanBack.Deployment.exe')) {
        if (-not (Test-Path (Join-Path $PackagePath $required) -PathType Leaf)) { throw "Package is missing $required." }
    }
    foreach ($folder in @('Web', 'Worker', 'Tools')) {
        $publishedFolder = Join-Path $PackagePath $folder
        if ((Get-ChildItem -LiteralPath $publishedFolder -Filter 'appsettings.*.json' -File) -or (Test-Path (Join-Path $publishedFolder 'App_Data'))) { throw 'Package contains environment-specific configuration or runtime data.' }
    }
}
function Grant-CertificateRead([string] $Thumbprint, [string[]] $Accounts, [switch] $AllowExpired) {
    $certificate = Get-Item "Cert:\LocalMachine\My\$Thumbprint" -ErrorAction Stop
    if (-not $certificate.HasPrivateKey -or (-not $AllowExpired -and ($certificate.NotAfter -le (Get-Date) -or $certificate.NotBefore -gt (Get-Date)))) { throw 'The active data protection certificate must be valid and have a machine private key; retained decryption certificates also require their private keys.' }
    $rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
    if ($null -eq $rsa) { throw 'The supplied script requires an RSA data protection certificate; configure other key providers manually.' }
    try {
        if ($rsa -is [Security.Cryptography.RSACng]) {
            $keyFile = Join-Path $env:ProgramData ('Microsoft\Crypto\Keys\' + $rsa.Key.UniqueName)
        } elseif ($rsa -is [Security.Cryptography.RSACryptoServiceProvider]) {
            $keyFile = Join-Path $env:ProgramData ('Microsoft\Crypto\RSA\MachineKeys\' + $rsa.CspKeyContainerInfo.UniqueKeyContainerName)
        } else { throw 'Unsupported certificate private-key provider; grant private-key read access manually.' }
        if (-not (Test-Path -LiteralPath $keyFile)) { throw 'Machine private-key file not found; use a machine certificate with a supported key provider.' }
        foreach ($account in $Accounts) {
            & icacls.exe $keyFile /grant:r ($account + ':(R)') | Out-Null
            Assert-NativeSuccess 'Grant data protection certificate private-key access'
        }
    } finally { $rsa.Dispose() }
}
function Copy-CodeDirectories([string] $Source, [string] $Destination) {
    foreach ($folder in @('Web', 'Worker', 'Tools')) {
        $target = Join-Path $Destination $folder
        if (Test-Path $target) { Remove-Item -LiteralPath $target -Recurse -Force }
        Copy-Item -LiteralPath (Join-Path $Source $folder) -Destination $target -Recurse
    }
}
