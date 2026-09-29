#requires -Version 5.1
<#
.SYNOPSIS
    Veroeffentlicht die NOOSE-Website und rollt sie auf den Produktiv-Server aus.

.DESCRIPTION
    Ein-Befehl-Deploy: Release veroeffentlichen -> mit tar packen (NICHT Compress-Archive,
    das zerschießt Dateien) -> per scp hochladen -> auf dem Server Dienst stoppen, Dateien
    tauschen (App_Data bleibt erhalten), Dienst starten, Health-Check.

    Prod-Schutz: Vor dem Deploy wird geprueft, dass das Ziel KEINE Demo-Instanz ist
    (Env-Flag Demo__AutoSetup). Auf den Prod-Server wird nur deployt, solange die Demo-Flag
    false ist. Demo-Ziele werden am Service-/Pfadnamen ("demo") bzw. der Demo-IP erkannt und
    uebersprungen; -AllowDemo erzwingt das Ueberspringen.

.EXAMPLE
    .\deploy.ps1
        Standard-Deploy auf root@62.169.28.155.

.EXAMPLE
    .\deploy.ps1 -SkipPublish
        Nutzt den vorhandenen .\publish-Ordner (kein erneutes dotnet publish).

.EXAMPLE
    .\deploy.ps1 -NoPause
        Ohne "Enter zum Schließen" am Ende (fuer Terminal-/CI-Nutzung).

.EXAMPLE
    .\deploy.ps1 -Server root@31.70.104.128 -AppDir /var/www/noose-demo -Service noose-demo
        Deploy auf die Demo-Instanz (Prod-Schutz wird automatisch uebersprungen).

.NOTES
    Am besten aus einer bereits offenen PowerShell starten. Bei "Run with PowerShell" /
    Doppelklick haelt das Skript das Fenster am Ende offen, damit Ausgabe & Fehler lesbar bleiben.
    Ohne SSH-Key fragt scp/ssh je einmal nach dem Server-Passwort (siehe DEPLOYMENT.md -> SSH-Key).
#>

[CmdletBinding()]
param(
    [string]$Server  = "root@62.169.28.155",
    [string]$AppDir  = "/var/www/noose",
    [string]$Service = "noose",
    [switch]$SkipPublish,
    [switch]$AllowDemo,
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

# Findet ssh/scp robust – PATH-unabhaengig und auch aus einem 32-bit-PowerShell heraus, wo
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
    # das Skript liegt in scripts\, das Projekt eine Ebene darueber
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $project = Join-Path $repoRoot "NOOSE-Website\NOOSE-Website.csproj"
    $publish = Join-Path $PSScriptRoot "publish"
    $tarball = Join-Path $PSScriptRoot "noose-publish.tgz"

    if (-not (Test-Path $project)) {
        throw "Projekt nicht gefunden: $project. Liegt deploy.ps1 wirklich in scripts\ innerhalb des Repos?"
    }

    # ssh/scp vorab auf vollen Pfad aufloesen (PATH-unabhaengig).
    $scp = Resolve-Exe 'scp'
    $ssh = Resolve-Exe 'ssh'
    # nie still haengen: Verbindungs-Timeout + neue Host-Keys automatisch akzeptieren (kein yes/no-Prompt).
    $sshOpts = @('-o', 'ConnectTimeout=10', '-o', 'StrictHostKeyChecking=accept-new')

    # 0) Prod-Schutz: NICHT auf einen Server deployen, der als Demo-Instanz konfiguriert ist
    #    (Env-Flag Demo__AutoSetup=true). Verhindert, dass ein Prod-Deploy versehentlich eine
    #    Demo-Box trifft. Demo-Ziele werden automatisch erkannt und uebersprungen; -AllowDemo erzwingt das.
    #    Laeuft VOR dem Publish (fail-fast) und fail-closed: kann das Flag nicht geprueft werden -> Abbruch.
    $isDemoTarget = $AllowDemo -or ($Service -like '*demo*') -or ($AppDir -like '*demo*') -or ($Server -like '*31.70.104.128*')
    if (-not $isDemoTarget) {
        $envFile = "/etc/$Service/$Service.env"
        # ACHTUNG: keine doppelten Anfuehrungszeichen im Remote-Skript (siehe Hinweis bei $remote unten).
        $remoteCheck =
            "FLAG=false; " +
            "if systemctl show '$Service' -p Environment 2>/dev/null | grep -iqE 'Demo__AutoSetup=true'; then FLAG=true; fi; " +
            "if [ -f '$envFile' ] && grep -iqE '^[[:space:]]*Demo__AutoSetup[[:space:]]*=[[:space:]]*true' '$envFile'; then FLAG=true; fi; " +
            'echo DEMO_FLAG=$FLAG'
        Write-Host "==> Prod-Schutz: pruefe Demo-Flag auf $Server ($envFile)" -ForegroundColor Cyan
        # stderr NICHT in die Pipeline ziehen: sonst verschluckt Out-String den ssh-Passwort-/Host-Key-Prompt -> sieht aus wie haengen.
        $checkOutput = (& $ssh @sshOpts $Server $remoteCheck | Out-String)
        if ($LASTEXITCODE -ne 0) {
            throw "Konnte das Demo-Flag nicht pruefen (ssh Exit $LASTEXITCODE). Aus Sicherheit abgebrochen.`nAusgabe: $checkOutput"
        }
        if ($checkOutput -match 'DEMO_FLAG=true') {
            throw "ABBRUCH: '$Server' ist als DEMO-Instanz konfiguriert (Demo__AutoSetup=true). Auf den Prod-Server wird nur deployt, solange die Demo-Flag false ist.`nFuer einen bewussten Demo-Deploy: -Service mit 'demo' im Namen (z. B. noose-demo) oder -AllowDemo setzen."
        }
        Write-Host "    Demo-Flag = false -> Prod-Deploy erlaubt." -ForegroundColor DarkGray
    } else {
        Write-Host "==> Demo-Ziel erkannt -> Prod-Schutz (Demo-Flag-Check) uebersprungen." -ForegroundColor DarkYellow
    }

    # 1) Release veroeffentlichen. Publish-Ordner vorher leeren, weil "dotnet publish" das Ziel NICHT
    #    aufraeumt: Altlasten frueherer Publishes wandern sonst mit ins Artefakt (so lagen z. B. noch
    #    quill.js / quill-table-better.* vom verworfenen Quill-2-Versuch darin). Unter -SkipPublish wird
    #    bewusst NICHT geleert (der vorhandene Ordner soll wiederverwendet werden).
    if (-not $SkipPublish) {
        if (Test-Path $publish) {
            Write-Host "==> Leere Publish-Ordner (keine Altlasten)" -ForegroundColor Cyan
            Remove-Item (Join-Path $publish '*') -Recurse -Force -ErrorAction Stop
        }
        Invoke-Step "Veroeffentliche Release" { dotnet publish $project -c Release -o $publish --nologo }
    } else {
        Write-Host "==> ueberspringe Publish (-SkipPublish)" -ForegroundColor DarkYellow
    }
    if (-not (Test-Path (Join-Path $publish "NOOSE-Website.dll"))) {
        throw "publish-Ordner unvollstaendig: $publish (NOOSE-Website.dll fehlt). Laeuft evtl. noch eine Dev-Instanz und sperrt bin/?"
    }

    # 1b) Selbst gehostete Quill-/Tabellen-Assets pruefen. dotnet publish kopiert wwwroot automatisch
    #     mit; dieser Check stellt sicher, dass die Editor-Dateien (inkl. vendored Tabellen-Modul) wirklich
    #     im Artefakt liegen — sonst fehlt im Editor der Tabellen-Button bzw. die Lese-Ansicht bricht.
    $quillDir = Join-Path $publish "wwwroot\lib\quill"
    $quillDateien = @("quill.min.js", "quill.snow.css", "table-module.js", "table-module.css", "quill-global.mjs")
    foreach ($f in $quillDateien) {
        $p = Join-Path $quillDir $f
        if (-not (Test-Path $p)) {
            throw "Publish-Output unvollstaendig: $p fehlt. Liegt die Datei in NOOSE-Website\wwwroot\lib\quill\ und wurde sie nicht ausgeschlossen?"
        }
    }
    Write-Host "==> Quill-/Tabellen-Assets im Artefakt vorhanden ($($quillDateien.Count) Dateien)" -ForegroundColor DarkGray

    # 2) Mit tar packen — zuverlaessig; Compress-Archive hat schon 0-Byte-Dateien erzeugt.
    if (Test-Path $tarball) { Remove-Item $tarball -Force }
    Invoke-Step "Packe Artefakt (tar)" { tar -czf $tarball -C $publish . }

    # 3) Auf den Server kopieren
    Invoke-Step "Lade auf Server hoch" { & $scp @sshOpts $tarball "${Server}:/tmp/noose-publish.tgz" }

    # 4) Auf dem Server ausrollen: Dienst stoppen, Dateien tauschen (App_Data behalten),
    #    Rechte setzen, Dienst starten, kurz warten, Health pruefen. Alles per && -> fail-fast.
    #
    #    ACHTUNG, NIE doppelte Anfuehrungszeichen in dieses Remote-Skript schreiben: Windows
    #    PowerShell 5.1 verschluckt eingebettete " beim Aufbau der argv fuer native Exes, das
    #    Skript kommt dann unquotiert auf dem Server an und bash bricht mit einem Syntaxfehler ab
    #    (z. B. bei den Klammern einer Meldung). Nur einfache Anfuehrungszeichen oder gar keine.
    #    Auch NICHT per Pipe an 'bash -s' schicken -- PowerShell stellt dem stdin eine UTF-8-BOM
    #    voran und beendet mit CRLF; das \r klebt am letzten } und bash meldet 'unexpected EOF'.
    $remote = "systemctl stop $Service" +
              " && find $AppDir -mindepth 1 -maxdepth 1 ! -name App_Data -exec rm -rf {} +" +
              " && tar -xzf /tmp/noose-publish.tgz -C $AppDir" +
              " && chown -R www-data:www-data $AppDir" +
              " && systemctl start $Service" +
              " && rm -f /tmp/noose-publish.tgz" +
              ' && { i=0; while [ $i -lt 30 ]; do curl -sf -o /dev/null http://127.0.0.1:5000/health && { echo Health-Check: OK; exit 0; }; i=$((i + 1)); sleep 2; done; echo Health-Check: FEHLGESCHLAGEN - kein 200 nach 60s; exit 1; }'
    Invoke-Step "Rolle auf dem Server aus" { & $ssh @sshOpts $Server $remote }

    # 5) Lokales Artefakt aufraeumen
    Remove-Item $tarball -Force -ErrorAction SilentlyContinue

    Write-Host ""
    Write-Host "Fertig. https://noose.info ist aktualisiert." -ForegroundColor Green
    Write-Host "Im Browser ggf. mit Strg+F5 hart neu laden (Asset-Cache)." -ForegroundColor Green
}
catch {
    $exitCode = 1
    Write-Host ""
    Write-Host "============================================" -ForegroundColor Red
    Write-Host "  DEPLOY FEHLGESCHLAGEN" -ForegroundColor Red
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
        $null = Read-Host "Enter druecken zum Schließen"
    }
}

exit $exitCode
