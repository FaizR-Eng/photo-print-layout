# Helper for safe file operations. Dot-source it:  . "<project>\.tools\fileops.ps1"
# Rules: never overwrite, never delete; every operation is logged to file-ops-log.csv.
$Global:PplRoot = Split-Path -Parent $PSScriptRoot   # the project folder (this file lives in .tools\)
$Global:PplLog  = Join-Path $PplRoot 'file-ops-log.csv'

function Log-Op([string]$Source, [string]$Destination, [string]$Action, [string]$Result) {
    $ts = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    $q = { param($s) if ($s) { '"' + ($s -replace '"', '""') + '"' } else { '' } }
    $line = "{0},{1},{2},{3},{4}" -f $ts, (& $q $Source), (& $q $Destination), (& $q $Action), (& $q $Result)
    for ($i = 0; $i -lt 50; $i++) {   # the log may be open briefly in another program: wait up to 5 s
        try { Add-Content -LiteralPath $PplLog -Encoding UTF8 -Value $line -ErrorAction Stop; return } catch { Start-Sleep -Milliseconds 100 }
    }
    Write-Warning "Could not write to the log (file busy): $line"
}

# Copy a file; stops (no overwrite) if the destination exists.
function Safe-Copy([string]$Source, [string]$Destination) {
    if (Test-Path -LiteralPath $Destination) { Log-Op $Source $Destination 'copy' 'SKIPPED: destination exists'; throw "Destination exists: $Destination" }
    $dir = Split-Path -LiteralPath $Destination
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null; Log-Op '' $dir 'create folder' 'ok' }
    Copy-Item -LiteralPath $Source -Destination $Destination
    Log-Op $Source $Destination 'copy' 'ok'
}

# Create a new folder; stops if it already exists.
function Safe-NewDir([string]$Path) {
    if (Test-Path -LiteralPath $Path) { Log-Op '' $Path 'create folder' 'SKIPPED: exists'; throw "Folder exists: $Path" }
    New-Item -ItemType Directory -Path $Path | Out-Null
    Log-Op '' $Path 'create folder' 'ok'
}
