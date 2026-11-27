# 渲染托盘图标样式稿（独立工具，不改主程序）。
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { throw "compiler not found: $csc" }

$outDir = Join-Path $env:TEMP 'trayshot'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }
$out = Join-Path $outDir 'TrayIconDesign.exe'

& $csc /nologo /target:exe /platform:x64 /codepage:65001 "/out:$out" `
    /reference:System.dll /reference:System.Drawing.dll `
    (Join-Path $root 'tools\TrayIconDesign.cs') 2>&1 | ForEach-Object { Write-Host $_ }
if ($LASTEXITCODE -ne 0) { throw "build failed: $LASTEXITCODE" }

$docs = Join-Path $root 'docs'
if (-not (Test-Path $docs)) { New-Item -ItemType Directory -Path $docs | Out-Null }
& $out $docs
