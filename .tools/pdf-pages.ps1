# Renders every page of a PDF to PNG with Windows' own PDF engine, plus an overview sheet (contact.png).
param([Parameter(Mandatory = $true)][string]$Pdf, [Parameter(Mandatory = $true)][string]$OutDir, [int]$Width = 900)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime, System.Drawing
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }
$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
$null = [Windows.Data.Pdf.PdfDocument, Windows.Data.Pdf, ContentType = WindowsRuntime]
$null = [Windows.Storage.Streams.InMemoryRandomAccessStream, Windows.Storage.Streams, ContentType = WindowsRuntime]
$ext = [System.WindowsRuntimeSystemExtensions].GetMethods()
$asOp = $ext | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
$asAct = $ext | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction' } | Select-Object -First 1
function AwaitOp($op, [Type]$type) { $t = $asOp.MakeGenericMethod($type).Invoke($null, @($op)); $t.Wait(); $t.Result }
function AwaitAct($a) { $t = $asAct.Invoke($null, @($a)); $t.Wait() }
$file = AwaitOp ([Windows.Storage.StorageFile]::GetFileFromPathAsync($Pdf)) ([Windows.Storage.StorageFile])
$doc = AwaitOp ([Windows.Data.Pdf.PdfDocument]::LoadFromFileAsync($file)) ([Windows.Data.Pdf.PdfDocument])
$pages = @()
for ($i = 0; $i -lt $doc.PageCount; $i++) {
    $page = $doc.GetPage($i)
    $ras = New-Object Windows.Storage.Streams.InMemoryRandomAccessStream
    $opt = New-Object Windows.Data.Pdf.PdfPageRenderOptions; $opt.DestinationWidth = $Width
    AwaitAct ($page.RenderToStreamAsync($ras, $opt))
    $s = [System.IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($ras.GetInputStreamAt(0))
    $png = Join-Path $OutDir ("page{0}.png" -f ($i + 1)); $fs = [IO.File]::Create($png); $s.CopyTo($fs); $fs.Close(); $s.Close(); $page.Dispose()
    $pages += $png
}
$tw = 300; $th = 424; $cols = 4; $rows = [math]::Ceiling($pages.Count / $cols)
$sheet = New-Object System.Drawing.Bitmap ($cols * ($tw + 16) + 16), ($rows * ($th + 36) + 16)
$g = [System.Drawing.Graphics]::FromImage($sheet); $g.Clear([System.Drawing.Color]::FromArgb(200, 205, 214)); $g.InterpolationMode = 'HighQualityBicubic'
$font = New-Object System.Drawing.Font 'Segoe UI', 11, ([System.Drawing.FontStyle]::Bold)
for ($i = 0; $i -lt $pages.Count; $i++) {
    $img = [System.Drawing.Image]::FromFile($pages[$i]); $x = 16 + ($i % $cols) * ($tw + 16); $y = 16 + [math]::Floor($i / $cols) * ($th + 36)
    $g.DrawString("Page " + ($i + 1), $font, [System.Drawing.Brushes]::Black, $x, $y); $g.DrawImage($img, $x, $y + 20, $tw, $th); $g.DrawRectangle([System.Drawing.Pens]::Gray, $x, $y + 20, $tw, $th); $img.Dispose()
}
$sheet.Save((Join-Path $OutDir 'contact.png'))
"pages: $($pages.Count)"
