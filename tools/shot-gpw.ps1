# 把 GPW 设置窗口渲染成 PNG（两种模式各一张），在临时目录里编译运行，不碰真实配置。
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "compiler not found: $csc" }

$outDir = Join-Path $env:TEMP 'gpushot'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$out = Join-Path $outDir 'GpwShot.exe'
$sources = @()
$sources += Get-ChildItem (Join-Path $root 'src\*.cs') | ForEach-Object { $_.FullName }
$sources += (Join-Path $root 'tools\GpwShot.cs')

& $csc /nologo /target:exe /platform:x64 /codepage:65001 /main:WirelessBattery.GpwShot `
    "/out:$out" `
    /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Core.dll `
    $sources
if ($LASTEXITCODE -ne 0) { throw "build failed: $LASTEXITCODE" }

Push-Location $outDir
try { & $out $outDir } finally { Pop-Location }
