# 打一个「U 盘离线交接」包：源码 + 文档 + 测试 + 已编译 exe + 完整 .git 历史。
# 用途：把整个开发工作区搬到另一台电脑，且不依赖 GitHub。
# 用法：
#   powershell -ExecutionPolicy Bypass -File tools\pack-dev.ps1
#   powershell -ExecutionPolicy Bypass -File tools\pack-dev.ps1 -NoGit      # 只要源码，不带历史
param(
    [string]$OutDir = "dist",
    [switch]$NoGit
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

$version = "unknown"
$info = Join-Path $root "bin\build-info.txt"
if (Test-Path $info) {
    $line = Get-Content $info -Encoding UTF8 | Select-String -Pattern '^version\s*=' | Select-Object -First 1
    if ($line) { $version = ($line.Line -split '=')[1].Trim() }
}

$stamp = Get-Date -Format "yyyyMMdd"
$name = "WirelessBattery-$version-dev-$stamp"
if ($NoGit) { $name += "-nogit" }

$outAbs = Join-Path $root $OutDir
if (-not (Test-Path $outAbs)) { New-Item -ItemType Directory -Path $outAbs | Out-Null }
$zipPath = Join-Path $outAbs ($name + ".zip")
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

# 不打包的内容：本地缓存、构建副产物、本机运行态
$skipDirPrefix = @(".workbuddy/", "dist/", "__pycache__/", ".vs/", "bin/tests/")
$skipFileExact = @("bin/scan.txt", "bin/crash.txt", "bin/settings.ini",
                   "bin/启动设备电量.cmd", "bin/打开设备电量.lnk", "启动设备电量.cmd")

function Normalize([string]$p) { return ($p -replace '\\', '/') }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
$utf8 = New-Object System.Text.UTF8Encoding($true)
$count = 0
$bytes = 0

try {
    foreach ($f in Get-ChildItem -Path $root -Recurse -Force -File) {
        $rel = Normalize ($f.FullName.Substring($root.Length).TrimStart('\', '/'))
        if ($rel -eq "") { continue }
        if ($NoGit -and $rel.StartsWith(".git/")) { continue }

        $skip = $false
        foreach ($d in $skipDirPrefix) { if ($rel.StartsWith($d)) { $skip = $true; break } }
        if (-not $skip) {
            if ($skipFileExact -contains $rel) { $skip = $true }
            elseif ($rel -match '\.(zip|log|pdb)$') { $skip = $true }
        }
        if ($skip) { continue }

        $entryName = "$name/$rel"
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $zip, $f.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal)
        $count++
        $bytes += $f.Length
    }

    # 包内说明：解压后第一眼看到的东西
    $readme = @"
WirelessBattery v$version  离线交接包（$stamp 生成）

一、解压到哪
   解压到固定目录，例如  D:\Projects\WirelessBattery
   不要放在桌面临时目录，也不要在压缩包里直接双击运行。

二、只想用软件，不想开发
   直接运行  bin\WirelessBattery.exe
   首次启动会自动生成 bin\settings.ini，并创建开机自启的任务计划。
   如果 Windows 提示"已阻止"：右键 exe 属性 → 勾选"解除锁定" → 确定；
   或先运行根目录的 解除锁定并启动.bat。

三、要继续开发 / 改代码
   1. 这台电脑不需要装 .NET SDK，Win10/11 自带 .NET Framework 4.x。
   2. 打开 PowerShell，进到解压后的目录，跑三条：
          powershell -ExecutionPolicy Bypass -File build.ps1 -Strict
          powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1
          powershell -ExecutionPolicy Bypass -File tools\pack.ps1
      看到 Build OK / ALL PASS 就说明环境没问题。
   3. 想保留版本历史（提交、回档、看改了啥），需要装 Git for Windows：
          https://git-scm.com/download/win
      装完先配一次身份：
          git config --global user.name  "你的名字"
          git config --global user.email "你的邮箱"
      然后在本目录执行  git status  就能看到完整历史。
      （本包含 .git 目录，历史、分支、v1.3 / v1.4 标签都在。）

四、换机器后第一次运行会看到什么
   - settings.ini 是便携的：如果从旧机器拷了配置，程序会发现机器指纹不同，
     清掉"上次电量缓存"，但保留阶梯参数、自定义设备名、勾选状态、GPW 电压校准。
   - 卡片要等第一轮扫描（最多约 30 秒）才会显示真实状态。
   - 旧机器插过的设备不会在本机凭空出现一张"未连接"的卡。

五、改完之后怎么带回去
   - 装了 Git：git commit，然后用 U 盘把 .git 整个拷回旧机器（或推到 GitHub）。
   - 没装 Git：按文件修改时间只拷改过的文件过去；最好一次只在一台机器上改。
   - 想彻底干净：把本机 .git 整个目录替换回旧机器，历史即同步。

六、包里有什么
   src/            源码（C#，约 1 万行）
   tests/          纯逻辑自检（不碰硬件）
   tools/          打包、图标、诊断探针、界面截图工具
   docs/           决策记录、回归清单、发布步骤、交接说明
   assets/         图标
   bin/            已编译的 exe 与 build-info.txt（构建产物）
   build.ps1       编译脚本
   README.md       用法与原理
   CHANGELOG.md    版本变更
   使用说明.txt     面向使用者

   未包含：bin\settings.ini（本机运行态）、bin\tests\（诊断工具产物）、
           dist\（历史发布包）、.workbuddy\（工具缓存）。
"@
    $entry = $zip.CreateEntry("$name/先看我.txt", [System.IO.Compression.CompressionLevel]::Optimal)
    $stream = $entry.Open()
    $writer = New-Object System.IO.StreamWriter($stream, $utf8)
    $writer.Write($readme.Replace("`r`n", "`n").Replace("`n", "`r`n"))
    $writer.Flush()
    $writer.Dispose()
    $count++
}
finally {
    $zip.Dispose()
}

$len = (Get-Item $zipPath).Length
Write-Host ""
Write-Host ("Pack OK   {0}" -f $zipPath)
Write-Host ("  files  {0}   withGit={1}" -f $count, (-not $NoGit))
Write-Host ("  size   {0:N0} KB  (raw {1:N1} MB)" -f ($len / 1KB), ($bytes / 1MB))
