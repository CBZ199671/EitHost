param(
    [Parameter(Mandatory = $true)]
    [string]$PackageDirectory,
    [Parameter(Mandatory = $true)]
    [string]$InstallRoot
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSHOME 'Modules\Microsoft.PowerShell.Utility\Microsoft.PowerShell.Utility.psd1') -Force

function Get-NormalizedDirectoryPath {
    param([string]$Path)

    return [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
}

function Test-PathInside {
    param(
        [string]$CandidatePath,
        [string]$RootPath
    )

    $candidate = Get-NormalizedDirectoryPath -Path $CandidatePath
    $root = Get-NormalizedDirectoryPath -Path $RootPath
    return $candidate.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Get-ChildRelativePath {
    param(
        [string]$RootPath,
        [string]$ChildPath
    )

    $root = (Get-NormalizedDirectoryPath -Path $RootPath) + '\'
    $child = [System.IO.Path]::GetFullPath($ChildPath)
    if (-not $child.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside root: $child"
    }

    return $child.Substring($root.Length)
}

function Get-DataSnapshot {
    param([string]$DataRoot)

    if (-not (Test-Path -LiteralPath $DataRoot -PathType Container)) {
        return @()
    }

    return @(
        Get-ChildItem -LiteralPath $DataRoot -Recurse -File -Force |
            Sort-Object FullName |
            ForEach-Object {
                [pscustomobject]@{
                    Path = Get-ChildRelativePath -RootPath $DataRoot -ChildPath $_.FullName
                    Length = $_.Length
                    LastWriteTimeUtcTicks = $_.LastWriteTimeUtc.Ticks
                    Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
                }
            }
    )
}

function Test-SnapshotEqual {
    param(
        [object[]]$Before,
        [object[]]$After
    )

    if ($Before.Count -ne $After.Count) {
        return $false
    }

    for ($index = 0; $index -lt $Before.Count; $index++) {
        if ($Before[$index].Path -cne $After[$index].Path -or
            $Before[$index].Length -ne $After[$index].Length -or
            $Before[$index].LastWriteTimeUtcTicks -ne $After[$index].LastWriteTimeUtcTicks -or
            $Before[$index].Sha256 -cne $After[$index].Sha256) {
            return $false
        }
    }

    return $true
}

function Get-PackageFiles {
    param([string]$PackageRoot)

    $files = [Collections.Generic.List[IO.FileInfo]]::new()
    $pending = [Collections.Generic.Queue[string]]::new()
    $pending.Enqueue($PackageRoot)
    while ($pending.Count -gt 0) {
        $directory = $pending.Dequeue()
        foreach ($entry in Get-ChildItem -LiteralPath $directory -Force) {
            if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Package cannot contain links: $($entry.FullName)"
            }

            if ($entry.PSIsContainer) {
                $pending.Enqueue($entry.FullName)
            }
            else {
                $files.Add($entry)
            }
        }
    }

    return @($files)
}

function Assert-PackageLayout {
    param([string]$PackageRoot)

    $requiredFiles = @(
        'EitHost.App.exe',
        'HDF.PInvoke.dll',
        'HDF.PInvoke.dll.config',
        'hdf5.dll',
        'hdf5_hl.dll',
        'USB2070.dll',
        'eithost.reconstruction.example.json',
        'scripts\install-usb2070-driver.ps1',
        'scripts\start-usb2070-driver-install-admin.ps1',
        'scripts\update-eithost-install.ps1',
        'README.md',
        'LICENSE',
        'THIRD_PARTY_NOTICES.md',
        'VERSION.json',
        'SHA256SUMS.txt'
    )
    $actualFiles = @(Get-PackageFiles -PackageRoot $PackageRoot | ForEach-Object {
        Get-ChildRelativePath -RootPath $PackageRoot -ChildPath $_.FullName
    })
    $missing = @($requiredFiles | Where-Object { $_ -cnotin $actualFiles })
    $unexpected = @($actualFiles | Where-Object { $_ -cnotin $requiredFiles })
    if ($missing.Count -gt 0 -or $unexpected.Count -gt 0) {
        throw "Package layout mismatch. Missing: $($missing -join ', '); unexpected: $($unexpected -join ', ')"
    }

    foreach ($relative in $requiredFiles) {
        if ((Get-Item -LiteralPath (Join-Path $PackageRoot $relative)).Length -eq 0) {
            throw "Required package file is empty: $relative"
        }
    }

    try {
        $version = Get-Content -Raw -LiteralPath (Join-Path $PackageRoot 'VERSION.json') | ConvertFrom-Json
    }
    catch {
        throw "Package VERSION.json is invalid: $($_.Exception.Message)"
    }

    if ($version.Product -cne 'EitHost' -or
        [string]::IsNullOrWhiteSpace([string]$version.Version) -or
        $version.Runtime -cne 'win-x64') {
        throw "Package VERSION.json does not identify an EitHost Windows x64 package."
    }
}

function Assert-PackageChecksums {
    param([string]$PackageRoot)

    $checksumPath = Join-Path $PackageRoot "SHA256SUMS.txt"
    if (-not (Test-Path -LiteralPath $checksumPath -PathType Leaf)) {
        throw "Package checksum manifest is missing: $checksumPath"
    }

    $expected = @{}
    foreach ($line in Get-Content -LiteralPath $checksumPath) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        if ($line -cnotmatch '^([0-9a-fA-F]{64})  (.+)$') {
            throw "Invalid package checksum entry: $line"
        }

        $relative = $Matches[2].Replace('/', '\')
        if ([IO.Path]::IsPathRooted($relative) -or
            $relative.Split('\') -contains '..' -or
            $relative -eq 'Data' -or
            $relative.StartsWith('Data\', [StringComparison]::OrdinalIgnoreCase) -or
            $relative -ieq 'SHA256SUMS.txt') {
            throw "Unsafe package checksum path: $relative"
        }

        if ($expected.ContainsKey($relative)) {
            throw "Duplicate package checksum path: $relative"
        }

        $filePath = Join-Path $PackageRoot $relative
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf) -or
            -not (Test-PathInside -CandidatePath $filePath -RootPath $PackageRoot)) {
            throw "Package checksum references a missing or external file: $relative"
        }

        $actualHash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash
        if ($actualHash -cne $Matches[1].ToUpperInvariant()) {
            throw "Package SHA-256 mismatch: $relative"
        }

        $expected[$relative] = $actualHash
    }

    $actualFiles = @(Get-PackageFiles -PackageRoot $PackageRoot | ForEach-Object {
        Get-ChildRelativePath -RootPath $PackageRoot -ChildPath $_.FullName
    } | Where-Object { $_ -ine 'SHA256SUMS.txt' })
    $missingChecksums = @($actualFiles | Where-Object { -not $expected.ContainsKey($_) })
    if ($missingChecksums.Count -gt 0 -or $expected.Count -ne $actualFiles.Count) {
        throw "Package checksum manifest does not cover exactly every payload file: $($missingChecksums -join ', ')"
    }

    return $actualFiles.Count + 1
}

function Get-ExistingInstallProgramFiles {
    param([string]$InstallRoot)

    $files = [Collections.Generic.List[IO.FileInfo]]::new()
    foreach ($entry in Get-ChildItem -LiteralPath $InstallRoot -Force) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Existing install cannot contain links: $($entry.FullName)"
        }
        if ($entry.Name -ieq 'Data') {
            if (-not $entry.PSIsContainer) {
                throw "Install Data path must be a directory; update did not start: $($entry.FullName)"
            }
            continue
        }
        if ($entry.PSIsContainer) {
            if ($entry.Name -cne 'scripts') {
                throw "Existing install contains an unexpected directory: $($entry.Name)"
            }
            foreach ($scriptEntry in Get-ChildItem -LiteralPath $entry.FullName -Force) {
                if ($scriptEntry.PSIsContainer -or
                    ($scriptEntry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw "Existing install contains an unexpected directory or link: $($scriptEntry.FullName)"
                }
                $files.Add($scriptEntry)
            }
            continue
        }
        $files.Add($entry)
    }
    return @($files)
}

function Test-ExactFileSet {
    param(
        [string[]]$Actual,
        [string[]]$Expected
    )

    if ($Actual.Count -ne $Expected.Count) {
        return $false
    }
    foreach ($relative in $Expected) {
        if ($Actual -cnotcontains $relative) {
            return $false
        }
    }
    return $true
}

function Assert-ExistingInstallChecksums {
    param(
        [string]$InstallRoot,
        [IO.FileInfo[]]$ProgramFiles
    )

    $checksumPath = Join-Path $InstallRoot 'SHA256SUMS.txt'
    $payloadFiles = @($ProgramFiles | Where-Object { $_.Name -cne 'SHA256SUMS.txt' })
    $payloadRelativePaths = @($payloadFiles | ForEach-Object {
        Get-ChildRelativePath -RootPath $InstallRoot -ChildPath $_.FullName
    })
    $verified = @{}
    foreach ($line in Get-Content -LiteralPath $checksumPath) {
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }
        if ($line -cnotmatch '^([0-9a-fA-F]{64})  (.+)$') {
            throw "Invalid existing install checksum entry: $line"
        }

        $expectedHash = $Matches[1].ToUpperInvariant()
        $relative = $Matches[2].Replace('/', '\')
        if ([IO.Path]::IsPathRooted($relative) -or
            $relative.Split('\') -contains '..' -or
            $relative -eq 'Data' -or
            $relative.StartsWith('Data\', [StringComparison]::OrdinalIgnoreCase) -or
            $relative -ieq 'SHA256SUMS.txt') {
            throw "Unsafe existing install checksum path: $relative"
        }
        if ($payloadRelativePaths -cnotcontains $relative) {
            throw "Existing install checksum references an unexpected file: $relative"
        }
        if ($verified.ContainsKey($relative)) {
            throw "Duplicate existing install checksum path: $relative"
        }

        $filePath = Join-Path $InstallRoot $relative
        if (-not (Test-Path -LiteralPath $filePath -PathType Leaf) -or
            -not (Test-PathInside -CandidatePath $filePath -RootPath $InstallRoot)) {
            throw "Existing install checksum references a missing or external file: $relative"
        }
        $actualHash = (Get-FileHash -LiteralPath $filePath -Algorithm SHA256).Hash
        if ($actualHash -cne $expectedHash) {
            throw "Existing install SHA-256 mismatch: $relative"
        }
        $verified[$relative] = $actualHash
    }

    $missing = @($payloadRelativePaths | Where-Object { -not $verified.ContainsKey($_) })
    if ($missing.Count -gt 0 -or $verified.Count -ne $payloadRelativePaths.Count) {
        throw "Existing install checksum manifest does not cover exactly every program file: $($missing -join ', ')"
    }
}

function Assert-ExistingInstallIdentity {
    param([string]$InstallRoot)

    if (-not (Test-Path -LiteralPath $InstallRoot)) {
        return
    }
    if (-not (Test-Path -LiteralPath $InstallRoot -PathType Container)) {
        throw "Install root must be a directory; update did not start: $InstallRoot"
    }

    $entries = @(Get-ChildItem -LiteralPath $InstallRoot -Force)
    if ($entries.Count -eq 0) {
        return
    }

    $legacyFiles = @(
        'EitHost.App.exe',
        'HDF.PInvoke.dll',
        'HDF.PInvoke.dll.config',
        'hdf5.dll',
        'hdf5_hl.dll',
        'USB2070.dll',
        'eithost.reconstruction.example.json',
        'scripts\install-usb2070-driver.ps1',
        'scripts\start-usb2070-driver-install-admin.ps1',
        'README.md',
        'SHA256SUMS.txt'
    )
    $currentFiles = @(
        'EitHost.App.exe',
        'HDF.PInvoke.dll',
        'HDF.PInvoke.dll.config',
        'hdf5.dll',
        'hdf5_hl.dll',
        'USB2070.dll',
        'eithost.reconstruction.example.json',
        'scripts\install-usb2070-driver.ps1',
        'scripts\start-usb2070-driver-install-admin.ps1',
        'scripts\update-eithost-install.ps1',
        'README.md',
        'LICENSE',
        'THIRD_PARTY_NOTICES.md',
        'VERSION.json',
        'SHA256SUMS.txt'
    )
    $programFiles = @(Get-ExistingInstallProgramFiles -InstallRoot $InstallRoot)
    $actualFiles = @($programFiles | ForEach-Object {
        Get-ChildRelativePath -RootPath $InstallRoot -ChildPath $_.FullName
    })
    $isLegacy = Test-ExactFileSet -Actual $actualFiles -Expected $legacyFiles
    $isCurrent = Test-ExactFileSet -Actual $actualFiles -Expected $currentFiles
    if (-not $isLegacy -and -not $isCurrent) {
        throw "Existing nonempty install root is not an exact verified EitHost legacy or current layout: $InstallRoot"
    }
    foreach ($file in $programFiles) {
        if ($file.Length -eq 0) {
            throw "Existing install program file is empty: $($file.FullName)"
        }
    }

    Assert-ExistingInstallChecksums -InstallRoot $InstallRoot -ProgramFiles $programFiles
    $executablePath = Join-Path $InstallRoot 'EitHost.App.exe'
    try {
        $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($executablePath)
    }
    catch {
        throw "Existing install executable version information is unreadable: $($_.Exception.Message)"
    }
    if ($versionInfo.ProductName -cne 'EitHost.App' -or
        $versionInfo.FileDescription -cne 'EitHost.App' -or
        $versionInfo.OriginalFilename -cne 'EitHost.App.dll' -or
        $versionInfo.InternalName -cne 'EitHost.App.dll') {
        throw "Existing install executable does not identify EitHost.App: $executablePath"
    }

    if ($isCurrent) {
        try {
            $version = Get-Content -Raw -LiteralPath (Join-Path $InstallRoot 'VERSION.json') | ConvertFrom-Json
        }
        catch {
            throw "Existing install VERSION.json is invalid: $($_.Exception.Message)"
        }
        if ($version.Product -cne 'EitHost' -or
            [string]::IsNullOrWhiteSpace([string]$version.Version) -or
            $version.Runtime -cne 'win-x64') {
            throw "Existing install VERSION.json does not identify an EitHost Windows x64 install."
        }
    }
}

function Copy-PackageTree {
    param(
        [string]$PackageRoot,
        [string]$DestinationRoot
    )

    New-Item -ItemType Directory -Path $DestinationRoot | Out-Null
    foreach ($file in Get-PackageFiles -PackageRoot $PackageRoot) {
        $relative = Get-ChildRelativePath -RootPath $PackageRoot -ChildPath $file.FullName
        if ($relative -eq 'Data' -or $relative.StartsWith('Data\', [StringComparison]::OrdinalIgnoreCase)) {
            throw "Publish package attempted to update runtime Data: $relative"
        }

        $destination = Join-Path $DestinationRoot $relative
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
    }
}

function Remove-OwnedDirectory {
    param(
        [string]$Path,
        [string]$Parent,
        [string]$Prefix
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $resolved = Get-NormalizedDirectoryPath -Path $Path
    if ((Split-Path -Parent $resolved) -cne $Parent -or
        -not (Split-Path -Leaf $resolved).StartsWith($Prefix, [StringComparison]::Ordinal)) {
        throw "Refusing to remove unexpected transaction directory: $resolved"
    }

    Remove-Item -LiteralPath $resolved -Recurse -Force
}

function Assert-OwnedTransactionDirectoryPath {
    param(
        [string]$Path,
        [string]$Parent,
        [string]$Suffix
    )

    $resolved = Get-NormalizedDirectoryPath -Path $Path
    $leaf = Split-Path -Leaf $resolved
    if ((Split-Path -Parent $resolved) -cne $Parent -or
        -not $leaf.StartsWith('.EitHost.Update.', [StringComparison]::Ordinal) -or
        -not $leaf.EndsWith($Suffix, [StringComparison]::Ordinal)) {
        throw "Unsafe update transaction path: $resolved"
    }

    return $resolved
}

function Remove-TransactionStateFile {
    param(
        [string]$Path,
        [string]$ExpectedPath
    )

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $resolved = [IO.Path]::GetFullPath($Path)
    $expected = [IO.Path]::GetFullPath($ExpectedPath)
    if (-not [string]::Equals($resolved, $expected, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
        throw "Refusing to remove unexpected update state path: $resolved"
    }

    Remove-Item -LiteralPath $resolved -Force
}

function Write-TransactionState {
    param(
        [string]$StatePath,
        [object]$State
    )

    $temporaryPath = $StatePath + '.tmp'
    $previousPath = $StatePath + '.previous'
    try {
        $json = $State | ConvertTo-Json -Depth 4
        $utf8 = [Text.UTF8Encoding]::new($false)
        $bytes = $utf8.GetBytes($json)
        $stream = [IO.FileStream]::new(
            $temporaryPath,
            [IO.FileMode]::Create,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None,
            4096,
            [IO.FileOptions]::WriteThrough)
        try {
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        }
        finally {
            $stream.Dispose()
        }
        if (Test-Path -LiteralPath $StatePath) {
            if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) {
                throw "Update state path is not a file: $StatePath"
            }
            if (Test-Path -LiteralPath $previousPath -PathType Leaf) {
                Remove-Item -LiteralPath $previousPath -Force
            }
            [IO.File]::Replace($temporaryPath, $StatePath, $previousPath)
        }
        else {
            [IO.File]::Move($temporaryPath, $StatePath)
        }
        $stateStream = [IO.FileStream]::new(
            $StatePath,
            [IO.FileMode]::Open,
            [IO.FileAccess]::ReadWrite,
            [IO.FileShare]::Read,
            4096,
            [IO.FileOptions]::WriteThrough)
        try {
            $stateStream.Flush($true)
        }
        finally {
            $stateStream.Dispose()
        }
        if (Test-Path -LiteralPath $previousPath -PathType Leaf) {
            Remove-Item -LiteralPath $previousPath -Force
        }
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function Read-TransactionState {
    param(
        [string]$StatePath,
        [string]$ExpectedInstallRoot,
        [string]$InstallParent
    )

    try {
        $state = Get-Content -Raw -LiteralPath $StatePath | ConvertFrom-Json
    }
    catch {
        throw "Update transaction state is unreadable; manual recovery is required: $StatePath ($($_.Exception.Message))"
    }

    foreach ($propertyName in @(
        'SchemaVersion', 'InstallRoot', 'StageRoot', 'BackupRoot', 'Phase', 'HadInstall')) {
        if ($null -eq $state.PSObject.Properties[$propertyName]) {
            throw "Update transaction state is missing ${propertyName}: $StatePath"
        }
    }
    if ([int]$state.SchemaVersion -ne 1 -or $state.HadInstall -isnot [bool]) {
        throw "Unsupported update transaction state: $StatePath"
    }
    if (-not [string]::Equals(
            (Get-NormalizedDirectoryPath -Path ([string]$state.InstallRoot)),
            $ExpectedInstallRoot,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Update transaction state targets a different install root: $StatePath"
    }

    $stageRoot = Assert-OwnedTransactionDirectoryPath `
        -Path ([string]$state.StageRoot) -Parent $InstallParent -Suffix '.stage'
    $backupRoot = Assert-OwnedTransactionDirectoryPath `
        -Path ([string]$state.BackupRoot) -Parent $InstallParent -Suffix '.backup'
    $validPhases = @(
        'Staging', 'Prepared', 'MovingOld', 'OldMoved',
        'MovingNew', 'NewMoved', 'MovingData', 'DataMoved', 'Committed')
    if ($validPhases -cnotcontains [string]$state.Phase) {
        throw "Update transaction state has an invalid phase: $($state.Phase)"
    }

    return [pscustomobject]@{
        SchemaVersion = 1
        InstallRoot = $ExpectedInstallRoot
        StageRoot = $stageRoot
        BackupRoot = $backupRoot
        Phase = [string]$state.Phase
        HadInstall = [bool]$state.HadInstall
    }
}

function Invoke-PendingTransactionRecovery {
    param(
        [string]$StatePath,
        [string]$InstallRoot,
        [string]$InstallParent
    )

    $temporaryStatePath = $StatePath + '.tmp'
    $previousStatePath = $StatePath + '.previous'
    $state = $null
    if (-not (Test-Path -LiteralPath $StatePath)) {
        if (Test-Path -LiteralPath $previousStatePath -PathType Leaf) {
            $state = Read-TransactionState `
                -StatePath $previousStatePath `
                -ExpectedInstallRoot $InstallRoot `
                -InstallParent $InstallParent
            [IO.File]::Move($previousStatePath, $StatePath)
        }
        elseif (Test-Path -LiteralPath $temporaryStatePath -PathType Leaf) {
            $state = Read-TransactionState `
                -StatePath $temporaryStatePath `
                -ExpectedInstallRoot $InstallRoot `
                -InstallParent $InstallParent
            [IO.File]::Move($temporaryStatePath, $StatePath)
        }
        else {
            return [pscustomobject]@{
                Recovered = $false
                CleanupPending = $false
                CleanupError = ''
            }
        }
    }
    if (-not (Test-Path -LiteralPath $StatePath -PathType Leaf)) {
        throw "Update transaction state path is not a file: $StatePath"
    }

    if ($null -eq $state) {
        try {
            $state = Read-TransactionState `
                -StatePath $StatePath -ExpectedInstallRoot $InstallRoot -InstallParent $InstallParent
        }
        catch {
            if (-not (Test-Path -LiteralPath $previousStatePath -PathType Leaf)) {
                throw
            }
            $state = Read-TransactionState `
                -StatePath $previousStatePath `
                -ExpectedInstallRoot $InstallRoot `
                -InstallParent $InstallParent
            Remove-Item -LiteralPath $StatePath -Force
            [IO.File]::Move($previousStatePath, $StatePath)
        }
    }
    foreach ($staleStatePath in @($temporaryStatePath, $previousStatePath)) {
        if (Test-Path -LiteralPath $staleStatePath -PathType Leaf) {
            Remove-Item -LiteralPath $staleStatePath -Force
        }
    }
    if ($state.Phase -ceq 'Committed') {
        try {
            if (Test-Path -LiteralPath $state.StageRoot) {
                Remove-OwnedDirectory `
                    -Path $state.StageRoot -Parent $InstallParent -Prefix '.EitHost.Update.'
            }
            if (Test-Path -LiteralPath $state.BackupRoot) {
                Remove-OwnedDirectory `
                    -Path $state.BackupRoot -Parent $InstallParent -Prefix '.EitHost.Update.'
            }
            Remove-TransactionStateFile -Path $StatePath -ExpectedPath $StatePath
            return [pscustomobject]@{
                Recovered = $true
                CleanupPending = $false
                CleanupError = ''
            }
        }
        catch {
            return [pscustomobject]@{
                Recovered = $true
                CleanupPending = $true
                CleanupError = $_.Exception.Message
            }
        }
    }

    try {
        $installExists = Test-Path -LiteralPath $InstallRoot -PathType Container
        $backupExists = Test-Path -LiteralPath $state.BackupRoot -PathType Container
        $stageExists = Test-Path -LiteralPath $state.StageRoot -PathType Container
        foreach ($path in @($InstallRoot, $state.BackupRoot, $state.StageRoot)) {
            if ((Test-Path -LiteralPath $path) -and
                -not (Test-Path -LiteralPath $path -PathType Container)) {
                throw "Update transaction path must be a directory: $path"
            }
        }

        if ($state.HadInstall) {
            if ($backupExists) {
                if ($installExists) {
                    if ($stageExists) {
                        throw "Interrupted update has both active and staged program directories: $StatePath"
                    }
                    $activeData = Join-Path $InstallRoot 'Data'
                    $backupData = Join-Path $state.BackupRoot 'Data'
                    if (Test-Path -LiteralPath $activeData) {
                        if (-not (Test-Path -LiteralPath $activeData -PathType Container)) {
                            throw "Interrupted update Data path is not a directory: $activeData"
                        }
                        if (Test-Path -LiteralPath $backupData) {
                            throw "Interrupted update has Data in both active and backup directories: $StatePath"
                        }
                        [IO.Directory]::Move($activeData, $backupData)
                    }
                    [IO.Directory]::Move($InstallRoot, $state.StageRoot)
                    $stageExists = $true
                    $installExists = $false
                }

                [IO.Directory]::Move($state.BackupRoot, $InstallRoot)
                $backupExists = $false
                $installExists = $true
            }
            elseif (-not $installExists) {
                throw "Interrupted update lost both the active and backup install directories: $StatePath"
            }
        }
        else {
            if ($backupExists) {
                throw "First-install transaction unexpectedly contains a backup: $StatePath"
            }
            if ($installExists) {
                if ($stageExists -or $state.Phase -cnotin @(
                        'MovingNew', 'NewMoved', 'MovingData', 'DataMoved')) {
                    throw "First-install transaction has an ambiguous active directory: $StatePath"
                }
                Assert-PackageLayout -PackageRoot $InstallRoot
                $null = Assert-PackageChecksums -PackageRoot $InstallRoot
            }
        }

        if (Test-Path -LiteralPath $state.StageRoot) {
            Remove-OwnedDirectory `
                -Path $state.StageRoot -Parent $InstallParent -Prefix '.EitHost.Update.'
        }
        Remove-TransactionStateFile -Path $StatePath -ExpectedPath $StatePath
    }
    catch {
        throw "Unable to recover interrupted update; transaction state was retained at $StatePath. $($_.Exception.Message)"
    }

    return [pscustomobject]@{
        Recovered = $true
        CleanupPending = $false
        CleanupError = ''
    }
}

function Get-InstallUpdateMutexName {
    param([string]$InstallRoot)

    $canonical = (Get-NormalizedDirectoryPath -Path $InstallRoot).ToUpperInvariant()
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($canonical))
    }
    finally {
        $sha256.Dispose()
    }
    $key = [BitConverter]::ToString($hash).Replace('-', '')
    return 'Global\EitHost.Update.' + $key
}

$resolvedPackage = Get-NormalizedDirectoryPath -Path $PackageDirectory
$resolvedInstall = Get-NormalizedDirectoryPath -Path $InstallRoot
$installParent = Split-Path -Parent $resolvedInstall
if ([string]::IsNullOrWhiteSpace($installParent)) {
    throw "Install root must have a parent directory: $resolvedInstall"
}
$installLeaf = Split-Path -Leaf $resolvedInstall
if ([string]::IsNullOrWhiteSpace($installLeaf)) {
    throw "Install root must have a directory name: $resolvedInstall"
}
$updateMutexName = Get-InstallUpdateMutexName -InstallRoot $resolvedInstall
$updateMutex = [Threading.Mutex]::new($false, $updateMutexName)
$updateMutexAcquired = $false
try {
    try {
        # Reject instead of queueing, so a later invocation cannot overwrite a newer package after waiting.
        $updateMutexAcquired = $updateMutex.WaitOne(0)
    }
    catch [Threading.AbandonedMutexException] {
        # The Windows kernel released a crashed updater's mutex; journal recovery below is now authoritative.
        $updateMutexAcquired = $true
    }
    if (-not $updateMutexAcquired) {
        throw "Another EitHost update is already running for install root: $resolvedInstall"
    }

    & {
New-Item -ItemType Directory -Force -Path $installParent | Out-Null
if ((Test-Path -LiteralPath $resolvedInstall) -and
    -not (Test-Path -LiteralPath $resolvedInstall -PathType Container)) {
    throw "Install root must be a directory; update did not start: $resolvedInstall"
}
$transactionStatePath = Join-Path $installParent ('.EitHost.Update.' + $installLeaf + '.state.json')
$runningInstallProcesses = @(
    Get-Process -Name "EitHost.App" -ErrorAction SilentlyContinue |
        Where-Object {
            try {
                [string]::Equals(
                    [System.IO.Path]::GetFullPath($_.Path),
                    (Join-Path $resolvedInstall "EitHost.App.exe"),
                    [StringComparison]::OrdinalIgnoreCase)
            }
            catch {
                $false
            }
        }
)
if ($runningInstallProcesses.Count -gt 0) {
    throw "Close the installed EitHost.App before updating: $resolvedInstall"
}

$hasPendingTransactionState = @(
    $transactionStatePath,
    $transactionStatePath + '.tmp',
    $transactionStatePath + '.previous'
).Where({ Test-Path -LiteralPath $_ }).Count -gt 0
Assert-ExistingInstallIdentity -InstallRoot $resolvedInstall

$initialRecovery = Invoke-PendingTransactionRecovery `
    -StatePath $transactionStatePath -InstallRoot $resolvedInstall -InstallParent $installParent
$installData = Join-Path $resolvedInstall "Data"
if ((Test-Path -LiteralPath $installData) -and
    -not (Test-Path -LiteralPath $installData -PathType Container)) {
    throw "Install Data path must be a directory; update did not start: $installData"
}
if ($hasPendingTransactionState) {
    Assert-ExistingInstallIdentity -InstallRoot $resolvedInstall
}
if ($initialRecovery.CleanupPending) {
    $pendingData = @(Get-DataSnapshot -DataRoot $installData)
    [pscustomobject]@{
        InstallRoot = $resolvedInstall
        DataRoot = $installData
        DataPreserved = $true
        DataFileCount = $pendingData.Count
        CopiedFileCount = 0
        TransactionalSwap = $true
        PackageChecksumsVerified = $false
        UpdateApplied = $false
        CleanupPending = $true
        CleanupError = $initialRecovery.CleanupError
        RecoveryPerformed = $true
    }
    return
}

if (-not (Test-Path -LiteralPath $resolvedPackage -PathType Container)) {
    throw "Package directory does not exist: $resolvedPackage"
}
if ([string]::Equals($resolvedPackage, $resolvedInstall, [StringComparison]::OrdinalIgnoreCase) -or
    (Test-PathInside -CandidatePath $resolvedPackage -RootPath $resolvedInstall) -or
    (Test-PathInside -CandidatePath $resolvedInstall -RootPath $resolvedPackage)) {
    throw "Package directory and install root must not overlap."
}

$packageData = Join-Path $resolvedPackage "Data"
if (Test-Path -LiteralPath $packageData) {
    throw "Publish package must not contain runtime Data: $packageData"
}
Assert-PackageLayout -PackageRoot $resolvedPackage
$packageFileCount = Assert-PackageChecksums -PackageRoot $resolvedPackage

$transactionId = [Guid]::NewGuid().ToString('N')
$stagingRoot = Join-Path $installParent ('.EitHost.Update.' + $transactionId + '.stage')
$backupRoot = Join-Path $installParent ('.EitHost.Update.' + $transactionId + '.backup')
$hadInstall = Test-Path -LiteralPath $resolvedInstall -PathType Container
$before = @(Get-DataSnapshot -DataRoot $installData)
$after = @()
$state = [ordered]@{
    SchemaVersion = 1
    InstallRoot = $resolvedInstall
    StageRoot = $stagingRoot
    BackupRoot = $backupRoot
    Phase = 'Staging'
    HadInstall = $hadInstall
}

try {
    Write-TransactionState -StatePath $transactionStatePath -State $state
    Copy-PackageTree -PackageRoot $resolvedPackage -DestinationRoot $stagingRoot
    Assert-PackageLayout -PackageRoot $stagingRoot
    if ((Assert-PackageChecksums -PackageRoot $stagingRoot) -ne $packageFileCount) {
        throw "Staged package file count mismatch."
    }
    $state.Phase = 'Prepared'
    Write-TransactionState -StatePath $transactionStatePath -State $state

    if ($hadInstall) {
        $state.Phase = 'MovingOld'
        Write-TransactionState -StatePath $transactionStatePath -State $state
        [IO.Directory]::Move($resolvedInstall, $backupRoot)
        $state.Phase = 'OldMoved'
        Write-TransactionState -StatePath $transactionStatePath -State $state
    }

    $state.Phase = 'MovingNew'
    Write-TransactionState -StatePath $transactionStatePath -State $state
    [IO.Directory]::Move($stagingRoot, $resolvedInstall)
    $state.Phase = 'NewMoved'
    Write-TransactionState -StatePath $transactionStatePath -State $state

    $backupData = Join-Path $backupRoot "Data"
    if ($hadInstall -and (Test-Path -LiteralPath $backupData -PathType Container)) {
        $state.Phase = 'MovingData'
        Write-TransactionState -StatePath $transactionStatePath -State $state
        [IO.Directory]::Move($backupData, $installData)
        $state.Phase = 'DataMoved'
        Write-TransactionState -StatePath $transactionStatePath -State $state
    }

    $after = @(Get-DataSnapshot -DataRoot $installData)
    if (-not (Test-SnapshotEqual -Before $before -After $after)) {
        throw "Install update changed runtime Data; update aborted for inspection: $installData"
    }

    $state.Phase = 'Committed'
    Write-TransactionState -StatePath $transactionStatePath -State $state
}
catch {
    $primaryError = $_
    try {
        $rollback = Invoke-PendingTransactionRecovery `
            -StatePath $transactionStatePath -InstallRoot $resolvedInstall -InstallParent $installParent
        if ($rollback.CleanupPending) {
            throw "Rollback unexpectedly reached a committed cleanup state."
        }
    }
    catch {
        throw "Install update failed and recovery also failed. Primary: $($primaryError.Exception.Message) Recovery: $($_.Exception.Message) State: $transactionStatePath"
    }
    throw $primaryError
}

$commitCleanup = Invoke-PendingTransactionRecovery `
    -StatePath $transactionStatePath -InstallRoot $resolvedInstall -InstallParent $installParent
[pscustomobject]@{
    InstallRoot = $resolvedInstall
    DataRoot = $installData
    DataPreserved = $true
    DataFileCount = $after.Count
    CopiedFileCount = $packageFileCount
    TransactionalSwap = $true
    PackageChecksumsVerified = $true
    UpdateApplied = $true
    CleanupPending = [bool]$commitCleanup.CleanupPending
    CleanupError = [string]$commitCleanup.CleanupError
    RecoveryPerformed = [bool]$initialRecovery.Recovered
}
    }
}
finally {
    if ($updateMutexAcquired) {
        try { $updateMutex.ReleaseMutex() } catch { }
    }
    $updateMutex.Dispose()
}
