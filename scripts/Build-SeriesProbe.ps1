param([switch]$SdkOnly)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$deps = Join-Path $root 'artifacts/deps/HIDMaestro-2.3.0'
$null = New-Item -ItemType Directory -Force $deps
$archive = Join-Path $deps 'HIDMaestro-v2.3.0.zip'
$expected = 'fddb61b76d076aa2cf508a46c1e30e23aa75b4ae622d52b15e7259959fd12295'
if (!(Test-Path -LiteralPath $archive)) {
    $temp = "$archive.$([guid]::NewGuid().ToString('N')).download"
    try {
        Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/hifihedgehog/HIDMaestro/releases/download/v2.3.0/HIDMaestro-v2.3.0.zip' -OutFile $temp
        if ((Get-FileHash -LiteralPath $temp).Hash -ne $expected) { throw 'Unexpected release SHA-256.' }
        Move-Item -LiteralPath $temp -Destination $archive
    }
    finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp } }
}
if ((Get-FileHash -LiteralPath $archive).Hash -ne $expected) { throw "Unexpected SHA-256 for $archive" }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    foreach ($name in 'HIDMaestro.Core.dll','LICENSE','THIRD-PARTY-NOTICES.txt') {
        [IO.Compression.ZipFileExtensions]::ExtractToFile($zip.GetEntry($name), (Join-Path $deps $name), $true)
    }
}
finally { $zip.Dispose() }
if ($SdkOnly) { return }
dotnet publish (Join-Path $root 'src/D2XPad.SeriesProbe/D2XPad.SeriesProbe.csproj') -c Release -o (Join-Path $root 'artifacts/SeriesProbe') -p:TreatWarningsAsErrors=true
exit $LASTEXITCODE
