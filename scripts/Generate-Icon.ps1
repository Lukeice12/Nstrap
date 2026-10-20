$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

# NstrapIcon.png is a format-only PNG conversion of the supplied NstrapIcon.webp.
# Resize the complete artwork with its aspect ratio intact; do not redraw or crop it.
$assetDirectory = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\Nstrap.App\Assets'))
$source = [System.Drawing.Image]::FromFile((Join-Path $assetDirectory 'NstrapIcon.png'))
$sizes = @(16, 20, 24, 32, 40, 48, 64, 96, 128, 256)
$frames = [System.Collections.Generic.List[byte[]]]::new()
try {
    foreach ($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $attributes = [System.Drawing.Imaging.ImageAttributes]::new()
        $memory = [System.IO.MemoryStream]::new()
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
            $scale = [Math]::Min($size / [double]$source.Width, $size / [double]$source.Height)
            $width = [Math]::Max(1, [int][Math]::Round($source.Width * $scale))
            $height = [Math]::Max(1, [int][Math]::Round($source.Height * $scale))
            $rectangle = [System.Drawing.Rectangle]::new(
                [int][Math]::Floor(($size - $width) / 2), [int][Math]::Floor(($size - $height) / 2), $width, $height)
            $graphics.DrawImage($source, $rectangle, 0, 0, $source.Width, $source.Height, [System.Drawing.GraphicsUnit]::Pixel, $attributes)
            $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
            $frames.Add($memory.ToArray())
        }
        finally {
            $memory.Dispose()
            $attributes.Dispose()
            $graphics.Dispose()
            $bitmap.Dispose()
        }
    }
}
finally { $source.Dispose() }

# Windows ICO directory followed by one PNG image for each requested resolution.
$stream = [System.IO.File]::Create((Join-Path $assetDirectory 'Nstrap.ico'))
$writer = [System.IO.BinaryWriter]::new($stream)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = [uint32](6 + 16 * $sizes.Count)
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { [byte]0 } else { [byte]$sizes[$index] }
        $writer.Write($dimension)
        $writer.Write($dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$frames[$index].Length)
        $writer.Write($offset)
        $offset += [uint32]$frames[$index].Length
    }
    foreach ($frame in $frames) { $writer.Write($frame) }
}
finally { $writer.Dispose() }
