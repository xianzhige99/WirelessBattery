$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    throw "csc.exe not found: $csc (requires the .NET Framework 4.x shipped with Windows)"
}

$bin = Join-Path $root 'bin'
if (-not (Test-Path $bin)) { New-Item -ItemType Directory -Path $bin | Out-Null }

$sources = Get-ChildItem (Join-Path $root 'src\*.cs') | ForEach-Object { $_.FullName }
$out = Join-Path $bin 'WirelessBattery.exe'
$icon = Join-Path $root 'assets\app.ico'
$manifest = Join-Path $root 'src\app.manifest'

$cscArgs = @(
    '/nologo',
    '/target:winexe',
    '/platform:x64',
    '/optimize+',
    '/codepage:65001',
    "/out:$out",
    '/reference:System.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll',
    '/reference:System.Core.dll'
)
if (Test-Path $icon) { $cscArgs += "/win32icon:$icon" }
if (Test-Path $manifest) { $cscArgs += "/win32manifest:$manifest" }
$cscArgs += $sources

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed, exit code $LASTEXITCODE" }

Copy-Item $out (Join-Path $bin ([char]0x65E0 + [char]0x7EBF + [char]0x8BBE + [char]0x5907 + [char]0x7535 + [char]0x91CF + '.exe')) -Force
Write-Host "Build OK:"
Write-Host "  $out"
