[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExecutablePath,
    [string]$ProcessArguments = '',
    [string]$WorkingDirectory = '',
    [ValidateRange(1, 2147483647)]
    [int]$TimeoutMilliseconds = 60000,
    [hashtable]$EnvironmentVariables = @{}
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Stop-EntireProcessTree {
    param([Diagnostics.Process]$Process)

    if ($Process.HasExited) {
        return
    }

    $terminationErrors = @()
    $treeKillSucceeded = $false
    $killTreeMethod = $Process.GetType().GetMethods() |
        Where-Object {
            if ($_.Name -ne 'Kill') { return $false }
            $parameters = $_.GetParameters()
            return $parameters.Count -eq 1 -and $parameters[0].ParameterType -eq [bool]
        } |
        Select-Object -First 1
    if ($null -ne $killTreeMethod) {
        try {
            $null = $killTreeMethod.Invoke($Process, @($true))
            $treeKillSucceeded = $true
        }
        catch {
            $terminationErrors += $_.Exception.Message
        }
    }
    if (-not $treeKillSucceeded) {
        try {
            $taskKillPath = Join-Path $env:SystemRoot 'System32\taskkill.exe'
            $taskKillOutput = & $taskKillPath /PID $Process.Id /T /F 2>&1
            $taskKillExitCode = $LASTEXITCODE
            if ($taskKillExitCode -ne 0) {
                throw "taskkill failed with exit code ${taskKillExitCode}: $($taskKillOutput -join ' ')"
            }
            $treeKillSucceeded = $true
        }
        catch {
            $terminationErrors += $_.Exception.Message
        }
    }

    if (-not $treeKillSucceeded) {
        try { if (-not $Process.HasExited) { $Process.Kill() } } catch { }
        $null = $Process.WaitForExit(5000)
        throw "Unable to terminate the entire process tree for PID $($Process.Id): $($terminationErrors -join '; ')"
    }

    if (-not $Process.WaitForExit(5000)) {
        try { $Process.Kill() } catch { }
        if (-not $Process.WaitForExit(5000)) {
            throw "Timed-out process $($Process.Id) did not exit after tree termination."
        }
    }

}

$startInfo = New-Object Diagnostics.ProcessStartInfo
$startInfo.FileName = $ExecutablePath
$startInfo.Arguments = $ProcessArguments
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
if ([string]::IsNullOrWhiteSpace($WorkingDirectory)) {
    $startInfo.WorkingDirectory = (Get-Location).Path
}
else {
    $startInfo.WorkingDirectory = [IO.Path]::GetFullPath($WorkingDirectory)
}
foreach ($name in $EnvironmentVariables.Keys) {
    $startInfo.EnvironmentVariables[[string]$name] = [string]$EnvironmentVariables[$name]
}

$process = [Diagnostics.Process]::Start($startInfo)
if ($null -eq $process) {
    throw "Unable to start process: $ExecutablePath"
}

try {
    $processId = $process.Id
    $timedOut = -not $process.WaitForExit($TimeoutMilliseconds)
    if ($timedOut) {
        Stop-EntireProcessTree -Process $process
    }

    [pscustomobject]@{
        ProcessId = $processId
        TimedOut = $timedOut
        ExitCode = if ($process.HasExited) { $process.ExitCode } else { $null }
    }
}
finally {
    $process.Dispose()
}
