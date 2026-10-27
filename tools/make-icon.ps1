# Generates src/AudioCut/app.ico (gradient tile with a waveform and a cut marker)
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\src\AudioCut\app.ico'
$sizes = 256, 64, 48, 32, 24, 16
$pngs = @()

foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $r = $s * 0.22
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, 2 * $r, 2 * $r, 180, 90)
    $path.AddArc($s - 2 * $r - 1, 0, 2 * $r, 2 * $r, 270, 90)
    $path.AddArc($s - 2 * $r - 1, $s - 2 * $r - 1, 2 * $r, 2 * $r, 0, 90)
    $path.AddArc(0, $s - 2 * $r - 1, 2 * $r, 2 * $r, 90, 90)
    $path.CloseFigure()
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.Point 0, 0), (New-Object System.Drawing.Point $s, $s), ([System.Drawing.Color]::FromArgb(255, 0x4F, 0xD1, 0xC5)), ([System.Drawing.Color]::FromArgb(255, 0x7C, 0x8C, 0xF8))
    $g.FillPath($brush, $path)

    # waveform bars
    $heights = if ($s -ge 32) { 0.22, 0.42, 0.62, 0.34, 0.52, 0.28, 0.16 } else { 0.3, 0.6, 0.4, 0.55, 0.25 }
    $n = $heights.Count
    $area = $s * 0.62
    $x0 = ($s - $area) / 2
    $bw = $area / ($n * 1.7)
    $gap = ($area - $n * $bw) / ($n - 1)
    $dark = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x0B, 0x22, 0x2A))
    for ($i = 0; $i -lt $n; $i++) {
        $h = [Math]::Max(2, $heights[$i] * $s)
        $x = $x0 + $i * ($bw + $gap)
        $y = ($s - $h) / 2
        $bp = New-Object System.Drawing.Drawing2D.GraphicsPath
        $rr = [Math]::Min($bw / 2, $h / 2)
        if ($rr -ge 1) {
            $bp.AddArc($x, $y, $bw, 2 * $rr, 180, 180)
            $bp.AddArc($x, $y + $h - 2 * $rr, $bw, 2 * $rr, 0, 180)
            $bp.CloseFigure()
            $g.FillPath($dark, $bp)
        } else { $g.FillRectangle($dark, $x, $y, $bw, $h) }
    }
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngs += , $ms.ToArray()
    $bmp.Dispose()
}

$fs = [System.IO.File]::Create($out)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = $pngs[$i]
    $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s }))); $bw.Write([byte]($(if ($s -ge 256) { 0 } else { $s })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$d.Length); $bw.Write([UInt32]$offset)
    $offset += $d.Length
}
foreach ($d in $pngs) { $bw.Write($d) }
$bw.Close()
Write-Host "Icon written to $out"
