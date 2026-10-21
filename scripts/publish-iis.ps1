#Requires -Version 7.0
[CmdletBinding()]
param(
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/iis'),
    [ValidateSet('Release', 'Debug')] [string] $Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
if ($output -eq $repository -or $repository.StartsWith($output.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar)) {
    throw 'OutputDirectory must not contain the repository.'
}
# A new staging directory makes a failed publish leave the previous package intact.
$stage = Join-Path $output ('staging-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    foreach ($project in @('Web', 'Worker', 'Deployment')) {
        $folder = if ($project -eq 'Deployment') { 'Tools' } else { $project }
        & dotnet publish (Join-Path $repository "src/ScanBack.$project/ScanBack.$project.csproj") `
            --configuration $Configuration --runtime win-x64 --self-contained false `
            -p:UseAppHost=true -p:ContinuousIntegrationBuild=true -p:RestoreLockedMode=true `
            --output (Join-Path $stage $folder) --nologo
        if ($LASTEXITCODE -ne 0) { throw "Publish failed for $project ($LASTEXITCODE)." }
        # Web SDK includes appsettings*.json even when Git ignores the file. Never
        # ship a developer's private SQL settings, local key ring or uploaded data.
        $publishedFolder = Join-Path $stage $folder
        Get-ChildItem -LiteralPath $publishedFolder -Filter 'appsettings.*.json' -File | Remove-Item -Force
        $data = Join-Path $publishedFolder 'App_Data'
        if (Test-Path -LiteralPath $data) { Remove-Item -LiteralPath $data -Recurse -Force }
    }
    Copy-Item (Join-Path $repository 'deploy') (Join-Path $stage 'deploy') -Recurse
    Copy-Item (Join-Path $repository 'deploy/web.config') (Join-Path $stage 'Web/web.config') -Force
    New-Item -ItemType Directory -Path (Join-Path $stage 'docs') -Force | Out-Null
    foreach ($document in @('iis-deployment.md', 'operations.md', 'security.md', 'privacy-operations.md', 'seo.md')) {
        Copy-Item (Join-Path $repository "docs/$document") (Join-Path $stage "docs/$document")
    }
    Copy-Item (Join-Path $repository 'deploy/INSTALL.md') (Join-Path $stage 'INSTALL.md')
    Copy-Item (Join-Path $repository 'LICENSE') (Join-Path $stage 'LICENSE')
    Copy-Item (Join-Path $repository 'THIRD_PARTY_NOTICES.md') (Join-Path $stage 'THIRD_PARTY_NOTICES.md')
    $manifest = foreach ($file in Get-ChildItem $stage -File -Recurse | Sort-Object FullName) {
        [ordered]@{ Path = [IO.Path]::GetRelativePath($stage, $file.FullName).Replace('\', '/'); Sha256 = (Get-FileHash $file.FullName -Algorithm SHA256).Hash }
    }
    [ordered]@{ Format = 1; Runtime = 'win-x64'; FrameworkDependent = $true; Files = @($manifest) } |
        ConvertTo-Json -Depth 5 | Set-Content (Join-Path $stage 'manifest.json') -Encoding utf8
    $package = Join-Path $output 'ScanBack-IIS-win-x64.zip'
    $temporaryZip = Join-Path $output ('package-' + [Guid]::NewGuid().ToString('N') + '.zip')
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $temporaryZip -CompressionLevel Optimal
    Move-Item $temporaryZip $package -Force
    (Get-FileHash $package -Algorithm SHA256).Hash + '  ScanBack-IIS-win-x64.zip' |
        Set-Content ($package + '.sha256') -Encoding ascii
    Write-Host "Package: $package"
    Write-Host 'Windows Server + IIS + .NET 10 Hosting Bundle are required on the target.'
}
finally { if (Test-Path $stage) { Remove-Item $stage -Recurse -Force } }
