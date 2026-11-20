$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'Build-SeriesProbe.ps1') -SdkOnly
foreach ($project in 'D2XPad.InputGame', 'D2XPad.Live') {
    dotnet publish (Join-Path $root "src/$project/$project.csproj") -c Release -o (Join-Path $root 'artifacts/Live') -p:TreatWarningsAsErrors=true
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
