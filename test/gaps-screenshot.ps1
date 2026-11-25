# Screenshot of the gap measurements in free layout (test settings folder, sample images only).
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
function Get($n) { return ,($T.GetField($n, $bf).GetValue($f)) }
function SetF($n, $v) { $T.GetField($n, $bf).SetValue($f, (U $v)) }
function Shot($name) { $b = New-Object System.Drawing.Bitmap $f.Width, $f.Height; $f.DrawToBitmap($b, (New-Object System.Drawing.Rectangle 0, 0, $f.Width, $f.Height)); $file = Join-Path $OutDir $name; $b.Save($file); Log-Op '' $file 'create screenshot' 'ok' }
$s = Join-Path $PplRoot 'test\samples'
$f = [Activator]::CreateInstance($T); $f.Width = 1300; $f.Height = 880
$f.Show(); [System.Windows.Forms.Application]::DoEvents()
SetF 'margin' 5.0; SetF 'gap' 3.0; Call 'SyncControls' $null
$f.AddFiles([string[]]@('s1-red-3x2.jpg', 's4-orange-3x2.jpg', 's3-blue-1x1.jpg', 's2-green-2x3.jpg' | ForEach-Object { Join-Path $s $_ }))
Call 'EnterFree' $null
$it = Get 'items'
$it[0].FX = 12; $it[0].FY = 15; $it[0].FW = 85
$it[1].FX = 103; $it[1].FY = 15; $it[1].FW = 85
$it[2].FX = 12; $it[2].FY = 80; $it[2].FW = 60
$it[3].FX = 120; $it[3].FY = 90; $it[3].FW = 50
foreach ($q in $it) { $q.FPage = 0 }
$l = Get 'selList'; $l.Clear(); $l.Add($it[0]); SetF 'sel' $it[0]
Call 'AfterChange' $null; Call 'FitZoom' $null; [System.Windows.Forms.Application]::DoEvents()
Shot 'gaps-one-selected.png'
$f.Hide(); $f.Dispose()
'ok'
