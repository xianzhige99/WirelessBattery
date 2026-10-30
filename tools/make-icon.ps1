#requires -Version 5.1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function New-RoundPath {
    param([float]$X, [float]$Y, [float]$W, [float]$H, [float]$R)
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [Math]::Min($R * 2.0, [Math]::Min($W, $H))
    if ($d -le 0) { $p.AddRectangle(([System.Drawing.RectangleF]::new($X, $Y, $W, $H))); return $p }
    $p.AddArc($X, $Y, $d, $d, 180, 90)
    $p.AddArc($X + $W - $d, $Y, $d, $d, 270, 90)
    $p.AddArc($X + $W - $d, $Y + $H - $d, $d, $d, 0, 90)
    $p.AddArc($X, $Y + $H - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function Draw-Icon {
    param([int]$S)
    $bmp = [System.Drawing.Bitmap]::new($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $bg = New-RoundPath 0 0 $S $S ($S * 0.225)
    $c1 = [System.Drawing.Color]::FromArgb(255, 79, 70, 229)
    $c2 = [System.Drawing.Color]::FromArgb(255, 6, 182, 212)
    $rect = [System.Drawing.RectangleF]::new(0, 0, $S, $S)
    $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new($rect, $c1, $c2, 45.0)
    $g.FillPath($brush, $bg)
    $brush.Dispose()
    $bg.Dispose()

    $hl = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(38, 255, 255, 255))
    $g.FillEllipse($hl, -0.22 * $S, -0.30 * $S, 0.95 * $S, 0.85 * $S)
    $hl.Dispose()

    $bx = 0.145 * $S
    $by = 0.315 * $S
    $bw = 0.605 * $S
    $bh = 0.365 * $S
    $br = 0.105 * $S
    $body = New-RoundPath $bx $by $bw $bh $br
    $stroke = [Math]::Max(1.15, 0.052 * $S)
    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, $stroke)
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPath($pen, $body)
    $pen.Dispose()
    $body.Dispose()

    $tx = 0.742 * $S
    $ty = 0.405 * $S
    $tw = 0.070 * $S
    $th = 0.185 * $S
    $term = New-RoundPath $tx $ty $tw $th (0.028 * $S)
    $wb = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
    $g.FillPath($wb, $term)
    $term.Dispose()

    $ix = $bx + $stroke * 0.85
    $iy = $by + $stroke * 0.85
    $ih = $bh - $stroke * 1.7
    $iw = ($bw - $stroke * 1.7) * 0.72
    $inner = New-RoundPath $ix $iy $iw $ih (0.045 * $S)
    $m1 = [System.Drawing.Color]::FromArgb(255, 167, 243, 208)
    $m2 = [System.Drawing.Color]::FromArgb(255, 110, 231, 183)
    $ir = [System.Drawing.RectangleF]::new($ix, $iy, $iw, $ih)
    $ib = [System.Drawing.Drawing2D.LinearGradientBrush]::new($ir, $m1, $m2, 90.0)
    $g.FillPath($ib, $inner)
    $ib.Dispose()
    $inner.Dispose()

    if ($S -ge 24) {
        $pts = New-Object 'System.Drawing.PointF[]' 6
        $pts[0] = [System.Drawing.PointF]::new((0.505 * $S), (0.345 * $S))
        $pts[1] = [System.Drawing.PointF]::new((0.415 * $S), (0.535 * $S))
        $pts[2] = [System.Drawing.PointF]::new((0.487 * $S), (0.535 * $S))
        $pts[3] = [System.Drawing.PointF]::new((0.445 * $S), (0.690 * $S))
        $pts[4] = [System.Drawing.PointF]::new((0.585 * $S), (0.470 * $S))
        $pts[5] = [System.Drawing.PointF]::new((0.510 * $S), (0.470 * $S))
        $g.FillPolygon($wb, $pts)
    }

    $wb.Dispose()
    $g.Dispose()
    return $bmp
}

function Get-DibBytes {
    param([System.Drawing.Bitmap]$Bmp, [int]$S)
    $rect = [System.Drawing.Rectangle]::new(0, 0, $S, $S)
    $data = $Bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $data.Stride
    $raw = New-Object byte[] ($stride * $S)
    [System.Runtime.InteropServices.Marshal]::Copy($data.Scan0, $raw, 0, $raw.Length)
    $Bmp.UnlockBits($data)

    $ms = New-Object System.IO.MemoryStream
    $bw = [System.IO.BinaryWriter]::new($ms)
    $bw.Write([UInt32]40)
    $bw.Write([Int32]$S)
    $bw.Write([Int32]($S * 2))
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]0)
    $bw.Write([UInt32]0)
    $bw.Write([Int32]0)
    $bw.Write([Int32]0)
    $bw.Write([UInt32]0)
    $bw.Write([UInt32]0)
    for ($y = $S - 1; $y -ge 0; $y--) {
        $bw.Write($raw, $y * $stride, $S * 4)
    }
    $maskStride = [int]([Math]::Ceiling($S / 32.0) * 4)
    $zero = New-Object byte[] $maskStride
    for ($y = 0; $y -lt $S; $y++) { $bw.Write($zero) }
    $bw.Flush()
    $bytes = $ms.ToArray()
    $bw.Dispose()
    $ms.Dispose()
    return $bytes
}

$out = Join-Path $PSScriptRoot '..\assets\app.ico'
$preview = Join-Path $PSScriptRoot '..\docs\icon-preview.png'
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @()
foreach ($s in $sizes) {
    $bmp = Draw-Icon $s
    if ($s -eq 256) {
        $bmp.Save($preview, [System.Drawing.Imaging.ImageFormat]::Png)
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $frames += ,@($s, $ms.ToArray())
        $ms.Dispose()
    }
    else {
        $frames += ,@($s, (Get-DibBytes $bmp $s))
    }
    $bmp.Dispose()
}

$ms = New-Object System.IO.MemoryStream
$bw = [System.IO.BinaryWriter]::new($ms)
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $s = [int]$f[0]
    $bytes = [byte[]]$f[1]
    $w = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$w)
    $bw.Write([byte]$w)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$bytes.Length)
    $bw.Write([UInt32]$offset)
    $offset += $bytes.Length
}
foreach ($f in $frames) { $bw.Write([byte[]]$f[1]) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($out, $ms.ToArray())
$bw.Dispose()
$ms.Dispose()

Write-Host ("Icon written: {0} ({1} bytes)" -f $out, (Get-Item $out).Length)


