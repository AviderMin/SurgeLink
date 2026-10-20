param(
  [Parameter(Mandatory=$true)][string]$Path,
  [int]$GridX = 8,
  [int]$GridY = 6
)
Add-Type -AssemblyName System.Drawing
$bmp = [System.Drawing.Bitmap]::FromFile($Path)
Write-Output "size=$($bmp.Width)x$($bmp.Height)"
$colors = @{}
$nonBlank = 0
for ($y = 0; $y -lt $bmp.Height; $y += 4) {
  for ($x = 0; $x -lt $bmp.Width; $x += 4) {
    $c = $bmp.GetPixel($x, $y)
    $key = "{0:X2}{1:X2}{2:X2}" -f $c.R, $c.G, $c.B
    if ($colors.ContainsKey($key)) { $colors[$key]++ } else { $colors[$key] = 1 }
    if ($c.R -gt 8 -or $c.G -gt 8 -or $c.B -gt 8) { $nonBlank++ }
  }
}
Write-Output "distinctColors=$($colors.Count) nonBlankSamples=$nonBlank"
$top = $colors.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 8 | ForEach-Object { "#$($_.Key)=$($_.Value)" }
Write-Output ("topColors: " + ($top -join ', '))
$cellW = [int]($bmp.Width / $GridX); $cellH = [int]($bmp.Height / $GridY)
for ($gy = 0; $gy -lt $GridY; $gy++) {
  $row = @()
  for ($gx = 0; $gx -lt $GridX; $gx++) {
    $sx = [Math]::Min($bmp.Width-1, $gx*$cellW + [int]($cellW/2))
    $sy = [Math]::Min($bmp.Height-1, $gy*$cellH + [int]($cellH/2))
    $c = $bmp.GetPixel($sx, $sy)
    $row += ("{0:X2}{1:X2}{2:X2}" -f $c.R,$c.G,$c.B)
  }
  Write-Output ("row$gy " + ($row -join ' '))
}
$bmp.Dispose()
