# Photo Print Layout - per-user installer (no admin rights needed).
#   -Action Install     copy the app to %LOCALAPPDATA%\Programs\PhotoPrintLayout, Start menu shortcut,
#                       right-click menu, Send To, and an entry in Settings > Apps (update in place, settings kept)
#   -Action Uninstall   remove the app, shortcuts, right-click menu, Send To and the Apps entry (settings are kept)
#   -Action AddMenu     only add the right-click menu and Send To
#   -Action RemoveMenu  only remove the right-click menu and Send To
#   -DryRun             show what would happen, change nothing
param([ValidateSet('Install', 'Uninstall', 'AddMenu', 'RemoveMenu')][string]$Action = 'Install', [switch]$DryRun)
$ErrorActionPreference = 'Stop'

$AppName   = 'Photo Print Layout'
$Dest      = Join-Path $env:LOCALAPPDATA 'Programs\PhotoPrintLayout'
$DestExe   = Join-Path $Dest 'PhotoPrintLayout.exe'
$Here      = $PSScriptRoot
$StartLnk  = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Photo Print Layout.lnk'
$SendToLnk = Join-Path $env:APPDATA 'Microsoft\Windows\SendTo\Photo Print Layout.lnk'
$UninstKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\PhotoPrintLayout'
$Exts      = 'jpg', 'jpeg', 'jfif', 'png', 'bmp', 'gif', 'tif', 'tiff', 'webp', 'heic', 'heif', 'psd', 'pdf'
$VerbKey   = 'PhotoPrintLayout'
$Files     = 'PhotoPrintLayout.exe', 'installer.ps1', 'Install.cmd', 'Uninstall.cmd', 'RightClick-Add.cmd', 'RightClick-Remove.cmd', 'SHA256.txt'

function Say([string]$t, [string]$c = 'Gray') { Write-Host $t -ForegroundColor $c }
function Do-It([string]$what, [scriptblock]$code) {
    if ($DryRun) { Say "  [dry run] $what" 'DarkCyan' } else { & $code; Say "  $what" }
}

function Stop-IfRunning {
    $running = Get-Process -Name 'PhotoPrintLayout' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path -like "$Dest*" }
    if ($running) {
        Say "Photo Print Layout is open. Please close it and run this again." 'Yellow'
        if (-not $DryRun) { exit 2 }
    }
}

function New-Shortcut([string]$lnk, [string]$target) {
    Do-It "shortcut: $lnk" {
        $dir = Split-Path $lnk
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
        $sh = New-Object -ComObject WScript.Shell
        $s = $sh.CreateShortcut($lnk)
        $s.TargetPath = $target
        $s.WorkingDirectory = Split-Path $target
        $s.IconLocation = "$target,0"
        $s.Description = 'Print several photos per page'
        $s.Save()
    }
}

function Remove-PathIfExists([string]$p) {
    if (Test-Path -LiteralPath $p) { Do-It "remove: $p" { Remove-Item -LiteralPath $p -Recurse -Force } }
}

function Add-Menu([string]$exe) {
    foreach ($e in $Exts) {
        $k = "HKCU:\Software\Classes\SystemFileAssociations\.$e\shell\$VerbKey"
        Do-It "right-click menu for .$e" {
            New-Item -Path "$k\command" -Force | Out-Null
            Set-ItemProperty -LiteralPath $k -Name '(default)' -Value 'Open in Photo Print Layout'
            Set-ItemProperty -LiteralPath $k -Name 'Icon' -Value "`"$exe`",0"
            Set-ItemProperty -LiteralPath $k -Name 'MultiSelectModel' -Value 'Player'
            Set-ItemProperty -LiteralPath "$k\command" -Name '(default)' -Value "`"$exe`" `"%1`""
        }
    }
    New-Shortcut $SendToLnk $exe
}

function Remove-Menu {
    foreach ($e in $Exts) {
        $k = "HKCU:\Software\Classes\SystemFileAssociations\.$e\shell\$VerbKey"
        if (Test-Path -LiteralPath $k) { Do-It "remove right-click menu for .$e" { Remove-Item -LiteralPath $k -Recurse -Force } }
    }
    Remove-PathIfExists $SendToLnk
}

Say ""
Say "$AppName - $Action$(if ($DryRun) { ' (dry run: nothing is changed)' })" 'Cyan'
Say ""

switch ($Action) {
    'Install' {
        $srcExe = Join-Path $Here 'PhotoPrintLayout.exe'
        if (-not (Test-Path -LiteralPath $srcExe)) { Say "PhotoPrintLayout.exe was not found next to this script." 'Red'; exit 1 }
        Stop-IfRunning
        $newVer = (Get-Item -LiteralPath $srcExe).VersionInfo.FileVersion
        if (-not (Test-Path -LiteralPath $Dest)) { Do-It "create folder: $Dest" { New-Item -ItemType Directory -Path $Dest | Out-Null } }
        if (Test-Path -LiteralPath $DestExe) {
            # update in place: move the old files into previous\<version-time>\ (nothing is deleted)
            $oldVer = (Get-Item -LiteralPath $DestExe).VersionInfo.FileVersion
            $bak = Join-Path $Dest ("previous\{0}-{1}" -f $oldVer, (Get-Date -Format 'yyyyMMdd-HHmmss'))
            Say "Updating version $oldVer to $newVer (old files kept in $bak)"
            Do-It "create folder: $bak" { New-Item -ItemType Directory -Path $bak -Force | Out-Null }
            foreach ($f in $Files) {
                $p = Join-Path $Dest $f
                if (Test-Path -LiteralPath $p) { Do-It "keep old $f" { Move-Item -LiteralPath $p -Destination (Join-Path $bak $f) } }
            }
        }
        else { Say "Installing version $newVer" }
        foreach ($f in $Files) {
            $p = Join-Path $Here $f
            if (Test-Path -LiteralPath $p) { Do-It "copy $f" { Copy-Item -LiteralPath $p -Destination (Join-Path $Dest $f) } }
        }
        New-Shortcut $StartLnk $DestExe
        Add-Menu $DestExe
        Do-It "entry in Settings > Apps" {
            New-Item -Path $UninstKey -Force | Out-Null
            $sizeKB = [int]((Get-ChildItem -LiteralPath $Dest -File | Measure-Object Length -Sum).Sum / 1024)
            $vals = @{ DisplayName = $AppName; DisplayVersion = $newVer; Publisher = 'Photo Print Layout (open source)'; DisplayIcon = "$DestExe,0";
                       InstallLocation = $Dest; UninstallString = "cmd.exe /c `"`"$Dest\Uninstall.cmd`"`""; URLInfoAbout = 'https://github.com/FaizR-Eng/photo-print-layout' }
            foreach ($k in $vals.Keys) { Set-ItemProperty -LiteralPath $UninstKey -Name $k -Value $vals[$k] }
            Set-ItemProperty -LiteralPath $UninstKey -Name 'NoModify' -Value 1 -Type DWord
            Set-ItemProperty -LiteralPath $UninstKey -Name 'NoRepair' -Value 1 -Type DWord
            Set-ItemProperty -LiteralPath $UninstKey -Name 'EstimatedSize' -Value $sizeKB -Type DWord
        }
        if (-not $DryRun) {
            $want = (Get-FileHash -LiteralPath $srcExe).Hash; $got = (Get-FileHash -LiteralPath $DestExe).Hash
            if ($want -eq $got) { Say "`nDone. Installed files match the build." 'Green' } else { Say "`nWarning: the installed file does not match the build." 'Red'; exit 3 }
            Say "Start it from the Start menu, or right-click photos > Show more options > Open in Photo Print Layout."
            Say "Your settings were kept: $env:APPDATA\PhotoPrintLayout"
        }
    }
    'Uninstall' {
        Stop-IfRunning
        Remove-Menu
        Remove-PathIfExists $StartLnk
        if (Test-Path -LiteralPath $UninstKey) { Do-It "remove entry in Settings > Apps" { Remove-Item -LiteralPath $UninstKey -Recurse -Force } }
        Remove-PathIfExists $Dest
        if (-not $DryRun) { Say "`nDone. Photo Print Layout was removed. Your settings and saved layouts were kept." 'Green' }
    }
    'AddMenu' {
        $exe = if (Test-Path -LiteralPath $DestExe) { $DestExe } else { Join-Path $Here 'PhotoPrintLayout.exe' }
        if (-not (Test-Path -LiteralPath $exe)) { Say "PhotoPrintLayout.exe not found." 'Red'; exit 1 }
        Say "Using: $exe"
        Add-Menu $exe
        if (-not $DryRun) { Say "`nDone. Right-click photos > Show more options > Open in Photo Print Layout (also in Send to)." 'Green' }
    }
    'RemoveMenu' {
        Remove-Menu
        if (-not $DryRun) { Say "`nDone. The right-click entry and Send To entry were removed." 'Green' }
    }
}
