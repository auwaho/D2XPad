[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$LiveExe,
    [Parameter(Mandatory)][ValidateSet('portable', 'service')][string]$Mode,
    [Parameter(Mandatory)][string]$ReportPath,
    [ValidateRange(5, 90)][int]$Seconds = 25,
    [string]$VibrationReportPath
)

$ErrorActionPreference = 'Stop'
$live = (Resolve-Path -LiteralPath $LiveExe).Path
$report = [IO.Path]::GetFullPath($ReportPath)
if (Test-Path -LiteralPath $report) { throw 'Choose an unused latency report path.' }

$start = [Diagnostics.ProcessStartInfo]::new($live)
$start.WorkingDirectory = Split-Path -Parent $live
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
foreach ($argument in 'play', '--desktop', '--output', $Mode, '--vibration') { $start.ArgumentList.Add($argument) }
$start.Environment['D2XPAD_LATENCY_LABEL'] = $Mode
$start.Environment['D2XPAD_LATENCY_REPORT'] = $report
$start.Environment['D2XPAD_LATENCY_SECONDS'] = [string]$Seconds

$process = [Diagnostics.Process]::Start($start)
$readLine = $process.StandardOutput.ReadLineAsync()
$readError = $process.StandardError.ReadToEndAsync()

try {
    $deadline = [DateTime]::UtcNow.AddSeconds($Seconds + 245)
    while (!(Test-Path -LiteralPath $report) -and [DateTime]::UtcNow -lt $deadline) {
        while ($null -ne $readLine -and $readLine.IsCompleted) {
            $line = $readLine.GetAwaiter().GetResult()
            if ($null -eq $line) { $readLine = $null; break }
            Write-Host $line
            $readLine = $process.StandardOutput.ReadLineAsync()
        }
        if ($process.HasExited) { throw "D2XPad exited with code $($process.ExitCode). $($readError.GetAwaiter().GetResult())" }
        Start-Sleep -Milliseconds 125
    }
    if (!(Test-Path -LiteralPath $report)) { throw 'D2XPad did not produce a latency report before the deadline.' }

    $latency = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    $statusJson = & $live status --json
    if ($LASTEXITCODE -ne 0) { throw 'D2XPad could not read the live session status.' }
    $status = ($statusJson -join "`n") | ConvertFrom-Json
    if ($status.processId -ne $process.Id -or $latency.converterProcessId -ne $process.Id -or
        $status.slot -ne $latency.xInputSlot -or $status.outputMode -ne $Mode -or
        $status.disconnects -ne 0 -or $status.reconnects -ne 0 -or $status.suspends -ne 0) {
        throw 'The measured session changed, or it did not use the requested output mode.'
    }
    $latency | Select-Object label, xInputSlot, matchedChanges, submittedChanges, coverage, medianMilliseconds, p99Milliseconds,
        pollMedianMilliseconds, pollP99Milliseconds, sufficientSample, problem

    if ($VibrationReportPath) {
        $vibration = @{
            LiveExe = $live
            ExpectedProcessId = $process.Id
            ExpectedSessionId = $status.sessionId
            ExpectedSlot = $status.slot
            ReportPath = $VibrationReportPath
        }
        & (Join-Path $PSScriptRoot 'Test-PhysicalVibration.ps1') @vibration
        if ($LASTEXITCODE -ne 0) { throw 'The physical vibration check failed.' }
    }
    if (!$latency.sufficientSample) {
        throw 'The latency sample did not meet the capture requirements. Review the report before comparing percentiles.'
    }
}
finally {
    $process.StandardInput.Close()
    if (!$process.WaitForExit(35000)) {
        Write-Warning 'D2XPad is still shutting down. Its own watchdog will release the virtual controller.'
    }
    elseif ($process.ExitCode -ne 0) {
        Write-Warning "D2XPad exited with code $($process.ExitCode). $($readError.GetAwaiter().GetResult())"
    }
    $process.Dispose()
}
