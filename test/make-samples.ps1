# Creates solid-colour sample "photos" for measuring printed sizes. Never touches real photos.
# Each sample is one flat colour (no text) so its printed box can be measured by colour.
param([string]$OutDir)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
. (Join-Path $PSScriptRoot '..\.tools\fileops.ps1')
if (-not (Test-Path -LiteralPath $OutDir)) { Safe-NewDir $OutDir }
$samples = @(
    @('s1-red-3x2.jpg',     1500, 1000, 220,  40,  40),
    @('s2-green-2x3.jpg',   1000, 1500,  40, 170,  60),
    @('s3-blue-1x1.jpg',    1200, 1200,  40,  80, 220),
    @('s4-orange-3x2.jpg',  1500, 1000, 240, 150,  30),
    @('s5-purple-2x3.png',   800, 1200, 140,  60, 190),
    @('s6-small-300px.jpg',  300,  200,  20, 160, 170)   # deliberately low resolution
)
foreach ($s in $samples) {
    $path = Join-Path $OutDir $s[0]
    if (Test-Path -LiteralPath $path) { Log-Op '' $path 'create sample' 'SKIPPED: exists'; continue }
    $b = New-Object System.Drawing.Bitmap $s[1], $s[2]
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.Clear([System.Drawing.Color]::FromArgb($s[3], $s[4], $s[5]))
    $g.Dispose()
    $fmt = if ($s[0] -like '*.png') { [System.Drawing.Imaging.ImageFormat]::Png } else { [System.Drawing.Imaging.ImageFormat]::Jpeg }
    $b.Save($path, $fmt); $b.Dispose()
    Log-Op '' $path 'create sample image' 'ok'
}
Get-ChildItem -LiteralPath $OutDir | Select-Object Name, Length
