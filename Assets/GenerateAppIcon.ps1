Add-Type -AssemblyName System.Drawing

function New-EnvelopeBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap([int]$size, [int]$size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::FromArgb(0, 0, 0, 0))

    $pad = [int][Math]::Round($size * 0.05)
    $left = $pad
    $right = $size - $pad
    $top = [int][Math]::Round($size * 0.08)
    $bottom = $size - $pad
    $midX = [int]($size / 2)
    $flapY = [int][Math]::Round($size * 0.48)

    $teal = [System.Drawing.Color]::FromArgb(255, 45, 212, 191)
    $bodyDark = [System.Drawing.Color]::FromArgb(255, 18, 18, 24)
    $flapDark = [System.Drawing.Color]::FromArgb(255, 28, 28, 36)
    $highlight = [System.Drawing.Color]::FromArgb(255, 236, 236, 244)

    $bodyBrush = New-Object System.Drawing.SolidBrush $bodyDark
    $flapBrush = New-Object System.Drawing.SolidBrush $flapDark
    $outlinePen = New-Object System.Drawing.Pen $teal, ([float][Math]::Max(2, $size / 24))
    $outlinePen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $accentPen = New-Object System.Drawing.Pen $teal, ([float][Math]::Max(2, $size / 20))
    $hiPen = New-Object System.Drawing.Pen $highlight, ([float][Math]::Max(1, $size / 48))

    $bodyRect = New-Object System.Drawing.Rectangle ($left + 1), ($flapY + 1), ($right - $left - 2), ($bottom - $flapY - 2)
    $g.FillRectangle($bodyBrush, $bodyRect)

    $flap = @(
        (New-Object System.Drawing.Point ($left + 1), ($flapY + 1)),
        (New-Object System.Drawing.Point ($right - 1), ($flapY + 1)),
        (New-Object System.Drawing.Point $midX, ($top + 1))
    )
    $g.FillPolygon($flapBrush, $flap)

    $outline = @(
        (New-Object System.Drawing.Point $left, $flapY),
        (New-Object System.Drawing.Point $midX, $top),
        (New-Object System.Drawing.Point $right, $flapY),
        (New-Object System.Drawing.Point $right, $bottom),
        (New-Object System.Drawing.Point $left, $bottom),
        (New-Object System.Drawing.Point $left, $flapY)
    )
    $g.DrawPolygon($outlinePen, $outline)
    $g.DrawLine($accentPen, $left + 3, $flapY, $right - 3, $flapY)
    $g.DrawLine($hiPen, $left + 4, ($flapY + 2), $midX, ($top + 4))
    $g.DrawLine($hiPen, $right - 4, ($flapY + 2), $midX, ($top + 4))

    $bodyBrush.Dispose()
    $flapBrush.Dispose()
    $outlinePen.Dispose()
    $accentPen.Dispose()
    $hiPen.Dispose()
    $g.Dispose()
    return $bmp
}

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$pngPath = Join-Path $root "AppIcon.png"
$icoPath = Join-Path $root "AppIcon.ico"
$sizes = @(16, 24, 32, 48, 64, 128, 256)

$preview = New-EnvelopeBitmap 256
$preview.Save($pngPath, [System.Drawing.Imaging.ImageFormat]::Png)
$preview.Dispose()

$mem = New-Object System.IO.MemoryStream
$writer = New-Object System.IO.BinaryWriter $mem
$writer.Write([uint16]0)
$writer.Write([uint16]1)
$writer.Write([uint16]$sizes.Count)
$offset = 6 + (16 * $sizes.Count)
$pngDatas = New-Object System.Collections.Generic.List[byte[]]

foreach ($iconSize in $sizes) {
    $bmp = New-EnvelopeBitmap $iconSize
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngDatas.Add($ms.ToArray()) | Out-Null
    $bmp.Dispose()
    $ms.Dispose()
}

for ($i = 0; $i -lt $sizes.Count; $i++) {
    $iconSize = $sizes[$i]
    $w = if ($iconSize -ge 256) { [byte]0 } else { [byte]$iconSize }
    $data = $pngDatas[$i]
    $writer.Write($w)
    $writer.Write($w)
    $writer.Write([byte]0)
    $writer.Write([byte]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]32)
    $writer.Write([uint32]$data.Length)
    $writer.Write([uint32]$offset)
    $offset += $data.Length
}

foreach ($data in $pngDatas) { $writer.Write($data) }
[System.IO.File]::WriteAllBytes($icoPath, $mem.ToArray())

$writer.Dispose()
$mem.Dispose()

Write-Output "Icon v3-teal (vector): $icoPath ($((Get-Item $icoPath).Length) bytes)"
