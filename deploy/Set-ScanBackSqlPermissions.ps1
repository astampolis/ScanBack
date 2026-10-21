#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Server,
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_]{0,62}$')] [string] $DatabaseName = 'ScanBack',
    [ValidatePattern('^[A-Za-z][A-Za-z0-9_.-]{0,50}$')] [string] $AppPoolName = 'ScanBack'
)
$ErrorActionPreference = 'Stop'
# Run on the SQL/IIS machine as a SQL security administrator after creating the empty DB.
# Inputs substituted by sqlcmd are allowlisted above; do not bypass this wrapper with untrusted SQLCMD variables.
& sqlcmd -S $Server -E -N -b -i (Join-Path $PSScriptRoot 'sql/runtime-permissions.sql') -v "DatabaseName=$DatabaseName" "AppPoolName=$AppPoolName"
if ($LASTEXITCODE -ne 0) { throw "SQL permission provisioning failed ($LASTEXITCODE)." }
Write-Host 'Runtime Windows principals have DML permission only. Use a separate migration operator.'
