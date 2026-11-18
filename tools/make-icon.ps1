# Draws the Lumikey icon (gradient rounded square with a glowing key) and writes src\app.ico
# with PNG-compressed 256/48/32/16 px images.
param([string]$Out = (Join-Path $PSScriptRoot "..\src\app.ico"))
Add-Type -AssemblyName System.Drawing

function RoundRect([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object Drawing.Drawing2D.GraphicsPath
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90); $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90); $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure(); $p
}

function Draw([int]$s) {
    $bmp = New-Object Drawing.Bitmap $s, $s
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = "AntiAlias"; $g.Clear([Drawing.Color]::Transparent)
    $k = $s / 256.0
    # outer gradient tile
    $outer = RoundRect (8*$k) (8*$k) (240*$k) (240*$k) (56*$k)
    $lg = New-Object Drawing.Drawing2D.LinearGradientBrush((New-Object Drawing.PointF 0, 0), (New-Object Drawing.PointF $s, $s),
        [Drawing.Color]::FromArgb(57, 230, 180), [Drawing.Color]::FromArgb(180, 107, 255))
    $cb = New-Object Drawing.Drawing2D.ColorBlend 3
    $cb.Colors = @([Drawing.Color]::FromArgb(57, 230, 180), [Drawing.Color]::FromArgb(58, 160, 255), [Drawing.Color]::FromArgb(180, 107, 255))
    $cb.Positions = @(0.0, 0.5, 1.0); $lg.InterpolationColors = $cb
    $g.FillPath($lg, $outer)
    # dark inner tile
    $inner = RoundRect (40*$k) (40*$k) (176*$k) (176*$k) (34*$k)
    $g.FillPath((New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(11, 13, 16))), $inner)
    # glowing key cap
    $glow = RoundRect (78*$k) (86*$k) (100*$k) (84*$k) (18*$k)
    $pb = New-Object Drawing.Drawing2D.PathGradientBrush($glow)
    $pb.CenterColor = [Drawing.Color]::FromArgb(255, 160, 255, 225); $pb.SurroundColors = @([Drawing.Color]::FromArgb(255, 57, 230, 180))
    $g.FillPath($pb, $glow)
    $g.Dispose(); $bmp
}

$sizes = 256, 48, 32, 16
$pngs = foreach ($s in $sizes) { $b = Draw $s; $ms = New-Object IO.MemoryStream; $b.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $b.Dispose(); , $ms.ToArray() }

$fs = [IO.File]::Create((Resolve-Path -LiteralPath (Split-Path $Out -Parent)).Path + "\" + (Split-Path $Out -Leaf))
$w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Close()
"icon written: $Out"
