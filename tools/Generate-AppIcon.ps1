param(
    [string]$OutputPath = (Join-Path $PSScriptRoot '..\src\MyTextEditor\Assets\AppIcon.ico')
)

Add-Type -AssemblyName System.Drawing

$size = 256
$bitmap = [System.Drawing.Bitmap]::new($size, $size)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.Clear([System.Drawing.Color]::Transparent)

$background = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#2563EB'))
$paper = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#F8FAFC'))
$cut = [System.Drawing.Pen]::new([System.Drawing.ColorTranslator]::FromHtml('#2563EB'), 13)
$cut.StartCap = $cut.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

$graphics.FillEllipse($background, 8, 8, 240, 240)

$document = [System.Drawing.Drawing2D.GraphicsPath]::new()
$document.AddPolygon([System.Drawing.Point[]]@(
    [System.Drawing.Point]::new(65, 43),
    [System.Drawing.Point]::new(159, 43),
    [System.Drawing.Point]::new(202, 86),
    [System.Drawing.Point]::new(202, 213),
    [System.Drawing.Point]::new(65, 213)
))
$graphics.FillPath($paper, $document)
$graphics.DrawLine($cut, 93, 111, 174, 111)
$graphics.DrawLine($cut, 93, 151, 174, 151)

$pngStream = [System.IO.MemoryStream]::new()
$bitmap.Save($pngStream, [System.Drawing.Imaging.ImageFormat]::Png)
$pngBytes = $pngStream.ToArray()

$directory = Split-Path -Parent $OutputPath
[System.IO.Directory]::CreateDirectory($directory) | Out-Null
$file = [System.IO.File]::Create($OutputPath)
$writer = [System.IO.BinaryWriter]::new($file)
$writer.Write([UInt16]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]1)
$writer.Write([Byte]0)
$writer.Write([Byte]0)
$writer.Write([Byte]0)
$writer.Write([Byte]0)
$writer.Write([UInt16]1)
$writer.Write([UInt16]32)
$writer.Write([UInt32]$pngBytes.Length)
$writer.Write([UInt32]22)
$writer.Write($pngBytes)
$writer.Dispose()

$document.Dispose()
$cut.Dispose()
$paper.Dispose()
$background.Dispose()
$graphics.Dispose()
$bitmap.Dispose()
$pngStream.Dispose()

Write-Output "Generated $OutputPath"
