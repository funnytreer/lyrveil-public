param(
    [string]$FontFamily = 'Microsoft YaHei UI',
    [switch]$TrayOnly
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$projectRoot = Split-Path -Parent $PSScriptRoot
$assetDirectory = Join-Path $projectRoot 'src\LyricsXQuiet.App\Assets'
$designDirectory = Join-Path $projectRoot 'design\icon'
$null = New-Item -ItemType Directory -Force -Path $assetDirectory

function New-RoundedRectanglePath {
    param([float]$Size, [float]$Radius)

    $diameter = $Radius * 2
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $path.AddArc(0, 0, $diameter, $diameter, 180, 90)
    $path.AddArc($Size - $diameter, 0, $diameter, $diameter, 270, 90)
    $path.AddArc($Size - $diameter, $Size - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc(0, $Size - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return $path
}

function Draw-LyricBar {
    param(
        [System.Drawing.Graphics]$Graphics,
        [System.Drawing.Brush]$Brush,
        [System.Drawing.RectangleF]$Bounds
    )

    $radius = $Bounds.Height / 2
    $y = $Bounds.Y + $radius
    $pen = [System.Drawing.Pen]::new($Brush, $Bounds.Height)
    try {
        $pen.StartCap = $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $Graphics.DrawLine($pen, [single]($Bounds.Left + $radius), [single]$y,
            [single]($Bounds.Right - $radius), [single]$y)
    }
    finally {
        $pen.Dispose()
    }
}

function New-TrayBitmap {
    param([int]$Size, [ValidateSet('dark', 'light')][string]$Ink)

    if ($Ink -eq 'dark') {
        $noteColor = '#0F8B7E'
        $gradientStart = '#0D9488'
        $gradientEnd = '#2DD4BF'
        $secondaryColor = '#334155'
    } else {
        $noteColor = '#5EEAD4'
        $gradientStart = '#5EEAD4'
        $gradientEnd = '#99F6E4'
        $secondaryColor = '#CBD5E1'
    }

    $mainBar = [System.Drawing.RectangleF]::new(11.4, 12.2, 9.8, 3.1)
    $secondaryBar = [System.Drawing.RectangleF]::new(11.4, 17.2, 7.2, 2.9)
    $note = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($noteColor))
    $stem = [System.Drawing.Pen]::new($note, [single]2.15)
    $gradient = [System.Drawing.Drawing2D.LinearGradientBrush]::new($mainBar,
        [System.Drawing.ColorTranslator]::FromHtml($gradientStart),
        [System.Drawing.ColorTranslator]::FromHtml($gradientEnd),
        [System.Drawing.Drawing2D.LinearGradientMode]::Horizontal)
    $secondary = [System.Drawing.SolidBrush]::new(
        [System.Drawing.Color]::FromArgb(230, [System.Drawing.ColorTranslator]::FromHtml($secondaryColor)))

    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $scale = [single]($Size / 24.0)
        $graphics.ScaleTransform($scale, $scale)

        $radius = 2.85
        $head = [System.Drawing.RectangleF]::new((5.7 - $radius), (18.3 - $radius), (2 * $radius), (2 * $radius))
        $graphics.FillEllipse($note, $head)
        $stem.StartCap = $stem.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $stem.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

        # Keep the flag's outer edge at y=2.8, including the round stroke.
        # This makes the painted extent 18.35 units along both axes.
        $halfStroke = $stem.Width / 2
        $points = [System.Drawing.PointF[]]@(
            [System.Drawing.PointF]::new(7.6, 17.9)
            [System.Drawing.PointF]::new(7.6, (4.8 + $halfStroke))
            [System.Drawing.PointF]::new(14.7, (2.8 + $halfStroke))
            [System.Drawing.PointF]::new(14.7, (6.4 + $halfStroke))
            [System.Drawing.PointF]::new(7.6, (7.8 + $halfStroke))
        )
        $graphics.DrawLines($stem, $points)
        Draw-LyricBar -Graphics $graphics -Brush $gradient -Bounds $mainBar
        Draw-LyricBar -Graphics $graphics -Brush $secondary -Bounds $secondaryBar
        return $bitmap
    }
    catch {
        $bitmap.Dispose()
        throw
    }
    finally {
        $graphics.Dispose()
        $secondary.Dispose()
        $gradient.Dispose()
        $stem.Dispose()
        $note.Dispose()
    }
}

function New-LyrveilBitmap {
    param([int]$Size)

    $bitmap = [System.Drawing.Bitmap]::new(
        $Size,
        $Size,
        [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $backgroundBrush = [System.Drawing.SolidBrush]::new(
        [System.Drawing.ColorTranslator]::FromHtml('#142E2C'))
    $textBrush = [System.Drawing.SolidBrush]::new(
        [System.Drawing.ColorTranslator]::FromHtml('#F4F7F6'))
    $progressBrush = [System.Drawing.SolidBrush]::new(
        [System.Drawing.ColorTranslator]::FromHtml('#33FFDD'))
    $font = [System.Drawing.Font]::new(
        $FontFamily,
        [single]($Size * 0.70),
        [System.Drawing.FontStyle]::Bold,
        [System.Drawing.GraphicsUnit]::Pixel)
    $format = [System.Drawing.StringFormat]::new()
    $path = New-RoundedRectanglePath -Size $Size -Radius ($Size * 0.215)

    try {
        $graphics.Clear([System.Drawing.Color]::Transparent)
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $graphics.FillPath($backgroundBrush, $path)

        $format.Alignment = [System.Drawing.StringAlignment]::Center
        $format.LineAlignment = [System.Drawing.StringAlignment]::Center
        $horizontalShift = if ($Size -le 32) { 1.0 } else { 0.0 }
        $verticalShift = [Math]::Max(1.0, $Size * 0.035)
        $textArea = [System.Drawing.RectangleF]::new(
            [single]$horizontalShift,
            [single]((-$Size * 0.025) + $verticalShift),
            $Size,
            [single]($Size * 1.035))
        $graphics.DrawString('词', $font, $textBrush, $textArea, $format)

        $state = $graphics.Save()
        try {
            $graphics.SetClip([System.Drawing.RectangleF]::new(0, 0, [single]($Size * 0.485), $Size))
            $graphics.DrawString('词', $font, $progressBrush, $textArea, $format)
        }
        finally {
            $graphics.Restore($state)
        }
    }
    finally {
        $path.Dispose()
        $format.Dispose()
        $font.Dispose()
        $progressBrush.Dispose()
        $textBrush.Dispose()
        $backgroundBrush.Dispose()
        $graphics.Dispose()
    }

    return $bitmap
}

function ConvertTo-PngBytes {
    param([System.Drawing.Bitmap]$Bitmap)

    $stream = [System.IO.MemoryStream]::new()
    try {
        $Bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
        return $stream.ToArray()
    }
    finally {
        $stream.Dispose()
    }
}

function Write-MultiSizeIcon {
    param(
        [hashtable]$Frames,
        [string]$Path
    )

    $orderedSizes = @($Frames.Keys | Sort-Object { [int]$_ })
    $headerSize = 6 + (16 * $orderedSizes.Count)
    $offset = $headerSize
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Create)
    $writer = [System.IO.BinaryWriter]::new($stream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$orderedSizes.Count)

        foreach ($sizeKey in $orderedSizes) {
            $size = [int]$sizeKey
            $bytes = [byte[]]$Frames[$sizeKey]
            $writer.Write([byte]$(if ($size -ge 256) { 0 } else { $size }))
            $writer.Write([byte]$(if ($size -ge 256) { 0 } else { $size }))
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$bytes.Length)
            $writer.Write([uint32]$offset)
            $offset += $bytes.Length
        }

        foreach ($sizeKey in $orderedSizes) {
            $writer.Write([byte[]]$Frames[$sizeKey])
        }
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

foreach ($tone in @('dark', 'light')) {
    $trayFrames = @{}
    foreach ($size in @(16, 20, 24, 32, 40, 48, 64)) {
        $bitmap = New-TrayBitmap -Size $size -Ink $tone
        try { $trayFrames[$size] = ConvertTo-PngBytes -Bitmap $bitmap }
        finally { $bitmap.Dispose() }
    }
    Write-MultiSizeIcon -Frames $trayFrames -Path (Join-Path $assetDirectory "tray-$tone-ink.ico")
}
if ($TrayOnly) { return }

$null = New-Item -ItemType Directory -Force -Path $designDirectory
$sizes = @(16, 20, 24, 32, 40, 48, 64, 128, 256)
$frames = @{}
foreach ($size in $sizes) {
    $bitmap = New-LyrveilBitmap -Size $size
    try {
        $bytes = ConvertTo-PngBytes -Bitmap $bitmap
        $frames[$size] = $bytes
        [System.IO.File]::WriteAllBytes(
            (Join-Path $assetDirectory "lyrveil-icon-$size.png"),
            $bytes)
    }
    finally {
        $bitmap.Dispose()
    }
}

Write-MultiSizeIcon -Frames $frames -Path (Join-Path $assetDirectory 'lyrveil-app.ico')

$preview = New-LyrveilBitmap -Size 512
try {
    $preview.Save(
        (Join-Path $designDirectory 'lyrveil-icon-preview.png'),
        [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $preview.Dispose()
}

Write-Output "Generated Lyrveil icon assets in $assetDirectory"
