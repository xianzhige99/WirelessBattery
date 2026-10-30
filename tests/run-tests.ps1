$ErrorActionPreference = 'Stop'

# 编译并运行纯逻辑自检（不碰硬件）。退出码 0 = 全部通过。
$root = Split-Path $PSScriptRoot -Parent
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }

$outDir = Join-Path $root 'bin\tests'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$out = Join-Path $outDir 'SelfTest.exe'
$sources = @()
$sources += Get-ChildItem (Join-Path $root 'src\*.cs') | ForEach-Object { $_.FullName }
$sources += (Join-Path $root 'tests\SelfTest.cs')

$cscArgs = @(
    '/nologo',
    '/target:exe',
    '/platform:x64',
    '/codepage:65001',
    '/main:WirelessBattery.SelfTestProgram',
    "/out:$out",
    '/reference:System.dll',
    '/reference:System.Drawing.dll',
    '/reference:System.Windows.Forms.dll',
    '/reference:System.Core.dll'
)
$cscArgs += $sources

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "SelfTest build failed, exit code $LASTEXITCODE" }

try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

& $out
exit $LASTEXITCODE
