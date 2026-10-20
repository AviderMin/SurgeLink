param([string]$OutFile)
Add-Type -AssemblyName System.Drawing
$sizes = @(256, 64, 48, 32, 16)
$pngs = @()
foreach ($size in $sizes) {
  $bmp = New-Object System.Drawing.Bitmap $size, $size
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
  $g.Clear([System.Drawing.Color]::Transparent)

  $pad = [Math]::Max(1, [int]($size * 0.06))
  $rect = New-Object System.Drawing.Rectangle $pad, $pad, ($size - 2*$pad), ($size - 2*$pad)
  $radius = [int]($size * 0.22)
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = $radius * 2
  $path.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
  $path.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
  $path.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
  $path.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
  $path.CloseFigure()

  $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, ([System.Drawing.Color]::FromArgb(255, 16, 22, 38)), ([System.Drawing.Color]::FromArgb(255, 6, 9, 18)), 90.0
  $g.FillPath($brush, $path)
  $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 76, 194, 255)), ([Math]::Max(1, $size * 0.028))
  $g.DrawPath($pen, $path)

  $accent = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 76, 194, 255))
  $soft = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 138, 214, 255))
  $font = New-Object System.Drawing.Font "Consolas", ($size * 0.46), ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
  $fmt = New-Object System.Drawing.StringFormat
  $fmt.Alignment = [System.Drawing.StringAlignment]::Center
  $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
  $g.DrawString(">_", $font, $accent, (New-Object System.Drawing.RectangleF 0, ($size * -0.04), $size, $size), $fmt)

  # small "link" dots on the right to hint at connectivity
  $dot = [Math]::Max(1.0, $size * 0.055)
  $dotBrush = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(200, 138, 214, 255))
  for ($i = 0; $i -lt 3; $i++) {
    $dx = $size - $pad - ($dot * 1.6) - ($i * $dot * 1.9)
    $dy = $size - $pad - ($dot * 1.8)
    $g.FillEllipse($dotBrush, $dx, $dy, $dot, $dot)
  }

  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
  $pngs += ,$ms.ToArray()
  $bmp.Dispose()
  $ms.Dispose()
}

$out = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $out
$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$sizes.Count)
$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]
  $dim = if ($s -ge 256) { 0 } else { $s }
  $bw.Write([byte]$dim)
  $bw.Write([byte]$dim)
  $bw.Write([byte]0)
  $bw.Write([byte]0)
  $bw.Write([uint16]1)
  $bw.Write([uint16]32)
  $bw.Write([uint32]$pngs[$i].Length)
  $bw.Write([uint32]$offset)
  $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush()
[System.IO.File]::WriteAllBytes($OutFile, $out.ToArray())
$bw.Dispose()
$out.Dispose()
Write-Output "wrote $OutFile ($((Get-Item $OutFile).Length) bytes)"
