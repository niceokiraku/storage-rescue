# StorageRescue のアプリアイコン (app.ico) を生成する。
# HDD（プラッタとヘッドアーム）に、緑の「救出（上向き矢印）」バッジを重ねたデザイン。
# 使い方: powershell -ExecutionPolicy Bypass -File tools\MakeIcon.ps1
param([string]$OutDir = (Join-Path $PSScriptRoot '..'))

Add-Type -AssemblyName System.Drawing

function New-RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function C([string]$hex) { [System.Drawing.ColorTranslator]::FromHtml($hex) }

function Render([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0
    $g.ScaleTransform($s, $s)
    $small = $size -le 24

    # --- HDD 本体 ---
    $body = New-RoundRect 36 10 170 232 26
    $bodyBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 10)), (New-Object System.Drawing.PointF(0, 242)),
        (C '#3D4652'), (C '#1F252D'))
    $g.FillPath($bodyBrush, $body)
    $g.DrawPath((New-Object System.Drawing.Pen((C '#6E7781'), 6)), $body)

    # --- プラッタ ---
    $platBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(50, 30)), (New-Object System.Drawing.PointF(190, 170)),
        (C '#F6F8FA'), (C '#8C959F'))
    $g.FillEllipse($platBrush, 51, 30, 140, 140)
    if (-not $small) {
        $g.DrawEllipse((New-Object System.Drawing.Pen((C '#AFB8C1'), 2)), 71, 50, 100, 100)
        $g.DrawEllipse((New-Object System.Drawing.Pen((C '#AFB8C1'), 2)), 91, 70, 60, 60)
    }
    $g.FillEllipse((New-Object System.Drawing.SolidBrush((C '#57606A'))), 107, 86, 28, 28)

    # --- ヘッドアーム ---
    if (-not $small) {
        $arm = New-Object System.Drawing.Pen((C '#D0D7DE'), 12)
        $arm.StartCap = 'Round'; $arm.EndCap = 'Round'
        $g.DrawLine($arm, 76, 208, 106, 138)
        $g.FillEllipse((New-Object System.Drawing.SolidBrush((C '#D0D7DE'))), 58, 190, 36, 36)
        $g.FillEllipse((New-Object System.Drawing.SolidBrush((C '#57606A'))), 67, 199, 18, 18)
    }

    # --- 救出バッジ（緑の円＋上向き矢印）---
    $cx = 186; $cy = 186; $r = 62
    $g.FillEllipse((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)), $cx - $r - 8, $cy - $r - 8, ($r + 8) * 2, ($r + 8) * 2)
    $badge = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, ($cy - $r))), (New-Object System.Drawing.PointF(0, ($cy + $r))),
        (C '#3FB950'), (C '#1A7F37'))
    $g.FillEllipse($badge, $cx - $r, $cy - $r, $r * 2, $r * 2)
    $arrow = New-Object System.Drawing.Drawing2D.GraphicsPath
    $pts = @(
        (New-Object System.Drawing.PointF($cx, ($cy - 40))),
        (New-Object System.Drawing.PointF(($cx + 36), ($cy - 2))),
        (New-Object System.Drawing.PointF(($cx + 14), ($cy - 2))),
        (New-Object System.Drawing.PointF(($cx + 14), ($cy + 38))),
        (New-Object System.Drawing.PointF(($cx - 14), ($cy + 38))),
        (New-Object System.Drawing.PointF(($cx - 14), ($cy - 2))),
        (New-Object System.Drawing.PointF(($cx - 36), ($cy - 2)))
    )
    $arrow.AddPolygon([System.Drawing.PointF[]]$pts)
    $g.FillPath((New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)), $arrow)

    $g.Dispose()
    return $bmp
}

# 32bpp DIB（BITMAPINFOHEADER + BGRA ボトムアップ + AND マスク）。256px 未満は互換性のためこの形式で格納する
function To-Dib([System.Drawing.Bitmap]$bmp) {
    $n = $bmp.Width
    $ms = [System.IO.MemoryStream]::new()
    $bw = [System.IO.BinaryWriter]::new($ms)
    $maskStride = [int]([Math]::Ceiling($n / 32.0) * 4)
    $bw.Write([uint32]40); $bw.Write([int32]$n); $bw.Write([int32]($n * 2))
    $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]0)
    $bw.Write([uint32]($n * $n * 4 + $maskStride * $n))
    $bw.Write([int32]0); $bw.Write([int32]0); $bw.Write([uint32]0); $bw.Write([uint32]0)
    for ($y = $n - 1; $y -ge 0; $y--) {
        for ($x = 0; $x -lt $n; $x++) {
            $c = $bmp.GetPixel($x, $y)
            $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A)
        }
    }
    $bw.Write((New-Object byte[] ($maskStride * $n))) # アルファを使うので AND マスクは全 0
    $bw.Flush()
    return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($sz in $sizes) {
    $bmp = Render $sz
    if ($sz -eq 256) {
        $ms = [System.IO.MemoryStream]::new()
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Save((Join-Path $PSScriptRoot 'app-256.png'), [System.Drawing.Imaging.ImageFormat]::Png) # プレビュー用
        $data = $ms.ToArray()
    } else {
        $data = To-Dib $bmp
    }
    $bmp.Dispose()
    , $data
}

# ICO（256px は PNG、それ以外は DIB で格納）
$ico = [System.IO.MemoryStream]::new()
$w = [System.IO.BinaryWriter]::new($ico)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $len = $pngs[$i].Length
    $w.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $w.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush()
[System.IO.File]::WriteAllBytes((Join-Path $OutDir 'app.ico'), $ico.ToArray())
Write-Host "app.ico を生成しました ($($sizes -join ', ') px)"
