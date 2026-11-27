# Build GpwBatteryProbe.exe with the .NET Framework compiler shipped with Windows.
# No SDK, no network, no third-party dependency.
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "compiler not found: $csc" }

$bin = Join-Path $root 'bin'
if (-not (Test-Path $bin)) { New-Item -ItemType Directory -Path $bin | Out-Null }

$out = Join-Path $bin 'GpwBatteryProbe.exe'
$src = Join-Path $root 'tools\GpwBatteryProbe.cs'

& $csc /nologo /target:exe /platform:x64 /codepage:65001 "/out:$out" $src
if ($LASTEXITCODE -ne 0) { throw "build failed, exit code $LASTEXITCODE" }

Write-Host "OK: $out"
