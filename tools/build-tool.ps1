param([string]$Tool = "UiShot")

$ErrorActionPreference = 'Stop'

# 把一个工具（UiShot / K02BSProbe）连同 src 一起编译到 bin\tests，便于本地诊断。

$root = Split-Path $PSScriptRoot -Parent
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }

$outDir = Join-Path $root 'bin\tests'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$source = Join-Path $root ("tools\" + $Tool + ".cs")
if (-not (Test-Path $source)) { throw "tool source not found: $source" }

$out = Join-Path $outDir ($Tool + ".exe")
$sources = @()
$sources += Get-ChildItem (Join-Path $root 'src\*.cs') | ForEach-Object { $_.FullName }
$sources += $source

$cscArgs = @(
    '/nologo',
    '/target:exe',
    '/platform:x64',
    '/codepage:65001',
    ("/main:WirelessBattery." + $Tool),
    "/out:$out",
    '/reference:System.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll',
    '/reference:System.Core.dll'
)
$cscArgs += $sources

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Tool build failed, exit code $LASTEXITCODE" }
Write-Host ("Tool OK: " + $out)
