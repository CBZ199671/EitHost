[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Port,
    [Parameter(Mandatory)][string]$ExpectedVersion,
    [Parameter(Mandatory)][string]$OutputPath,
    [ValidateRange(1, 50)][int]$Sessions = 10,
    [ValidateRange(2, 200)][int]$QueriesPerSession = 50
)

# Read-only DDS commands 0x08/0x09. Does not flash, enable DAC, or start scanning.
$ErrorActionPreference = 'Stop'
$results = [Collections.Generic.List[object]]::new()
$failure = $null
try {
    for ($session = 1; $session -le $Sessions; $session++) {
        $serial = [IO.Ports.SerialPort]::new($Port, 115200, [IO.Ports.Parity]::None, 8, [IO.Ports.StopBits]::One)
        $serial.DtrEnable = $false
        $serial.RtsEnable = $false
        $serial.Handshake = [IO.Ports.Handshake]::None
        $serial.ReadTimeout = 1000
        $serial.WriteTimeout = 1000
        try {
            $serial.Open()
            Start-Sleep -Milliseconds 100
            $serial.DiscardInBuffer()
            for ($query = 0; $query -lt $QueriesPerSession; $query++) {
                $command = if (($query % 2) -eq 0) { 8 } else { 9 }
                $request = [byte[]]@(0xaa, $command, (0xaa -bxor $command))
                $received = [Collections.Generic.List[byte]]::new()
                $expectedLength = if ($command -eq 8) { 26 } else { 17 }
                $timer = [Diagnostics.Stopwatch]::StartNew()
                $serial.Write($request, 0, $request.Length)
                while ($received.Count -lt $expectedLength -and $timer.ElapsedMilliseconds -lt 1000) {
                    $available = $serial.BytesToRead
                    if ($available -gt 0) {
                        $chunk = [byte[]]::new($available)
                        $read = $serial.Read($chunk, 0, $chunk.Length)
                        for ($i = 0; $i -lt $read; $i++) { $received.Add($chunk[$i]) }
                    } else { Start-Sleep -Milliseconds 1 }
                }
                $timer.Stop()
                $checksum = 0
                foreach ($value in $received) { $checksum = $checksum -bxor $value }
                $valid = $received.Count -eq $expectedLength -and $received[0] -eq 0x55 -and
                    $received[1] -eq 2 -and $received[2] -eq $command -and $received[3] -eq 0 -and
                    $received[4] -eq ($expectedLength - 6) -and $checksum -eq 0
                $version = if ($valid -and $command -eq 8) { "$($received[5]).$($received[6]).$($received[7])" } else { $null }
                if ($command -eq 8 -and $version -ne $ExpectedVersion) { $valid = $false }
                $results.Add([ordered]@{ Session = $session; Query = $query + 1; Command = $command;
                    Passed = $valid; CompleteFrameMs = $timer.Elapsed.TotalMilliseconds;
                    ReportedVersion = $version; RawHex = [BitConverter]::ToString($received.ToArray()) })
                if (-not $valid) { throw "Invalid reply / version at session $session, query $($query + 1): $([BitConverter]::ToString($received.ToArray()))" }
                Start-Sleep -Milliseconds 10
            }
        } finally {
            if ($serial.IsOpen) { $serial.Close() }
            $serial.Dispose()
        }
    }
} catch { $failure = $_.Exception.Message }

$path = [IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path (Split-Path -Parent $path) -Force | Out-Null
$passed = $null -eq $failure -and $results.Count -eq ($Sessions * $QueriesPerSession)
$report = [ordered]@{
    Timestamp = [DateTimeOffset]::Now.ToString('o'); Port = $Port; ExpectedVersion = $ExpectedVersion
    Passed = $passed; RequestedSessions = $Sessions; Queries = $results.Count
    MaximumCompleteFrameMs = ($results | ForEach-Object { $_.CompleteFrameMs } | Measure-Object -Maximum).Maximum
    Scope = 'Read-only capabilities / scan-status queries and serial reopen; no power cycling, flashing, analog or timing validation'
    Error = $failure; Results = $results
}
$report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $path -Encoding utf8
if (-not $passed) { throw "Communication check failed: $failure; report=$path" }
Write-Output "DDS communication passed: $($results.Count) queries / $Sessions serial sessions; max $([Math]::Round($report.MaximumCompleteFrameMs, 2)) ms; version $ExpectedVersion; report=$path"
