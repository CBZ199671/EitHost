[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BuildDirectory,
    [string]$SdccRoot = (Join-Path $env:LOCALAPPDATA 'EitHostTools\SDCC\4.6.0')
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$build = [IO.Path]::GetFullPath($BuildDirectory)
$reportRoot = Join-Path $build 'startup-tests'
New-Item -ItemType Directory -Path $reportRoot -Force | Out-Null
$simulator = Join-Path $SdccRoot 'bin\s51.exe'
$compiler = Join-Path $SdccRoot 'bin\sdcc.exe'
foreach ($tool in @($simulator, $compiler)) {
    if (-not (Test-Path -LiteralPath $tool)) { throw "Missing tool: $tool; specify a full SDCC 4.6.0 installation." }
}
$manifest = Get-Content -LiteralPath (Join-Path $build 'firmware-build.json') -Raw | ConvertFrom-Json
$hex = Join-Path $build ([IO.Path]::GetFileName($manifest.hex_path))
if ((Get-FileHash -LiteralPath $hex).Hash -ne $manifest.hex_sha256) { throw 'Build HEX hash mismatch' }
$map = Get-Content -LiteralPath (Join-Path $build 'AD9106.map') -Raw
$listing = Get-Content -LiteralPath (Join-Path $build 'build-work\src_main.rst') -Raw
$uartListing = Get-Content -LiteralPath (Join-Path $build 'build-work\src_usart.rst') -Raw
$results = [Collections.Generic.List[object]]::new()

function Get-MapSymbol([string]$Text, [string]$Name) {
    $match = [regex]::Match($Text, '(?m)^\s*(?:[CD]:)?\s*([0-9A-F]{8})\s+' + [regex]::Escape($Name) + '(?:\s|$)')
    if (-not $match.Success) { throw "Missing linked symbol: $Name" }
    return [Convert]::ToInt32($match.Groups[1].Value, 16)
}

function Invoke-Simulation([string]$Image, [string[]]$Commands, [string]$Name) {
    # uCsim's Windows argument parser splits absolute paths containing spaces.
    Push-Location (Split-Path -Parent $Image)
    try {
        $text = (($Commands + 'quit') -join "`n" | & $simulator -q -b -t C52 ([IO.Path]::GetFileName($Image))) -join "`n"
        $exitCode = $LASTEXITCODE
    } finally { Pop-Location }
    $text | Set-Content -LiteralPath (Join-Path $reportRoot "$Name.log") -Encoding utf8
    if ($exitCode -ne 0 -or $text -match '(?i)no loadable file|unknown command|syntax error|invalid address') {
        throw "Simulator command failed: $Name"
    }
    return $text
}

function Assert-Breakpoint([string]$Text, [int]$Address) {
    if ($Text -notmatch ('Stop at 0x{0:x6}:.*Breakpoint' -f $Address)) { throw "Did not reach breakpoint 0x$($Address.ToString('x4'))" }
}

function Read-Dump([string]$Text, [int]$AddressDigits = 4) {
    $memory = @{}
    foreach ($row in [regex]::Matches($Text, '(?m)^0x([0-9a-f]{' + $AddressDigits + '})\s+((?:[0-9a-f]{2}(?: |$)){1,8})')) {
        $offset = [Convert]::ToInt32($row.Groups[1].Value, 16)
        $bytes = $row.Groups[2].Value.Trim() -split ' +'
        for ($i = 0; $i -lt $bytes.Count; $i++) { $memory[$offset + $i] = [Convert]::ToInt32($bytes[$i], 16) }
    }
    return $memory
}

function Read-Port([string]$Text, [int]$Address) {
    $rows = [regex]::Matches($Text, '(?m)^0x' + $Address.ToString('x2') + '\s+\w+:.*?0x([0-9a-f]{2})')
    if ($rows.Count -eq 0) { throw "Missing SFR dump: $Address" }
    return [Convert]::ToInt32($rows[$rows.Count - 1].Groups[1].Value, 16)
}

function Get-FunctionInstructions([string]$Text, [string]$Name) {
    $body = [regex]::Match($Text, '(?s)' + [regex]::Escape($Name) + ':.*?(?=;Allocation info|\z)').Value
    return @([regex]::Matches($body, '(?m)^\s*([0-9A-F]{6})\s+((?:[0-9A-F]{2} )+)\s+\[\d+\]\s+\d+\s+([^\r\n]+)') | ForEach-Object {
        [pscustomobject]@{ Address = [Convert]::ToInt32($_.Groups[1].Value, 16); Assembly = $_.Groups[3].Value.Trim(); Bytes = $_.Groups[2].Value.Trim() -split ' +' }
    })
}

$main = Get-MapSymbol $map '_main'
$mainInstructions = Get-FunctionInstructions $listing '_main'
$enable = @($mainInstructions | Where-Object Assembly -Match '^setb\s+_EA$')
if ($enable.Count -ne 1) { throw 'Cannot identify normal main-loop entry' }
$loop = $enable[0].Address + $enable[0].Bytes.Count
$send = Get-MapSymbol $map '_UART0_Send_Data'
$state = Get-MapSymbol $map '_USART_RX_STA'
$expectedLength = Get-MapSymbol $map '_USART_RX_EXPECTED_LEN'
$buffer = Get-MapSymbol $map '_USART_RX_BUF'
$xiseg = Get-MapSymbol $map 's_XISEG'
$xilen = Get-MapSymbol $map 'l_XISEG'
$responseMatch = [regex]::Match($listing, '(?m)^\s*([0-9A-F]{6})\s+\d+\s+_g_response_frame:')
if (-not $responseMatch.Success) { throw 'Missing linked response buffer' }
$response = [Convert]::ToInt32($responseMatch.Groups[1].Value, 16)

# Full production image; no simulator ROM patches. Sampled requests are injected
# after reception: this checks boot/RAM/dispatch, not physical UART timing.
foreach ($p3 in @(0, 255)) {
    foreach ($fill in @(0, 165)) {
        foreach ($command in @(8, 9)) {
            $name = "boot-p3-$p3-ram-$fill-cmd-$command"
            $commands = @('reset', ('fill xram 0 0x1fff 0x{0:x2}' -f $fill),
                ('set memory sfr 0xb0 0x{0:x2}' -f $p3), 'set memory sfr 0xb1 0xfc', 'set memory sfr 0xb2 0',
                'set memory sfr 0x8e 1', ('break 0x{0:x4}' -f $main), 'step 300000',
                ('dump xram 0x{0:x4} 0x{1:x4}' -f $xiseg, ($xiseg + $xilen - 1)),
                ('clear 0x{0:x4}' -f $main), ('break 0x{0:x4}' -f $loop), 'step 300000', 'dump sfr 0xb0 0xb2',
                ('set memory iram 0x{0:x2} 3 0x80' -f $state),
                ('set memory xram 0x{0:x4} 0xaa {1} {2}' -f $buffer, $command, (0xaa -bxor $command)),
                ('clear 0x{0:x4}' -f $loop), ('break 0x{0:x4}' -f $send), 'step 300000',
                ('dump xram 0x{0:x4} 0x{1:x4}' -f $response, ($response + 25)))
            $text = Invoke-Simulation $hex $commands $name
            foreach ($address in @($main, $loop, $send)) { Assert-Breakpoint $text $address }
            $dump = Read-Dump $text
            for ($i = 0; $i -lt $xilen; $i++) {
                if ($dump[$xiseg + $i] -ne 0) { throw "Production initializers were not zero at main: $name" }
            }
            if (((Read-Port $text 0xb0) -band 3) -ne 3) { throw "UART latches not released: $name" }
            $replyLength = if ($command -eq 8) { 26 } else { 17 }
            $reply = [byte[]](0..($replyLength - 1) | ForEach-Object { $dump[$response + $_] })
            $expected = if ($command -eq 8) { '55-02-08-00-14-01-04-01-00-1F-00-0E-10-00-00-00-07-D0-00-01-15-C6-10-00-02-59' } else { '55-02-09-00-0B-00-00-00-00-00-00-00-00-00-00-00-55' }
            if ([BitConverter]::ToString($reply) -ne $expected) { throw "Wrong production response: $name / $([BitConverter]::ToString($reply))" }
            $results.Add(@{ Case = $name; Passed = $true; AutomaticBoot = $true; Response = $expected })
        }
    }
}

# XINIT copy boundaries, including crossing 256-byte pages and ending at 8 KB.
# Fixtures link the very same assembled startup object as the delivered image.
foreach ($length in @(0, 1, 11, 255, 256, 257, 511, 512, 513, 4097, 7802)) {
    $name = "xinit-$length"
    $source = "__xdata unsigned char reserved[0x176];`n"
    if ($length -gt 0) {
        $values = 0..($length - 1) | ForEach-Object { (37 * $_ + 19) -band 255 }
        $source += "__xdata unsigned char initialized[$length] = {" + ($values -join ',') + "};`n"
    }
    $source += 'void main(void) { for (;;) {} }'
    $source | Set-Content -LiteralPath (Join-Path $reportRoot "$name.c") -Encoding ascii
    Push-Location $reportRoot
    try {
        $output = & $compiler --model-large --nooverlay --opt-code-size --Werror -c "$name.c" 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Fixture compile failed: $output" }
        $output = & $compiler --model-large --Werror --iram-size 0x100 --xram-loc 0x10 --xram-size 0x1ff0 -o "$name.ihx" "$name.rel" '..\build-work\stc8h_xinit.rel' 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Fixture link failed: $output" }
    } finally { Pop-Location }
    $fixtureMap = Get-Content -LiteralPath (Join-Path $reportRoot "$name.map") -Raw
    $fixtureMain = Get-MapSymbol $fixtureMap '_main'
    $fixtureStart = Get-MapSymbol $fixtureMap 's_XISEG'
    if ((Get-MapSymbol $fixtureMap 'l_XINIT') -ne $length) { throw "Wrong fixture size: $name" }
    $text = Invoke-Simulation (Join-Path $reportRoot "$name.ihx") @('reset', 'fill xram 0 0x1fff 0xa5',
        'set memory sfr 0xa0 0x5a', ('break 0x{0:x4}' -f $fixtureMain), 'step 2000000', 'dump xram 0 0x1fff', 'dump sfr 0xa0 0xa0') $name
    Assert-Breakpoint $text $fixtureMain
    $dump = Read-Dump $text
    if ($dump.Count -ne 8192) { throw "Incomplete XRAM dump: $name" }
    for ($i = 0; $i -lt 8192; $i++) {
        $expected = 0xa5
        if ($i -ge 0x10 -and $i -lt 0x186) { $expected = 0 }
        if ($length -gt 0 -and $i -ge $fixtureStart -and $i -lt $fixtureStart + $length) { $expected = (37 * ($i - $fixtureStart) + 19) -band 255 }
        if ($dump[$i] -ne $expected) { throw "XINIT overwritten/uninitialized byte at $i in $name" }
    }
    if ((Read-Port $text 0xa0) -ne 0x5a) { throw "XINIT changed P2 GPIO: $name" }
    $results.Add(@{ Case = $name; Passed = $true; CheckedXramBytes = 8192; P2Preserved = $true })
}

# Interrupt-boundary tests of the linked reset routine. C52 does not model the
# STC RX peripheral: assert RI, then inject the sampled byte in the ISR register
# immediately after its SBUF read. CPU interrupt entry, masking and ISR execute
# unchanged. No instructions/ROM are replaced.
$reset = Get-MapSymbol $map '_UART0_ResetReceiver'
$resetInstructions = Get-FunctionInstructions $uartListing '_UART0_ResetReceiver'
$isrInstructions = Get-FunctionInstructions $uartListing '_UART0_Inter'
$sample = @($isrInstructions | Where-Object Assembly -Match '^mov\s+r7,_SBUF$')
if ($sample.Count -ne 1) { throw 'Review UART RX sampled-register injection for this compiler output' }
$afterSample = $sample[0].Address + $sample[0].Bytes.Count
$criticalStart = @($resetInstructions | Where-Object Assembly -Match '^clr\s+_EA$')[0].Address
$criticalEnd = @($resetInstructions | Where-Object Assembly -Match '^mov\s+_EA,c$')[0].Address
if ($criticalEnd -le $criticalStart) { throw 'Receiver reset is not interrupt-protected' }
$boundaries = @($resetInstructions | Where-Object { $_.Address -gt $criticalStart -and $_.Address -le $criticalEnd })
foreach ($instruction in $boundaries) {
    $name = 'reset-pending-rx-{0:x4}' -f $instruction.Address
    $commands = @('reset', ('break 0x{0:x4}' -f $loop), 'step 300000', ('clear 0x{0:x4}' -f $loop),
        ('set memory iram 0x{0:x2} 3 0x80' -f $state), ('set memory iram 0x{0:x2} 3' -f $expectedLength),
        'set memory sfr 0x8e 0xd0', 'set memory sfr 0xaf 0x24', 'set memory sfr 0xef 1',
        ('set memory iram 0x71 0x{0:x2} 0x{1:x2}' -f ($loop -band 255), ($loop -shr 8)),
        'set memory sfr 0x81 0x72', ('pc 0x{0:x4}' -f $reset), ('break 0x{0:x4}' -f $instruction.Address), 'step 1000',
        ('clear 0x{0:x4}' -f $instruction.Address), 'set memory sfr 0x98 0x51', ('break 0x{0:x4}' -f $afterSample), 'step 1000',
        ('dump iram 0x{0:x2} 0x{1:x2}' -f $state, $expectedLength),
        'set memory iram 0x0f 0xaa', ('clear 0x{0:x4}' -f $afterSample), ('break 0x{0:x4}' -f $loop), 'step 1000',
        ('dump iram 0x{0:x2} 0x{1:x2}' -f $state, $expectedLength), ('dump xram 0x{0:x4} 0x{0:x4}' -f $buffer),
        'dump sfr 0xa8 0xa8')
    $text = Invoke-Simulation $hex $commands $name
    foreach ($address in @($instruction.Address, $afterSample)) { Assert-Breakpoint $text $address }
    # First dump (before applying AA) must already be completely reset.
    $atSample = ($text -split ('Stop at 0x{0:x6}:' -f $afterSample), 2)[1]
    $beforeResume = ($atSample -split 'Stop at ', 2)[0]
    $initial = Read-Dump $beforeResume 2
    if ($initial[$state] -ne 0 -or $initial[$state + 1] -ne 0 -or $initial[$expectedLength] -ne 0) { throw "UART ISR entered during a partial reset: $name" }
    $final = Read-Dump $text 2
    $xram = Read-Dump $text
    if ($final[$state] -ne 1 -or $final[$state + 1] -ne 0 -or $final[$expectedLength] -ne 0 -or $xram[$buffer] -ne 0xaa) { throw "Pending next-frame header was lost: $name" }
    if (((Read-Port $text 0xa8) -band 0x80) -eq 0) { throw "Reset failed to restore EA: $name" }
    $results.Add(@{ Case = $name; Passed = $true; PendingHeaderPreserved = $true })
}

# Calls during boot / an enclosing critical section must not enable interrupts.
$text = Invoke-Simulation $hex @('reset', ('break 0x{0:x4}' -f $loop), 'step 300000',
    'set memory sfr 0xa8 0x10', ('set memory iram 0x71 0x{0:x2} 0x{1:x2}' -f ($loop -band 255), ($loop -shr 8)),
    'set memory sfr 0x81 0x72', ('pc 0x{0:x4}' -f $reset), 'step 1000', 'dump sfr 0xa8 0xa8') 'reset-preserves-disabled-ea'
if ((Read-Port $text 0xa8) -ne 0x10) { throw 'Reset enabled interrupts owned by its caller' }
$results.Add(@{ Case = 'reset-preserves-disabled-ea'; Passed = $true })

[ordered]@{
    Passed = $true
    HardwareValidated = $false
    HexSha256 = $manifest.hex_sha256
    Timestamp = [DateTimeOffset]::Now.ToString('o')
    Scope = 'C52 CPU/RAM + real interrupt entry with injected sampled RX bytes; no STC timer/physical UART/analog timing model; no ROM patches'
    Cases = $results
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $build 'firmware-startup-check.json') -Encoding utf8
Write-Output "Firmware startup / receiver regression passed: $($results.Count) cases."
