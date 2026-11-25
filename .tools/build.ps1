# Builds src\PhotoPrintLayout.cs into a NEW folder builds\yyyyMMdd-HHmmss\ (never overwrites an older build).
# Also copies the install scripts (if present) next to the .exe so the build folder is self-contained.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'fileops.ps1')
$fw  = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$src = Join-Path $PplRoot 'src'
$builds = Join-Path $PplRoot 'builds'
if (-not (Test-Path -LiteralPath $builds)) { Safe-NewDir $builds }
$out = Join-Path $builds (Get-Date -Format 'yyyyMMdd-HHmmss')
Safe-NewDir $out
$exe = Join-Path $out 'PhotoPrintLayout.exe'
$args = @('/nologo', '/target:winexe', '/optimize+', "/out:$exe", "/win32icon:$(Join-Path $src 'app.ico')",
          '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Xaml.dll', '/r:System.Core.dll',
          "/r:$(Join-Path $fw 'WPF\PresentationCore.dll')", "/r:$(Join-Path $fw 'WPF\WindowsBase.dll')",
          (Join-Path $src 'PhotoPrintLayout.cs'))
& (Join-Path $fw 'csc.exe') @args
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $exe)) { Log-Op (Join-Path $src 'PhotoPrintLayout.cs') $exe 'build' 'FAILED'; throw 'Build failed' }
Log-Op (Join-Path $src 'PhotoPrintLayout.cs') $exe 'build' 'ok'
foreach ($f in 'Install.cmd', 'Uninstall.cmd', 'RightClick-Add.cmd', 'RightClick-Remove.cmd', 'installer.ps1') {
    $s = Join-Path $PplRoot ('installer\' + $f)
    if (Test-Path -LiteralPath $s) { Safe-Copy $s (Join-Path $out $f) }
}
$h = (Get-FileHash -LiteralPath $exe).Hash
Set-Content -LiteralPath (Join-Path $out 'SHA256.txt') -Value "PhotoPrintLayout.exe $h" -Encoding ASCII
Log-Op '' (Join-Path $out 'SHA256.txt') 'create file (build hash)' 'ok'
"BUILD OK: $out"
"version: " + (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
