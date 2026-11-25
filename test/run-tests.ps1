# Automatic tests for Photo Print Layout 1.4.
# Uses only the generated sample images in test\samples, its own settings folder (never yours),
# and never prints to a real printer (Windows print engine preview, built-in PDF, and Microsoft Print to PDF if installed).
param([Parameter(Mandatory = $true)][string]$Exe, [Parameter(Mandatory = $true)][string]$OutDir)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\.tools\fileops.ps1')
Add-Type -AssemblyName System.Drawing, System.Windows.Forms, System.Runtime.WindowsRuntime
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System; using System.Drawing; using System.Drawing.Imaging;
public static class Meas {
    static byte[] Bytes(Bitmap b, out int stride) {
        var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        stride = d.Stride; var buf = new byte[d.Stride * b.Height];
        System.Runtime.InteropServices.Marshal.Copy(d.Scan0, buf, 0, buf.Length); b.UnlockBits(d); return buf;
    }
    public static int[] Box(Bitmap b, int r, int g, int bl, int tol) {
        int st; var buf = Bytes(b, out st);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (int y = 0; y < b.Height; y++) for (int x = 0; x < b.Width; x++) {
            int i = y * st + x * 4;
            if (Math.Abs(buf[i + 2] - r) <= tol && Math.Abs(buf[i + 1] - g) <= tol && Math.Abs(buf[i] - bl) <= tol) {
                if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y; }
        }
        return maxX < 0 ? null : new[] { minX, minY, maxX + 1, maxY + 1 };
    }
    public static bool Dark(Bitmap b, int x, int y) { if (x < 0 || y < 0 || x >= b.Width || y >= b.Height) return false; var c = b.GetPixel(x, y); return c.R < 110 && c.G < 110 && c.B < 110; }
    public static int DarkCount(Bitmap b, int x0, int y0, int x1, int y1) { int n = 0; for (int y = Math.Max(0, y0); y < Math.Min(b.Height, y1); y++) for (int x = Math.Max(0, x0); x < Math.Min(b.Width, x1); x++) { var c = b.GetPixel(x, y); if (c.R < 110 && c.G < 110 && c.B < 110) n++; } return n; }
    // mean absolute difference per channel (0..255) between two same-size images
    public static double Diff(Bitmap a, Bitmap b) {
        if (a.Width != b.Width || a.Height != b.Height) return 999;
        int sa, sb; var x = Bytes(a, out sa); var y = Bytes(b, out sb); long sum = 0; long n = 0;
        for (int r = 0; r < a.Height; r++) for (int c = 0; c < a.Width; c++) { int i = r * sa + c * 4, j = r * sb + c * 4; sum += Math.Abs(x[i] - y[j]) + Math.Abs(x[i + 1] - y[j + 1]) + Math.Abs(x[i + 2] - y[j + 2]); n += 3; }
        return (double)sum / n;
    }
}
'@

$root = $PplRoot
$samples = Join-Path $root 'test\samples'
$COLORS = @{ red = @(220,40,40); green = @(40,170,60); blue = @(40,80,220); orange = @(240,150,30); purple = @(140,60,190); small = @(20,160,170) }
$S = @{ red = 's1-red-3x2.jpg'; green = 's2-green-2x3.jpg'; blue = 's3-blue-1x1.jpg'; orange = 's4-orange-3x2.jpg'; purple = 's5-purple-2x3.png'; small = 's6-small-300px.jpg' }
function SamplePath($k) { Join-Path $samples $S[$k] }

# ---- isolation: own settings folder and own single-instance name
$testSettings = Join-Path $OutDir 'settings'
if (-not (Test-Path -LiteralPath $testSettings)) { New-Item -ItemType Directory -Path $testSettings | Out-Null; Log-Op '' $testSettings 'create folder (test settings)' 'ok' }
$env:PPL_SETTINGS_DIR = $testSettings
$env:PPL_INSTANCE = 'test-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$userSettings = Join-Path $env:APPDATA 'PhotoPrintLayout\settings.txt'
$userSettingsHash = if (Test-Path -LiteralPath $userSettings) { (Get-FileHash -LiteralPath $userSettings).Hash } else { 'none' }
$sampleHash = Get-ChildItem -LiteralPath $samples | ForEach-Object { $_.Name + '=' + (Get-FileHash -LiteralPath $_.FullName).Hash }

$results = New-Object System.Collections.Generic.List[object]
function Check([string]$area, [string]$name, [bool]$ok, [string]$detail) {
    $results.Add([pscustomobject]@{ Area = $area; Test = $name; Result = $(if ($ok) { 'PASS' } else { 'FAIL' }); Detail = $detail })
    Write-Host ("{0,-4} {1,-14} {2}  {3}" -f $(if ($ok) { 'PASS' } else { 'FAIL' }), $area, $name, $detail) -ForegroundColor $(if ($ok) { 'Green' } else { 'Red' })
}
function Skip([string]$area, [string]$name, [string]$why) {
    $results.Add([pscustomobject]@{ Area = $area; Test = $name; Result = 'SKIP'; Detail = $why }); Write-Host ("SKIP {0,-14} {1}  {2}" -f $area, $name, $why) -ForegroundColor Yellow
}
function Near($a, $b, $tol = 0.5) { [math]::Abs($a - $b) -le $tol }

# ---- app access through reflection
$asm = [Reflection.Assembly]::LoadFrom($Exe)
$T = $asm.GetType('PhotoPrintLayout.MainForm')
$bf = [Reflection.BindingFlags]'NonPublic,Public,Instance'
$script:f = $null
function U($a) { if ($a -is [psobject]) { $a.psobject.BaseObject } else { $a } }
function Call($name, $a) { if ($a) { $a = [object[]]@($a | ForEach-Object { U $_ }) }; return $T.GetMethod($name, $bf).Invoke($script:f, $a) }
function Get($name) { return ,($T.GetField($name, $bf).GetValue($script:f)) }
function SetF($name, $v) { $T.GetField($name, $bf).SetValue($script:f, (U $v)) }
function Prop($name) { return $T.GetProperty($name, $bf).GetValue($script:f) }
function New-App([string[]]$files, [scriptblock]$setup) {
    if ($script:f) { $script:f.Hide(); $script:f.Dispose() }   # Dispose, not Close: never writes settings
    $script:f = [Activator]::CreateInstance($T)
    $script:f.Show(); [System.Windows.Forms.Application]::DoEvents()
    SetF 'landscape' $false; SetF 'cols' 2; SetF 'rows' 2; SetF 'margin' 5.0; SetF 'gap' 3.0; SetF 'free' $false
    SetF 'guideMode' 0; SetF 'cut' $false; SetF 'captionMode' 0; SetF 'boxW' 0.0; SetF 'boxH' 0.0; SetF 'previewEdge' $false
    if ($setup) { & $setup }
    Call 'SyncControls' $null
    if ($files) { $script:f.AddFiles([string[]]$files) }
    Call 'AfterChange' $null
}
function Sel([int[]]$idx) { $items = Get 'items'; $l = Get 'selList'; $l.Clear(); foreach ($i in $idx) { $l.Add($items[$i]) }; SetF 'sel' $items[$idx[-1]]; Call 'UpdateSel' $null }

# ---- page rendering helpers (10 px per mm)
$PXMM = 10
function Engine-Pages([string]$label) {
    $pd = Call 'MakeDoc' $null
    $pc = New-Object System.Drawing.Printing.PreviewPrintController
    $pd.PrintController = $pc; $pd.Print()
    $list = @(); $n = 0
    foreach ($pg in $pc.GetPreviewPageInfo()) {
        $n++
        $bmp = New-Object System.Drawing.Bitmap ([int][math]::Round($pg.PhysicalSize.Width * 0.254 * $PXMM)), ([int][math]::Round($pg.PhysicalSize.Height * 0.254 * $PXMM))
        $g = [System.Drawing.Graphics]::FromImage($bmp); $g.Clear([System.Drawing.Color]::White); $g.DrawImage($pg.Image, 0, 0, $bmp.Width, $bmp.Height); $g.Dispose()
        $file = Join-Path $OutDir ("{0}-engine-p{1}.png" -f $label, $n); $bmp.Save($file); Log-Op '' $file 'create test output' 'ok'
        $list += ,$bmp
    }
    $pd.Dispose()
    return ,$list
}
# render a PDF with Windows' own PDF engine
$ext = [System.WindowsRuntimeSystemExtensions].GetMethods()
$asOp = $ext | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
$asAct = $ext | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction' } | Select-Object -First 1
$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
$null = [Windows.Data.Pdf.PdfDocument, Windows.Data.Pdf, ContentType = WindowsRuntime]
$null = [Windows.Storage.Streams.InMemoryRandomAccessStream, Windows.Storage.Streams, ContentType = WindowsRuntime]
function AwaitOp($op, [Type]$type) { $t = $asOp.MakeGenericMethod($type).Invoke($null, @($op)); $t.Wait(); $t.Result }
function AwaitAct($a) { $t = $asAct.Invoke($null, @($a)); $t.Wait() }
function Pdf-Pages([string]$pdf, [string]$label) {
    $file = AwaitOp ([Windows.Storage.StorageFile]::GetFileFromPathAsync($pdf)) ([Windows.Storage.StorageFile])
    $doc = AwaitOp ([Windows.Data.Pdf.PdfDocument]::LoadFromFileAsync($file)) ([Windows.Data.Pdf.PdfDocument])
    $list = @(); $sizes = @()
    for ($i = 0; $i -lt $doc.PageCount; $i++) {
        $page = $doc.GetPage($i)
        $wmm = $page.Size.Width / 96 * 25.4; $hmm = $page.Size.Height / 96 * 25.4
        $sizes += ,@($wmm, $hmm)
        $ras = New-Object Windows.Storage.Streams.InMemoryRandomAccessStream
        $opt = New-Object Windows.Data.Pdf.PdfPageRenderOptions
        $opt.DestinationWidth = [uint32][math]::Round($wmm * $PXMM); $opt.DestinationHeight = [uint32][math]::Round($hmm * $PXMM)
        AwaitAct ($page.RenderToStreamAsync($ras, $opt))
        $s = [System.IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($ras.GetInputStreamAt(0))
        $png = Join-Path $OutDir ("{0}-pdf-p{1}.png" -f $label, ($i + 1))
        $fs = [IO.File]::Create($png); $s.CopyTo($fs); $fs.Close(); $s.Close(); $page.Dispose()
        Log-Op '' $png 'create test output' 'ok'
        # Windows renders at screen scaling (e.g. 175 %); bring the image back to exactly 10 px per mm
        $raw = New-Object System.Drawing.Bitmap $png
        $tw = [int][math]::Round($wmm * $PXMM); $th = [int][math]::Round($hmm * $PXMM)
        $bmp = New-Object System.Drawing.Bitmap $tw, $th
        $g = [System.Drawing.Graphics]::FromImage($bmp); $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'; $g.DrawImage($raw, 0, 0, $tw, $th); $g.Dispose(); $raw.Dispose()
        $list += ,$bmp
    }
    return @{ Pages = $list; Sizes = $sizes }
}
function BoxMM($bmp, $key, $tol = 22) { $col = $COLORS[$key]; $b = [Meas]::Box($bmp, $col[0], $col[1], $col[2], $tol); if (-not $b) { return $null }; return @(($b[0] / $PXMM), ($b[1] / $PXMM), (($b[2] - $b[0]) / $PXMM), (($b[3] - $b[1]) / $PXMM)) }
function BoxText($b) { if ($b) { "x{0:0.0} y{1:0.0} w{2:0.0} h{3:0.0}" -f $b[0], $b[1], $b[2], $b[3] } else { 'not found' } }
function CheckBox([string]$area, [string]$name, $bmp, $key, [double[]]$exp, $tol = 0.5) {
    $b = BoxMM $bmp $key
    $ok = $b -and (Near $b[0] $exp[0] $tol) -and (Near $b[1] $exp[1] $tol) -and (Near $b[2] $exp[2] $tol) -and (Near $b[3] $exp[3] $tol)
    Check $area $name $ok ("measured " + (BoxText $b) + " / expected " + (BoxText $exp))
}

trap { Check 'ERROR' ("script line " + $_.InvocationInfo.ScriptLineNumber) $false ($_.Exception.Message); continue }
Write-Host "`n=== Photo Print Layout tests: $Exe ===`n" -ForegroundColor Cyan
New-App $null $null
Check 'setup' 'version' ((Get-Item -LiteralPath $Exe).VersionInfo.FileVersion -eq '1.5.0.0') ((Get-Item -LiteralPath $Exe).VersionInfo.FileVersion)
Check 'setup' 'uses test settings folder' ($T.GetProperty('SettingsPath', [Reflection.BindingFlags]'NonPublic,Static').GetValue($null) -like "$testSettings*") ''

# ============ 1. Grid layout sizes (print engine and built-in PDF)
New-App @((SamplePath 'red'), (SamplePath 'green'), (SamplePath 'blue'), (SamplePath 'orange')) $null
$expA = @{ red = @(5.0, 43.17, 98.5, 65.67); green = @(108.42, 5.0, 94.67, 142.0); blue = @(5.0, 171.75, 98.5, 98.5); orange = @(106.5, 188.17, 98.5, 65.67) }
$eng = Engine-Pages 'A-grid'
Check 'grid' 'page count' ($eng.Count -eq 1) "$($eng.Count) page(s)"
foreach ($k in 'red', 'green', 'blue', 'orange') { CheckBox 'grid' "print engine: $k" $eng[0] $k $expA[$k] }
$pdfA = Join-Path $OutDir 'A-grid.pdf'; Call 'WritePdf' @($pdfA, 300); Log-Op '' $pdfA 'create test output (PDF)' 'ok'
$pa = Pdf-Pages $pdfA 'A-grid'
Check 'pdf' 'page size A4' ((Near $pa.Sizes[0][0] 210 0.3) -and (Near $pa.Sizes[0][1] 297 0.3)) ("{0:0.0} x {1:0.0} mm" -f $pa.Sizes[0][0], $pa.Sizes[0][1])
foreach ($k in 'red', 'green', 'blue', 'orange') { CheckBox 'pdf' "PDF: $k" $pa.Pages[0] $k $expA[$k] }
$d = [Meas]::Diff($eng[0], $pa.Pages[0])
Check 'preview=print' 'grid page 1 matches' ($d -lt 3.0) ("mean difference {0:0.00} of 255" -f $d)

# ============ 2. Free layout 15 x 10 cm and 10 x 15 cm
New-App @((SamplePath 'red')) { }
Call 'EnterFree' $null; Sel @(0)
(Get 'nX').Value = 1.0; (Get 'nY').Value = 1.0; (Get 'nWmm').Value = 15.0
$eng = Engine-Pages 'B-free'
CheckBox 'free' '15x10 cm at 1/1 cm (engine)' $eng[0] 'red' @(10, 10, 150, 100)
$pdfB = Join-Path $OutDir 'B-free.pdf'; Call 'WritePdf' @($pdfB, 300); Log-Op '' $pdfB 'create test output (PDF)' 'ok'
$pb = Pdf-Pages $pdfB 'B-free'
CheckBox 'free' '15x10 cm at 1/1 cm (PDF)' $pb.Pages[0] 'red' @(10, 10, 150, 100)
Call 'Act' @('rr'); (Get 'nX').Value = 2.0; (Get 'nY').Value = 3.0; (Get 'nHmm').Value = 15.0
$eng = Engine-Pages 'B2-free-rot'
CheckBox 'free' 'rotated 10x15 cm (engine)' $eng[0] 'red' @(20, 30, 100, 150)

# ============ 3. Landscape and multi-page order
New-App @((SamplePath 'red'), (SamplePath 'green'), (SamplePath 'blue'), (SamplePath 'orange')) { SetF 'landscape' $true; SetF 'cols' 2; SetF 'rows' 1 }
Sel @(1); Call 'Act' @('rr'); Call 'AllPhotos' @('fit')
$eng = Engine-Pages 'C-land'
Check 'landscape' 'pages and size' (($eng.Count -eq 2) -and ($eng[0].Width -gt $eng[0].Height)) ("{0} pages, {1}x{2} px" -f $eng.Count, $eng[0].Width, $eng[0].Height)
CheckBox 'landscape' 'red left cell' $eng[0] 'red' @(5, 57.67, 142, 94.67)
CheckBox 'landscape' 'green rotated right cell' $eng[0] 'green' @(150, 57.67, 142, 94.67)
CheckBox 'landscape' 'page 2 orange' $eng[1] 'orange' @(150, 57.67, 142, 94.67)
New-App @((SamplePath 'red'), (SamplePath 'green'), (SamplePath 'blue'), (SamplePath 'orange'), (SamplePath 'purple'), (SamplePath 'small')) $null
$eng = Engine-Pages 'D-multi'
Check 'multi-page' 'two pages' ($eng.Count -eq 2) "$($eng.Count)"
Check 'multi-page' 'page 1 has red..orange, page 2 purple+small' (((BoxMM $eng[0] 'orange') -ne $null) -and ((BoxMM $eng[1] 'purple') -ne $null) -and ((BoxMM $eng[1] 'small') -ne $null) -and ((BoxMM $eng[1] 'red') -eq $null)) ''
$pdfD = Join-Path $OutDir 'D-multi.pdf'; Call 'WritePdf' @($pdfD, 300); $pdd = Pdf-Pages $pdfD 'D-multi'
for ($i = 0; $i -lt 2; $i++) { $d = [Meas]::Diff($eng[$i], $pdd.Pages[$i]); Check 'preview=print' "multi-page page $($i + 1) matches" ($d -lt 3.0) ("mean difference {0:0.00}" -f $d) }

# ============ 4. Standard photo sizes
New-App @((SamplePath 'red'), (SamplePath 'orange')) $null
$cb = Get 'cbSize'; $cb.SelectedIndex = 2   # 10 x 15 cm
Check 'sizes' '10x15: 2 per A4 page' ((Prop 'PerPage') -eq 2) ("per page: " + (Prop 'PerPage'))
$eng = Engine-Pages 'E-10x15'
CheckBox 'sizes' '10x15 photo 1 = 150x100 mm' $eng[0] 'red' @(30, 47.0, 150, 100)
CheckBox 'sizes' '10x15 photo 2 = 150x100 mm' $eng[0] 'orange' @(30, 150.0, 150, 100)
$pdfE = Join-Path $OutDir 'E-10x15.pdf'; Call 'WritePdf' @($pdfE, 300); $pe = Pdf-Pages $pdfE 'E-10x15'
CheckBox 'sizes' '10x15 in PDF = 150x100 mm' $pe.Pages[0] 'red' @(30, 47.0, 150, 100)
New-App @((SamplePath 'blue')) $null
$cb = Get 'cbSize'; $cb.SelectedIndex = 9   # passport 3.5 x 4.5
Check 'sizes' 'passport: 30 per A4 page' ((Prop 'PerPage') -eq 30) ("per page: " + (Prop 'PerPage'))
$eng = Engine-Pages 'F-passport'
CheckBox 'sizes' 'passport photo = 35x45 mm' $eng[0] 'blue' @(11.5, 6, 35, 45)
Sel @(0); Call 'Act' @('fillpage'); Check 'sizes' 'repeat to fill page -> 30 copies' ((Get 'items').Count -eq 30) ("photos: " + (Get 'items').Count)
Call 'Undo' $null; Call 'Undo' $null
Check 'undo' 'undo restores size choice' (((Get 'boxW') -eq 0) -and ((Get 'items').Count -eq 1)) ("boxW=" + (Get 'boxW') + " photos=" + (Get 'items').Count)

# ============ 5. Crop marks, cut lines, captions
New-App @((SamplePath 'red')) { SetF 'cols' 1; SetF 'rows' 1; SetF 'margin' 20.0; SetF 'guideMode' 2 }
$eng = Engine-Pages 'G-marks'
$rb = BoxMM $eng[0] 'red'
$x1 = [int]($rb[0] * $PXMM); $y1 = [int]($rb[1] * $PXMM)
$tick = [Meas]::DarkCount($eng[0], $x1 - 50, $y1 - 2, $x1 - 20, $y1 + 3)
$gapPx = [Meas]::DarkCount($eng[0], $x1 - 12, $y1 - 2, $x1 - 2, $y1 + 3)
Check 'crop marks' 'corner tick drawn outside photo, gap kept' (($tick -gt 20) -and ($gapPx -eq 0)) "tick pixels $tick, gap pixels $gapPx"
New-App @((SamplePath 'red'), (SamplePath 'orange')) { SetF 'guideMode' 1 }
$eng = Engine-Pages 'G2-cutlines'
$lineDark = [Meas]::DarkCount($eng[0], 40, 48, 1000, 52) + [Meas]::DarkCount($eng[0], 40, 50, 1000, 51)
$cutGrey = 0; for ($x = 100; $x -lt 900; $x += 50) { $c = $eng[0].GetPixel($x, 50); if ($c.R -lt 230 -and [math]::Abs($c.R - $c.B) -lt 15) { $cutGrey++ } }
Check 'cut lines' 'grey line along top of first cell' ($cutGrey -ge 12) "$cutGrey of 16 samples grey"
New-App @((SamplePath 'red'), (SamplePath 'orange')) { SetF 'captionMode' 1; SetF 'captionPt' 9.0 }
$capH = 9 * 0.3528 * 1.6
$eng = Engine-Pages 'H-captions'
CheckBox 'captions' 'photo shrinks to make room for caption' $eng[0] 'red' @(5, (5 + (142 - $capH - 65.67) / 2), 98.5, 65.67)
$cellBottom = 5 + 142 - $capH
$txt = [Meas]::DarkCount($eng[0], 50, [int]($cellBottom * $PXMM), 1000, [int](($cellBottom + $capH) * $PXMM))
Check 'captions' 'file name text printed under photo' ($txt -gt 50) "$txt dark pixels in caption band"
SetF 'captionMode' 2; Sel @(0); (Get 'txtCaption').Text = 'Hello'; Call 'AfterChange' $null
Check 'captions' 'own text stored on photo' ((Get 'items')[0].Caption -eq 'Hello') ''

# ============ 6. Low resolution, printer edge, margin warning
New-App @((SamplePath 'small'), (SamplePath 'red')) { SetF 'cols' 1; SetF 'rows' 2 }
$dSmall = Call 'EffDpi' @(0); $dBig = Call 'EffDpi' @(1)
Check 'low-res' 'small photo detected (< 100 dpi)' ($dSmall -lt 100) ("{0:0} dpi" -f $dSmall)
Check 'low-res' 'big photo not flagged (>= 150 dpi)' ($dBig -ge 150) ("{0:0} dpi" -f $dBig)
Call 'RefreshHardMargins' $null
$hm = @((Get 'hmL'), (Get 'hmT'), (Get 'hmR'), (Get 'hmB'))
$pr = Get 'printerName'
if ($pr -like 'EPSON*') { Check 'printer edge' "edge read from $pr" ((($hm | Measure-Object -Maximum).Maximum -gt 1) -and (($hm | Measure-Object -Maximum).Maximum -lt 10)) ("L{0:0.0} T{1:0.0} R{2:0.0} B{3:0.0} mm" -f $hm[0], $hm[1], $hm[2], $hm[3]) }
else { Skip 'printer edge' 'edge read from printer' "default printer is $pr" }
SetF 'margin' 0.0; Call 'UpdateMarginWarning' $null
$warn = (Get 'lblMarginWarn').Visible
if ($pr -like 'EPSON*') { Check 'printer edge' 'warning shown when margin 0 mm' $warn '' } else { Skip 'printer edge' 'warning shown when margin 0 mm' 'needs a printer with an edge' }

# ============ 7. Print window (live preview, orientation follows)
New-App @((SamplePath 'red'), (SamplePath 'green'), (SamplePath 'blue'), (SamplePath 'orange'), (SamplePath 'purple')) $null
$pwType = $asm.GetType('PhotoPrintLayout.MainForm+PrintWindow')
$pw = [Activator]::CreateInstance($pwType, [object[]]@($script:f))
$pw.Show(); [System.Windows.Forms.Application]::DoEvents()
$pbf = [Reflection.BindingFlags]'NonPublic,Instance'
$pwType.GetMethod('Regenerate', $pbf).Invoke($pw, $null)
$pv = $pwType.GetField('pv', $pbf).GetValue($pw); $pdw = $pwType.GetField('pd', $pbf).GetValue($pw)
Check 'print window' 'preview shows the document' ($pv.Document -ne $null -and $pdw -ne $null) ''
Check 'print window' 'preview uses app paper' ($pdw.DefaultPageSettings.PaperSize.PaperName -eq (Get 'paper').PaperName) ($pdw.DefaultPageSettings.PaperSize.PaperName)
$rbL = $pwType.GetField('rbL', $pbf).GetValue($pw); $rbL.Checked = $true; [System.Windows.Forms.Application]::DoEvents()
$pdw = $pwType.GetField('pd', $pbf).GetValue($pw)
Check 'print window' 'switch to landscape updates app and preview' ((Get 'landscape') -and $pdw.DefaultPageSettings.Landscape) ("app landscape=" + (Get 'landscape') + ", preview landscape=" + $pdw.DefaultPageSettings.Landscape)
$info = $pwType.GetField('lblInfo', $pbf).GetValue($pw).Text
Check 'print window' 'info line shows pages' ($info -match 'page') ($info.Split("`n")[0])
$pw.Hide(); $pw.Dispose()
# real Microsoft Print to PDF output (only if that printer is installed)
$pdfPrinter = [System.Drawing.Printing.PrinterSettings]::InstalledPrinters | Where-Object { $_ -eq 'Microsoft Print to PDF' }
if ($pdfPrinter) {
    New-App @((SamplePath 'red'), (SamplePath 'green'), (SamplePath 'blue'), (SamplePath 'orange')) $null
    $pdP = Call 'MakeDoc' $null
    $pdP.PrinterSettings.PrinterName = 'Microsoft Print to PDF'
    Call 'ApplyPage' @($pdP)
    $out = Join-Path $OutDir 'I-printer.pdf'
    $pdP.PrinterSettings.PrintToFile = $true; $pdP.PrinterSettings.PrintFileName = $out
    $pdP.PrintController = New-Object System.Drawing.Printing.StandardPrintController
    $pdP.Print(); $pdP.Dispose(); Start-Sleep -Milliseconds 800; Log-Op '' $out 'create test output (printed PDF)' 'ok'
    $pp = Pdf-Pages $out 'I-printer'
    foreach ($k in 'red', 'green', 'blue', 'orange') { CheckBox 'printer PDF' "Microsoft Print to PDF: $k" $pp.Pages[0] $k $expA[$k] }
    $eng = Engine-Pages 'I-engine'
    $d = [Meas]::Diff($eng[0], $pp.Pages[0]); Check 'preview=print' 'preview matches printed PDF' ($d -lt 4.0) ("mean difference {0:0.00}" -f $d)
} else { Skip 'printer PDF' 'print through Microsoft Print to PDF' 'printer not installed (run Add-PDF-Printer.cmd as administrator)' }

# ============ 8. Saved layout keeps the new settings
New-App @((SamplePath 'red'), (SamplePath 'blue')) { SetF 'captionMode' 2; SetF 'guideMode' 2 }
$cb = Get 'cbSize'; $cb.SelectedIndex = 3   # 13 x 18
(Get 'items')[0].Caption = 'First'
$lay = Join-Path $OutDir 'roundtrip.photolayout'
$ci = [Globalization.CultureInfo]::InvariantCulture
# write the file the same way the app's Save does (Save opens a dialog), then reopen it with the app
$items = Get 'items'
$lines = @("PhotoPrintLayout`t1", ("page`t{0}`t0`t2`t2`t5`t3`t0`t0`t{1}`t{2}`t{3}`t{4}`t{5}" -f (Get 'paper').PaperName, (Get 'boxW').ToString('R', $ci), (Get 'boxH').ToString('R', $ci), (Get 'guideMode'), (Get 'captionMode'), (Get 'captionPt').ToString('R', $ci)))
foreach ($p in $items) { $lines += ("photo`t{0}`t{1}`t1`t1`t0`t0`t0`t0`t0`t0`t0`t{2}" -f $p.Path, $p.Rot, $p.Caption) }
[IO.File]::WriteAllLines($lay, [string[]]$lines); Log-Op '' $lay 'create test output (layout)' 'ok'
New-App $null $null
Call 'LoadLayoutFile' @($lay)
Check 'layout file' 'size, guides, captions and caption text restored' (((Get 'boxW') -eq 130) -and ((Get 'guideMode') -eq 2) -and ((Get 'captionMode') -eq 2) -and ((Get 'items')[0].Caption -eq 'First') -and ((Get 'items').Count -eq 2)) ("box {0}x{1}, guides {2}, captions {3}, text '{4}'" -f (Get 'boxW'), (Get 'boxH'), (Get 'guideMode'), (Get 'captionMode'), (Get 'items')[0].Caption)
Check 'recent' 'opened layout added to Recent' ((Get 'recentLayouts')[0] -eq $lay) ''
$script:f.Hide(); $script:f.Dispose(); $script:f = $null

# ============ 8b. PDF pages as photos, gap measurements, exact gap (v1.5)
New-App @((SamplePath 'red')) { SetF 'cols' 1; SetF 'rows' 1; SetF 'margin' 0.0 }
$pdf1 = Join-Path $OutDir 'input-one-page.pdf'; Call 'WritePdf' @($pdf1, 150); Log-Op '' $pdf1 'create test input (PDF)' 'ok'
New-App @((SamplePath 'red'), (SamplePath 'green'), (SamplePath 'blue'), (SamplePath 'orange'), (SamplePath 'purple')) $null
$pdf2 = Join-Path $OutDir 'input-two-pages.pdf'; Call 'WritePdf' @($pdf2, 150); Log-Op '' $pdf2 'create test input (PDF)' 'ok'
New-App @($pdf1) $null
$it = Get 'items'
$ratioOk = ($it.Count -eq 1) -and ([math]::Abs($it[0].W / $it[0].H - 210.06 / 296.93) -lt 0.01)
Check 'pdf input' 'one-page PDF opens as a photo (A4 shape)' ($ratioOk -and $it[0].PdfPage -eq 0) ("photos {0}, page index {1}, {2} x {3} px" -f $it.Count, $it[0].PdfPage, $it[0].W, $it[0].H)
$eng = Engine-Pages 'J-pdf-input'
Check 'pdf input' 'PDF page content is printed' ($null -ne (BoxMM $eng[0] 'red')) (BoxText (BoxMM $eng[0] 'red'))
New-App @($pdf2) $null
$it = Get 'items'
Check 'pdf input' 'two-page PDF opens as two photos' (($it.Count -eq 2) -and ($it[0].PdfPage -eq 0) -and ($it[1].PdfPage -eq 1)) ("{0}: {1}" -f $it.Count, (($it | ForEach-Object Name) -join ', '))
$eng = Engine-Pages 'J2-pdf-two'
Check 'pdf input' 'page 2 of the PDF shows its photo (purple)' ($null -ne (BoxMM $eng[0] 'purple')) ''
New-App @((SamplePath 'red'), (SamplePath 'orange'), (SamplePath 'blue')) $null
Call 'EnterFree' $null
$it = Get 'items'
$it[0].FX = 10; $it[0].FY = 20; $it[0].FW = 50; $it[1].FX = 72.5; $it[1].FY = 20; $it[1].FW = 50; $it[2].FX = 10; $it[2].FY = 100; $it[2].FW = 40
foreach ($q in $it) { $q.FPage = 0 }
Call 'AfterChange' $null
$gaps = @(Call 'FindGaps' @(0))
$gR = $gaps | Where-Object { $_.Dir -eq 0 } | Select-Object -First 1
$gL = $gaps | Where-Object { $_.Dir -eq 1 } | Select-Object -First 1
$gB = $gaps | Where-Object { $_.Dir -eq 3 } | Select-Object -First 1
Check 'gaps' 'gap to the photo on the right = 12.5 mm' ($gR -and -not $gR.Edge -and [math]::Abs($gR.Mm - 12.5) -lt 0.01) ("{0:0.00} mm" -f $gR.Mm)
Check 'gaps' 'gap to the left paper edge = 10.0 mm' ($gL -and $gL.Edge -and [math]::Abs($gL.Mm - 10) -lt 0.01) ("{0:0.00} mm" -f $gL.Mm)
Check 'gaps' 'gap to the photo below = 46.7 mm' ($gB -and -not $gB.Edge -and [math]::Abs($gB.Mm - (100 - (20 + 50 * 2 / 3))) -lt 0.01) ("{0:0.00} mm" -f $gB.Mm)
$it[2].FX = 140; $it[2].FY = 20
Sel @(0, 1, 2); SetF 'alignGap' 4.0; Call 'Align' @('disth')
$it = Get 'items'
$g1 = $it[1].FX - ($it[0].FX + $it[0].FW); $g2 = $it[2].FX - ($it[1].FX + $it[1].FW)
Check 'gaps' 'Space evenly with exact gap 4 mm' (([math]::Abs($g1 - 4) -lt 0.01) -and ([math]::Abs($g2 - 4) -lt 0.01)) ("gaps {0:0.00} / {1:0.00} mm" -f $g1, $g2)
$gaps = @(Call 'FindGaps' @(1)); $gR = $gaps | Where-Object { $_.Dir -eq 0 } | Select-Object -First 1
Check 'gaps' 'measurement agrees after arranging (4.0 mm)' ($gR -and [math]::Abs($gR.Mm - 4) -lt 0.01) ("{0:0.00} mm" -f $gR.Mm)
$script:f.Hide(); $script:f.Dispose(); $script:f = $null
# ============ 9. Right-click / Send To behaviour with real processes (one window gathers all files)
function Wait-Report($file, $want, $sec = 20) { $t0 = Get-Date; while (((Get-Date) - $t0).TotalSeconds -lt $sec) { if (Test-Path -LiteralPath $file) { $c = @(Get-Content -LiteralPath $file -ErrorAction SilentlyContinue); if ($c.Count -ge $want) { return $c } }; Start-Sleep -Milliseconds 250 }; if (Test-Path -LiteralPath $file) { return @(Get-Content -LiteralPath $file) } else { return @() } }
function Stop-TestApps { Get-Process -Name 'PhotoPrintLayout' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Exe } | Stop-Process -Force; Start-Sleep -Milliseconds 400 }
$five = @('red', 'green', 'blue', 'orange', 'purple' | ForEach-Object { SamplePath $_ })
foreach ($case in @(@{ Name = 'one file (right-click)'; Mode = 'each'; Files = @($five[0]) },
                     @{ Name = 'five files at once (right-click, one copy per file)'; Mode = 'each'; Files = $five },
                     @{ Name = 'five files in one command (Send To)'; Mode = 'one'; Files = $five })) {
    Stop-TestApps
    $env:PPL_INSTANCE = 'test-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
    $rep = Join-Path $OutDir ('report-' + $env:PPL_INSTANCE + '.txt'); $env:PPL_TEST_REPORT = $rep
    if ($case.Mode -eq 'each') { foreach ($fl in $case.Files) { Start-Process -FilePath $Exe -ArgumentList ('"' + $fl + '"') } }
    else { Start-Process -FilePath $Exe -ArgumentList (($case.Files | ForEach-Object { '"' + $_ + '"' }) -join ' ') }
    $got = Wait-Report $rep $case.Files.Count
    Start-Sleep -Milliseconds 1500
    $procs = @(Get-Process -Name 'PhotoPrintLayout' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Exe })
    $ok = ($got.Count -eq $case.Files.Count) -and ($procs.Count -eq 1) -and (@($got | Sort-Object) -join '|') -eq (@($case.Files | Sort-Object) -join '|')
    Check 'right-click' $case.Name $ok ("{0} photo(s) in one window, {1} app window(s) left" -f $got.Count, $procs.Count)
    if (Test-Path -LiteralPath $rep) { Log-Op '' $rep 'create test output (report)' 'ok' }
}
Stop-TestApps
Remove-Item Env:\PPL_TEST_REPORT -ErrorAction SilentlyContinue

# ============ 10. Nothing of yours changed
$sampleHash2 = Get-ChildItem -LiteralPath $samples | ForEach-Object { $_.Name + '=' + (Get-FileHash -LiteralPath $_.FullName).Hash }
Check 'safety' 'sample photos unchanged (SHA-256)' ($null -eq (Compare-Object $sampleHash $sampleHash2)) "$($sampleHash.Count) files"
$us2 = if (Test-Path -LiteralPath $userSettings) { (Get-FileHash -LiteralPath $userSettings).Hash } else { 'none' }
Check 'safety' 'your settings file unchanged' ($us2 -eq $userSettingsHash) ''

$csv = Join-Path $OutDir 'test-results.csv'
$results | Export-Csv -LiteralPath $csv -NoTypeInformation -Encoding UTF8; Log-Op '' $csv 'create test report' 'ok'
$pass = @($results | Where-Object Result -eq 'PASS').Count; $fail = @($results | Where-Object Result -eq 'FAIL').Count; $skip = @($results | Where-Object Result -eq 'SKIP').Count
Write-Host ("`nTOTAL: {0} pass, {1} fail, {2} skip" -f $pass, $fail, $skip) -ForegroundColor $(if ($fail) { 'Red' } else { 'Green' })
