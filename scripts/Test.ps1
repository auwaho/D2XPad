[CmdletBinding()]
param([string]$NodePath = 'node')

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$tests = Join-Path $root 'tests'
$binaries = Join-Path $root 'artifacts/test-bin'
& (Join-Path $PSScriptRoot 'Build-SeriesProbe.ps1') -SdkOnly

function Build-Check([string]$project, [string]$output) {
    dotnet build $project -c Release -o $output -m:1 -p:TreatWarningsAsErrors=true -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project." }
}

foreach ($name in 'LiveState', 'Reconnect', 'SleepOutput', 'CalibrationWizardChecks') {
    $suite = Join-Path $tests $name
    $output = Join-Path $binaries $name
    Build-Check (Join-Path $suite "$name.csproj") $output
    dotnet (Join-Path $output "$name.dll")
    if ($LASTEXITCODE -ne 0) { throw "Tests failed: $name." }
}

$capture = Join-Path $binaries 'D2XPad.Capture'
Build-Check (Join-Path $root 'src/D2XPad.Capture/D2XPad.Capture.csproj') $capture
& (Join-Path $tests 'Test-Capture.ps1') -ExePath (Join-Path $capture 'D2XPad.Capture.exe')

& $NodePath --test (Join-Path $tests 'Test-LiveBrowser.cjs')
if ($LASTEXITCODE -ne 0) { throw 'Browser monitor tests failed.' }
