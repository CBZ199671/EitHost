[CmdletBinding()]
param(
    [string]$InfPath,
    [string]$SmokeReportPath,
    [string]$LogPath
)

$ErrorActionPreference = 'Stop'

$installerPath = Join-Path $PSScriptRoot 'install-usb2070-driver.ps1'
if (-not (Test-Path -LiteralPath $installerPath)) {
    throw "Installer script not found: $installerPath"
}

$arguments = New-Object System.Collections.Generic.List[string]
$arguments.Add('-NoProfile')
$arguments.Add('-NoExit')
$arguments.Add('-ExecutionPolicy')
$arguments.Add('Bypass')
$arguments.Add('-File')
$arguments.Add("`"$installerPath`"")

if (-not [string]::IsNullOrWhiteSpace($InfPath)) {
    $arguments.Add('-InfPath')
    $arguments.Add("`"$([System.IO.Path]::GetFullPath($InfPath))`"")
}

if (-not [string]::IsNullOrWhiteSpace($SmokeReportPath)) {
    $arguments.Add('-SmokeReportPath')
    $arguments.Add("`"$([System.IO.Path]::GetFullPath($SmokeReportPath))`"")
}

if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
    $arguments.Add('-LogPath')
    $arguments.Add("`"$([System.IO.Path]::GetFullPath($LogPath))`"")
}

Write-Host 'Launching administrator PowerShell through UAC for USB2070 driver install.'
Write-Host "Installer script: $installerPath"
Write-Host 'The administrator window stays open so pnputil output remains visible.'
Start-Process -FilePath 'powershell.exe' -Verb RunAs -Wait -ArgumentList $arguments
