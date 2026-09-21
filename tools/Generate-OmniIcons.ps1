param([string]$OutputDirectory = (Join-Path $PSScriptRoot '../artifacts/omni-icons'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$output = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($output) | Out-Null

function Draw-Icon([string]$variant, [int]$size) {
    $bitmap = [Drawing.Bitmap]::new($size, $size)
    $g = [Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.ScaleTransform(($size / 256.0), ($size / 256.0))
    $blue = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#2563EB'))
    $teal = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#18B8A6'))
    $purple = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#7950E8'))
    $white = [Drawing.SolidBrush]::new([Drawing.Color]::White)
    $ring = [Drawing.Drawing2D.GraphicsPath]::new()
    try {
        switch ($variant) {
            'A' {
                $ring.AddEllipse(24,24,208,208)
                $ring.AddEllipse(64,64,128,128)
                $g.FillPath($blue,$ring)
                $g.FillRectangle($teal,160,76,24,104)
                $g.FillRectangle($teal,145,76,54,12)
                $g.FillRectangle($teal,145,168,54,12)
            }
            'B' {
                $g.FillRectangle($teal,30,24,140,174)
                $g.FillRectangle($blue,72,56,154,178)
                $pen = [Drawing.Pen]::new([Drawing.Color]::White,18)
                $g.DrawEllipse($pen,103,85,91,106)
                $pen.Dispose()
                $g.FillRectangle($white,119,207,60,8)
            }
            'C' {
                $g.FillEllipse($purple,24,24,208,208)
                $g.FillPie($blue,24,24,208,208,-90,180)
                $g.FillRectangle($white,69,74,48,108)
                $g.FillRectangle($white,139,74,48,108)
                $g.FillRectangle($teal,101,116,54,24)
            }
        }
    } finally { $ring.Dispose(); $blue.Dispose(); $teal.Dispose(); $purple.Dispose(); $white.Dispose(); $g.Dispose() }
    return ,$bitmap
}

$svgs = @{
    A = '<path fill="#2563EB" fill-rule="evenodd" d="M128 24a104 104 0 1 0 0 208 104 104 0 1 0 0-208zm0 40a64 64 0 1 1 0 128 64 64 0 1 1 0-128z"/><path fill="#18B8A6" d="M160 76h24v104h-24zM145 76h54v12h-54zM145 168h54v12h-54z"/>'
    B = '<path fill="#18B8A6" d="M30 24h140v174H30z"/><path fill="#2563EB" d="M72 56h154v178H72z"/><ellipse cx="148.5" cy="138" rx="45.5" ry="53" fill="none" stroke="white" stroke-width="18"/><path fill="white" d="M119 207h60v8h-60z"/>'
    C = '<circle cx="128" cy="128" r="104" fill="#7950E8"/><path fill="#2563EB" d="M128 24a104 104 0 0 1 0 208z"/><path fill="white" d="M69 74h48v108H69zM139 74h48v108h-48z"/><path fill="#18B8A6" d="M101 116h54v24h-54z"/>'
}

foreach ($variant in @('A','B','C')) {
    [IO.File]::WriteAllText((Join-Path $output "OmniEdit-$variant.svg"), ('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256">' + $svgs[$variant] + '</svg>'))
    $frames = [Collections.Generic.List[byte[]]]::new()
    $sizes = @(16,24,32,48,64,128,256)
    foreach ($size in $sizes) {
        $bitmap = Draw-Icon $variant $size
        $stream = [IO.MemoryStream]::new()
        try {
            $bitmap.Save($stream,[Drawing.Imaging.ImageFormat]::Png)
            $frames.Add($stream.ToArray())
            if ($size -eq 256) { $bitmap.Save((Join-Path $output "OmniEdit-$variant.png"),[Drawing.Imaging.ImageFormat]::Png) }
        } finally { $stream.Dispose(); $bitmap.Dispose() }
    }
    $file = [IO.File]::Create((Join-Path $output "OmniEdit-$variant.ico"))
    $writer = [IO.BinaryWriter]::new($file)
    try {
        $writer.Write([uint16]0); $writer.Write([uint16]1); $writer.Write([uint16]$sizes.Count)
        $offset = 6 + 16 * $sizes.Count
        for ($i=0; $i -lt $sizes.Count; $i++) {
            $dimension = if ($sizes[$i] -eq 256) { 0 } else { $sizes[$i] }
            $writer.Write([byte]$dimension); $writer.Write([byte]$dimension)
            $writer.Write([byte]0); $writer.Write([byte]0)
            $writer.Write([uint16]1); $writer.Write([uint16]32)
            $writer.Write([uint32]$frames[$i].Length); $writer.Write([uint32]$offset)
            $offset += $frames[$i].Length
        }
        foreach ($frame in $frames) { $writer.Write($frame) }
    } finally { $writer.Dispose(); $file.Dispose() }
}

$sheet = [Drawing.Bitmap]::new(1080,470)
$g = [Drawing.Graphics]::FromImage($sheet)
$g.Clear([Drawing.ColorTranslator]::FromHtml('#F4F6FA'))
$g.TextRenderingHint = [Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$titleFont = [Drawing.Font]::new('Segoe UI',20,[Drawing.FontStyle]::Bold)
$bodyFont = [Drawing.Font]::new('Segoe UI',12)
$dark = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#1E293B'))
$muted = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#536478'))
try {
    $g.DrawString('OmniEdit / Icon concepts',$titleFont,$dark,30,20)
    $labels = @('A  O + Text Cursor','B  Layered Documents','C  Compare + Merge')
    for ($i=0; $i -lt 3; $i++) {
        $x = 30 + $i * 350
        $g.FillRectangle([Drawing.Brushes]::White,$x,75,320,360)
        $icon = Draw-Icon @('A','B','C')[$i] 208
        $g.DrawImage($icon,($x+56),96,208,208)
        $icon.Dispose()
        $g.DrawString($labels[$i],$bodyFont,$dark,($x+22),326)
        $g.DrawString('16px        24px          32px',$bodyFont,$muted,($x+22),357)
        $left = $x+24
        foreach ($size in @(16,24,32)) {
            $small = Draw-Icon @('A','B','C')[$i] $size
            $g.DrawImageUnscaled($small,$left,390)
            $small.Dispose(); $left += 80
        }
    }
    $sheet.Save((Join-Path $output 'OmniEdit-icon-options.png'),[Drawing.Imaging.ImageFormat]::Png)
} finally { $titleFont.Dispose(); $bodyFont.Dispose(); $dark.Dispose(); $muted.Dispose(); $g.Dispose(); $sheet.Dispose() }
Write-Output (Join-Path $output 'OmniEdit-icon-options.png')
