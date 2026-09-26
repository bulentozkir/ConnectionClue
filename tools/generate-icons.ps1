# Creates the ConnectionClue brand mark, Windows icons, MSIX tiles, and nine requested logo sizes.
# Usage: pwsh tools/generate-icons.ps1
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$appAssets = Join-Path $root 'src\ConnectionClue.App\Assets'
$msixAssets = Join-Path $root 'packaging\msix\Assets'
$logos = Join-Path $root 'logos'
New-Item -ItemType Directory -Force $appAssets, $msixAssets, $logos | Out-Null

function New-Frame([int]$size, [string]$badge = '') {
    $bmp = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $inset = [single]($size * 0.025)
    $edge = [single]($size - 2 * $inset)
    $corner = [single]($edge * 0.22)
    $diameter = [single](2 * $corner)
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc($inset, $inset, $diameter, $diameter, 180, 90)
    $path.AddArc($inset + $edge - $diameter, $inset, $diameter, $diameter, 270, 90)
    $path.AddArc($inset + $edge - $diameter, $inset + $edge - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($inset, $inset + $edge - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()

    $fill = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.PointF]::new([single]0, [single]0),
        [System.Drawing.PointF]::new([single]$size, [single]$size),
        [System.Drawing.Color]::FromArgb(17, 55, 119),
        [System.Drawing.Color]::FromArgb(8, 132, 137))
    $g.FillPath($fill, $path)
    $edgePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(90, 226, 250, 250), [single][Math]::Max(0.7, $size * 0.018))
    $g.DrawPath($edgePen, $path)

    $points = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new([single]($size * 0.18), [single]($size * 0.71)),
        [System.Drawing.PointF]::new([single]($size * 0.39), [single]($size * 0.49)),
        [System.Drawing.PointF]::new([single]($size * 0.59), [single]($size * 0.60)),
        [System.Drawing.PointF]::new([single]($size * 0.79), [single]($size * 0.33)))
    $route = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(248, 255, 255, 255), [single][Math]::Max(1.3, $size * 0.067))
    $route.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $route.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $route.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLines($route, $points)

    $node = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(250, 255, 255, 255))
    $nodeCore = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(21, 69, 127))
    $nodeSize = [single][Math]::Max(2.5, $size * 0.145)
    $coreSize = [single][Math]::Max(1.1, $nodeSize * 0.34)
    foreach ($i in @(0, 1, 3)) {
        $x = $points[$i].X
        $y = $points[$i].Y
        $g.FillEllipse($node, [single]($x - $nodeSize / 2), [single]($y - $nodeSize / 2), $nodeSize, $nodeSize)
        $g.FillEllipse($nodeCore, [single]($x - $coreSize / 2), [single]($y - $coreSize / 2), $coreSize, $coreSize)
    }

    # The lens marks the point ConnectionClue helps the user investigate.
    $cx = $points[2].X
    $cy = $points[2].Y
    $lensSize = [single]($size * 0.33)
    $lensRadius = [single]($lensSize / 2)
    $lensFill = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(238, 10, 48, 83))
    $g.FillEllipse($lensFill, [single]($cx - $lensRadius), [single]($cy - $lensRadius), $lensSize, $lensSize)
    $gold = [System.Drawing.Color]::FromArgb(255, 201, 92)
    $lensPen = [System.Drawing.Pen]::new($gold, [single][Math]::Max(1.2, $size * 0.052))
    $g.DrawEllipse($lensPen, [single]($cx - $lensRadius), [single]($cy - $lensRadius), $lensSize, $lensSize)
    $handle = [System.Drawing.Pen]::new($gold, [single][Math]::Max(1.4, $size * 0.066))
    $handle.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $handle.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($handle, [single]($cx + $lensRadius * 0.66), [single]($cy + $lensRadius * 0.66),
        [single]($size * 0.84), [single]($size * 0.84))
    $clueDot = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
    $clueDotSize = [single][Math]::Max(1.4, $size * 0.075)
    $g.FillEllipse($clueDot, [single]($cx - $clueDotSize / 2), [single]($cy - $clueDotSize / 2), $clueDotSize, $clueDotSize)

    if ($badge) {
        $d = [single]($size * 0.39)
        $x = [single]($size - $d - $size * 0.035)
        $bg = if ($badge -eq 'warning') {
            [System.Drawing.Color]::FromArgb(255, 201, 92)
        } else {
            [System.Drawing.Color]::FromArgb(196, 43, 28)
        }
        $ink = if ($badge -eq 'warning') { [System.Drawing.Color]::FromArgb(28, 36, 48) } else { [System.Drawing.Color]::White }
        $g.FillEllipse([System.Drawing.SolidBrush]::new($bg), $x, $x, $d, $d)
        $g.DrawEllipse([System.Drawing.Pen]::new([System.Drawing.Color]::White, [single][Math]::Max(0.8, $size * 0.035)), $x, $x, $d, $d)
        $bp = [System.Drawing.Pen]::new($ink, [single][Math]::Max(1.1, $size * 0.052))
        $bp.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $bp.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $center = [single]($x + $d / 2)
        if ($badge -eq 'warning') {
            $g.DrawLine($bp, $center, [single]($x + $d * 0.23), $center, [single]($x + $d * 0.56))
            $dot = [single][Math]::Max(0.8, $size * 0.045)
            $g.FillEllipse([System.Drawing.SolidBrush]::new($ink), [single]($center - $dot / 2),
                [single]($x + $d * 0.70), $dot, $dot)
        } else {
            $k = [single]($d * 0.19)
            $g.DrawLine($bp, [single]($center - $k), [single]($center - $k), [single]($center + $k), [single]($center + $k))
            $g.DrawLine($bp, [single]($center - $k), [single]($center + $k), [single]($center + $k), [single]($center - $k))
        }
    }

    $stream = [IO.MemoryStream]::new()
    $bmp.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $stream.ToArray()
    foreach ($resource in @($g, $bmp, $path, $fill, $edgePen, $route, $node, $nodeCore, $lensFill, $lensPen, $handle, $clueDot)) {
        if ($resource) { $resource.Dispose() }
    }
    $stream.Dispose()
    return ,$bytes
}

function Save-Ico([string]$name, [int[]]$sizes, [string]$badge = '') {
    $frames = [System.Collections.Generic.List[byte[]]]::new()
    foreach ($size in $sizes) { $frames.Add([byte[]](New-Frame $size $badge)) }

    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new($stream)
    $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($i = 0; $i -lt $sizes.Count; $i++) {
        $dimension = [byte]($sizes[$i] % 256)
        $writer.Write($dimension); $writer.Write($dimension); $writer.Write([byte]0); $writer.Write([byte]0)
        $writer.Write([uint16]1); $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
        $offset += $frames[$i].Length
    }
    foreach ($frame in $frames) { $writer.Write([byte[]]$frame) }
    $bytes = $stream.ToArray()
    $writer.Dispose(); $stream.Dispose()
    [IO.File]::WriteAllBytes((Join-Path $appAssets $name), $bytes)
}

function Draw-Mark([System.Drawing.Graphics]$graphics, [single]$x, [single]$y, [int]$size) {
    $stream = [IO.MemoryStream]::new([byte[]](New-Frame $size ''))
    $image = [System.Drawing.Image]::FromStream($stream)
    $graphics.DrawImage($image, $x, $y, [single]$size, [single]$size)
    $image.Dispose(); $stream.Dispose()
}

function New-ArtCanvas([int]$width, [int]$height) {
    $bitmap = [System.Drawing.Bitmap]::new($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $background = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.PointF]::new([single]0, [single]0),
        [System.Drawing.PointF]::new([single]$width, [single]$height),
        [System.Drawing.Color]::FromArgb(6, 22, 45),
        [System.Drawing.Color]::FromArgb(8, 72, 83))
    $graphics.FillRectangle($background, 0, 0, $width, $height)
    $orbit = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(27, 94, 218, 198), [single][Math]::Max(2, $width * 0.002))
    $orbitSize = [single]([Math]::Max($width, $height) * 0.78)
    $graphics.DrawEllipse($orbit, [single]($width * 0.52), [single](-$height * 0.31), $orbitSize, $orbitSize)
    $background.Dispose(); $orbit.Dispose()
    return @{ Bitmap = $bitmap; Graphics = $graphics }
}

function Draw-Wordmark([System.Drawing.Graphics]$graphics, [single]$centerX, [single]$y, [single]$fontSize) {
    $font = [System.Drawing.Font]::new('Segoe UI', $fontSize, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $white = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(248, 250, 255))
    $mint = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(73, 222, 190))
    $firstWidth = $graphics.MeasureString('Connection', $font).Width
    $secondWidth = $graphics.MeasureString('Clue', $font).Width
    $gap = [single]($fontSize * 0.015)
    $startX = [single]($centerX - ($firstWidth + $secondWidth - $gap) / 2)
    $graphics.DrawString('Connection', $font, $white, $startX, $y)
    $graphics.DrawString('Clue', $font, $mint, [single]($startX + $firstWidth - $gap), $y)
    $font.Dispose(); $white.Dispose(); $mint.Dispose()
}

function Draw-CenteredText([System.Drawing.Graphics]$graphics, [string]$text, [single]$centerX,
    [single]$y, [single]$fontSize, [System.Drawing.Color]$color, [single]$maxWidth) {
    $font = [System.Drawing.Font]::new('Segoe UI', $fontSize, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $brush = [System.Drawing.SolidBrush]::new($color)
    $format = [System.Drawing.StringFormat]::new()
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    $format.LineAlignment = [System.Drawing.StringAlignment]::Near
    $height = [single]($fontSize * 1.8)
    $rect = [System.Drawing.RectangleF]::new([single]($centerX - $maxWidth / 2), $y, $maxWidth, $height)
    $graphics.DrawString($text, $font, $brush, $rect, $format)
    $format.Dispose(); $brush.Dispose(); $font.Dispose()
}

function Save-BrandArtwork([string]$fileName, [string]$kind, [int]$width, [int]$height) {
    $canvas = New-ArtCanvas $width $height
    $bitmap = $canvas.Bitmap
    $graphics = $canvas.Graphics
    $center = [single]($width / 2)
    switch ($kind) {
        'BoxArt' {
            $mark = [int]($width * 0.43)
            Draw-Mark $graphics ([single](($width - $mark) / 2)) ([single]($height * 0.075)) $mark
            Draw-Wordmark $graphics $center ([single]($height * 0.625)) ([single]($width * 0.064))
            Draw-CenteredText $graphics 'Find where the connection breaks.' $center ([single]($height * 0.742)) ([single]($width * 0.031)) ([System.Drawing.Color]::FromArgb(222, 235, 246)) ([single]($width * 0.86))
            Draw-CenteredText $graphics 'WINDOWS 11 NETWORK DIAGNOSTICS' $center ([single]($height * 0.865)) ([single]($width * 0.021)) ([System.Drawing.Color]::FromArgb(117, 191, 199)) ([single]($width * 0.86))
        }
        'Landscape' {
            $mark = [int]($height * 0.50)
            Draw-Mark $graphics ([single]($width * 0.105)) ([single](($height - $mark) / 2)) $mark
            $textCenter = [single]($width * 0.675)
            Draw-Wordmark $graphics $textCenter ([single]($height * 0.35)) ([single]($height * 0.104))
            Draw-CenteredText $graphics 'Find where the connection breaks.' $textCenter ([single]($height * 0.505)) ([single]($height * 0.043)) ([System.Drawing.Color]::FromArgb(222, 235, 246)) ([single]($width * 0.48))
            $accent = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(210, 255, 201, 92), [single]($height * 0.004))
            $graphics.DrawLine($accent, [single]($width * 0.49), [single]($height * 0.67), [single]($width * 0.86), [single]($height * 0.67))
            $accent.Dispose()
            Draw-CenteredText $graphics 'CONNECTION PATH DIAGNOSTICS' $textCenter ([single]($height * 0.705)) ([single]($height * 0.027)) ([System.Drawing.Color]::FromArgb(117, 191, 199)) ([single]($width * 0.48))
        }
        'Poster' {
            $mark = [int]($width * 0.58)
            Draw-Mark $graphics ([single](($width - $mark) / 2)) ([single]($height * 0.105)) $mark
            Draw-Wordmark $graphics $center ([single]($height * 0.59)) ([single]($width * 0.087))
            Draw-CenteredText $graphics 'Find where the connection breaks.' $center ([single]($height * 0.70)) ([single]($width * 0.044)) ([System.Drawing.Color]::FromArgb(222, 235, 246)) ([single]($width * 0.88))
            $accent = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(210, 255, 201, 92), [single]($width * 0.006))
            $graphics.DrawLine($accent, [single]($width * 0.22), [single]($height * 0.80), [single]($width * 0.78), [single]($height * 0.80))
            $accent.Dispose()
            Draw-CenteredText $graphics 'CHECK  -  LOCATE  -  IMPROVE' $center ([single]($height * 0.835)) ([single]($width * 0.034)) ([System.Drawing.Color]::FromArgb(73, 222, 190)) ([single]($width * 0.9))
            Draw-CenteredText $graphics 'WINDOWS 11 CONNECTION DIAGNOSTICS' $center ([single]($height * 0.90)) ([single]($width * 0.025)) ([System.Drawing.Color]::FromArgb(160, 188, 207)) ([single]($width * 0.9))
        }
    }
    $bitmap.Save((Join-Path $logos $fileName), [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()
}

function Save-AppxTile([string]$name, [int]$width, [int]$height, [int]$markSize) {
    $bitmap = [System.Drawing.Bitmap]::new($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $graphics.Clear([System.Drawing.Color]::Transparent)
    Draw-Mark $graphics ([single](($width - $markSize) / 2)) ([single](($height - $markSize) / 2)) $markSize
    $bitmap.Save((Join-Path $msixAssets $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose(); $bitmap.Dispose()
}

Save-Ico 'ConnectionClue.ico' @(16, 20, 24, 32, 48, 64, 128, 256)
Save-Ico 'TrayWarning.ico' @(16, 20, 24, 32, 48) 'warning'
Save-Ico 'TrayProblem.ico' @(16, 20, 24, 32, 48) 'problem'
[IO.File]::WriteAllBytes((Join-Path $appAssets 'ConnectionClue.png'), [byte[]](New-Frame 128 ''))

foreach ($size in @(44, 71, 150, 300, 512, 1024)) {
    $name = "ConnectionClue-Logo-${size}x${size}.png"
    [IO.File]::WriteAllBytes((Join-Path $logos $name), [byte[]](New-Frame $size ''))
}
Save-BrandArtwork 'ConnectionClue-BoxArt-1080x1080.png' 'BoxArt' 1080 1080
Save-BrandArtwork 'ConnectionClue-Logo-1920x1080.png' 'Landscape' 1920 1080
Save-BrandArtwork 'ConnectionClue-Poster-720x1080.png' 'Poster' 720 1080

Save-AppxTile 'StoreLogo.png' 50 50 50
Save-AppxTile 'Square44x44Logo.png' 44 44 40
Save-AppxTile 'Square150x150Logo.png' 150 150 96
Save-AppxTile 'Wide310x150Logo.png' 310 150 96

Get-ChildItem $logos -File | Sort-Object Name | Select-Object Name, Length | Format-Table -AutoSize
