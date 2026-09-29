#requires -Version 5.1
<#
.SYNOPSIS
    Rollt die NOOSE-Website (Prod oder Demo) als Container auf dem VPS aus.

.DESCRIPTION
    Das Image baut GitHub Actions bei jedem Push auf master (.github/workflows/image.yml) und legt
    es unter ghcr.io/nullradix-dev/noose-website:<commit-sha> ab. Dieses Skript baut NICHTS selbst:
      0. Tag bestimmen (Standard: aktueller Commit von origin/master)
      1. Prod-Schutz: die Prod-Env darf kein Demo__AutoSetup=true enthalten (fail-closed)
      2. deploy/compose.yml nach /opt/noose/compose.yml hochladen und pruefen
      3. Image ziehen, Tag in /opt/noose/.env setzen, Container neu erstellen. Compose stoppt den
         alten Container, bevor der neue startet -> nie zwei Instanzen (BackgroundServices!).
      4. Health-Check auf 127.0.0.1:5000 (Prod) bzw. :5001 (Demo)

    Rollback: dasselbe Skript mit -Tag <aelterer-commit> ausfuehren. Die Datenbank (Dienst db)
    wird von diesem Skript nie neu gestartet; Aenderungen an ihrer Definition in compose.yml
    muessen bewusst auf dem Server mit "docker compose up -d db" angewendet werden.

.EXAMPLE
    .\deploy.ps1
        Prod auf den aktuellen Stand von origin/master bringen.

.EXAMPLE
    .\deploy.ps1 -Target demo
        Demo-Instanz (demo.noose.info) auf den aktuellen Stand von origin/master bringen.

.EXAMPLE
    .\deploy.ps1 -Tag 323a5e7
        Bestimmten Commit ausrollen (z. B. Rollback). Kurze SHAs werden lokal aufgeloest.

.NOTES
    Voraussetzungen: SSH-Key fuer root@62.169.28.155, auf dem Server einmalig "docker login ghcr.io"
    (Classic-PAT mit read:packages, siehe docs/DEPLOYMENT.md). Die GitHub Action fuer den Commit muss
    fertig sein, sonst bricht Schritt 3 mit "Image nicht gefunden" ab.
#>

[CmdletBinding()]
param(
    [string]$Server = "root@62.169.28.155",
    [ValidateSet("prod", "demo")]
    [string]$Target = "prod",
    [string]$Tag,
    [switch]$NoPause
)

$ErrorActionPreference = "Stop"
$exitCode = 0
$image = "ghcr.io/nullradix-dev/noose-website"

function Invoke-Step {
    param([string]$Label, [scriptblock]$Action)
    Write-Host "==> $Label" -ForegroundColor Cyan
    & $Action
    if ($LASTEXITCODE -ne 0) { throw "Schritt fehlgeschlagen: $Label (Exit $LASTEXITCODE)" }
}

# Findet ssh/scp robust - PATH-unabhaengig und auch aus einem 32-bit-PowerShell heraus, wo
# C:\Windows\System32 per WOW64 auf SysWOW64 umgeleitet wird und die 64-bit-OpenSSH-Exe dort fehlt.
function Resolve-Exe {
    param([string]$Name)
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidates = @(
        (Join-Path $env:WINDIR "System32\OpenSSH\$Name.exe"),   # 64-bit-Prozess
        (Join-Path $env:WINDIR "Sysnative\OpenSSH\$Name.exe"),  # aus 32-bit-Prozess -> echtes System32
        (Join-Path $env:ProgramFiles "Git\usr\bin\$Name.exe")   # Git for Windows als Fallback
    )
    foreach ($p in $candidates) {
        if ($p -and (Test-Path $p)) { return $p }
    }
    throw "$Name nicht gefunden. Tipp: deploy.ps1 in der normalen (64-bit) Windows PowerShell starten, oder OpenSSH-Client installieren (Einstellungen > Apps > Optionale Features > 'OpenSSH-Client')."
}

try {
    # das Skript liegt in scripts\, das Repo eine Ebene darueber
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $compose = Join-Path $repoRoot "deploy\compose.yml"
    if (-not (Test-Path $compose)) { throw "deploy\compose.yml nicht gefunden: $compose" }

    if ($Target -eq "prod") { $service = "noose";      $tagVar = "NOOSE_TAG"; $port = 5000; $url = "https://noose.info" }
    else                    { $service = "noose-demo"; $tagVar = "DEMO_TAG";  $port = 5001; $url = "https://demo.noose.info" }

    $scp = Resolve-Exe 'scp'
    $ssh = Resolve-Exe 'ssh'
    # nie still haengen: Verbindungs-Timeout + neue Host-Keys automatisch akzeptieren (kein yes/no-Prompt).
    $sshOpts = @('-o', 'ConnectTimeout=10', '-o', 'StrictHostKeyChecking=accept-new')

    # 0) Tag bestimmen. Ohne -Tag: aktueller Commit von origin/master. Kurze SHAs lokal aufloesen,
    #    weil GHCR nur die volle SHA als Tag kennt.
    if (-not $Tag) {
        Invoke-Step "Hole origin/master" { git -C $repoRoot fetch origin master --quiet }
        $Tag = "$(git -C $repoRoot rev-parse origin/master)".Trim()
    } elseif ($Tag -match '^[0-9a-f]{7,39}$') {
        $resolved = git -C $repoRoot rev-parse --verify --quiet "$Tag^{commit}"
        if ($LASTEXITCODE -ne 0 -or -not $resolved) { throw "Commit '$Tag' lokal nicht gefunden (vorher git fetch?)." }
        $Tag = "$resolved".Trim()
    }
    if ($Tag -notmatch '^([0-9a-f]{40}|latest)$') { throw "Ungueltiger Tag '$Tag' (erwartet: Commit-SHA oder latest)." }
    Write-Host "    Ziel: $Target ($service), Image $image`:$Tag" -ForegroundColor DarkGray

    # 1) Prod-Schutz: NICHT als Prod ausrollen, wenn die Prod-Env als Demo konfiguriert ist
    #    (Demo__AutoSetup=true -> Demo-Daten wuerden eingespielt). Fail-closed: kann das Flag nicht
    #    geprueft werden -> Abbruch.
    #
    #    ACHTUNG, NIE doppelte Anfuehrungszeichen in Remote-Skripte schreiben: Windows PowerShell 5.1
    #    verschluckt eingebettete " beim Aufbau der argv fuer native Exes, das Skript kommt dann
    #    unquotiert auf dem Server an. Nur einfache Anfuehrungszeichen oder gar keine. Auch NICHT per
    #    Pipe an 'bash -s' schicken (UTF-8-BOM + CRLF auf stdin).
    if ($Target -eq "prod") {
        $remoteCheck =
            "test -f /etc/noose/noose.env || { echo ENVFILE_MISSING; exit 1; }; " +
            "if grep -iqE '^[[:space:]]*Demo__AutoSetup[[:space:]]*=[[:space:]]*[^[:alnum:]]?true' /etc/noose/noose.env; then echo DEMO_FLAG=true; else echo DEMO_FLAG=false; fi"
        Write-Host "==> Prod-Schutz: pruefe Demo-Flag in /etc/noose/noose.env" -ForegroundColor Cyan
        # stderr NICHT in die Pipeline ziehen: sonst verschluckt Out-String den ssh-Passwort-/Host-Key-Prompt.
        $checkOutput = (& $ssh @sshOpts $Server $remoteCheck | Out-String)
        if ($LASTEXITCODE -ne 0 -or $checkOutput -notmatch 'DEMO_FLAG=(true|false)') {
            throw "Konnte das Demo-Flag nicht pruefen (ssh Exit $LASTEXITCODE). Aus Sicherheit abgebrochen.`nAusgabe: $checkOutput"
        }
        if ($checkOutput -match 'DEMO_FLAG=true') {
            throw "ABBRUCH: Die Prod-Env enthaelt Demo__AutoSetup=true. Auf Prod wird nur ausgerollt, solange die Demo-Flag false ist."
        }
        Write-Host "    Demo-Flag = false -> Prod-Deploy erlaubt." -ForegroundColor DarkGray
    }

    # 2) compose.yml und backup.sh als *.new hochladen; uebernommen werden sie erst nach der Pruefung
    #    (die .env mit den Tags bleibt auf dem Server)
    $backup = Join-Path $repoRoot "deploy\backup.sh"
    Invoke-Step "Lade compose.yml hoch" { & $scp @sshOpts $compose "${Server}:/opt/noose/compose.yml.new" }
    Invoke-Step "Lade backup.sh hoch" { & $scp @sshOpts $backup "${Server}:/opt/noose/backup.sh.new" }

    # 3) Ausrollen. Bash-Variablen stehen in '...'-Teilen, damit PowerShell sie nicht ersetzt.
    $remote = "set -e; cd /opt/noose" +
              " && docker compose -f compose.yml.new config -q && mv compose.yml.new compose.yml" +
              " && sed -i 's/\r$//' backup.sh.new && install -m 700 backup.sh.new backup.sh && rm backup.sh.new" +
              " && { docker pull -q ${image}:$Tag >/dev/null || { echo Image ${image}:$Tag nicht gefunden - GitHub Action Container-Image abwarten.; exit 1; }; }" +
              " && echo vorher: `$(grep ^${tagVar}= .env)" +
              " && sed -i 's/^${tagVar}=.*/${tagVar}=$Tag/' .env" +
              " && docker compose up -d --no-deps $service" +
              ' && { i=0; while [ $i -lt 45 ]; do curl -sf -o /dev/null http://127.0.0.1:' + $port + '/health && { echo Health-Check: OK; break; }; i=$((i + 1)); sleep 2; done; [ $i -lt 45 ] || { echo Health-Check: FEHLGESCHLAGEN - kein 200 nach 90s; docker logs --tail 40 ' + $service + '; exit 1; }; }' +
              " && { docker image prune -af --filter until=168h --filter label=org.opencontainers.image.source=https://github.com/NULLRADIX-DEV/NOOSE-Website >/dev/null || true; }"
    Invoke-Step "Rolle $service auf dem Server aus" { & $ssh @sshOpts $Server $remote }

    Write-Host ""
    Write-Host "Fertig. $url laeuft mit $($Tag.Substring(0, [Math]::Min(7, $Tag.Length)))." -ForegroundColor Green
    Write-Host "Im Browser ggf. mit Strg+F5 hart neu laden (Asset-Cache)." -ForegroundColor Green
}
catch {
    $exitCode = 1
    Write-Host ""
    Write-Host "============================================" -ForegroundColor Red
    Write-Host "  DEPLOY FEHLGESCHLAGEN" -ForegroundColor Red
    Write-Host "============================================" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host "Rollback: .\deploy.ps1 -Target $Target -Tag <vorheriger-commit> (siehe 'vorher:' oben)" -ForegroundColor DarkYellow
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
