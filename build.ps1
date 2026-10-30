param(
    [string]$Version = "",
    [switch]$Clean,
    [switch]$Strict
)

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    throw "csc.exe not found: $csc (requires the .NET Framework 4.x shipped with Windows)"
}

$bin = Join-Path $root 'bin'
if (-not (Test-Path $bin)) { New-Item -ItemType Directory -Path $bin | Out-Null }

$asm = Join-Path $root 'src\AssemblyInfo.cs'

# -Version 1.4.0 / 1.4.0.1 : rewrite AssemblyInfo.cs (single source of truth) before compiling.
if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw "Version must look like 1.4.0 or 1.4.0.0" }
    if ($Version.Split('.').Count -eq 3) { $Version = $Version + '.0' }
    $text = [System.IO.File]::ReadAllText($asm)
    $text = [regex]::Replace($text, 'AssemblyVersion\("[0-9.]+"\)', 'AssemblyVersion("' + $Version + '")')
    $text = [regex]::Replace($text, 'AssemblyFileVersion\("[0-9.]+"\)', 'AssemblyFileVersion("' + $Version + '")')
    [System.IO.File]::WriteAllText($asm, $text, (New-Object System.Text.UTF8Encoding($false)))
    Write-Host "Version set to $Version"
}

if ($Clean) {
    Get-ChildItem (Join-Path $bin '*.exe') -ErrorAction SilentlyContinue | Remove-Item -Force
}

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
if ($Strict) { $cscArgs += '/warnaserror+' }
$cscArgs += $sources

$sw = [Diagnostics.Stopwatch]::StartNew()
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed, exit code $LASTEXITCODE" }
$sw.Stop()

$zh = Join-Path $bin ([char]0x65E0 + [char]0x7EBF + [char]0x8BBE + [char]0x5907 + [char]0x7535 + [char]0x91CF + '.exe')
Copy-Item $out $zh -Force

# build-info.txt: what exactly this binary is (version / commit / hash).
$ver = 'unknown'
$asmText = [System.IO.File]::ReadAllText($asm)
$m = [regex]::Match($asmText, 'AssemblyFileVersion\("([0-9.]+)"\)')
if ($m.Success) { $ver = $m.Groups[1].Value }

$commit = 'no-git'
try {
    Push-Location $root
    $c = & git rev-parse --short HEAD 2>$null
    if ($LASTEXITCODE -eq 0 -and $c) { $commit = $c.Trim() }
    $dirty = & git status --porcelain 2>$null
    if ($dirty) { $commit = $commit + ' (dirty)' }
    Pop-Location
} catch { }

$hash = (Get-FileHash $out -Algorithm SHA256).Hash
$cscVer = (& $csc /? 2>&1 | Select-Object -First 1)

$info = @(
    "version=$ver",
    "built=$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss zzz')",
    "commit=$commit",
    "exe=WirelessBattery.exe",
    "sha256=$hash",
    "compiler=$cscVer",
    "ms=$($sw.ElapsedMilliseconds)"
)
[System.IO.File]::WriteAllLines((Join-Path $bin 'build-info.txt'), $info, (New-Object System.Text.UTF8Encoding($false)))

Write-Host "Build OK:"
Write-Host "  $out"
Write-Host "  version $ver  commit $commit  $($sw.ElapsedMilliseconds)ms"
