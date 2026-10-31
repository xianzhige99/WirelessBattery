param([string]$Version = "")

$ErrorActionPreference = 'Stop'

# 打一个可上传到 GitHub Release 的便携包：
#   dist\WirelessBattery-<版本>-win64.zip
# 内容只放“用户需要的”：exe、启动脚本、说明、示例配置、构建信息（不放源码和测试）。

$root = Split-Path $PSScriptRoot -Parent
$build = Join-Path $root 'build.ps1'

if ($Version) {
    & $build -Clean -Strict -Version $Version
} else {
    & $build -Clean -Strict
}

$bin = Join-Path $root 'bin'
$asm = Join-Path $root 'src\AssemblyInfo.cs'
$ver = 'unknown'
$m = [regex]::Match([System.IO.File]::ReadAllText($asm), 'AssemblyFileVersion\("([0-9.]+)"\)')
if ($m.Success) { $ver = $m.Groups[1].Value }

$dist = Join-Path $root 'dist'
$stage = Join-Path $dist ('WirelessBattery-' + $ver)
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage | Out-Null

$files = @(
    (Join-Path $bin 'WirelessBattery.exe'),
    (Join-Path $bin ([char]0x65E0 + [char]0x7EBF + [char]0x8BBE + [char]0x5907 + [char]0x7535 + [char]0x91CF + '.exe')),
    (Join-Path $root ([char]0x89E3 + [char]0x9664 + [char]0x9501 + [char]0x5B9A + [char]0x5E76 + [char]0x542F + [char]0x52A8 + '.bat')),
    (Join-Path $root ([char]0x4EE5 + [char]0x7BA1 + [char]0x7406 + [char]0x5458 + [char]0x8EAB + [char]0x4EFD + [char]0x8FD0 + [char]0x884C + '.bat')),
    (Join-Path $root ([char]0x4F7F + [char]0x7528 + [char]0x8BF4 + [char]0x660E + '.txt')),
    (Join-Path $root 'settings.example.ini'),
    (Join-Path $bin 'build-info.txt')
)

foreach ($f in $files) {
    if (-not (Test-Path $f)) { throw ("missing file: " + $f) }
    Copy-Item $f $stage -Force
}

$zip = Join-Path $dist ('WirelessBattery-' + $ver + '-win64.zip')
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal

Write-Host ("Pack OK: " + $zip)
Write-Host ("  version " + $ver + "  size " + [math]::Round((Get-Item $zip).Length / 1KB) + " KB")
