# Deployment — NOOSE-Website

Diese Anleitung beschreibt, wie die NOOSE-Website als Container auf den Produktiv-Server ausgerollt wird,
wie der Server aufgebaut ist und wie man typische Probleme löst.

---

## 1. Überblick / Architektur

Seit 30.09.2026 laufen NOOSE, die Demo und die Datenbank als **Docker-Container** (Compose) auf dem Server.
nginx bleibt als Reverse-Proxy auf dem Host.

```
Browser ──HTTPS──> nginx (Host, Port 443, TLS via Let's Encrypt)
                     │  Reverse-Proxy, leitet WebSockets (Blazor/SignalR) durch
                     ▼
                   Container "noose" (Kestrel, 127.0.0.1:5000), User www-data
                     │
                     ▼
                   Container "noose-db" (MariaDB 10.11, 127.0.0.1:3306, DB "noose")
```

| Was | Wert |
|-----|------|
| Server (SSH) | `root@62.169.28.155` (Ubuntu 24.04) |
| Domain | `noose.info` (+ `www`) → A-Record auf die Server-IP |
| Container-Image | `ghcr.io/nullradix-dev/noose-website:<commit-sha>` (+ `latest`), gebaut von GitHub Actions, Paket **privat** |
| Server-Verzeichnis | `/opt/noose/` (`compose.yml`, `.env`, `backup.sh`, `db/`, `data/`) |
| Container | `noose` (Prod, Port 5000), `noose-demo` (Demo, Port 5001), `noose-db` (MariaDB, Port 3306) — alle `network_mode: host`, `restart: unless-stopped` |
| Secrets/Env | `/etc/noose/noose.env` (chmod 600, nur root) — unverändert gegenüber dem Betrieb ohne Container |
| Zeitzone | `Europe/Berlin` (via `TZ` in `/etc/noose/noose.env`) — **zwingend**, sonst alle Zeiten 2 h zu früh |
| Datenbank | **MariaDB 10.11** im Container `noose-db` (Datenverzeichnis `/opt/noose/db`), DB `noose` (Demo: `noose_demo`), Connection-String weiterhin `Server=127.0.0.1` |
| nginx-Site | `/etc/nginx/sites-available/noose` |
| TLS | Let's Encrypt (certbot, erneuert sich automatisch) |
| Uploads/Schlüssel | `App_Data` liegt **nicht im Image**, sondern als Volume unter `/opt/noose/data/prod` (**niemals löschen!**) |
| Server-Dashboard | 1Panel (nur über VPN erreichbar) |

Docker auf dem Server: Docker CE + Compose-Plugin aus dem offiziellen Docker-Repo. `/etc/docker/daemon.json`
setzt `"ip": "127.0.0.1"` (veröffentlichte Ports nur auf Loopback, weil Docker die ufw umgeht), Log-Driver
`local` (20 MB × 5) und `live-restore`. Firewall (ufw): nur 22/80/443 öffentlich.

Speicherlimits: `noose-db` 768 MB, `noose` 1 GB, `noose-demo` 512 MB.

Wichtige App-Mechanik (siehe `Program.cs` / `Data/DatabaseConnectionResolver.cs`):
- **Verbindungs-Auswahl:** Erst `ConnectionStrings:ProductionConnection`, sonst Fallback auf
  `DefaultConnection`. Auf dem Server zeigt `ProductionConnection` auf die MariaDB im Container (`127.0.0.1`).
- **Auto-Migration beim Start:** ausstehende EF-Migrationen werden automatisch angewendet —
  kein manuelles `dotnet ef database update` gegen Produktiv nötig.
- **Reverse-Proxy:** `UseForwardedHeaders()` + persistente Data-Protection-Schlüssel unter
  `App_Data/keys` (sonst werden bei jedem Neustart alle Nutzer ausgeloggt).

---

## 2. Routine-Deploy (der einfache Weg)

Ablauf: Änderung nach `master` mergen (der Branch ist geschützt → per PR). Die GitHub Action
**„Container-Image“** (`.github/workflows/image.yml`) baut daraus das Image und legt es in GHCR ab
(bei einem PR nur Build zur Prüfung des Dockerfiles, Push erst auf `master`). **Warten, bis die Action
für den Commit fertig ist**, dann aus dem Repo-Root (das Skript liegt in `scripts\`):

```powershell
.\scripts\deploy.ps1                 # Prod, aktueller Commit von origin/master
.\scripts\deploy.ps1 -Target demo    # Demo-Instanz (noose-demo)
.\scripts\deploy.ps1 -Tag 323a5e7    # bestimmter Commit, z. B. Rollback (kurze SHA geht)
```

Das Skript baut nichts lokal und packt nichts mehr. Es macht: Tag bestimmen → **Prod-Schutz** (die Prod-Env
darf kein `Demo__AutoSetup=true` enthalten) → `deploy/compose.yml` nach `/opt/noose/compose.yml` hochladen →
Image ziehen → Tag in `/opt/noose/.env` setzen (`NOOSE_TAG` bzw. `DEMO_TAG`) →
`docker compose up -d --no-deps <dienst>` (der alte Container stoppt vorher, es laufen nie zwei Instanzen) →
Health-Check auf `http://127.0.0.1:5000/health` (Demo: `:5001`). Am Ende im Browser **Strg+F5** (Asset-Cache leeren).

Weitere Parameter: `-Server root@andere.ip`, `-NoPause`.

- **Rollback:** dasselbe Skript mit `-Tag <älterer-commit>`.
- **Datenbank:** `deploy.ps1` startet den Dienst `db` nie neu. Änderungen an dessen Definition in
  `compose.yml` bewusst auf dem Server anwenden: `cd /opt/noose && docker compose up -d db`.
- **Migrationen** laufen wie bisher beim App-Start automatisch.

> **Hinweis:** Ein Deploy ersetzt den Container — alle eingeloggten Nutzer sehen kurz das
> Reconnect-Modal, `/_blazor/negotiate` liefert währenddessen **502**. Das ist erwartet und dauert
> ~10–25 s (bei migrationsschweren Releases länger). Clientseitige `ERR_NAME_NOT_RESOLVED`- bzw.
> `ERR_NETWORK_CHANGED`-Fehler kommen dagegen **nicht** vom Server (siehe Abschnitt 7).

### Einmalig: Server bei GHCR anmelden

Die Images sind privat. Der Server braucht einmalig:

```bash
docker login ghcr.io -u <github-user>
```

als Passwort einen **Classic-PAT** mit Scope nur `read:packages` (GHCR nimmt keine Fine-grained-Tokens).
Läuft der PAT ab, scheitert der Deploy mit „unauthorized“ — neuen PAT erzeugen und `docker login` wiederholen.

### SSH-Key (passwortloser Deploy, empfohlen)

Damit `deploy.ps1` nicht nach dem Passwort fragt, einmalig einen Schlüssel hinterlegen
(in PowerShell auf deinem PC):

```powershell
# Schlüssel erzeugen (falls noch keiner da ist) – Enter für Default-Pfad, leere Passphrase ok
ssh-keygen -t ed25519

# Öffentlichen Schlüssel auf den Server kopieren
type $env:USERPROFILE\.ssh\id_ed25519.pub | ssh root@62.169.28.155 "mkdir -p ~/.ssh && cat >> ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys"
```

Danach läuft `.\scripts\deploy.ps1` komplett ohne Passwort-Eingabe.

---

## 3. Manueller Deploy (Fallback, falls das Skript mal nicht geht)

Auf dem Server (das Image muss in GHCR vorhanden sein, also Action abgewartet):
```bash
cd /opt/noose
# Tag in .env setzen (NOOSE_TAG=<commit-sha>; für die Demo DEMO_TAG)
nano .env
docker compose pull noose
docker compose up -d --no-deps noose
docker logs -f noose         # Logs prüfen (Strg+C beendet)
curl -s http://127.0.0.1:5000/health
```
`/opt/noose/compose.yml` ist die Kopie von `deploy/compose.yml`.

---

## 4. Betrieb / nützliche Befehle (auf dem Server)

```bash
cd /opt/noose && docker compose ps              # Status aller Container
docker logs -f noose                            # Live-Logs Prod
docker logs -f noose-demo                       # Live-Logs Demo
docker logs noose-db                            # Logs der Datenbank
cd /opt/noose && docker compose restart noose   # Neustart (nur ohne Env-Änderung)
# Nach Änderung an /etc/noose/noose.env: docker compose up -d --force-recreate --no-deps noose (Demo: noose-demo)
curl -s http://127.0.0.1:5000/health            # erwartet: Healthy

# Datenbank-Konsole (root per Socket, kein Passwort nötig)
docker exec -it noose-db mariadb noose
#   darin z. B.: SHOW TABLES;

# TLS-Zertifikat: certbot erneuert automatisch; Test:
certbot renew --dry-run
```
Logs und Container lassen sich auch im Server-Dashboard 1Panel (nur über VPN erreichbar) → Containers ansehen.

> Die alten systemd-Dienste `noose`/`noose-demo`, die Host-MariaDB sowie `/var/www/noose` und
> `/var/www/noose-demo` sind gestoppt bzw. deaktiviert und nur noch als Rollback vorhanden (werden später
> entfernt). `journalctl -u noose` zeigt daher nur noch alte Logs.

### Backups der Datenbank

**Automatisches Server-Backup (Cron):** `/opt/noose/backup.sh` (Kopie von `deploy/backup.sh`, `deploy.ps1` lädt sie bei jedem Deploy hoch) läuft per
root-Cron täglich um 04:15 Uhr:
```
15 4 * * * /opt/noose/backup.sh >> /var/log/noose-backup.log 2>&1
```
Es sichert beide Datenbanken aus dem Container `noose-db` nach `/root/backups/noose-JJJJ-MM-TT.sql.gz` und
`/root/backups/noose_demo-JJJJ-MM-TT.sql.gz`, prüft jeden Dump auf Vollständigkeit (unvollständige werden
verworfen), rotiert nur diese täglichen Dateien (`<db>-JJJJ-MM-TT.sql.gz`, 30 Tage) und schreibt das Log nach `/var/log/noose-backup.log`.

> **Nicht im Server-Backup:** die Uploads unter `App_Data` (`/opt/noose/data/prod`), nur die Datenbanken.

**Manuelles Backup vom PC** (Dump auf dem Server **+** Kopie auf den PC, aus dem Repo-Root (das Skript liegt in `scripts\`)):
```powershell
.\scripts\backup-db.ps1
# Server-Kopie:  /root/backups/noose-<datum>_<zeit>.sql.gz
# PC-Kopie:      %USERPROFILE%\NOOSE-Backups\noose-<datum>_<zeit>.sql.gz
```
Parameter (Defaults): `-Server root@62.169.28.155`, `-Database noose`, `-RemoteDir /root/backups`,
`-LocalDir %USERPROFILE%\NOOSE-Backups`, `-RetentionDays 30`, `-NoPause`. `-RetentionDays` löscht auf dem Server nur
manuelle Dumps (`<db>-<datum>_<zeit>.sql.gz`); die täglichen Cron-Dateien rotiert `backup.sh`. Die PC-Kopien (Offsite) bleiben
**alle** erhalten. Setzt einen hinterlegten SSH-Key voraus (siehe Abschnitt SSH-Key).

**Restore** einer Kopie (auf dem Server):
```bash
gunzip < /root/backups/noose-2026-07-13.sql.gz | docker exec -i noose-db mariadb noose
```

---

## 5. Einmalige Server-Einrichtung (Referenz / Disaster Recovery)

Falls der Server neu aufgesetzt werden muss — die Erstinstallation in Kurzform.

### 5.1 Pakete
```bash
apt update && apt upgrade -y
# Docker CE + Compose-Plugin aus dem offiziellen Docker-Repo (docs.docker.com/engine/install/ubuntu)
# Webserver + TLS
apt install -y nginx certbot python3-certbot-nginx
```
Danach `/etc/docker/daemon.json` anlegen (siehe Abschnitt 1: `"ip": "127.0.0.1"`, Log-Driver `local`,
`live-restore`) und einmalig `docker login ghcr.io` ausführen (siehe Abschnitt 2).

### 5.2 Verzeichnisse, Env-Dateien, Datenbank
Reihenfolge (alles als root auf dem Server):
1. Docker installieren und `/etc/docker/daemon.json` anlegen (5.1), dann `docker login ghcr.io -u <github-user>` (Abschnitt 2).
2. Verzeichnisse anlegen:
   ```bash
   mkdir -p /opt/noose/db /opt/noose/data/prod /opt/noose/data/demo
   chown 999:999 /opt/noose/db && chmod 700 /opt/noose/db          # MariaDB-Container (UID 999)
   chown 33:33 /opt/noose/data/prod /opt/noose/data/demo           # www-data, App_Data
   ```
3. Env-Dateien `/etc/noose/noose.env` (siehe 5.3) und `/etc/noose-demo/noose-demo.env` anlegen, je `chmod 600`.
4. `/opt/noose/.env` schreiben. Beide Tags sind Pflicht (`compose.yml` verlangt sie auch für `db`); `<sha>` = Commit,
   dessen Image die Action „Container-Image“ gebaut hat:
   ```bash
   printf 'NOOSE_TAG=<sha>\nDEMO_TAG=<sha>\n' > /opt/noose/.env
   ```
5. `deploy/compose.yml` und `deploy/backup.sh` nach `/opt/noose/` kopieren, `chmod 700 /opt/noose/backup.sh`
   (bei späteren Deploys lädt `deploy.ps1` beide Dateien selbst hoch).
6. Datenbank, entweder
   - **Bestehendes MariaDB-Datenverzeichnis übernehmen** (kalt, MariaDB gestoppt): Inhalt nach `/opt/noose/db/`
     kopieren, `chown -R 999:999 /opt/noose/db`; oder
   - **Leeres Datenverzeichnis** initialisieren: einmalig MariaDB gegen das Volume starten, ~30 s warten, beenden:
     ```bash
     docker run -d --name noose-db-init -e MARIADB_RANDOM_ROOT_PASSWORD=1 -v /opt/noose/db:/var/lib/mysql mariadb:10.11
     docker rm -f noose-db-init
     cd /opt/noose && docker compose up -d db
     ```
     Datenbanken und User anlegen (`docker exec -it noose-db mariadb`, root per Socket):
     ```sql
     CREATE DATABASE noose CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
     CREATE DATABASE noose_demo CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
     CREATE USER 'noose'@'localhost' IDENTIFIED BY 'DEIN_DB_PASSWORT';
     CREATE USER 'noose'@'127.0.0.1' IDENTIFIED BY 'DEIN_DB_PASSWORT';
     GRANT ALL PRIVILEGES ON noose.* TO 'noose'@'localhost', 'noose'@'127.0.0.1';
     GRANT ALL PRIVILEGES ON noose_demo.* TO 'noose'@'localhost', 'noose'@'127.0.0.1';
     FLUSH PRIVILEGES;
     EXIT;
     ```
     (Passwort = das aus dem Connection-String.) Dumps zurückspielen:
     ```bash
     gunzip < noose-<datum>.sql.gz      | docker exec -i noose-db mariadb noose
     gunzip < noose_demo-<datum>.sql.gz | docker exec -i noose-db mariadb noose_demo
     ```
7. `cd /opt/noose && docker compose up -d db`, dann vom PC `.\scripts\deploy.ps1` (Prod) und
   `.\scripts\deploy.ps1 -Target demo`.
8. nginx + HTTPS (5.4, 5.5) und Backup-Cron (Abschnitt 4): `15 4 * * * /opt/noose/backup.sh >> /var/log/noose-backup.log 2>&1`.

### 5.3 Secrets / Env-Datei
`/etc/noose/noose.env` (danach `chmod 600` + `chown root:root`):
```ini
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:5000
# Zeitzone des App-Prozesses. ZWINGEND: In Blazor Server nutzt .ToLocalTime() die
# Server-Zeitzone. Ohne dies läuft der Server in UTC und alle Zeiten sind 2 h zu früh
# (über Mitternacht sogar der falsche Tag). Nach Änderung: Container neu erstellen (`restart` liest die Env-Datei nicht neu): `cd /opt/noose && docker compose up -d --force-recreate --no-deps noose`.
TZ=Europe/Berlin
ConnectionStrings__ProductionConnection=Server=127.0.0.1;Port=3306;Database=noose;User ID=noose;Password=DEIN_DB_PASSWORT;SslMode=None;
Authentication__Discord__ClientId=DEINE_DISCORD_CLIENT_ID
Authentication__Discord__ClientSecret=DEIN_DISCORD_CLIENT_SECRET
Bootstrap__AdminDiscordId=DEINE_DISCORD_ID
# NOOSEI. Der Schlüssel je Anbieter; Adresse, Modell und Preis haben Vorgaben im Code.
# Welcher Anbieter genutzt wird, schaltet der KI-Eigner unter /einstellungen?tab=ki-anbieter um.
Llm__ApiKey=DEIN_OPENROUTER_SCHLUESSEL
Llm__DeepSeek__ApiKey=DEIN_DEEPSEEK_SCHLUESSEL
Ki__OwnerDiscordId=DEINE_DISCORD_ID
```

### 5.4 nginx
`/etc/nginx/sites-available/noose`:
```nginx
# WebSocket-Upgrade für Blazor Server (SignalR) — zwingend
map $http_upgrade $connection_upgrade {
    default upgrade;
    ''      close;
}
server {
    listen 80;
    server_name noose.info www.noose.info;
    location / {
        proxy_pass         http://127.0.0.1:5000;
        proxy_http_version 1.1;
        proxy_set_header   Upgrade $http_upgrade;
        proxy_set_header   Connection $connection_upgrade;
        proxy_set_header   Host $host;
        proxy_set_header   X-Real-IP $remote_addr;
        proxy_set_header   X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
        proxy_cache_bypass $http_upgrade;
        proxy_read_timeout 100s;
    }
}
```
```bash
ln -s /etc/nginx/sites-available/noose /etc/nginx/sites-enabled/
rm -f /etc/nginx/sites-enabled/default
nginx -t && systemctl reload nginx

ufw allow OpenSSH
ufw allow 'Nginx Full'
ufw --force enable
```

### 5.5 HTTPS (nachdem DNS auf den Server zeigt)
```bash
certbot --nginx -d noose.info -d www.noose.info
```
certbot ergänzt den 443-Block + http→https-Weiterleitung automatisch und erneuert sich selbst.

### 5.6 DNS (im STRATO-Kundenbereich)
| Typ | Host | Wert |
|-----|------|------|
| A | `@` | `62.169.28.155` |
| A | `www` | `62.169.28.155` |
| AAAA | `@` / `www` | löschen **oder** auf die Server-IPv6 setzen (sonst muss nginx auch auf `[::]:80/443` lauschen) |

### 5.7 Discord-Login
Im Discord Developer Portal → OAuth2 → Redirects eintragen:
```
https://noose.info/signin-discord
```

---

## 6. Image-Build per GitHub Action

Das Container-Image baut `.github/workflows/image.yml` („Container-Image“): bei einem PR nur Build (prüft das
Dockerfile), bei einem Push auf `master` Build **und** Push nach GHCR
(`ghcr.io/nullradix-dev/noose-website:<volle-commit-sha>` plus `latest`). Das `Dockerfile` baut mit `sdk:10.0`
und läuft auf `aspnet:10.0` (Ubuntu 24.04) als `www-data`; die Quill-Assets werden im Build geprüft.

Die Action deployt **nicht** auf den Server — das bleibt ein bewusster Schritt mit `.\scripts\deploy.ps1`
(Abschnitt 2).

---

## 7. Troubleshooting

| Symptom | Ursache & Lösung |
|---------|------------------|
| **`Connect Timeout expired`** beim Start, Container im Neustart-Loop | DB nicht erreichbar. Die gemanagte STRATO-DB (`*.webspace-host.com`) ist vom V-Server aus **nicht** erreichbar → MariaDB-Container nutzen (Abschnitt 5.2) und `ProductionConnection` auf `127.0.0.1` zeigen lassen. Läuft `noose-db`? → `cd /opt/noose && docker compose ps`. |
| **`Kein Connection-String konfiguriert`** | Weder `ProductionConnection` noch `DefaultConnection` gesetzt/erreichbar → `/etc/noose/noose.env` prüfen, Container neu erstellen (`restart` liest die Env-Datei nicht neu): `cd /opt/noose && docker compose up -d --force-recreate --no-deps noose`. |
| **certbot scheitert mit IPv6-Adresse / `204`** | Alter `AAAA`-Eintrag zeigt auf STRATO-Parkserver. AAAA löschen (oder auf Server-IPv6 setzen), bis `getent ahosts noose.info` nur die `62.169.28.155` zeigt, dann certbot erneut. |
| **`Failed to determine the https port for redirect`** (Log) | Harmlos. Tritt nur bei direkten http-Anfragen an Kestrel auf; über nginx+TLS verschwindet die Warnung. |
| **Login: „invalid redirect_uri"** | Im Discord Developer Portal `https://noose.info/signin-discord` als Redirect eintragen. |
| **Zeiten 2 h zu früh / falscher Tag** | Server läuft in UTC. In Blazor Server nutzt `.ToLocalTime()` die Server-Zeitzone. `TZ=Europe/Berlin` in `/etc/noose/noose.env` ergänzen, dann Container neu erstellen: `cd /opt/noose && docker compose up -d --force-recreate --no-deps noose` (`restart` liest die Env-Datei nicht neu; Neustart nötig — `TimeZoneInfo.Local` ist pro Prozess gecacht). |
| **502 Bad Gateway** | App läuft nicht → `cd /opt/noose && docker compose ps` + `docker logs --tail 100 noose`. |
| **Deploy scheitert mit „unauthorized“** | GHCR-Anmeldung des Servers fehlt oder der PAT ist abgelaufen → neuen Classic-PAT (`read:packages`) erzeugen, `docker login ghcr.io -u <github-user>` wiederholen (Abschnitt 2). |
| **Deploy: „Image nicht gefunden“** | Die GitHub Action „Container-Image“ für den Commit ist noch nicht fertig oder fehlgeschlagen → abwarten bzw. Action prüfen, dann erneut deployen. |
| **Nutzer nach jedem Deploy ausgeloggt** | Data-Protection-Schlüssel weg → `App_Data` muss als Volume (`/opt/noose/data/prod`) eingebunden bleiben und darf **nicht** gelöscht werden. |
| **Konsole: `ERR_NAME_NOT_RESOLVED` / `ERR_NETWORK_CHANGED`, WebSocket schließt mit `1006`, danach automatische Erholung** | Praktisch immer **clientseitig**: Der Browser konnte `noose.info` nicht auflösen bzw. hat abgebrochen, weil sich die Netzwerkschnittstelle geändert hat (WLAN-Wechsel, VPN, Adapter-Reset, Standby). Die Anfrage hat den Server nie erreicht — nginx und Kestrel können diese Codes gar nicht erzeugen. Gegenprobe Server: `docker compose ps` (Laufzeit des Containers älter als der Vorfall = App war nie weg), `docker logs --since <HH:MM> noose` (kein Neustart) und `grep "<HH:MM>" /var/log/nginx/access.log` (keine Zeilen = kam nie an). Gegenprobe Client: `nslookup noose.info 1.1.1.1` aus einem anderen Netz (Handy-Hotspot). Bei echtem App-Ausfall käme **502**, bei totem Host `ERR_CONNECTION_REFUSED`/`ERR_CONNECTION_TIMED_OUT` — nie `ERR_NAME_NOT_RESOLVED`. Das Reconnect-Modal fängt das ab; nichts zu tun. |
| **`ERR_CONNECTION_RESET` auf einzelne GUID-benannte Requests** (Uploads/Anhänge aus `App_Data/uploads`) | **Nur zusammen mit** dem Muster oben clientseitig (abgerissene Verbindung beim Netzwechsel). **Isoliert** — also ohne `ERR_NAME_NOT_RESOLVED` und bei stabiler WebSocket-Verbindung — serverseitig prüfen: `docker logs --tail 200 noose` auf Exceptions im Datei-Endpoint, `/var/log/nginx/error.log` auf `upstream prematurely closed connection`. |
