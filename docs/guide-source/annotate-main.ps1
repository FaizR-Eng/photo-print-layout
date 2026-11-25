# Draws numbered callouts on main.png -> main-annotated.png (positions match the 1250 x 860 window shot).
param([Parameter(Mandatory = $true)][string]$Dir)
Add-Type -AssemblyName System.Drawing
$src = [System.Drawing.Image]::FromFile((Join-Path $Dir 'main.png')); $b = New-Object System.Drawing.Bitmap $src; $src.Dispose()
$g = [System.Drawing.Graphics]::FromImage($b); $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
$red = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(225, 29, 72)); $white = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 2.5
$font = New-Object System.Drawing.Font 'Segoe UI', 11, ([System.Drawing.FontStyle]::Bold)
$fmt = New-Object System.Drawing.StringFormat; $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
$marks = @(@(1, 482, 50), @(2, 614, 50), @(3, 1010, 50), @(4, 216, 166), @(5, 306, 338), @(6, 306, 500), @(7, 306, 720),
           @(8, 516, 300), @(9, 1024, 440), @(10, 950, 482), @(11, 788, 569), @(12, 262, 842), @(13, 1050, 842))
foreach ($m in $marks) {
    $r = New-Object System.Drawing.RectangleF ($m[1] - 14), ($m[2] - 14), 28, 28
    $g.FillEllipse($red, $r); $g.DrawEllipse($white, $r); $g.DrawString([string]$m[0], $font, [System.Drawing.Brushes]::White, $r, $fmt)
}
$g.Dispose(); $b.Save((Join-Path $Dir 'main-annotated.png'), [System.Drawing.Imaging.ImageFormat]::Png)
'ok'
