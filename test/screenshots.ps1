# Takes screenshots of the main window and the Print window (test settings folder, sample images only).
param([string]$Exe, [string]$OutDir)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\.tools\fileops.ps1')
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
$env:PPL_SETTINGS_DIR = Join-Path $OutDir 'settings'
if (-not (Test-Path -LiteralPath $env:PPL_SETTINGS_DIR)) { New-Item -ItemType Directory -Path $env:PPL_SETTINGS_DIR | Out-Null }
$asm = [Reflection.Assembly]::LoadFrom($Exe)
$T = $asm.GetType('PhotoPrintLayout.MainForm'); $bf = [Reflection.BindingFlags]'NonPublic,Public,Instance'
function U($a) { if ($a -is [psobject]) { $a.psobject.BaseObject } else { $a } }
function Call($n, $a) { if ($a) { $a = [object[]]@($a | ForEach-Object { U $_ }) }; $T.GetMethod($n, $bf).Invoke($f, $a) }
function SetF($n, $v) { $T.GetField($n, $bf).SetValue($f, (U $v)) }
function Shot($form, $name) { $b = New-Object System.Drawing.Bitmap $form.Width, $form.Height; $form.DrawToBitmap($b, (New-Object System.Drawing.Rectangle 0, 0, $form.Width, $form.Height)); $file = Join-Path $OutDir $name; $b.Save($file); Log-Op '' $file 'create screenshot' 'ok' }
$s = Join-Path $PplRoot 'test\samples'
$f = [Activator]::CreateInstance($T); $f.Width = 1300; $f.Height = 880
$f.Show(); [System.Windows.Forms.Application]::DoEvents()
SetF 'margin' 5.0; SetF 'gap' 3.0; SetF 'cols' 2; SetF 'rows' 2; SetF 'guideMode' 2; SetF 'captionMode' 1
Call 'SyncControls' $null
$f.AddFiles([string[]]@('s1-red-3x2.jpg', 's2-green-2x3.jpg', 's6-small-300px.jpg', 's4-orange-3x2.jpg' | ForEach-Object { Join-Path $s $_ }))
Call 'FitZoom' $null; [System.Windows.Forms.Application]::DoEvents()
Shot $f 'main-window.png'
$pwT = $asm.GetType('PhotoPrintLayout.MainForm+PrintWindow')
$pw = [Activator]::CreateInstance($pwT, [object[]]@($f))
$pw.Show(); [System.Windows.Forms.Application]::DoEvents()
$pwT.GetMethod('Regenerate', [Reflection.BindingFlags]'NonPublic,Instance').Invoke($pw, $null)
[System.Windows.Forms.Application]::DoEvents(); Start-Sleep -Milliseconds 500; [System.Windows.Forms.Application]::DoEvents()
Shot $pw 'print-window.png'
$pw.Hide(); $pw.Dispose(); $f.Hide(); $f.Dispose()
'ok'
