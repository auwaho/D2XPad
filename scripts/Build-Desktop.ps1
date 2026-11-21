param(
    [switch]$StageOnly,
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root 'artifacts'
$output = Join-Path $artifacts 'Desktop'
$token = [guid]::NewGuid().ToString('N')
$stage = Join-Path $artifacts "Desktop-stage-$token"
$desktopProject = Join-Path $root 'src/D2XPad.Desktop/D2XPad.Desktop.csproj'
& (Join-Path $PSScriptRoot 'Build-SeriesProbe.ps1') -SdkOnly

$versionProperties = @()
if ($Version) {
    $versionProperties = @("-p:Version=$Version", '-p:IncludeSourceRevisionInInformationalVersion=false')
}
dotnet publish $desktopProject -c Release -o $stage -m:1 -p:TreatWarningsAsErrors=true -p:UseSharedCompilation=false @versionProperties
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
foreach ($file in 'D2XPad.exe', 'D2XPad.Live.exe', 'HIDMaestro.Core.dll') {
    if (!(Test-Path -LiteralPath (Join-Path $stage $file) -PathType Leaf)) { throw "Published desktop is missing $file." }
}
$setupStage = Join-Path $artifacts "ServiceSetup-stage-$token"
$manifest = "$setupStage.sha256"
$servicePayload = 'D2XPad.Live.exe', 'D2XPad.Live.dll', 'D2XPad.Live.deps.json', 'D2XPad.Live.runtimeconfig.json',
    'HIDMaestro.Core.dll', 'HIDMaestro/LICENSE', 'HIDMaestro/THIRD-PARTY-NOTICES.txt'
$hashes = foreach ($file in $servicePayload) {
    "$file|$((Get-FileHash -LiteralPath (Join-Path $stage $file) -Algorithm SHA256).Hash)"
}
[IO.File]::WriteAllLines($manifest, [string[]]$hashes)
dotnet publish (Join-Path $root 'src/D2XPad.ServiceSetup/D2XPad.ServiceSetup.csproj') -c Release -o $setupStage -m:1 -p:TreatWarningsAsErrors=true -p:UseSharedCompilation=false "-p:D2XPadServiceManifest=$manifest" @versionProperties
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$setup = Join-Path $setupStage 'D2XPad.ServiceSetup.exe'
$setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
dotnet publish $desktopProject -c Release -o $stage -m:1 -p:TreatWarningsAsErrors=true -p:UseSharedCompilation=false "-p:D2XPadServiceSetupSha256=$setupHash" @versionProperties
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
foreach ($entry in $hashes) {
    $file, $expected = $entry -split '\|', 2
    if ((Get-FileHash -LiteralPath (Join-Path $stage $file) -Algorithm SHA256).Hash -ne $expected) {
        throw "Service payload changed while pinning its installer: $file. Rebuild the package."
    }
}
Copy-Item -LiteralPath $setup -Destination $stage

if ($StageOnly) {
    Write-Output (Join-Path $stage 'D2XPad.exe')
    return
}

$lease = [IO.File]::Open((Join-Path $artifacts 'desktop-install.lock'), [IO.FileMode]::OpenOrCreate,
    [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
$backup = Join-Path $artifacts "Desktop-previous-$token"
$previousProfiles = Join-Path $backup 'profiles'
$stagedProfiles = Join-Path $stage 'profiles'
$profilesMoved = $false
$createdLegacy = $null
try {
    $prefix = $output + [IO.Path]::DirectorySeparatorChar
    $running = Get-Process -Name D2XPad,D2XPad.Live -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -and $_.Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
    }
    if ($running) { throw "Exit the deployed D2XPad before installing. The verified package is in $stage." }
    foreach ($path in $artifacts, $output, (Join-Path $output 'profiles')) {
        if ((Test-Path -LiteralPath $path) -and ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Cannot replace a redirected package directory: $path."
        }
    }
    if ([IO.Directory]::Exists($output)) { [IO.Directory]::Move($output, $backup) }
    try {
        if ([IO.Directory]::Exists($previousProfiles)) {
            [IO.Directory]::Move($previousProfiles, $stagedProfiles)
            $profilesMoved = $true
        }
        $null = [IO.Directory]::CreateDirectory($stagedProfiles)
        $legacy = Join-Path $backup '85hf-bindings.json'
        $shipped = Join-Path $root 'src/D2XPad.Live/85hf-bindings.json'
        if ((Test-Path -LiteralPath $legacy -PathType Leaf) -and
            (Get-FileHash -LiteralPath $legacy).Hash -ne (Get-FileHash -LiteralPath $shipped).Hash) {
            $destination = Join-Path $stagedProfiles '85hf-bindings.json'
            if (Test-Path -LiteralPath $destination) {
                $destination = Join-Path $stagedProfiles "85hf-bindings-legacy-$token.json"
            }
            [IO.File]::Copy($legacy, $destination, $false)
            $createdLegacy = $destination
        }
        [IO.Directory]::Move($stage, $output)
    }
    catch {
        if ($createdLegacy) { [IO.File]::Delete($createdLegacy) }
        if ($profilesMoved) { [IO.Directory]::Move($stagedProfiles, $previousProfiles) }
        if ([IO.Directory]::Exists($backup)) { [IO.Directory]::Move($backup, $output) }
        throw
    }
}
finally { $lease.Dispose() }
Write-Output (Join-Path $output 'D2XPad.exe')
