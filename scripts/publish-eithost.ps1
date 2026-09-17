param(
    [string]$Configuration = "Release",
    [string]$OutputRoot = "",
    [string]$Runtime = "",
    [string]$Version = "",
    [string]$InstallRoot = "",
    [string]$FirmwareHexPath = "",
    [string]$TimingReportPath = "",
    [switch]$MinimalGui
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1') -Force

function Write-Log {
    param([string]$Message)
    $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
    Write-Host "[$timestamp] $Message"
}

function Get-ChildRelativePath {
    param(
        [string]$RootPath,
        [string]$ChildPath
    )

    $root = [System.IO.Path]::GetFullPath($RootPath).TrimEnd('\') + '\'
    $child = [System.IO.Path]::GetFullPath($ChildPath)
    if (-not $child.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside root: $child"
    }

    return $child.Substring($root.Length)
}

function Invoke-DotNetPublish {
    param(
        [string]$ProjectPath,
        [string]$OutputPath,
        [string]$Configuration,
        [string]$Runtime,
        [string]$Version,
        [bool]$MinimalGui
    )

    $args = @(
        "publish",
        $ProjectPath,
        "--configuration",
        $Configuration,
        "--output",
        $OutputPath,
        "-p:Version=$Version"
    )

    if ($MinimalGui) {
        $args += @(
            "--runtime", $Runtime,
            "--self-contained", "true",
            "-p:PublishSingleFile=true",
            "-p:IncludeNativeLibrariesForSelfExtract=true",
            "-p:EnableCompressionInSingleFile=true",
            "-p:DebugType=None",
            "-p:DebugSymbols=false"
        )
    }
    else {
        $args += "--no-restore"
        if (-not [string]::IsNullOrWhiteSpace($Runtime)) {
            $args += @("--runtime", $Runtime, "--self-contained", "false")
        }
    }

    Write-Log ("dotnet " + ($args -join " "))
    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $ProjectPath with exit code $LASTEXITCODE"
    }
}

function Test-RequiredFiles {
    param(
        [string]$Name,
        [string]$DirectoryPath,
        [string[]]$RequiredFiles
    )

    $checks = foreach ($fileName in $RequiredFiles) {
        $path = Join-Path $DirectoryPath $fileName
        [pscustomobject]@{
            Name = $fileName
            Path = $path
            Exists = Test-Path -LiteralPath $path -PathType Leaf
        }
    }

    $missing = @($checks | Where-Object { -not $_.Exists })
    [pscustomobject]@{
        Name = $Name
        Directory = $DirectoryPath
        Passed = ($missing.Count -eq 0)
        RequiredFiles = $checks
        MissingFiles = @($missing | Select-Object -ExpandProperty Name)
    }
}

function Add-MarkdownSection {
    param(
        [System.Text.StringBuilder]$Builder,
        [object]$Check
    )

    [void]$Builder.AppendLine("## $($Check.Name)")
    [void]$Builder.AppendLine()
    [void]$Builder.AppendLine("- Directory: ``$($Check.Directory)``")
    [void]$Builder.AppendLine("- Passed: $($Check.Passed)")
    [void]$Builder.AppendLine("- Required files:")
    foreach ($item in $Check.RequiredFiles) {
        [void]$Builder.AppendLine("  - ``$($item.Name)``: $($item.Exists)")
    }

    if ($Check.MissingFiles.Count -gt 0) {
        [void]$Builder.AppendLine("- Missing files: $($Check.MissingFiles -join ', ')")
    }

    [void]$Builder.AppendLine()
}

$scriptDirectory = Split-Path -Parent $PSCommandPath
$repoRoot = Split-Path -Parent $scriptDirectory
if ([string]::IsNullOrWhiteSpace($Version)) {
    $projectXml = [xml](Get-Content -LiteralPath (Join-Path $repoRoot 'src\EitHost.App\EitHost.App.csproj') -Raw)
    $Version = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version').InnerText.Trim()
}
# Ordinary publishing has one destination; explicit isolated outputs retain the legacy tooling flow.
if ([string]::IsNullOrWhiteSpace($OutputRoot) -and [string]::IsNullOrWhiteSpace($InstallRoot)) {
    if ($Configuration -ne 'Release' -or ($Runtime -ne '' -and $Runtime -ne 'win-x64') -or
        -not [string]::IsNullOrWhiteSpace($FirmwareHexPath) -or -not [string]::IsNullOrWhiteSpace($TimingReportPath)) {
        throw 'Canonical publishing uses Release/win-x64. Explicit diagnostic or combined deliveries require -OutputRoot.'
    }
    & (Join-Path $scriptDirectory 'package-eithost.ps1') -Version $Version
    return
}
if ($MinimalGui -and [string]::IsNullOrWhiteSpace($Runtime)) {
    $Runtime = "win-x64"
}
$temporaryOutput = $false
if ($MinimalGui) {
    if ([string]::IsNullOrWhiteSpace($InstallRoot)) {
        $InstallRoot = Join-Path $repoRoot "release\EitHost-Windows-x64"
    }
    $InstallRoot = [System.IO.Path]::GetFullPath($InstallRoot)
    if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $OutputRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("EitHost.Publish." + [guid]::NewGuid().ToString("N"))
        $temporaryOutput = $true
    }
}
elseif ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    throw "Non-minimal publish requires an explicit -OutputRoot."
}

$OutputRoot = [System.IO.Path]::GetFullPath($OutputRoot)
if ((Test-Path -LiteralPath $OutputRoot) -and
    @(Get-ChildItem -LiteralPath $OutputRoot -Force).Count -gt 0) {
    throw "Publish output must be a new isolated directory: $OutputRoot"
}

$appProject = Join-Path $repoRoot "src\EitHost.App\EitHost.App.csproj"
$toolsProject = Join-Path $repoRoot "src\EitHost.Tools\EitHost.Tools.csproj"
$appOutput = Join-Path $OutputRoot "EitHost.App"
$toolsOutput = Join-Path $OutputRoot "EitHost.Tools"
$runtimeLabel = if ([string]::IsNullOrWhiteSpace($Runtime)) { "framework-dependent" } else { $Runtime }

Write-Log "EitHost publish started"
Write-Log "Repository root: $repoRoot"
Write-Log "Output root: $OutputRoot"
Write-Log "Configuration: $Configuration"
Write-Log "Runtime: $Runtime"
if ($MinimalGui) {
    Write-Log "Install root: $InstallRoot"
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null

try {
Invoke-DotNetPublish `
    -ProjectPath $appProject `
    -OutputPath $appOutput `
    -Configuration $Configuration `
    -Runtime $Runtime `
    -Version $Version `
    -MinimalGui $MinimalGui

if ($MinimalGui) {
    $minimalRequiredFiles = @(
        "EitHost.App.exe",
        "HDF.PInvoke.dll",
        "HDF.PInvoke.dll.config",
        "hdf5.dll",
        "hdf5_hl.dll",
        "USB2070.dll",
        "eithost.reconstruction.example.json"
    )
    $appCheck = Test-RequiredFiles `
        -Name "EitHost.App minimal GUI" `
        -DirectoryPath $appOutput `
        -RequiredFiles $minimalRequiredFiles
    if (-not $appCheck.Passed) {
        throw "Minimal GUI publish is missing required files: $($appCheck.MissingFiles -join ', ')"
    }

    $packageDirectory = Join-Path $OutputRoot "EitHost-Windows-x64"
    New-Item -ItemType Directory -Force -Path $packageDirectory | Out-Null
    foreach ($fileName in $minimalRequiredFiles) {
        Copy-Item `
            -LiteralPath (Join-Path $appOutput $fileName) `
            -Destination (Join-Path $packageDirectory $fileName)
    }

    $packageScripts = Join-Path $packageDirectory "scripts"
    New-Item -ItemType Directory -Force -Path $packageScripts | Out-Null
    foreach ($scriptName in @(
        "install-usb2070-driver.ps1",
        "start-usb2070-driver-install-admin.ps1",
        "update-eithost-install.ps1"
    )) {
        Copy-Item `
            -LiteralPath (Join-Path $scriptDirectory $scriptName) `
            -Destination (Join-Path $packageScripts $scriptName)
    }
    Copy-Item `
        -LiteralPath (Join-Path $repoRoot "packaging\README.zh-CN.md") `
        -Destination (Join-Path $packageDirectory "README.md")
    foreach ($fileName in @("LICENSE", "THIRD_PARTY_NOTICES.md")) {
        Copy-Item `
            -LiteralPath (Join-Path $repoRoot $fileName) `
            -Destination (Join-Path $packageDirectory $fileName)
    }
    [ordered]@{
        Product = "EitHost"
        Version = $Version
        BuiltAt = (Get-Date).ToString("O")
        Runtime = $Runtime
        SelfContained = $true
        Trimmed = $false
    } | ConvertTo-Json | Set-Content `
        -LiteralPath (Join-Path $packageDirectory "VERSION.json") `
        -Encoding UTF8

    $smokeDirectory = Join-Path $OutputRoot "hdf5-smoke"
    New-Item -ItemType Directory -Force -Path $smokeDirectory | Out-Null
    $smokeFailurePath = Join-Path $smokeDirectory "hdf5-smoke-test.failure.txt"
    Write-Log "Running packaged HDF5 smoke test"
    $smokeProcess = & (Join-Path $scriptDirectory 'invoke-bounded-process.ps1') `
        -ExecutablePath (Join-Path $packageDirectory "EitHost.App.exe") `
        -ProcessArguments ('--hdf5-smoke-test "' + $smokeDirectory + '"') `
        -WorkingDirectory $smokeDirectory `
        -TimeoutMilliseconds 60000 `
        -EnvironmentVariables @{
            DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $smokeDirectory 'bundle-cache'
            DOTNET_ROOT = Join-Path $smokeDirectory 'no-shared-runtime'
            DOTNET_ROOT_X64 = Join-Path $smokeDirectory 'no-shared-runtime'
            DOTNET_MULTILEVEL_LOOKUP = '0'
        }
    if ($smokeProcess.TimedOut) {
        throw "Packaged HDF5 smoke test timed out after 60 seconds."
    }
    $smokeTestPassed = $smokeProcess.ExitCode -eq 0 -and
        -not (Test-Path -LiteralPath $smokeFailurePath -PathType Leaf)
    if (-not $smokeTestPassed) {
        if (Test-Path -LiteralPath $smokeFailurePath -PathType Leaf) {
            Write-Host (Get-Content -Raw -LiteralPath $smokeFailurePath)
        }
        throw "Packaged HDF5 smoke test failed with exit code $($smokeProcess.ExitCode)"
    }
    $resolvedSmokeDirectory = [System.IO.Path]::GetFullPath($smokeDirectory)
    $expectedSmokeDirectory = [System.IO.Path]::GetFullPath((Join-Path $OutputRoot "hdf5-smoke"))
    if (-not [string]::Equals(
            $resolvedSmokeDirectory,
            $expectedSmokeDirectory,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove unexpected HDF5 smoke directory: $resolvedSmokeDirectory"
    }
    Remove-Item -LiteralPath $resolvedSmokeDirectory -Recurse -Force
    if (Test-Path -LiteralPath $resolvedSmokeDirectory) {
        throw "HDF5 smoke directory cleanup did not complete: $resolvedSmokeDirectory"
    }

    $checksumPath = Join-Path $packageDirectory "SHA256SUMS.txt"
    $checksumLines = Get-ChildItem -Recurse -File -LiteralPath $packageDirectory |
        Where-Object { $_.FullName -ne $checksumPath } |
        Sort-Object FullName |
        ForEach-Object {
            $relativePath = (Get-ChildRelativePath -RootPath $packageDirectory -ChildPath $_.FullName).Replace('\', '/')
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $relativePath"
        }
    $checksumLines | Set-Content -LiteralPath $checksumPath -Encoding ASCII

    $sourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()
    $sourceDirty = -not [string]::IsNullOrWhiteSpace((& git -C $repoRoot status --short) -join "`n")
    $installUpdateScript = Join-Path $scriptDirectory "update-eithost-install.ps1"
    $installUpdate = & $installUpdateScript `
        -PackageDirectory $packageDirectory `
        -InstallRoot $InstallRoot
    if (-not $installUpdate.DataPreserved) {
        throw "Install update did not preserve runtime Data."
    }
    if (-not $installUpdate.UpdateApplied) {
        throw "A previous committed update still has cleanup pending; close processes using the old backup and publish again. $($installUpdate.CleanupError)"
    }

    $manifestDirectory = Split-Path -Parent $InstallRoot
    $manifest = [pscustomobject]@{
        GeneratedAt = (Get-Date).ToString("O")
        RepositoryRoot = $repoRoot
        StagingRoot = $OutputRoot
        InstallRoot = $InstallRoot
        DataRoot = $installUpdate.DataRoot
        Configuration = $Configuration
        Runtime = $Runtime
        Version = $Version
        SourceCommit = $sourceCommit
        SourceDirty = $sourceDirty
        MinimalGui = $true
        SmokeTestPassed = $smokeTestPassed
        DataPreserved = $installUpdate.DataPreserved
        DataFileCount = $installUpdate.DataFileCount
        CleanupPending = $installUpdate.CleanupPending
        CleanupError = $installUpdate.CleanupError
        RecoveryPerformed = $installUpdate.RecoveryPerformed
        Passed = ($appCheck.Passed -and $smokeTestPassed)
        App = $appCheck
        Packages = [pscustomobject]@{
            GuiDirectory = $InstallRoot
            ChecksumFile = (Join-Path $InstallRoot "SHA256SUMS.txt")
        }
    }
    $jsonPath = Join-Path $manifestDirectory "publish-manifest.json"
    $markdownPath = Join-Path $manifestDirectory "publish-manifest.md"
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
    @(
        "# EitHost Minimal GUI Publish Manifest",
        "",
        "- GeneratedAt: ``$($manifest.GeneratedAt)``",
        "- SourceCommit: ``$sourceCommit``",
        "- SourceDirty: $sourceDirty",
        "- Runtime: ``$Runtime``",
        "- Version: ``$Version``",
        "- SmokeTestPassed: $smokeTestPassed",
        "- DataPreserved: $($installUpdate.DataPreserved)",
        "- CleanupPending: $($installUpdate.CleanupPending)",
        "- InstallRoot: ``$InstallRoot``",
        "- DataRoot: ``$($installUpdate.DataRoot)``",
        "- Checksums: ``$(Join-Path $InstallRoot 'SHA256SUMS.txt')``"
    ) | Set-Content -LiteralPath $markdownPath -Encoding UTF8

    Write-Log "Minimal GUI publish manifest: $markdownPath"
    Write-Log "Minimal GUI install: $InstallRoot"
    Write-Log "EitHost publish finished"
    return
}

Invoke-DotNetPublish `
    -ProjectPath $toolsProject `
    -OutputPath $toolsOutput `
    -Configuration $Configuration `
    -Runtime $Runtime `
    -Version $Version `
    -MinimalGui $false

$appCheck = Test-RequiredFiles `
    -Name "EitHost.App" `
    -DirectoryPath $appOutput `
    -RequiredFiles @("EitHost.App.exe", "EitHost.App.dll", "EitHost.Core.dll", "USB2070.dll", "eithost.reconstruction.example.json")
$toolsCheck = Test-RequiredFiles `
    -Name "EitHost.Tools" `
    -DirectoryPath $toolsOutput `
    -RequiredFiles @("EitHost.Tools.exe", "EitHost.Tools.dll", "EitHost.Core.dll", "USB2070.dll")

$appZipPath = Join-Path $OutputRoot "EitHost.App-v$Version-$runtimeLabel.zip"
$toolsZipPath = Join-Path $OutputRoot "EitHost.Tools-v$Version-$runtimeLabel.zip"
Compress-Archive -Path (Join-Path $appOutput '*') -DestinationPath $appZipPath
Compress-Archive -Path (Join-Path $toolsOutput '*') -DestinationPath $toolsZipPath
$appZipSha256 = (Get-FileHash -LiteralPath $appZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
$toolsZipSha256 = (Get-FileHash -LiteralPath $toolsZipPath -Algorithm SHA256).Hash.ToLowerInvariant()

$deliveryDirectory = Join-Path $OutputRoot 'Delivery'
New-Item -ItemType Directory -Force -Path $deliveryDirectory | Out-Null
$firmwareDeliveryPath = $null
if (-not [string]::IsNullOrWhiteSpace($FirmwareHexPath)) {
    $resolvedFirmware = (Resolve-Path -LiteralPath $FirmwareHexPath -ErrorAction Stop).Path
    $firmwareDeliveryPath = Join-Path $deliveryDirectory (Split-Path -Leaf $resolvedFirmware)
    Copy-Item -LiteralPath $resolvedFirmware -Destination $firmwareDeliveryPath
}

$timingDeliveryPath = $null
if (-not [string]::IsNullOrWhiteSpace($TimingReportPath)) {
    $resolvedTimingReport = (Resolve-Path -LiteralPath $TimingReportPath -ErrorAction Stop).Path
    $timingDeliveryPath = Join-Path $deliveryDirectory (Split-Path -Leaf $resolvedTimingReport)
    Copy-Item -LiteralPath $resolvedTimingReport -Destination $timingDeliveryPath
}

$sourceCommit = (& git -C $repoRoot rev-parse HEAD).Trim()

$manifest = [pscustomobject]@{
    GeneratedAt = (Get-Date).ToString("O")
    RepositoryRoot = $repoRoot
    OutputRoot = $OutputRoot
    Configuration = $Configuration
    Runtime = $Runtime
    Version = $Version
    SourceCommit = $sourceCommit
    Passed = ($appCheck.Passed -and $toolsCheck.Passed)
    App = $appCheck
    Tools = $toolsCheck
    Packages = [pscustomobject]@{
        AppZip = $appZipPath
        AppZipSha256 = $appZipSha256
        ToolsZip = $toolsZipPath
        ToolsZipSha256 = $toolsZipSha256
    }
    FirmwareHex = $firmwareDeliveryPath
    TimingReport = $timingDeliveryPath
}

$jsonPath = Join-Path $OutputRoot "publish-manifest.json"
$markdownPath = Join-Path $OutputRoot "publish-manifest.md"
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$builder = [System.Text.StringBuilder]::new()
[void]$builder.AppendLine("# EitHost Publish Manifest")
[void]$builder.AppendLine()
[void]$builder.AppendLine("- GeneratedAt: ``$($manifest.GeneratedAt)``")
[void]$builder.AppendLine("- RepositoryRoot: ``$repoRoot``")
[void]$builder.AppendLine("- OutputRoot: ``$OutputRoot``")
[void]$builder.AppendLine("- Configuration: ``$Configuration``")
[void]$builder.AppendLine("- Runtime: ``$Runtime``")
[void]$builder.AppendLine("- Version: ``$Version``")
[void]$builder.AppendLine("- SourceCommit: ``$sourceCommit``")
[void]$builder.AppendLine("- Passed: $($manifest.Passed)")
[void]$builder.AppendLine()
Add-MarkdownSection -Builder $builder -Check $appCheck
Add-MarkdownSection -Builder $builder -Check $toolsCheck
[void]$builder.AppendLine("## Packages")
[void]$builder.AppendLine()
[void]$builder.AppendLine("- App ZIP: ``$appZipPath``")
[void]$builder.AppendLine("- App SHA256: ``$appZipSha256``")
[void]$builder.AppendLine("- Tools ZIP: ``$toolsZipPath``")
[void]$builder.AppendLine("- Tools SHA256: ``$toolsZipSha256``")
[void]$builder.AppendLine("- Firmware HEX: ``$firmwareDeliveryPath``")
[void]$builder.AppendLine("- Timing report: ``$timingDeliveryPath``")
[void]$builder.AppendLine()
$builder.ToString() | Set-Content -LiteralPath $markdownPath -Encoding UTF8

Write-Log "Publish manifest: $markdownPath"
Write-Log "Publish JSON: $jsonPath"
Write-Log "EitHost publish finished"

if (-not $manifest.Passed) {
    throw "Publish validation failed. See $markdownPath"
}

Write-Host "EitHost publish output: $OutputRoot"
Write-Host "Publish manifest: $markdownPath"
}
finally {
    if ($temporaryOutput -and (Test-Path -LiteralPath $OutputRoot)) {
        Write-Log "Removing disposable staging root: $OutputRoot"
        Remove-Item -LiteralPath $OutputRoot -Recurse -Force
    }
}
