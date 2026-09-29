#requires -Version 5.1
<#
.SYNOPSIS
    Sichert die Produktiv-Datenbank der NOOSE-Website: Dump auf dem Server + Download auf den PC.

.DESCRIPTION
    Ein-Befehl-Backup: per SSH auf dem Server einen konsistenten Dump per
    "docker exec noose-db mariadb-dump" erzeugen (--single-transaction, inkl. Routinen/Events), gzip-komprimiert unter /root/backups ablegen
    (Server-Kopie), das Ergebnis per scp auf den PC herunterladen (PC-Kopie) und die Groessen
    beider Kopien vergleichen. Alte manuelle Server-Dumps (<db>-<datum>_<zeit>) aelter als -RetentionDays werden aufgeraeumt,
    die taeglichen Cron-Dumps von /opt/noose/backup.sh bleiben davon unberuehrt;
    die PC-Kopien (Offsite) bleiben ALLE erhalten.

    Nutzt dieselbe robuste ssh/scp-Aufloesung wie deploy.ps1 (PATH-unabhaengig, auch aus 32-bit
    PowerShell). Erfordert einen hinterlegten SSH-Key; sonst fragt ssh/scp nach dem Passwort.

.EXAMPLE
    .\backup-db.ps1
        Standard-Backup der Prod-DB 'noose' -> Server /root/backups + PC %USERPROFILE%\NOOSE-Backups.

.EXAMPLE
    .\backup-db.ps1 -RetentionDays 60 -NoPause
        Serverseitig 60 Tage aufbewahren, ohne "Enter zum Schliessen" am Ende (Terminal/CI).

.EXAMPLE
    .\backup-db.ps1 -LocalDir D:\Backups\NOOSE
        PC-Kopie in einen anderen Ordner legen.

.NOTES
    Am besten aus einer normalen (64-bit) Windows PowerShell starten (siehe deploy.ps1 -> Resolve-Exe).
    Restore einer Kopie:  gunzip < noose-<datum>.sql.gz | docker exec -i noose-db mariadb noose   (auf dem Server).
    Der taegliche Server-Backup-Cron (/opt/noose/backup.sh) laeuft unabhaengig davon; dieses Skript ist der manuelle Dump + Download.
#>

[CmdletBinding()]
param(
    [string]$Server        = "root@62.169.28.155",
    [string]$Database      = "noose",
    [string]$RemoteDir     = "/root/backups",
    [string]$LocalDir      = (Join-Path $env:USERPROFILE "NOOSE-Backups"),
    [int]$RetentionDays    = 30,
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
$exitCode = 0

function Invoke-Step {
    param([string]$Label, [scriptblock]$Action)
    Write-Host "==> $Label" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "Schritt fehlgeschlagen: $Label (Exit $LASTEXITCODE)" }
}

# Resolve ssh/scp PATH-independently (works even from a 32-bit PowerShell where System32 is WOW64-redirected).
function Resolve-Exe {
    param([string]$Name)
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidates = @(
        (Join-Path $env:WINDIR "System32\OpenSSH\$Name.exe"),   # 64-bit process
        (Join-Path $env:WINDIR "Sysnative\OpenSSH\$Name.exe"),  # from 32-bit process -> real System32
        (Join-Path $env:ProgramFiles "Git\usr\bin\$Name.exe")   # Git for Windows fallback
    )
    foreach ($p in $candidates) {
        if ($p -and (Test-Path $p)) { return $p }
    }
    throw "$Name nicht gefunden. Tipp: backup-db.ps1 in der normalen (64-bit) Windows PowerShell starten, oder OpenSSH-Client installieren (Einstellungen > Apps > Optionale Features > 'OpenSSH-Client')."
}

try {
    $ssh = Resolve-Exe 'ssh'
    $scp = Resolve-Exe 'scp'
    # never hang silently: connect timeout + auto-accept new host keys (no yes/no prompt).
    $sshOpts = @('-o', 'ConnectTimeout=15', '-o', 'StrictHostKeyChecking=accept-new')

    # 1) Remote dump. Placeholders (__X__) get filled with params; shell $vars stay literal
    #    because the here-string is single-quoted (no PowerShell expansion).
    $remote = @'
set -e
mkdir -p '__REMOTEDIR__'
f="__REMOTEDIR__/__DB__-$(date +%F_%H%M%S).sql.gz"
docker exec noose-db mariadb-dump --single-transaction --quick --routines --events '__DB__' | gzip > "$f"
integ=OK
gzip -t "$f" || integ=BAD
case "$(zcat "$f" | tail -1)" in *"Dump completed"*) : ;; *) integ=BAD ;; esac
echo "FILE=$f"
echo "SIZE=$(stat -c %s "$f")"
echo "INTEGRITY=$integ"
find '__REMOTEDIR__' -maxdepth 1 -name '__DB__-*_*.sql.gz' -type f -mtime +__RETENTION__ -delete 2>/dev/null || true
'@
    $remote = $remote.Replace('__REMOTEDIR__', $RemoteDir).Replace('__DB__', $Database).Replace('__RETENTION__', "$RetentionDays")

    Write-Host "==> Erzeuge Dump auf $Server ($RemoteDir/$Database-<datum>.sql.gz)" -ForegroundColor Cyan
    # Copy the dump script to the server and run it there - transparent (no base64/obfuscation that
    # trips AV/agent guards) and sidesteps Windows PowerShell argv quote-mangling. Write it without a
    # BOM and with LF line endings so the remote bash reads it cleanly.
    $localScript = Join-Path ([System.IO.Path]::GetTempPath()) ("noose-dump-{0}.sh" -f [guid]::NewGuid())
    [System.IO.File]::WriteAllText($localScript, ($remote -replace "`r", ""), (New-Object System.Text.UTF8Encoding($false)))
    try {
        Invoke-Step "Uebertrage Dump-Skript" { & $scp @sshOpts $localScript "${Server}:/tmp/noose-dump.sh" }
    } finally {
        Remove-Item $localScript -Force -ErrorAction SilentlyContinue
    }
    # keep bash's exit code (not rm's) so a failed dump is detected.
    $out = & $ssh @sshOpts $Server 'bash /tmp/noose-dump.sh; rc=$?; rm -f /tmp/noose-dump.sh; exit $rc'
    if ($LASTEXITCODE -ne 0) { throw "Remote-Dump fehlgeschlagen (ssh Exit $LASTEXITCODE).`nAusgabe: $($out -join "`n")" }

    $remoteFile = ($out | Select-String '^FILE=')      -replace '^FILE=', ''
    $remoteSize = ($out | Select-String '^SIZE=')      -replace '^SIZE=', ''
    $integrity  = ($out | Select-String '^INTEGRITY=') -replace '^INTEGRITY=', ''
    if (-not $remoteFile) { throw "Konnte den Dump-Pfad nicht aus der Server-Ausgabe lesen.`nAusgabe: $($out -join "`n")" }
    if ($integrity -ne 'OK') { throw "Dump-Integritaetspruefung fehlgeschlagen ($remoteFile). Backup NICHT vertrauenswuerdig." }
    $remoteSize = [int64]$remoteSize
    Write-Host ("    Server-Kopie: {0} ({1:N0} Bytes, Integritaet OK)" -f $remoteFile, $remoteSize) -ForegroundColor DarkGray

    # 2) Download to the PC (second copy).
    New-Item -ItemType Directory -Force -Path $LocalDir | Out-Null
    $leaf      = Split-Path $remoteFile -Leaf
    $localFile = Join-Path $LocalDir $leaf
    Invoke-Step "Lade Kopie auf den PC ($LocalDir)" { & $scp @sshOpts "${Server}:$remoteFile" $LocalDir }

    # 3) Verify the PC copy matches the server byte-for-byte in size.
    if (-not (Test-Path $localFile)) { throw "Download fehlgeschlagen: $localFile nicht gefunden." }
    $localSize = (Get-Item $localFile).Length
    if ($localSize -ne $remoteSize) {
        throw "Groessen weichen ab: Server $remoteSize Bytes vs. PC $localSize Bytes. Download unvollstaendig."
    }

    Write-Host ""
    Write-Host "Backup erfolgreich." -ForegroundColor Green
    Write-Host ("  Server:  {0}" -f $remoteFile) -ForegroundColor Green
    Write-Host ("  PC:      {0}" -f $localFile) -ForegroundColor Green
    Write-Host ("  Groesse: {0:N0} Bytes  (Server-Aufbewahrung: {1} Tage)" -f $localSize, $RetentionDays) -ForegroundColor Green
}
catch {
    $exitCode = 1
    Write-Host ""
    Write-Host "============================================" -ForegroundColor Red
    Write-Host "  BACKUP FEHLGESCHLAGEN" -ForegroundColor Red
    Write-Host "============================================" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    if ($_.ScriptStackTrace) {
        Write-Host ""
        Write-Host $_.ScriptStackTrace -ForegroundColor DarkGray
    }
}
finally {
    if (-not $NoPause) {
        Write-Host ""
        $null = Read-Host "Enter druecken zum Schliessen"
    }
}

exit $exitCode
