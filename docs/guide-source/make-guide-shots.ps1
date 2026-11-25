# Takes the screenshots for the user guide (uses drawn sample pictures, its own settings folder, never prints).
#   powershell -STA -ExecutionPolicy Bypass -File make-guide-shots.ps1 -Exe <PhotoPrintLayout.exe> -OutDir <new folder>
param([Parameter(Mandatory = $true)][string]$Exe, [Parameter(Mandatory = $true)][string]$OutDir)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\..\.tools\fileops.ps1')
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null; Log-Op '' $OutDir 'create folder' 'ok' }
$env:PPL_SETTINGS_DIR = Join-Path $OutDir 'settings'
if (-not (Test-Path -LiteralPath $env:PPL_SETTINGS_DIR)) { New-Item -ItemType Directory -Path $env:PPL_SETTINGS_DIR | Out-Null }

# ---- drawn sample "photos" (simple landscapes)
$photoDir = Join-Path $OutDir 'photos'
if (-not (Test-Path -LiteralPath $photoDir)) { New-Item -ItemType Directory -Path $photoDir | Out-Null }
function Col($h) { [System.Drawing.ColorTranslator]::FromHtml($h) }
function Scene($name, $w, $h, $sky1, $sky2, $hill1, $hill2, $sun) {
    $path = Join-Path $photoDir $name
    $b = New-Object System.Drawing.Bitmap $w, $h; $g = [System.Drawing.Graphics]::FromImage($b); $g.SmoothingMode = 'AntiAlias'
    $r = New-Object System.Drawing.Rectangle 0, 0, $w, $h
    $g.FillRectangle((New-Object System.Drawing.Drawing2D.LinearGradientBrush $r, (Col $sky1), (Col $sky2), 90), $r)
    $g.FillEllipse((New-Object System.Drawing.SolidBrush (Col $sun)), [int]($w * 0.68), [int]($h * 0.14), [int]([math]::Min($w, $h) * 0.16), [int]([math]::Min($w, $h) * 0.16))
    $p1 = [System.Drawing.Point[]]@((New-Object System.Drawing.Point 0, ([int]($h * 0.62))), (New-Object System.Drawing.Point ([int]($w * 0.3)), ([int]($h * 0.42))), (New-Object System.Drawing.Point ([int]($w * 0.6)), ([int]($h * 0.6))), (New-Object System.Drawing.Point $w, ([int]($h * 0.48))), (New-Object System.Drawing.Point $w, $h), (New-Object System.Drawing.Point 0, $h))
    $g.FillPolygon((New-Object System.Drawing.SolidBrush (Col $hill1)), $p1)
    $p2 = [System.Drawing.Point[]]@((New-Object System.Drawing.Point 0, ([int]($h * 0.78))), (New-Object System.Drawing.Point ([int]($w * 0.45)), ([int]($h * 0.66))), (New-Object System.Drawing.Point $w, ([int]($h * 0.8))), (New-Object System.Drawing.Point $w, $h), (New-Object System.Drawing.Point 0, $h))
    $g.FillPolygon((New-Object System.Drawing.SolidBrush (Col $hill2)), $p2)
    $g.Dispose(); $b.Save($path, [System.Drawing.Imaging.ImageFormat]::Jpeg); $b.Dispose()
    return $path
}
function Portrait($name, $w, $h) {   # simple passport-style picture
    $path = Join-Path $photoDir $name
    $b = New-Object System.Drawing.Bitmap $w, $h; $g = [System.Drawing.Graphics]::FromImage($b); $g.SmoothingMode = 'AntiAlias'
    $g.Clear((Col '#dbe7f3'))
    $g.FillEllipse((New-Object System.Drawing.SolidBrush (Col '#c89a7a')), [int]($w * 0.3), [int]($h * 0.18), [int]($w * 0.4), [int]($h * 0.38))
    $g.FillEllipse((New-Object System.Drawing.SolidBrush (Col '#3b4a63')), [int]($w * 0.12), [int]($h * 0.62), [int]($w * 0.76), [int]($h * 0.6))
    $g.Dispose(); $b.Save($path, [System.Drawing.Imaging.ImageFormat]::Jpeg); $b.Dispose()
    return $path
}
$lake   = Scene 'Lake.jpg'   1500 1000 '#7cc4f2' '#e6f5ff' '#3f8f5a' '#2e6b43' '#fff3b0'
$tower  = Scene 'Tower.jpg'  1000 1500 '#f6a96b' '#fde3c3' '#8a5a44' '#5b3a2e' '#fff1c7'
$desert = Scene 'Desert.jpg'  420  280 '#f3c96b' '#fff4d6' '#d9a25a' '#b47b3c' '#ffffff'   # small on purpose (low resolution)
$garden = Scene 'Garden.jpg' 1000 1500 '#9ad1c9' '#e9fbf7' '#5aa35f' '#3d7c45' '#fffbe0'
$sea    = Scene 'Sea.jpg'    1200 1200 '#5b8fd6' '#cfe2ff' '#2f6db5' '#1f4f8a' '#ffe9a8'
$hills  = Scene 'Hills.jpg'  1500 1000 '#b9a7e6' '#f1ecff' '#7a6bb8' '#574a8f' '#fff6d5'
$face   = Portrait 'Passport.jpg' 700 900

# ---- app access
$asm = [Reflection.Assembly]::LoadFrom($Exe)
$T = $asm.GetType('PhotoPrintLayout.MainForm'); $bf = [Reflection.BindingFlags]'NonPublic,Public,Instance'
function U($a) { if ($a -is [psobject]) { $a.psobject.BaseObject } else { $a } }
function Call($n, $a) { if ($a) { $a = [object[]]@($a | ForEach-Object { U $_ }) }; return $T.GetMethod($n, $bf).Invoke($script:f, $a) }
function Get($n) { return ,($T.GetField($n, $bf).GetValue($script:f)) }
function SetF($n, $v) { $T.GetField($n, $bf).SetValue($script:f, (U $v)) }
function Prop($n) { return $T.GetProperty($n, $bf).GetValue($script:f) }
function Sel1($p) { $l = Get 'selList'; $l.Clear(); if ($p) { $l.Add($p) }; SetF 'sel' $p; Call 'UpdateSel' $null }
function Snap($form) { $b = New-Object System.Drawing.Bitmap $form.Width, $form.Height; $form.DrawToBitmap($b, (New-Object System.Drawing.Rectangle 0, 0, $form.Width, $form.Height)); return $b }
function Save($bmp, $name) { $file = Join-Path $OutDir $name; $bmp.Save($file); Log-Op '' $file 'create guide screenshot' 'ok' }
function Crop($bmp, $x, $y, $w, $h, $name) { Save ($bmp.Clone((New-Object System.Drawing.Rectangle $x, $y, $w, $h), $bmp.PixelFormat)) $name }
function New-App($w = 1250, $h = 860) {
    if ($script:f) { $script:f.Hide(); $script:f.Dispose() }
    $script:f = [Activator]::CreateInstance($T)
    $script:f.StartPosition = 'Manual'; $script:f.Location = New-Object System.Drawing.Point -6000, 0   # off-screen: the real mouse can't disturb it
    $script:f.Width = $w; $script:f.Height = $h
    $script:f.Show(); [System.Windows.Forms.Application]::DoEvents()
    SetF 'margin' 5.0; SetF 'gap' 3.0; SetF 'cols' 2; SetF 'rows' 2; SetF 'free' $false; SetF 'guideMode' 0; SetF 'captionMode' 0; SetF 'boxW' 0.0; SetF 'boxH' 0.0; SetF 'landscape' $false
    Call 'SyncControls' $null
}
function Side() { return (Get 'arrangeBox').Parent }
function ScrollSideTo($ctl, $offset) { $s = Side; $s.AutoScrollPosition = New-Object System.Drawing.Point 0, 0; [System.Windows.Forms.Application]::DoEvents(); $s.AutoScrollPosition = New-Object System.Drawing.Point 0, ([math]::Max(0, $ctl.Top - $offset)); [System.Windows.Forms.Application]::DoEvents() }
$mk = { param($x, $y) New-Object System.Windows.Forms.MouseEventArgs ([System.Windows.Forms.MouseButtons]::Left), 1, $x, $y, 0 }

# 1) main window: 2 x 2 grid, Tower selected, size label on Garden, low-resolution Desert
New-App
$script:f.AddFiles([string[]]@($lake, $tower, $desert, $garden))
$it = Get 'items'; Sel1 $it[1]; SetF 'hoverIndex' ([int]3)
Call 'FitZoom' $null; [System.Windows.Forms.Application]::DoEvents()
$b = Snap $script:f; Save $b 'main.png'
# side panel: layout section
ScrollSideTo (Get 'presetButtons')[0].Parent 40
$b = Snap $script:f; Crop $b 0 66 318 760 'side-layout.png'
# side panel: selected photo section
ScrollSideTo (Get 'selBox') 40
$b = Snap $script:f; Crop $b 0 66 318 760 'side-selected.png'

# 2) photo sizes: passport, repeated to fill the page, with crop marks
New-App
$script:f.AddFiles([string[]]@($face))
(Get 'cbSize').SelectedIndex = 9
SetF 'guideMode' 2; Call 'SyncControls' $null
$it = Get 'items'; Sel1 $it[0]; Call 'Act' @('fillpage'); Sel1 $null
Call 'FitZoom' $null; [System.Windows.Forms.Application]::DoEvents()
$b = Snap $script:f; Save $b 'sizes-passport.png'

# 3) captions and 10 x 15 cm
New-App
$script:f.AddFiles([string[]]@($lake, $hills))
(Get 'cbSize').SelectedIndex = 2
SetF 'captionMode' 1; SetF 'guideMode' 1; Call 'SyncControls' $null; Call 'FitZoom' $null; [System.Windows.Forms.Application]::DoEvents()
$b = Snap $script:f; Save $b 'sizes-10x15-captions.png'

# 4) free layout: gap measurements with one photo selected
New-App
$script:f.AddFiles([string[]]@($lake, $tower, $sea, $garden))
Call 'EnterFree' $null
$it = Get 'items'
$it[0].FX = 12; $it[0].FY = 15; $it[0].FW = 85
$it[1].FX = 103; $it[1].FY = 15; $it[1].FW = 60
$it[2].FX = 12; $it[2].FY = 80; $it[2].FW = 65
$it[3].FX = 120; $it[3].FY = 130; $it[3].FW = 55
foreach ($q in $it) { $q.FPage = 0 }
Sel1 $it[0]; Call 'AfterChange' $null; Call 'FitZoom' $null; [System.Windows.Forms.Application]::DoEvents()
$b = Snap $script:f; Save $b 'free-gaps.png'
# arrange panel
ScrollSideTo (Get 'arrangeBox') 10
$b = Snap $script:f; Crop $b 0 66 318 760 'side-arrange.png'
(Side).AutoScrollPosition = New-Object System.Drawing.Point 0, 0; [System.Windows.Forms.Application]::DoEvents()

# 5) dragging: Garden snapping under Sea (guide lines + gaps live)
$preview = Get 'preview'; $ppm = Prop 'Ppm'; $asp = $preview.AutoScrollPosition
Sel1 $it[3]
$r = $T.GetMethod('FreeRectPx', $bf).Invoke($script:f, @($it[3]))
$cx = [int]($r.X + $r.Width / 2 + $asp.X); $cy = [int]($r.Y + $r.Height / 2 + $asp.Y)
$tx = (12.6 - $it[3].FX); $ty = (150.4 - $it[3].FY)
Call 'PreviewMouseDown' @($preview, (& $mk $cx $cy))
Call 'PreviewMouseMove' @($preview, (& $mk ($cx + 8) $cy))
Call 'PreviewMouseMove' @($preview, (& $mk ([int]($cx + $tx * $ppm)) ([int]($cy + $ty * $ppm))))
$b = Snap $script:f; Save $b 'free-drag.png'
Call 'PreviewMouseUp' @($preview, (& $mk 0 0))

# 6) box selection
$pr = $T.GetMethod('PageRectPx', $bf).Invoke($script:f, @(0)); $asp = $preview.AutoScrollPosition
$x0 = [int]($pr.X + 3 + $asp.X); $y0 = [int]($pr.Y + 3 + $asp.Y); $x1 = [int]($pr.X + 120 * $ppm + $asp.X); $y1 = [int]($pr.Y + 110 * $ppm + $asp.Y)
Call 'PreviewMouseDown' @($preview, (& $mk $x0 $y0))
Call 'PreviewMouseMove' @($preview, (& $mk $x1 $y1))
$b = Snap $script:f; Save $b 'free-boxselect.png'
Call 'PreviewMouseUp' @($preview, (& $mk $x1 $y1))

# 7) Print window
New-App
$script:f.AddFiles([string[]]@($lake, $tower, $desert, $garden, $sea))
Call 'FitZoom' $null
$pwT = $asm.GetType('PhotoPrintLayout.MainForm+PrintWindow')
$pw = [Activator]::CreateInstance($pwT, [object[]]@($script:f))
$pw.StartPosition = 'Manual'; $pw.Location = New-Object System.Drawing.Point 0, 0; $pw.Width = 1150; $pw.Height = 800   # on screen: the preview only draws when visible
$pw.Show(); [System.Windows.Forms.Application]::DoEvents()
$pwT.GetMethod('Regenerate', [Reflection.BindingFlags]'NonPublic,Instance').Invoke($pw, $null)
[System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 600; [System.Windows.Forms.Application]::DoEvents()
$b = Snap $pw; Save $b 'print-window.png'
$pw.Hide(); $pw.Dispose()
$script:f.Hide(); $script:f.Dispose()
'ok'
