#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$InfPath,
    [string]$SmokeReportPath,
    [string]$LogPath
)

$ErrorActionPreference = 'Stop'

function Resolve-RepoRoot {
    $directory = Get-Item -LiteralPath $PSScriptRoot
    while ($null -ne $directory) {
        if (Test-Path -LiteralPath (Join-Path $directory.FullName 'EitHost.slnx')) {
            return $directory.FullName
        }

        $directory = $directory.Parent
    }

    return (Split-Path -Parent $PSScriptRoot)
}

function Get-SearchRoots {
    param([string]$RepoRoot)

    $roots = New-Object System.Collections.Generic.List[string]
    foreach ($candidate in @(
            $RepoRoot,
            (Split-Path -Parent $RepoRoot),
            (Split-Path -Parent $PSScriptRoot)
        )) {
        if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate)) {
            $full = [System.IO.Path]::GetFullPath($candidate)
            if (-not $roots.Contains($full)) {
                $roots.Add($full)
            }
        }
    }

    return $roots
}

function Get-InfCandidateInfo {
    param([string]$Path)

    $driverDirectory = Split-Path -Parent $Path
    $catalogPath = Join-Path $driverDirectory 'usb2070x64.cat'
    $catalogStatus = 'MissingCatalog'
    $catalogStatusMessage = 'catalog file missing'

    if (Test-Path -LiteralPath $catalogPath) {
        $signature = Get-AuthenticodeSignature -LiteralPath $catalogPath
        $catalogStatus = $signature.Status.ToString()
        $catalogStatusMessage = $signature.StatusMessage
    }

    $validRank = if ($catalogStatus -eq 'Valid') { 0 } else { 10 }
    $sdkVersionRank = 9999
    if ($Path -match 'USB2070 SDK[^\r\n\\]*(\d+)\.(\d+)') {
        $sdkVersionRank = -(([int]$Matches[1] * 100) + [int]$Matches[2])
    }

    $windowsRank = if ($Path -match 'WIN10') { 0 } elseif ($Path -match 'WIN7-10') { 1 } elseif ($Path -match 'WIN7-8') { 2 } else { 3 }
    $signerRank = if ($catalogStatus -eq 'Valid' -and $catalogStatusMessage -match 'Signature verified') { 0 } else { 1 }
    $copyRank = if ($Path -match ' - ') { 1 } else { 0 }

    [pscustomobject]@{
        Path = [System.IO.Path]::GetFullPath($Path)
        CatalogPath = $catalogPath
        CatalogStatus = $catalogStatus
        CatalogStatusMessage = $catalogStatusMessage
        ValidRank = $validRank
        SdkVersionRank = $sdkVersionRank
        WindowsRank = $windowsRank
        SignerRank = $signerRank
        CopyRank = $copyRank
    }
}

function Find-Usb2070InfCandidates {
    param([string]$RepoRoot)

    $seen = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
    $candidates = New-Object System.Collections.Generic.List[object]

    foreach ($root in (Get-SearchRoots -RepoRoot $RepoRoot)) {
        Get-ChildItem -LiteralPath $root -Recurse -Filter 'USB2070.inf' -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match 'Driver x64' } |
            ForEach-Object {
                $fullPath = [System.IO.Path]::GetFullPath($_.FullName)
                if ($seen.Add($fullPath)) {
                    $candidates.Add((Get-InfCandidateInfo -Path $fullPath))
                }
            }
    }

    return $candidates |
        Sort-Object -Property ValidRank, SdkVersionRank, WindowsRank, SignerRank, CopyRank, Path
}

function Resolve-Usb2070InfPath {
    param(
        [string]$RequestedInfPath,
        [string]$RepoRoot
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedInfPath)) {
        $fullPath = [System.IO.Path]::GetFullPath($RequestedInfPath)
        if (-not (Test-Path -LiteralPath $fullPath)) {
            throw "USB2070.inf not found: $fullPath"
        }

        return $fullPath
    }

    $candidates = @(Find-Usb2070InfCandidates -RepoRoot $RepoRoot)
    if ($candidates.Count -eq 0) {
        throw "USB2070.inf not found. Pass -InfPath with the driver INF path."
    }

    $best = $candidates[0]
    return $best.Path
}

function Write-InstallLog {
    param([string]$Message)

    $line = "[{0:yyyy-MM-dd HH:mm:ss}] {1}" -f (Get-Date), $Message
    $line | Tee-Object -FilePath $LogPath -Append
}

$repoRoot = Resolve-RepoRoot
$InfPath = Resolve-Usb2070InfPath -RequestedInfPath $InfPath -RepoRoot $repoRoot

if ([string]::IsNullOrWhiteSpace($SmokeReportPath)) {
    $SmokeReportPath = Join-Path $repoRoot 'artifacts\hardware-smoke-after-driver.md'
}

$SmokeReportPath = [System.IO.Path]::GetFullPath($SmokeReportPath)
$smokeReportDirectory = Split-Path -Parent $SmokeReportPath
if (-not (Test-Path -LiteralPath $smokeReportDirectory)) {
    New-Item -ItemType Directory -Path $smokeReportDirectory | Out-Null
}

if ([string]::IsNullOrWhiteSpace($LogPath)) {
    $LogPath = Join-Path $repoRoot 'artifacts\usb2070-driver-install.log'
}

$LogPath = [System.IO.Path]::GetFullPath($LogPath)
$logDirectory = Split-Path -Parent $LogPath
if (-not (Test-Path -LiteralPath $logDirectory)) {
    New-Item -ItemType Directory -Path $logDirectory | Out-Null
}

Write-InstallLog "USB2070 driver install started"
Write-InstallLog "Repository root: $repoRoot"
Write-InstallLog "USB2070 INF: $InfPath"
Write-InstallLog "Smoke report: $SmokeReportPath"
Write-InstallLog "Log: $LogPath"

$candidateInfo = Get-InfCandidateInfo -Path $InfPath
Write-InstallLog "Selected INF catalog status: $($candidateInfo.CatalogStatus) - $($candidateInfo.CatalogStatusMessage)"
Write-InstallLog "Discovered INF candidates:"
foreach ($candidate in @(Find-Usb2070InfCandidates -RepoRoot $repoRoot)) {
    Write-InstallLog "  $($candidate.CatalogStatus) vr=$($candidate.ValidRank) sdk=$($candidate.SdkVersionRank) win=$($candidate.WindowsRank) signer=$($candidate.SignerRank) copy=$($candidate.CopyRank) path=$($candidate.Path)"
}

$driverDirectory = Split-Path -Parent $InfPath
foreach ($signaturePath in @(
        (Join-Path $driverDirectory 'USB2070.inf'),
        (Join-Path $driverDirectory 'usb2070x64.cat'),
        (Join-Path $driverDirectory 'USB2070.sys')
    )) {
    if (Test-Path -LiteralPath $signaturePath) {
        $signature = Get-AuthenticodeSignature -LiteralPath $signaturePath
        Write-InstallLog "Signature $([System.IO.Path]::GetFileName($signaturePath)): $($signature.Status) - $($signature.StatusMessage)"
    }
}

Write-InstallLog "Install command: pnputil /add-driver `"$InfPath`" /install"

$pnputilExitCode = 0
if ($PSCmdlet.ShouldProcess($InfPath, 'Install USB2070 driver with pnputil')) {
    $pnputilOutput = & pnputil /add-driver $InfPath /install 2>&1
    $pnputilExitCode = $LASTEXITCODE
    foreach ($line in $pnputilOutput) {
        Write-InstallLog $line.ToString()
    }

    Write-InstallLog "pnputil exit code: $pnputilExitCode"
    if ($pnputilExitCode -ne 0) {
        Write-InstallLog "pnputil failed with exit code: $pnputilExitCode"
    }
}

Write-InstallLog "Regenerating hardware smoke report: $SmokeReportPath"
$smokeOutput = dotnet run --project (Join-Path $repoRoot 'src\EitHost.Tools\EitHost.Tools.csproj') -- hardware-smoke --output $SmokeReportPath 2>&1
$smokeExitCode = $LASTEXITCODE
foreach ($line in $smokeOutput) {
    Write-InstallLog $line.ToString()
}

Write-InstallLog "hardware smoke exit code: $smokeExitCode"
Write-InstallLog "USB2070 driver install finished"
Write-Host "USB2070 driver install log: $LogPath"
Write-Host "Post-install hardware report: $SmokeReportPath"

if ($pnputilExitCode -ne 0) {
    throw "pnputil failed with exit code $pnputilExitCode. See log: $LogPath"
}

if ($smokeExitCode -ne 0) {
    throw "hardware smoke report failed with exit code $smokeExitCode. See log: $LogPath"
}
