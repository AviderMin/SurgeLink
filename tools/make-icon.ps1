param(
    [string]$SourceFile = (Join-Path $PSScriptRoot '../docs/ICON.jpg'),
    [string]$OutFile = (Join-Path $PSScriptRoot '../src/SurgeLink.App/Assets/AppIcon.ico')
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$source = [System.Drawing.Image]::FromFile([IO.Path]::GetFullPath($SourceFile))
$sizes = @(256, 128, 96, 64, 48, 32, 24, 20, 16)
$pngs = @()
try {
    foreach ($size in $sizes) {
        $bitmap = [System.Drawing.Bitmap]::new($size, $size)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $buffer = [IO.MemoryStream]::new()
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $scale = [Math]::Min($size / $source.Width, $size / $source.Height)
            $width = [int][Math]::Round($source.Width * $scale)
            $height = [int][Math]::Round($source.Height * $scale)
            $graphics.DrawImage($source, [int](($size - $width) / 2), [int](($size - $height) / 2), $width, $height)
            $bitmap.Save($buffer, [System.Drawing.Imaging.ImageFormat]::Png)
            $pngs += ,$buffer.ToArray()
        }
        finally { $buffer.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
}
finally { $source.Dispose() }
$output = [IO.MemoryStream]::new()
$writer = [IO.BinaryWriter]::new($output)
try {
    $writer.Write([uint16]0)
    $writer.Write([uint16]1)
    $writer.Write([uint16]$sizes.Count)
    $offset = 6 + 16 * $sizes.Count
    for ($index = 0; $index -lt $sizes.Count; $index++) {
        $dimension = if ($sizes[$index] -eq 256) { 0 } else { $sizes[$index] }
        $writer.Write([byte]$dimension)
        $writer.Write([byte]$dimension)
        $writer.Write([byte]0)
        $writer.Write([byte]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]32)
        $writer.Write([uint32]$pngs[$index].Length)
        $writer.Write([uint32]$offset)
        $offset += $pngs[$index].Length
    }
    foreach ($png in $pngs) { $writer.Write([byte[]]$png) }
    $writer.Flush()
    [IO.File]::WriteAllBytes([IO.Path]::GetFullPath($OutFile), $output.ToArray())
}
finally { $writer.Dispose(); $output.Dispose() }
Get-Item -LiteralPath $OutFile | Select-Object FullName, Length
