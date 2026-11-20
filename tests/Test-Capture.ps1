# Run after publishing D2XPad.Capture. No controller or driver is needed.
[CmdletBinding()]
param(
    [string] $ExePath,
    [string] $ProfilePath = (Join-Path $PSScriptRoot '../src/D2XPad.Capture/85hf-capture-map.json')
)

$ErrorActionPreference = 'Stop'
if (!$ExePath) { $ExePath = Join-Path $PSScriptRoot '../artifacts/85HF/D2XPad.Capture.exe' }
$ExePath = (Resolve-Path -LiteralPath $ExePath).Path
$profileData = Get-Content -LiteralPath $ProfilePath -Raw -Encoding UTF8 | ConvertFrom-Json
$profileHash = (Get-FileHash -LiteralPath $ProfilePath -Algorithm SHA256).Hash.ToLowerInvariant()
$steps = [ordered]@{}

# Fixture offsets and masks describe the 85HF payload independent of the decoder.
# Each sample overrides these neutral bytes by offset.
$steps['neutral'] = @(@{})
$buttonOffsets = [ordered]@{
    a = @(8, 1); b = @(8, 2); x = @(8, 8); y = @(8, 16); lb = @(8, 64); rb = @(8, 128)
    select = @(9, 4); start = @(9, 8); home = @(9, 16); l3 = @(9, 32); r3 = @(9, 64)
    l4 = @(10, 1); r4 = @(10, 2); pl = @(8, 32); pr = @(8, 4)
}
foreach ($button in $buttonOffsets.Keys) {
    $index, $mask = $buttonOffsets[$button]
    $steps["button_$button"] = @(@{ $index = $mask })
}
$hat = 0
foreach ($direction in 'up','up_right','right','down_right','down','down_left','left','up_left') {
    $steps["dpad_$direction"] = @(@{ 1 = $hat })
    $hat++
}
foreach ($stick in 'ls','rs') {
    $x = if ($stick -eq 'ls') { 2 } else { 4 }
    $y = $x + 1
    $steps["${stick}_left"]  = @(@{ $x = 0 })
    $steps["${stick}_right"] = @(@{ $x = 255 })
    $steps["${stick}_up"]    = @(@{ $y = 255 })
    $steps["${stick}_down"]  = @(@{ $y = 0 })
}
$ramp = @(0,16,48,64,96,128,160,192,224,255,224,192,160,128,96,64,48,16,0)
$steps['lt_sweep'] = @($ramp | ForEach-Object { @{ 7 = $_ } })
$steps['rt_sweep'] = @($ramp | ForEach-Object { @{ 6 = $_ } })
$steps['both_full'] = @(@{ 7 = 255 }, @{ 6 = 255; 7 = 255 })
$steps['lt_low_rt_high'] = @(@{ 7 = 64 }, @{ 6 = 192; 7 = 64 })
$steps['lt_high_rt_low'] = @(@{ 7 = 192 }, @{ 6 = 64; 7 = 192 })
$steps['pl_a'] = @(@{ 8 = 32 }, @{ 8 = 33 }, @{ 8 = 1 })
$steps['r4_rb'] = @(@{ 10 = 2 }, @{ 8 = 128; 10 = 2 }, @{ 8 = 128 })
$steps['neutral_end'] = @(@{})
if ($steps.Count -ne 40) { throw 'Invalid 85HF stage fixture.' }

function Write-Session([string] $Path, [string] $Scenario) {
    $length = if ($Scenario -eq 'prefix') { 33 } else { 32 }
    $source = @{ id = 'c1'; transport = 'Bluetooth'; usagePage = 1; usage = 5
        vid = 0x2dc8; pid = 0x2067; inputReportLength = $length; pathHash = ('c' * 64); containerHash = ('d' * 64) }
    $header = @{ type = 'session'; schemaVersion = 1; startedUtc = '2026-11-20T10:00:00Z'
        os = 'Windows (synthetic)'; tickFrequency = 1000; profile = $profileData
        profileSha256 = $profileHash; collections = @($source) }
    $writer = [IO.StreamWriter]::new($Path, $false, [Text.UTF8Encoding]::new($false))
    try {
        $writer.WriteLine((ConvertTo-Json $header -Depth 20 -Compress))
        $ticks = 0
        $count = 0
        foreach ($name in $steps.Keys) {
            $writer.WriteLine((ConvertTo-Json @{type='step'; step=$name; phase='start'; ticks=(++$ticks)} -Compress))
            if ($Scenario -eq 'disconnect' -and $name -eq 'lt_sweep') { break }
            $samples = @(@{}) + $steps[$name] + @(@{})
            # Change-only devices may send no packet while the pad stays at rest.
            if ($Scenario -eq 'good' -and $name -eq 'neutral_end') { $samples = @() }
            foreach ($sample in $samples) {
                $frame = [byte[]]::new(32)
                $frame[1] = 15
                foreach ($i in 2..5) { $frame[$i] = 128 }
                foreach ($i in $sample.Keys) { $frame[$i] = $sample[$i] }
                if ($Scenario -eq 'swapped') { $frame[6], $frame[7] = $frame[7], $frame[6] }
                if ($Scenario -eq 'reversed') {
                    $frame[3] = 255 - $frame[3]
                    $frame[5] = 255 - $frame[5]
                }
                if ($Scenario -eq 'prefix') { $frame = [byte[]](@(1) + $frame) }
                $hex = [BitConverter]::ToString($frame).Replace('-', '')
                $ticks += 8
                $writer.WriteLine((ConvertTo-Json @{type='report'; collection='c1'; step=$name; hex=$hex; ticks=$ticks} -Compress))
                $count++
            }
            $writer.WriteLine((ConvertTo-Json @{type='step'; step=$name; phase='end'; ticks=(++$ticks)} -Compress))
        }
        $done = $Scenario -ne 'disconnect'
        $why = if (!$done) { 'disconnected' }
        $writer.WriteLine((ConvertTo-Json @{type='end'; completed=$done; reason=$why; reports=$count; ticks=(++$ticks)} -Compress))
    }
    finally { $writer.Dispose() }
}

$fixture = [IO.Path]::GetTempFileName()
try {
    foreach ($scenario in 'good','prefix','swapped','reversed','disconnect') {
        Write-Session $fixture $scenario
        $result = (& $ExePath analyze $fixture) -join "`n"
        $exitCode = $LASTEXITCODE
        $expected = if ($scenario -in 'good','prefix') { 0 } else { 1 }
        $passed = switch ($scenario) {
            'good'       { $result -match '\[\+\] lt_sweep' -and $result -match '\[\+\] rs_up' }
            'prefix'     { $result -match '\+1\s+\S+\s+.*40/40' -and $result -match '\[\+\] rt_sweep' }
            'swapped'    { $result -notmatch '\[\+\] lt_sweep' -and $result -match '\[\+\] button_l4' }
            'reversed'   { $result -notmatch '\[\+\] ls_up' -and $result -notmatch '\[\+\] rs_down' -and $result -match '\[\+\] lt_sweep' }
            'disconnect' { $result -match '\[\?\] lt_sweep' -and $result -match '\[\+\] rs_down' }
        }
        if ($exitCode -ne $expected -or !$passed) { throw "$scenario failed (exit $exitCode, expected $expected):`n$result" }
        Write-Output "$scenario OK (exit $exitCode)"
    }
}
finally { Remove-Item -LiteralPath $fixture }
