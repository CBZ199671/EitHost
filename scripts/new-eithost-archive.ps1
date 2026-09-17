[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory,
    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]+$')]
    [string]$ArchiveName,
    [string]$SevenZipPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1') -Force
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem

$requiredFiles = @(
    'EitHost.App.exe',
    'HDF.PInvoke.dll',
    'HDF.PInvoke.dll.config',
    'hdf5.dll',
    'hdf5_hl.dll',
    'USB2070.dll',
    'eithost.reconstruction.example.json',
    'scripts/install-usb2070-driver.ps1',
    'scripts/start-usb2070-driver-install-admin.ps1',
    'scripts/update-eithost-install.ps1',
    'README.md',
    'LICENSE',
    'THIRD_PARTY_NOTICES.md',
    'VERSION.json'
)
$packageRoot = [IO.Path]::GetFullPath($PackageDirectory).TrimEnd('\', '/')
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory).TrimEnd('\', '/')
if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) {
    throw "Package directory does not exist: $packageRoot"
}
if ((Split-Path -Leaf $packageRoot) -ne 'EitHost-Windows-x64') {
    throw 'Package directory must be named EitHost-Windows-x64.'
}
if ($outputRoot -eq $packageRoot -or
    $outputRoot.StartsWith($packageRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Archive output must be outside the package directory.'
}

# Validate directory entries before descending, including empty folders and links.
$packageEntries = @(Get-ChildItem -LiteralPath $packageRoot -Force)
$packageFiles = [Collections.Generic.List[IO.FileInfo]]::new()
foreach ($entry in $packageEntries) {
    if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Package cannot contain links: $($entry.FullName)"
    }
    if ($entry.PSIsContainer) {
        if ($entry.Name -cne 'scripts') { throw "Package contains unexpected directory: $($entry.Name)" }
        foreach ($scriptFile in Get-ChildItem -LiteralPath $entry.FullName -Force) {
            if ($scriptFile.PSIsContainer -or ($scriptFile.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Package contains unexpected directory or link: $($scriptFile.Name)"
            }
            $packageFiles.Add($scriptFile)
        }
    }
    else { $packageFiles.Add($entry) }
}
$actualFiles = @($packageFiles | ForEach-Object {
    $_.FullName.Substring($packageRoot.Length + 1).Replace('\', '/')
})
$unexpected = @($actualFiles | Where-Object { $_ -cnotin $requiredFiles })
if ($unexpected.Count -gt 0) {
    throw "Package contains unexpected files: $($unexpected -join ', ')"
}
foreach ($relative in $requiredFiles) {
    $filePath = Join-Path $packageRoot $relative
    if (-not (Test-Path -LiteralPath $filePath -PathType Leaf) -or
        (Get-Item -LiteralPath $filePath).Length -eq 0) {
        throw "Required package file is missing or empty: $relative"
    }
}

$zipPath = Join-Path $outputRoot ($ArchiveName + '.zip')
$sevenZipArchive = Join-Path $outputRoot ($ArchiveName + '.7z')
foreach ($archivePath in @($zipPath, $sevenZipArchive)) {
    if (Test-Path -LiteralPath $archivePath) {
        throw "Refusing to overwrite an existing archive: $archivePath"
    }
}
if ($SevenZipPath -and -not (Test-Path -LiteralPath $SevenZipPath -PathType Leaf)) {
    throw "7-Zip executable not found: $SevenZipPath"
}

$checksumPath = Join-Path $packageRoot 'SHA256SUMS.txt'
$expectedHashes = @{}
foreach ($relative in ($requiredFiles | Sort-Object)) {
    $expectedHashes[$relative] = (Get-FileHash -LiteralPath (Join-Path $packageRoot $relative) -Algorithm SHA256).Hash.ToLowerInvariant()
}
@($requiredFiles | Sort-Object | ForEach-Object {
    "$($expectedHashes[$_])  $_"
}) | Set-Content -LiteralPath $checksumPath -Encoding ASCII
$expectedHashes['SHA256SUMS.txt'] = (Get-FileHash -LiteralPath $checksumPath -Algorithm SHA256).Hash.ToLowerInvariant()

function Assert-ArchiveContents {
    param([string]$ExtractedDirectory)
    $files = @(Get-ChildItem -LiteralPath $ExtractedDirectory -Recurse -File -Force)
    if ($files.Count -ne $expectedHashes.Count) {
        throw 'Extracted archive file count differs from the verified package.'
    }
    foreach ($relative in $expectedHashes.Keys) {
        $filePath = Join-Path $ExtractedDirectory ('EitHost-Windows-x64/' + $relative)
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf) -or
            (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expectedHashes[$relative]) {
            throw "Extracted archive SHA-256 mismatch: $relative"
        }
    }
}

$temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/')
$verificationRoot = Join-Path $temporaryBase ('EitHost.ArchiveVerify.' + [Guid]::NewGuid().ToString('N'))
if ((Split-Path -Parent $verificationRoot) -ne $temporaryBase) {
    throw 'Invalid archive verification directory.'
}
$createdArchives = [Collections.Generic.List[string]]::new()
$completed = $false
$zipOwned = $false
$sevenZipOwned = $false
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
try {
    $zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    $zipOwned = $true
    $createdArchives.Add($zipPath)
    try {
        foreach ($relative in ($expectedHashes.Keys | Sort-Object)) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, (Join-Path $packageRoot $relative), ('EitHost-Windows-x64/' + $relative),
                [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally { $zip.Dispose() }
    $zipVerification = Join-Path $verificationRoot 'zip'
    [IO.Compression.ZipFile]::ExtractToDirectory($zipPath, $zipVerification)
    Assert-ArchiveContents -ExtractedDirectory $zipVerification

    if ($SevenZipPath) {
        $sevenZipOwned = $true
        & $SevenZipPath a -t7z -mx=9 -m0=LZMA2 -md=128m -mfb=273 -mmt=2 -ms=on -bso0 -bsp0 -- $sevenZipArchive $packageRoot
        if ($LASTEXITCODE -ne 0) { throw "7-Zip compression failed: $LASTEXITCODE" }
        $createdArchives.Add($sevenZipArchive)
        $sevenZipVerification = Join-Path $verificationRoot '7z'
        & $SevenZipPath x -y -bso0 -bsp0 "-o$sevenZipVerification" -- $sevenZipArchive
        if ($LASTEXITCODE -ne 0) { throw "7-Zip extraction failed: $LASTEXITCODE" }
        Assert-ArchiveContents -ExtractedDirectory $sevenZipVerification
    }

    $archives = @($createdArchives | ForEach-Object {
        [pscustomobject]@{
            Path = $_
            Bytes = (Get-Item -LiteralPath $_).Length
            Sha256 = (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()
            ExtractedHashesVerified = $true
        }
    })
    $completed = $true
    [pscustomobject]@{
        FileCount = $expectedHashes.Count
        UnpackedBytes = (Get-ChildItem -LiteralPath $packageRoot -Recurse -File | Measure-Object Length -Sum).Sum
        Archives = $archives
        SmallestArchive = ($archives | Sort-Object Bytes | Select-Object -First 1).Path
    }
}
finally {
    $cleanupErrors = @()
    if (-not $completed) {
        if ($sevenZipOwned -and (Test-Path -LiteralPath $sevenZipArchive -PathType Leaf)) {
            try { Remove-Item -LiteralPath $sevenZipArchive -Force }
            catch { $cleanupErrors += $_.Exception.Message }
        }
        if ($zipOwned -and (Test-Path -LiteralPath $zipPath -PathType Leaf)) {
            try { Remove-Item -LiteralPath $zipPath -Force }
            catch { $cleanupErrors += $_.Exception.Message }
        }
    }
    if (Test-Path -LiteralPath $verificationRoot) {
        try {
            $resolvedVerification = [IO.Path]::GetFullPath($verificationRoot)
            if ((Split-Path -Parent $resolvedVerification) -ne $temporaryBase -or
                -not (Split-Path -Leaf $resolvedVerification).StartsWith('EitHost.ArchiveVerify.')) {
                throw "Refusing to clean an unexpected directory: $resolvedVerification"
            }
            Remove-Item -LiteralPath $resolvedVerification -Recurse -Force
        }
        catch { $cleanupErrors += $_.Exception.Message }
    }
    if ($cleanupErrors.Count -gt 0) {
        throw "Archive cleanup failed: $($cleanupErrors -join '; ')"
    }
}
