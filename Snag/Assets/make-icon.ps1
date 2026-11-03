# Generates snag.ico: a violet gradient tile with a download arrow, at the sizes Windows uses.
param([string]$OutFile = (Join-Path $PSScriptRoot 'snag.ico'))

Add-Type -AssemblyName System.Drawing

function New-IconPng([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)

    # Rounded tile
    $inset = [single]($s * 0.04)
    $w = [single]($s - 2 * $inset)
    $d = [single]($s * 0.44)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc($inset, $inset, $d, $d, 180, 90)
    $path.AddArc($inset + $w - $d, $inset, $d, $d, 270, 90)
    $path.AddArc($inset + $w - $d, $inset + $w - $d, $d, $d, 0, 90)
    $path.AddArc($inset, $inset + $w - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $from = New-Object System.Drawing.PointF 0, 0
    $to = New-Object System.Drawing.PointF $s, $s
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $from, $to, ([System.Drawing.Color]::FromArgb(255, 124, 92, 255)), ([System.Drawing.Color]::FromArgb(255, 192, 75, 242))
    $g.FillPath($brush, $path)

    # Download arrow + tray
    $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([single][Math]::Max(1.6, $s * 0.085))
    $pen.StartCap = 'Round'
    $pen.EndCap = 'Round'
    $pen.LineJoin = 'Round'
    $cx = [single]($s / 2)
    $g.DrawLine($pen, $cx, [single]($s * 0.24), $cx, [single]($s * 0.60))
    $chevron = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ([single]($s * 0.34)), ([single]($s * 0.45))),
        (New-Object System.Drawing.PointF $cx, ([single]($s * 0.61))),
        (New-Object System.Drawing.PointF ([single]($s * 0.66)), ([single]($s * 0.45)))
    )
    $g.DrawLines($pen, $chevron)
    $g.DrawLine($pen, [single]($s * 0.30), [single]($s * 0.76), [single]($s * 0.70), [single]($s * 0.76))
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$sizes = 256, 64, 48, 32, 24, 16
$images = foreach ($s in $sizes) { , (New-IconPng $s) }

$fs = [System.IO.File]::Create($OutFile)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $data = $images[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim)
    $bw.Write([byte]$dim)
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]$data.Length)
    $bw.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($data in $images) { $bw.Write($data) }
$bw.Dispose()
Write-Output "Wrote $OutFile"
