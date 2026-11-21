[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$LiveExe,
    [Parameter(Mandatory)][ValidateRange(1, 2147483647)][int]$ExpectedProcessId,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{32}$')][string]$ExpectedSessionId,
    [Parameter(Mandatory)][ValidateRange(0, 3)][int]$ExpectedSlot,
    [Parameter(Mandatory)][string]$ReportPath
)

$ErrorActionPreference = 'Stop'
$live = (Resolve-Path -LiteralPath $LiveExe).Path
$record = [ordered]@{
    processId = $ExpectedProcessId
    sessionId = $ExpectedSessionId
    slot = $ExpectedSlot
    pulseMilliseconds = 400
    leftMotor = 24576
    rightMotor = 20480
    startCode = $null
    stopCode = $null
    effectsBefore = $null
    effectsAfter = $null
    passed = $false
    error = $null
}

function Read-Session {
    $json = & $live status --json
    if ($LASTEXITCODE -ne 0) { throw 'D2XPad could not read the live session status.' }
    $status = ($json -join "`n") | ConvertFrom-Json
    if ($status.processId -ne $ExpectedProcessId -or $status.sessionId -ne $ExpectedSessionId -or
        $status.slot -ne $ExpectedSlot -or $status.layoutId -ne '85hf-bluetooth' -or
        !$status.connected -or $status.suspended -or $status.waitingForNeutral -or !$status.vibrationEnabled) {
        throw 'The matching live Bluetooth 85HF session with game vibration enabled is required.'
    }
    if ($status.vibrationProblem) { throw $status.vibrationProblem }
    return $status
}

try {
    if ((Get-Process -Id $ExpectedProcessId).Path -ne $live) { throw 'The running converter belongs to a different build.' }

    if (!('D2XPadServiceRumbleCheck' -as [type])) {
        Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
using System.Threading;

public static class D2XPadServiceRumbleCheck
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Vibration
    {
        public ushort Left;
        public ushort Right;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("xinput1_4.dll", ExactSpelling = true)]
    private static extern uint XInputSetState(uint slot, ref Vibration vibration);

    public static uint[] Pulse(uint slot, ushort left, ushort right, int milliseconds)
    {
        uint[] codes = new uint[2];
        var vibration = new Vibration { Left = left, Right = right };
        try
        {
            codes[0] = XInputSetState(slot, ref vibration);
            if (codes[0] == 0) Thread.Sleep(milliseconds);
        }
        finally
        {
            vibration = new Vibration();
            codes[1] = XInputSetState(slot, ref vibration);
        }
        return codes;
    }
}
'@
    }

    $before = Read-Session
    $record.effectsBefore = $before.vibrationEffects
    $codes = [D2XPadServiceRumbleCheck]::Pulse($ExpectedSlot, $record.leftMotor, $record.rightMotor,
        $record.pulseMilliseconds)
    $record.startCode = $codes[0]
    $record.stopCode = $codes[1]
    Start-Sleep -Milliseconds 500
    $after = Read-Session
    $record.effectsAfter = $after.vibrationEffects
    if ($before.generation -ne $after.generation -or $before.devicePath -ne $after.devicePath) {
        throw 'The physical controller changed during the pulse.'
    }
    if ($record.startCode -ne 0 -or $record.stopCode -ne 0) { throw 'XInput did not accept both vibration commands.' }
    if ($record.effectsAfter - $record.effectsBefore -ne 1) { throw 'Expected one successful nonzero physical motor write.' }
    $record.passed = $true
}
catch {
    $record.error = $_.Exception.Message
}
finally {
    $record.completedUtc = [DateTime]::UtcNow.ToString('o')
    $record | ConvertTo-Json | Tee-Object -FilePath $ReportPath
}

if (!$record.passed) { exit 1 }
