# Generates src\DeviceMaster.Ui\DeviceMaster.ico (multi-resolution 16/32/48/64/128/256):
# a swept 4-blade fan in a cyan->blue gradient on a dark rounded tile, GDI+ offline.
Add-Type -AssemblyName System.Drawing
$icoPath = Join-Path $PSScriptRoot 'src\DeviceMaster.Ui\DeviceMaster.ico'
$previewPath = Join-Path $PSScriptRoot 'dist\icon-preview.png'

function New-RoundRect([single]$x,[single]$y,[single]$w,[single]$h,[single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $dia = 2 * $r
    $p.AddArc($x, $y, $dia, $dia, 180, 90)
    $p.AddArc($x + $w - $dia, $y, $dia, $dia, 270, 90)
    $p.AddArc($x + $w - $dia, $y + $h - $dia, $dia, $dia, 0, 90)
    $p.AddArc($x, $y + $h - $dia, $dia, $dia, 90, 90)
    $p.CloseFigure()
    return $p
}
function C([int]$a,[int]$r,[int]$g,[int]$b) { return [System.Drawing.Color]::FromArgb($a,$r,$g,$b) }

function Draw-Icon([int]$s) {
    $sc = $s / 256.0
    $bmp = New-Object System.Drawing.Bitmap($s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb))
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # dark tile
    $m = 12 * $sc; $rw = 232 * $sc; $rr = 54 * $sc
    $path = New-RoundRect $m $m $rw $rw $rr
    $rectF = New-Object System.Drawing.RectangleF($m, $m, $rw, $rw)
    $bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rectF, (C 255 20 26 40), (C 255 10 13 20), ([System.Drawing.Drawing2D.LinearGradientMode]::ForwardDiagonal))
    $g.FillPath($bg, $path)
    $bpen = New-Object System.Drawing.Pen((C 255 48 62 92), ([single][Math]::Max(1.0, 3 * $sc)))
    $g.DrawPath($bpen, $path)

    $cx = 128 * $sc; $cy = 128 * $sc

    # soft glow behind the fan
    $glowR = 96 * $sc
    $gb = New-Object System.Drawing.SolidBrush((C 46 34 200 238))
    $g.FillEllipse($gb, $cx - $glowR, $cy - $glowR, 2 * $glowR, 2 * $glowR)

    # four swept blades: an ellipse above the hub, rotated 0/90/180/270 with a tilt
    $bladeW = 52 * $sc; $bladeH = 86 * $sc; $hubGap = 18 * $sc
    $bladeRect = New-Object System.Drawing.RectangleF((-$bladeW / 2), (-($hubGap + $bladeH)), $bladeW, $bladeH)
    $gradRect = New-Object System.Drawing.RectangleF((-$bladeW), (-($hubGap + $bladeH)), (2 * $bladeW), ($bladeH + $hubGap))
    foreach ($angle in 0, 90, 180, 270) {
        $g.TranslateTransform($cx, $cy)
        $g.RotateTransform($angle + 30)   # +30 = swept look
        $blade = New-Object System.Drawing.Drawing2D.LinearGradientBrush($gradRect, (C 255 56 214 236), (C 255 86 130 255), ([System.Drawing.Drawing2D.LinearGradientMode]::Vertical))
        $g.FillEllipse($blade, $bladeRect)
        $blade.Dispose()
        $g.ResetTransform()
    }

    # hub: dark disc + light ring + accent dot
    $hubR = 30 * $sc
    $hb = New-Object System.Drawing.SolidBrush((C 255 14 18 28))
    $g.FillEllipse($hb, $cx - $hubR, $cy - $hubR, 2 * $hubR, 2 * $hubR)
    $hpen = New-Object System.Drawing.Pen((C 255 120 190 255), ([single][Math]::Max(1.0, 6 * $sc)))
    $g.DrawEllipse($hpen, $cx - $hubR, $cy - $hubR, 2 * $hubR, 2 * $hubR)
    if ($s -ge 32) {
        $dotR = 8 * $sc
        $db = New-Object System.Drawing.SolidBrush((C 255 56 214 236))
        $g.FillEllipse($db, $cx - $dotR, $cy - $dotR, 2 * $dotR, 2 * $dotR)
        $db.Dispose()
    }

    $g.Dispose(); $bg.Dispose(); $bpen.Dispose(); $gb.Dispose(); $hb.Dispose(); $hpen.Dispose(); $path.Dispose()
    return $bmp
}

$sizes = @(256,128,64,48,32,16)
$pngs = @()
foreach ($s in $sizes) {
    $bmp = Draw-Icon $s
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    if ($s -eq 256) { New-Item -ItemType Directory -Force (Split-Path $previewPath) | Out-Null; [System.IO.File]::WriteAllBytes($previewPath, $ms.ToArray()) }
    $pngs += ,($ms.ToArray())
    $ms.Dispose(); $bmp.Dispose()
}

$fs = New-Object System.IO.FileStream($icoPath, [System.IO.FileMode]::Create)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $len = $pngs[$i].Length
    $bw.Write([byte]($s % 256)); $bw.Write([byte]($s % 256))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$len); $bw.Write([UInt32]$offset)
    $offset += $len
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush(); $bw.Close(); $fs.Close()
Write-Output ("ICO written: " + $icoPath + " (" + (Get-Item $icoPath).Length + " bytes)")
