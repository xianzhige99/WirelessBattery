# Build and run the icon preview tool (reflection into IconFactory).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "compiler not found" }
if (-not (Test-Path (Join-Path $root 'bin\WirelessBattery.exe'))) { throw "build the app first" }

$out = Join-Path $root 'bin\IconPreview.exe'
$src = Join-Path $root 'tools\IconPreview.cs'
& $csc /nologo /target:exe /platform:x64 /codepage:65001 ("/out:" + $out) /reference:System.dll /reference:System.Drawing.dll /reference:System.Core.dll $src
if ($LASTEXITCODE -ne 0) { throw "build failed" }

Push-Location $root
try { & $out 'bin\WirelessBattery.exe' 'docs' } finally { Pop-Location }
