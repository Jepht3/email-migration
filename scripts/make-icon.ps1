<#
.SYNOPSIS
    Génère src/MailMigrator.App/Assets/app.ico (16 à 256 px) à partir du logo provisoire LEWEBMAX.
    À relancer, ou à remplacer par l'icône officielle, si le logo change.
#>
Add-Type -AssemblyName System.Drawing
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root "src\MailMigrator.App\Assets\app.ico"

function New-RoundedRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); return $p
}

function New-LogoPng([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'
    $s = $size / 32.0
    $g.ScaleTransform($s, $s)
    $rect = New-Object System.Drawing.RectangleF 0, 0, 32, 32
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(59, 130, 246)), ([System.Drawing.Color]::FromArgb(124, 58, 237)), 45
    $g.FillPath($brush, (New-RoundedRect 0 0 32 32 8))
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 1.8
    $pen.LineJoin = 'Round'; $pen.StartCap = 'Round'; $pen.EndCap = 'Round'
    $g.DrawPath($pen, (New-RoundedRect 6 10.5 16.5 12 1.5))
    $g.DrawLines($pen, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF 6.5, 11.5), (New-Object System.Drawing.PointF 14.25, 17), (New-Object System.Drawing.PointF 22, 11.5)))
    $g.FillEllipse([System.Drawing.Brushes]::White, 17.5, 5, 9, 9)
    $arrow = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(79, 70, 229)), 1.4
    $arrow.StartCap = 'Round'; $arrow.EndCap = 'Round'; $arrow.LineJoin = 'Round'
    $g.DrawLine($arrow, 20, 9.5, 24.2, 9.5)
    $g.DrawLines($arrow, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF 22.6, 7.8), (New-Object System.Drawing.PointF 24.3, 9.5), (New-Object System.Drawing.PointF 22.6, 11.2)))
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return ,$ms.ToArray()
}

# Fichier ICO : en-tête + répertoire + images PNG (format accepté par Windows Vista et suivants).
$sizes = 16, 24, 32, 48, 64, 128, 256
$images = foreach ($size in $sizes) { ,(New-LogoPng $size) }
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([UInt16]0); $w.Write([UInt16]1); $w.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([UInt16]1); $w.Write([UInt16]32)
    $w.Write([UInt32]$images[$i].Length); $w.Write([UInt32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write($img) }
[System.IO.File]::WriteAllBytes($target, $out.ToArray())
"Icône écrite : $target ($((Get-Item $target).Length) octets)"
