[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$')]
    [string]$Version = '',
    [string]$OutputDirectory = '',
    [switch]$ZipOnly,
    [switch]$CompressBundle
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1') -Force
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src\EitHost.App\EitHost.App.csproj'
if ([string]::IsNullOrWhiteSpace($Version)) {
    $projectXml = [xml](Get-Content -LiteralPath $projectPath -Raw)
    $Version = $projectXml.SelectSingleNode('/Project/PropertyGroup/Version').InnerText.Trim()
}
if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$') {
    throw "Invalid application version: $Version"
}
$canonicalInstall = [IO.Path]::GetFullPath((Join-Path $repoRoot 'release\EitHost-Windows-x64'))
$updateCanonicalRelease = [string]::IsNullOrWhiteSpace($OutputDirectory) -or
    [string]::Equals([IO.Path]::GetFullPath($OutputDirectory), $canonicalInstall, [StringComparison]::OrdinalIgnoreCase)
$buildStamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$archiveName = "EitHost-$Version-Windows-x64-$buildStamp"
if ($updateCanonicalRelease) {
    $OutputDirectory = $canonicalInstall
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $updateCanonicalRelease -and (Test-Path -LiteralPath $OutputDirectory) -and
    @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -gt 0) {
    throw "Distribution output must be a new empty directory: $OutputDirectory"
}
foreach ($protectedRelative in @('src', 'release', 'tests', 'scripts', 'packaging', '.git')) {
    $protectedPath = Join-Path $repoRoot $protectedRelative
    if (-not $updateCanonicalRelease -and ($OutputDirectory -eq $protectedPath -or
        $OutputDirectory.StartsWith($protectedPath + '\', [StringComparison]::OrdinalIgnoreCase))) {
        throw "Distribution output overlaps a protected project directory: $OutputDirectory"
    }
}

function Get-BuildInputHashes {
    $paths = [Collections.Generic.List[string]]::new()
    $pending = [Collections.Generic.Queue[string]]::new()
    $pending.Enqueue((Join-Path $repoRoot 'src\EitHost.App'))
    $pending.Enqueue((Join-Path $repoRoot 'src\EitHost.Core'))
    while ($pending.Count -gt 0) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Dequeue() -Force) {
            if ($item.PSIsContainer) {
                if ($item.Name -notin @('bin', 'obj', 'Data') -and
                    ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
                    $pending.Enqueue($item.FullName)
                }
            }
            else { $paths.Add($item.FullName) }
        }
    }
    foreach ($relative in @(
        'Directory.Build.props', 'Directory.Build.targets', 'Directory.Packages.props',
        'global.json', 'NuGet.Config', 'LICENSE', 'THIRD_PARTY_NOTICES.md',
        'AGENTS.md', 'package.cmd', 'packaging\RELEASE-RULES.md', 'scripts\publish-eithost.ps1',
        'packaging\README.zh-CN.md', 'scripts\package-eithost.ps1',
        'scripts\new-eithost-archive.ps1', 'scripts\install-usb2070-driver.ps1',
        'scripts\start-usb2070-driver-install-admin.ps1',
        'scripts\update-eithost-install.ps1', 'scripts\invoke-bounded-process.ps1')) {
        $inputPath = Join-Path $repoRoot $relative
        if (Test-Path -LiteralPath $inputPath -PathType Leaf) { $paths.Add($inputPath) }
    }
    foreach ($inputPath in ($paths | Sort-Object -Unique)) {
        $relative = $inputPath.Substring($repoRoot.Length + 1).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $inputPath -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $relative"
    }
}

function Invoke-PackagedSmoke {
    param([string]$PackageRoot, [string]$ProbeRoot)
    New-Item -ItemType Directory -Path $ProbeRoot -Force | Out-Null
    $process = & (Join-Path $PSScriptRoot 'invoke-bounded-process.ps1') `
        -ExecutablePath (Join-Path $PackageRoot 'EitHost.App.exe') `
        -ProcessArguments ('--hdf5-smoke-test "' + $ProbeRoot + '"') `
        -WorkingDirectory $ProbeRoot `
        -TimeoutMilliseconds 60000 `
        -EnvironmentVariables @{
            DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $ProbeRoot 'bundle-cache'
            DOTNET_ROOT = Join-Path $ProbeRoot 'no-shared-runtime'
            DOTNET_ROOT_X64 = Join-Path $ProbeRoot 'no-shared-runtime'
            DOTNET_MULTILEVEL_LOOKUP = '0'
        }
    if ($process.TimedOut) {
        throw 'Packaged HDF5 smoke test timed out.'
    }
    $failurePath = Join-Path $ProbeRoot 'hdf5-smoke-test.failure.txt'
    if ($process.ExitCode -ne 0 -or (Test-Path -LiteralPath $failurePath)) {
        $detail = if (Test-Path -LiteralPath $failurePath) { Get-Content -LiteralPath $failurePath -Raw } else { '' }
        throw "Packaged HDF5 smoke test failed ($($process.ExitCode)): $detail"
    }
}

$temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$stagingRoot = Join-Path $temporaryBase ('EitHost.Package.' + [Guid]::NewGuid().ToString('N'))
if ((Split-Path -Parent $stagingRoot) -ne $temporaryBase) { throw 'Invalid staging directory.' }
$packageRoot = Join-Path $stagingRoot 'EitHost-Windows-x64'
$publishRoot = Join-Path $stagingRoot 'publish'
$buildRoot = Join-Path $stagingRoot 'build'
$archiveRoot = Join-Path $stagingRoot 'archives'
$sourceInputs = @(Get-BuildInputHashes)
$sourceCommit = $null
$sourceDirty = $null
if (Get-Command git -ErrorAction SilentlyContinue) {
    $commitOutput = & git -C $repoRoot rev-parse --verify HEAD 2>$null
    if ($LASTEXITCODE -eq 0) {
        $sourceCommit = $commitOutput.Trim()
        $sourceDirty = -not [string]::IsNullOrWhiteSpace((& git -C $repoRoot status --short) -join "`n")
    }
}
$sevenZipPath = ''
if (-not $updateCanonicalRelease -and -not $ZipOnly) {
    $sevenZipCommand = Get-Command 7z.exe -ErrorAction SilentlyContinue
    if ($sevenZipCommand) { $sevenZipPath = $sevenZipCommand.Source }
    elseif (Test-Path -LiteralPath 'C:\Program Files\7-Zip\7z.exe' -PathType Leaf) {
        $sevenZipPath = 'C:\Program Files\7-Zip\7z.exe'
    }
}

try {
    Write-Host "Building current source in disposable staging: $stagingRoot"
    $compression = $CompressBundle.IsPresent.ToString().ToLowerInvariant()
    $publishArguments = @(
        'publish', $projectPath, '--configuration', 'Release',
        '--runtime', 'win-x64', '--self-contained', 'true',
        '--artifacts-path', $buildRoot, '--output', $publishRoot,
        "-p:Version=$Version", '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        "-p:EnableCompressionInSingleFile=$compression", '-p:PublishTrimmed=false',
        '-p:PublishReadyToRun=false', '-p:DebugType=None', '-p:DebugSymbols=false',
        '-p:UseSharedCompilation=false', '-v:minimal'
    )
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $LASTEXITCODE" }
    if (@(Compare-Object $sourceInputs @(Get-BuildInputHashes)).Count -gt 0) {
        throw 'Build inputs changed during publishing; save edits and package again.'
    }

    New-Item -ItemType Directory -Path (Join-Path $packageRoot 'scripts') -Force | Out-Null
    foreach ($relative in @(
        'EitHost.App.exe', 'HDF.PInvoke.dll', 'HDF.PInvoke.dll.config',
        'hdf5.dll', 'hdf5_hl.dll', 'USB2070.dll', 'eithost.reconstruction.example.json',
        'scripts\install-usb2070-driver.ps1', 'scripts\start-usb2070-driver-install-admin.ps1',
        'scripts\update-eithost-install.ps1')) {
        Copy-Item -LiteralPath (Join-Path $publishRoot $relative) -Destination (Join-Path $packageRoot $relative)
    }
    Copy-Item -LiteralPath (Join-Path $repoRoot 'packaging\README.zh-CN.md') -Destination (Join-Path $packageRoot 'README.md')
    foreach ($relative in @('LICENSE', 'THIRD_PARTY_NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $repoRoot $relative) -Destination (Join-Path $packageRoot $relative)
    }
    $versionInfo = [ordered]@{
        Product = 'EitHost'
        Version = $Version
        BuiltAt = (Get-Date).ToString('O')
        Runtime = 'win-x64'
        SelfContained = $true
        Trimmed = $false
        BundleCompression = $CompressBundle.IsPresent
        SourceCommit = $sourceCommit
        SourceDirty = $sourceDirty
        SourceFilesSha256 = $sourceInputs
    }
    $versionInfo | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $packageRoot 'VERSION.json') -Encoding UTF8

    Write-Host 'Verifying packaged HDF5 without a shared .NET runtime or existing bundle cache...'
    Invoke-PackagedSmoke -PackageRoot $packageRoot -ProbeRoot (Join-Path $stagingRoot 'smoke-staged')
    if ($updateCanonicalRelease) {
        if (@(Compare-Object $sourceInputs @(Get-BuildInputHashes)).Count -gt 0) {
            throw 'Build inputs changed during verification; package again.'
        }
        $checksumPath = Join-Path $packageRoot 'SHA256SUMS.txt'
        @(Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Sort-Object FullName | ForEach-Object {
            $relative = $_.FullName.Substring($packageRoot.Length + 1).Replace('\', '/')
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash  $relative"
        }) | Set-Content -LiteralPath $checksumPath -Encoding ASCII

        Write-Host "Updating the canonical release and verifying retained Data: $canonicalInstall"
        $installUpdate = & (Join-Path $PSScriptRoot 'update-eithost-install.ps1') `
            -PackageDirectory $packageRoot -InstallRoot $canonicalInstall
        if (-not $installUpdate.UpdateApplied -or -not $installUpdate.DataPreserved) {
            throw "Canonical release update did not complete: $($installUpdate.CleanupError)"
        }
        Invoke-PackagedSmoke -PackageRoot $canonicalInstall -ProbeRoot (Join-Path $stagingRoot 'smoke-installed')
        if (@(Compare-Object $sourceInputs @(Get-BuildInputHashes)).Count -gt 0) {
            throw 'Build inputs changed during installation; publish current source again before delivery.'
        }
        $installedVersion = (Get-Content -LiteralPath (Join-Path $canonicalInstall 'VERSION.json') -Raw | ConvertFrom-Json).Version
        $executableVersion = (Get-Item -LiteralPath (Join-Path $canonicalInstall 'EitHost.App.exe')).VersionInfo.ProductVersion
        if ($installedVersion -ne $Version -or $executableVersion.Split('+')[0] -ne $Version) {
            throw 'Installed executable and VERSION.json do not match the requested version.'
        }
        $publishManifest = [ordered]@{
            GeneratedAt = (Get-Date).ToString('O'); Version = $Version
            InstallRoot = $canonicalInstall; DataRoot = $installUpdate.DataRoot
            SourceCommit = $sourceCommit; SourceDirty = $sourceDirty; SourceInputsUnchanged = $true
            Runtime = 'win-x64'; SelfContained = $true; Passed = $true
            SmokeTestPassed = $true; InstalledSmokeTestPassed = $true
            DataPreserved = $installUpdate.DataPreserved; DataFileCount = $installUpdate.DataFileCount
            CleanupPending = $installUpdate.CleanupPending; CleanupError = $installUpdate.CleanupError
            ExeSha256 = (Get-FileHash -LiteralPath (Join-Path $canonicalInstall 'EitHost.App.exe') -Algorithm SHA256).Hash
        }
        $publishManifest | ConvertTo-Json -Depth 5 | Set-Content `
            -LiteralPath (Join-Path (Split-Path -Parent $canonicalInstall) 'publish-manifest.json') -Encoding UTF8
        @(
            '# EitHost canonical release', '', "- Version: $Version", "- InstallRoot: $canonicalInstall",
            '- Staged and installed HDF5 smoke tests: passed',
            "- DataPreserved: $($installUpdate.DataPreserved); files: $($installUpdate.DataFileCount)",
            "- SourceInputsUnchanged: true; SourceCommit: $sourceCommit; SourceDirty: $sourceDirty",
            "- CleanupPending: $($installUpdate.CleanupPending)"
        ) | Set-Content -LiteralPath (Join-Path (Split-Path -Parent $canonicalInstall) 'publish-manifest.md') -Encoding UTF8
        Write-Host "Latest EitHost $Version is ready: $(Join-Path $canonicalInstall 'EitHost.App.exe')"
        return
    }
    Write-Host 'Creating archives and verifying every extracted file...'
    $archiveResult = & (Join-Path $PSScriptRoot 'new-eithost-archive.ps1') `
        -PackageDirectory $packageRoot -OutputDirectory $archiveRoot `
        -ArchiveName $archiveName -SevenZipPath $sevenZipPath

    # Test the actual ZIP payload from a new path, after archive extraction.
    $unpackRoot = Join-Path $stagingRoot 'extracted'
    $zipPath = ($archiveResult.Archives | Where-Object { $_.Path.EndsWith('.zip') }).Path
    [IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $unpackRoot)
    Invoke-PackagedSmoke -PackageRoot (Join-Path $unpackRoot 'EitHost-Windows-x64') `
        -ProbeRoot (Join-Path $stagingRoot 'smoke-extracted')
    if (@(Compare-Object $sourceInputs @(Get-BuildInputHashes)).Count -gt 0) {
        throw 'Build inputs changed during verification; package again.'
    }

    if ((Test-Path -LiteralPath $OutputDirectory) -and
        @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -gt 0) {
        throw "Distribution output became nonempty during the build: $OutputDirectory"
    }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    foreach ($archive in $archiveResult.Archives) {
        $destination = Join-Path $OutputDirectory (Split-Path -Leaf $archive.Path)
        [IO.File]::Copy($archive.Path, $destination, $false)
        $archive.Path = $destination
    }
    @($archiveResult.Archives | ForEach-Object {
        "$($_.Sha256)  $(Split-Path -Leaf $_.Path)"
    }) | Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ASCII
    $smallest = $archiveResult.Archives | Sort-Object Bytes | Select-Object -First 1
    [ordered]@{
        Version = $Version
        VerifiedAt = (Get-Date).ToString('O')
        FileCount = $archiveResult.FileCount
        UnpackedBytes = $archiveResult.UnpackedBytes
        PackagedHdf5SmokePassed = $true
        ExtractedHdf5SmokePassed = $true
        NoSharedRuntimeOrExistingBundleCache = $true
        SourceInputsUnchanged = $true
        SourceCommit = $sourceCommit
        SourceDirty = $sourceDirty
        Archives = $archiveResult.Archives
        SmallestArchive = $smallest.Path
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'verification.json') -Encoding UTF8
    Write-Host "Distribution ready: $OutputDirectory"
    foreach ($archive in $archiveResult.Archives) {
        Write-Host ('  {0:N2} MiB  {1}' -f ($archive.Bytes / 1MB), $archive.Path)
    }
    Write-Host "Smallest archive: $($smallest.Path)"
}
finally {
    if (Test-Path -LiteralPath $stagingRoot) {
        $resolvedStaging = [IO.Path]::GetFullPath($stagingRoot)
        if ((Split-Path -Parent $resolvedStaging) -ne $temporaryBase -or
            -not (Split-Path -Leaf $resolvedStaging).StartsWith('EitHost.Package.')) {
            throw "Refusing to clean an unexpected directory: $resolvedStaging"
        }
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}
