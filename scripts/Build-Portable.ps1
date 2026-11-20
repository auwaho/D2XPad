[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^v[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$releases = Join-Path $root 'artifacts/releases'
$fileName = "D2XPad-$Version-win-x64.zip"
$archive = Join-Path $releases $fileName
$checksum = "$archive.sha256"
if ((Test-Path -LiteralPath $archive) -or (Test-Path -LiteralPath $checksum)) {
    throw "A package already exists for $Version. Choose a new version or move the previous files out of $releases."
}

$license = Join-Path $root 'LICENSE'
if (!(Test-Path -LiteralPath $license -PathType Leaf)) { throw 'The project LICENSE is required for packaging.' }

$appVersion = $Version.Substring(1)
$numericVersion = [version]($appVersion.Split('-')[0])
$fileVersion = "$numericVersion.0"
$build = @(& (Join-Path $PSScriptRoot 'Build-Desktop.ps1') -StageOnly -Version $appVersion)
$build | Out-Host
if ($LASTEXITCODE -ne 0) { throw 'Portable desktop publish failed.' }
$stage = Split-Path ($build | Select-Object -Last 1) -Parent
foreach ($file in 'D2XPad.exe', 'D2XPad.dll', 'D2XPad.deps.json', 'D2XPad.runtimeconfig.json',
    'D2XPad.Live.exe', 'D2XPad.Live.dll', 'D2XPad.Live.deps.json', 'D2XPad.Live.runtimeconfig.json',
    'HIDMaestro.Core.dll', 'HIDMaestro/LICENSE', 'HIDMaestro/THIRD-PARTY-NOTICES.txt') {
    if (!(Test-Path -LiteralPath (Join-Path $stage $file) -PathType Leaf)) { throw "Package is missing $file." }
}

foreach ($file in 'D2XPad.exe', 'D2XPad.Live.exe') {
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $stage $file))
    if ($info.ProductVersion -cne $appVersion -or $info.FileVersion -ne $fileVersion) {
        throw "$file reports product version '$($info.ProductVersion)' and file version '$($info.FileVersion)'. Expected '$appVersion' and '$fileVersion' for $Version."
    }
}

Copy-Item -LiteralPath $license -Destination (Join-Path $stage 'LICENSE')
$null = [IO.Directory]::CreateDirectory((Join-Path $stage 'profiles'))
@'
D2XPad portable

Extract all files to a writable folder and open D2XPad.exe.
The app requires 64-bit Windows and the .NET 10 Desktop Runtime for x64.
Windows may ask for permission to set up the bundled HIDMaestro driver when you start conversion.
Keep the profiles folder when upgrading to preserve your mappings.
'@ | Set-Content -LiteralPath (Join-Path $stage 'START-HERE.txt') -Encoding utf8

$null = [IO.Directory]::CreateDirectory($releases)
$temporary = Join-Path $releases ('.package-' + [guid]::NewGuid().ToString('N'))
$createdArchive = $false
try {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $temporary, [IO.Compression.CompressionLevel]::Optimal, $false)
    $hash = (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText("$temporary.sha256", "$hash  $fileName`n", [Text.Encoding]::ASCII)
    [IO.File]::Move($temporary, $archive)
    $createdArchive = $true
    [IO.File]::Move("$temporary.sha256", $checksum)
}
catch {
    if ($createdArchive) { [IO.File]::Delete($archive) }
    throw
}
finally {
    [IO.File]::Delete($temporary)
    [IO.File]::Delete("$temporary.sha256")
}
Write-Output $archive
Write-Output $checksum
