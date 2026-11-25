# Baseline: runs the CURRENT app (no changes) on the sample copies, renders the print pages
# through Windows' print engine (preview controller, nothing sent to a printer), and measures
# where each coloured photo lands on the paper, in millimetres.
# Does NOT close the form normally, so the app never writes its settings file.
param([string]$Exe, [string]$OutDir)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\.tools\fileops.ps1')
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System; using System.Drawing; using System.Drawing.Imaging;
public static class Meas {
    // bounding box (in pixels) of all pixels within tol of the colour; returns null if none
    public static int[] Box(Bitmap b, int r, int g, int bl, int tol) {
        var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        byte[] buf = new byte[d.Stride * b.Height];
        System.Runtime.InteropServices.Marshal.Copy(d.Scan0, buf, 0, buf.Length);
        b.UnlockBits(d);
        for (int y = 0; y < b.Height; y++) {
            int row = y * d.Stride;
            for (int x = 0; x < b.Width; x++) {
                int B = buf[row + x * 4], G = buf[row + x * 4 + 1], R = buf[row + x * 4 + 2];
                if (Math.Abs(R - r) <= tol && Math.Abs(G - g) <= tol && Math.Abs(B - bl) <= tol) {
                    if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
            }
        }
        return maxX < 0 ? null : new[] { minX, minY, maxX + 1, maxY + 1 };
    }
}
'@

$samples = Join-Path $PplRoot 'test\samples'
$colors = @{ 's1-red-3x2.jpg' = @(220,40,40); 's2-green-2x3.jpg' = @(40,170,60); 's3-blue-1x1.jpg' = @(40,80,220); 's4-orange-3x2.jpg' = @(240,150,30); 's5-purple-2x3.png' = @(140,60,190); 's6-small-300px.jpg' = @(20,160,170) }
$hashBefore = Get-ChildItem -LiteralPath $samples | ForEach-Object { $_.Name + '=' + (Get-FileHash -LiteralPath $_.FullName).Hash }

$asm = [Reflection.Assembly]::LoadFrom($Exe)
$t = $asm.GetType('PhotoPrintLayout.MainForm')
$bf = [Reflection.BindingFlags]'NonPublic,Public,Instance'
function Call($name, $a) { if ($a) { $a = [object[]]@($a | ForEach-Object { if ($_ -is [psobject]) { $_.psobject.BaseObject } else { $_ } }) }; return $t.GetMethod($name, $bf).Invoke($script:f, $a) }
function Get($name) { return ,($t.GetField($name, $bf).GetValue($script:f)) }
function SetF($name, $v) { $t.GetField($name, $bf).SetValue($script:f, $v) }
function Prop($name) { return $t.GetProperty($name, $bf).GetValue($script:f) }

$report = New-Object System.Collections.Generic.List[string]
function Measure-Pages($label) {
    $pd = Call 'MakeDoc' $null
    $pc = New-Object System.Drawing.Printing.PreviewPrintController
    $pd.PrintController = $pc
    $pd.Print()
    $pages = $pc.GetPreviewPageInfo()
    $paper = $pd.DefaultPageSettings.PaperSize
    $land = $pd.DefaultPageSettings.Landscape
    $report.Add("== $label : $($pages.Count) page(s), paper '$($paper.PaperName)' landscape=$land")
    $n = 0
    foreach ($pg in $pages) {
        $n++
        $wmm = $pg.PhysicalSize.Width * 0.254; $hmm = $pg.PhysicalSize.Height * 0.254
        $pxmm = 10
        $bmp = New-Object System.Drawing.Bitmap ([int]($wmm * $pxmm)), ([int]($hmm * $pxmm))
        $g = [System.Drawing.Graphics]::FromImage($bmp); $g.Clear([System.Drawing.Color]::White)
        $g.DrawImage($pg.Image, 0, 0, $bmp.Width, $bmp.Height); $g.Dispose()
        $file = Join-Path $OutDir ("{0}-page{1}.png" -f ($label -replace '\W', '_'), $n)
        $bmp.Save($file); Log-Op '' $file 'create test output' 'ok'
        $report.Add(("  page {0}: {1:0.0} x {2:0.0} mm" -f $n, $wmm, $hmm))
        foreach ($k in $colors.Keys | Sort-Object) {
            $c = $colors[$k]; $box = [Meas]::Box($bmp, $c[0], $c[1], $c[2], 18)
            if ($box) { $report.Add(("    {0,-20} x {1,6:0.0}  y {2,6:0.0}  w {3,6:0.0}  h {4,6:0.0} mm" -f $k, ($box[0] / $pxmm), ($box[1] / $pxmm), (($box[2] - $box[0]) / $pxmm), (($box[3] - $box[1]) / $pxmm))) }
        }
        $bmp.Dispose()
    }
}

$script:f = [Activator]::CreateInstance($t)
$f.Show(); [System.Windows.Forms.Application]::DoEvents()
$report.Add("app version: " + $f.Text + "   printer: " + (Get 'printerName') + "   paper: " + (Get 'paper').PaperName)

# A: grid 2x2, A4 portrait, margin 5, gap 3, four photos (fit)
SetF 'landscape' $false; SetF 'cols' 2; SetF 'rows' 2; SetF 'margin' 5.0; SetF 'gap' 3.0; SetF 'cut' $false; SetF 'free' $false
Call 'SyncControls' $null
$f.AddFiles([string[]]@('s1-red-3x2.jpg','s2-green-2x3.jpg','s3-blue-1x1.jpg','s4-orange-3x2.jpg' | ForEach-Object { Join-Path $samples $_ }))
$W = 0.0; $H = 0.0; $args2 = [object[]]@($W, $H); $t.GetMethod('CellSize', $bf).Invoke($f, $args2)
$report.Add(("expected cell: {0:0.00} x {1:0.00} mm" -f $args2[0], $args2[1]))
$report.Add("expected A: red x5.0 y43.2 w98.5 h65.7 | green x108.4 y5.0 w94.7 h142.0 | blue x5.0 y171.8 w98.5 h98.5 | orange x106.5 y188.2 w98.5 h65.7")
Measure-Pages 'A grid 2x2 portrait'

# B: free layout, red photo set to exactly 15 x 10 cm at 1 cm / 1 cm (via the cm fields)
Call 'EnterFree' $null
$items = Get 'items'
$sel = Get 'selList'; $sel.Clear(); $sel.Add($items[0]); SetF 'sel' $items[0]; Call 'UpdateSel' $null
(Get 'nX').Value = 1.0; (Get 'nY').Value = 1.0; (Get 'nWmm').Value = 15.0
$report.Add("expected B: red x10.0 y10.0 w150.0 h100.0")
Measure-Pages 'B free 15x10cm'

# C: landscape, 2 per page (2 columns x 1 row), green rotated 90
SetF 'free' $false; SetF 'landscape' $true; SetF 'cols' 2; SetF 'rows' 1
Call 'SyncControls' $null; Call 'AfterChange' $null
$items = Get 'items'; $sel = Get 'selList'; $sel.Clear(); $sel.Add($items[1]); SetF 'sel' $items[1]; Call 'Act' @('rr')
$report.Add("expected C: page 297x210; cells 142x200; red fit x5.0 y(5+(200-94.7)/2)=57.7 w142 h94.7; green rotated -> 3:2 -> x150 w142 h94.7")
Measure-Pages 'C landscape 2up'

# D: multi-page order: six photos 2x2 portrait -> 2 pages
SetF 'landscape' $false; SetF 'cols' 2; SetF 'rows' 2; Call 'SyncControls' $null
$f.AddFiles([string[]]@('s5-purple-2x3.png','s6-small-300px.jpg' | ForEach-Object { Join-Path $samples $_ }))
$items = Get 'items'; foreach ($p in $items) { $p.Rot = 0 }
Call 'AfterChange' $null
$report.Add("expected D: page1 red,green,blue,orange ; page2 purple (top-left), small (top-right)")
Measure-Pages 'D multipage'

$f.Hide(); $f.Dispose()   # no Close(): avoids writing the settings file
$hashAfter = Get-ChildItem -LiteralPath $samples | ForEach-Object { $_.Name + '=' + (Get-FileHash -LiteralPath $_.FullName).Hash }
$report.Add("sample files unchanged: " + ((Compare-Object $hashBefore $hashAfter) -eq $null))
$rep = Join-Path $OutDir 'baseline-report.txt'
$report | Set-Content -LiteralPath $rep -Encoding UTF8; Log-Op '' $rep 'create test report' 'ok'
$report
