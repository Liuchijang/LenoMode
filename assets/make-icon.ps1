# Renders assets\LenoMode.ico: black tile, dot-matrix "L", red dot. PNG-compressed ICO entries.
Add-Type -AssemblyName System.Drawing
$sizes = 16, 24, 32, 48, 64, 256
$pngs = foreach ($s in $sizes) {
  $bmp = New-Object System.Drawing.Bitmap $s, $s
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'
  $g.Clear([System.Drawing.Color]::Transparent)
  $r = [Math]::Max(2, $s * 0.12)
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $d = $r * 2
  $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($s - $d - 1, 0, $d, $d, 270, 90)
  $path.AddArc($s - $d - 1, $s - $d - 1, $d, $d, 0, 90); $path.AddArc(0, $s - $d - 1, $d, $d, 90, 90)
  $path.CloseFigure()
  $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::Black)), $path)
  # 5x5 dot grid, "L" lit in white, rest dim.
  $n = 5; $step = $s * 0.16; $off = ($s - $step * ($n - 1)) / 2; $dot = [Math]::Max(1.5, $s * 0.09)
  $on = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(242, 242, 242))
  $dim = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(60, 60, 60))
  $red = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(215, 25, 33))
  for ($y = 0; $y -lt $n; $y++) { for ($x = 0; $x -lt $n; $x++) {
    $lit = ($x -eq 1 -and $y -le 3) -or ($y -eq 3 -and $x -ge 1 -and $x -le 3)
    $b = if ($x -eq 3 -and $y -eq 0) { $red } elseif ($lit) { $on } else { $dim }
    $cx = $off + $x * $step; $cy = $off + $y * $step
    if ($s -ge 32 -or $lit -or $b -eq $red) { $g.FillEllipse($b, $cx - $dot / 2, $cy - $dot / 2, $dot, $dot) }
  } }
  $g.Dispose()
  $ms = New-Object System.IO.MemoryStream
  $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
  ,$ms.ToArray()
}
$out = New-Object System.IO.MemoryStream
$w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
  $s = $sizes[$i]; $len = $pngs[$i].Length
  $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
  $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$len); $w.Write([uint32]$offset)
  $offset += $len
}
foreach ($p in $pngs) { $w.Write($p) }
[System.IO.File]::WriteAllBytes("$PSScriptRoot\LenoMode.ico", $out.ToArray())
