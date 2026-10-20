$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$assetDirectory = Join-Path $PSScriptRoot '..\src\Nstrap.App\Assets'
$bitmap = [System.Drawing.Bitmap]::new(256, 256)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$background = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#B7A4FF'))
$foreground = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#19132F'))
$shape = [System.Drawing.Drawing2D.GraphicsPath]::new()
$shape.AddArc(0, 0, 100, 100, 180, 90)
$shape.AddArc(155, 0, 100, 100, 270, 90)
$shape.AddArc(155, 155, 100, 100, 0, 90)
$shape.AddArc(0, 155, 100, 100, 90, 90)
$shape.CloseFigure()
$graphics.FillPath($background, $shape)
$points = [System.Drawing.Point[]]@(
    [System.Drawing.Point]::new(58,192), [System.Drawing.Point]::new(58,64),
    [System.Drawing.Point]::new(94,64), [System.Drawing.Point]::new(164,150),
    [System.Drawing.Point]::new(164,64), [System.Drawing.Point]::new(200,64),
    [System.Drawing.Point]::new(200,192), [System.Drawing.Point]::new(164,192),
    [System.Drawing.Point]::new(94,104), [System.Drawing.Point]::new(94,192))
$graphics.FillPolygon($foreground, $points)
$memory = [System.IO.MemoryStream]::new()
$bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
$png = $memory.ToArray()
$stream = [System.IO.File]::Create((Join-Path $assetDirectory 'Nstrap.ico'))
$writer = [System.IO.BinaryWriter]::new($stream)
$writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]1)
$writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0); $writer.Write([byte]0)
$writer.Write([uint16]1); $writer.Write([uint16]32); $writer.Write([uint32]$png.Length); $writer.Write([uint32]22)
$writer.Write($png)
$writer.Dispose(); $memory.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
$shape.Dispose(); $background.Dispose(); $foreground.Dispose()
