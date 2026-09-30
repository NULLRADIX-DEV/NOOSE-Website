# Deployment — NOOSE-Website

Diese Anleitung beschreibt, wie die NOOSE-Website als Container auf der Server-Plattform ausgerollt wird
und wie man typische Probleme löst.

> **Server-Details** (Pfade, Ports, Benutzer, Backups) stehen in der privaten Betriebsdoku. Sie gehören nie in dieses
> öffentliche Repo, ebenso keine Secrets.

---

## 1. Überblick / Architektur

NOOSE läuft auf einer gemeinsamen Server-Plattform. Prod (`noose.info`) und Demo (`demo.noose.info`) sind zwei
**getrennte Apps**. Jede hat:

- einen eigenen Linux-User und ein eigenes rootless Docker,
- eine eigene MariaDB 10.11 (Compose-Dienst `db`, Connection-String `Server=db;...`),
- ein eigenes Speicherlimit.

```
Browser ──HTTPS──> Reverse-Proxy der Plattform (TLS, WebSockets für Blazor/SignalR)
                     ▼
                   App-Container "noose" (Kestrel)  ──>  MariaDB-Container "db"
                   (Prod und Demo je in einem eigenen Docker)
```

| Was | Wert |
|-----|------|
| Container-Image | `ghcr.io/nullradix-dev/noose-website:<commit-sha>` (+ `latest`), gebaut von GitHub Actions |
| Compose-Dateien | `deploy/compose.yml` (Prod), `deploy/demo/compose.yml` (Demo) |
| Vom Server gesetzt | Image-Tag, Port und Netzwerk (`APP_COMMIT`, `APP_PORT`, `APP_SUBNET`) |
| Secrets/Env | nur auf dem Server, nie im Repo |
| Zeitzone | `Europe/Berlin` (`TZ` in der Server-Env) — **zwingend**, sonst alle Zeiten 2 h zu früh |
| Uploads/Schlüssel | `App_Data` liegt **nicht im Image**, sondern als Volume auf dem Server (**niemals löschen!**) |

Wichtige App-Mechanik (siehe `Program.cs` / `Data/DatabaseConnectionResolver.cs`):
- **Verbindungs-Auswahl:** Erst `ConnectionStrings:ProductionConnection`, sonst Fallback auf
  `DefaultConnection`. Auf dem Server zeigt `ProductionConnection` auf den Compose-Dienst `db`.
- **Auto-Migration beim Start:** ausstehende EF-Migrationen werden automatisch angewendet —
  kein manuelles `dotnet ef database update` gegen Produktiv nötig.
- **Reverse-Proxy:** `UseForwardedHeaders()` vertraut zusätzlich dem App-Netzwerk über
  `ForwardedHeaders:KnownNetworks` (Liste von CIDRs, setzt die Compose-Datei). Dazu kommen persistente
  Data-Protection-Schlüssel unter `App_Data/keys` (sonst werden bei jedem Neustart alle Nutzer ausgeloggt).
- **Prod-Schutz:** `deploy/compose.yml` erzwingt `Demo__AutoSetup=false`.

---

## 2. Deploy (GitHub Actions)

Ablauf: Änderung nach `master` mergen (der Branch ist geschützt → per PR). Der Workflow
**„Container-Image“** (`.github/workflows/image.yml`) baut daraus das Image und legt es in GHCR ab
(bei einem PR nur Build zur Prüfung des Dockerfiles, Push erst auf `master`). **Warten, bis er für den Commit
fertig ist.** Danach: **Actions → Deploy → Run workflow** (`.github/workflows/deploy.yml`, Kopie des
Plattform-Templates).

| Input | Bedeutung |
|-------|-----------|
| `environment` | `production` (Prod, Default) oder `demo` |
| Commit | leer = aktueller `master`, sonst Commit-SHA |
| `rollback` | Häkchen = zurück auf das vorherige Release |

Der Server prüft vor dem Umschalten alles und behält das vorherige Release, wenn ein Start fehlschlägt. Vor jedem
Wechsel dumpt er alle Datenbanken des laufenden Releases.

- **Rollback:** Workflow mit Häkchen „rollback“ starten. **Achtung:** Ein Rollback macht EF-Migrationen nicht
  rückgängig. Darum gibt es den Dump vor jedem Wechsel; das Zurückspielen macht der Admin auf dem Server.
- **Demo:** derselbe Workflow mit `environment` = `demo` (siehe `DEPLOYMENT-DEMO.md`).
- **Migrationen** laufen beim App-Start automatisch.
- Am Ende im Browser **Strg+F5** (Asset-Cache leeren).

> **Hinweis:** Ein Deploy ersetzt den Container — alle eingeloggten Nutzer sehen kurz das
> Reconnect-Modal, `/_blazor/negotiate` liefert währenddessen **502**. Das ist erwartet und dauert
> ~10–25 s (bei migrationsschweren Releases länger). Clientseitige `ERR_NAME_NOT_RESOLVED`- bzw.
> `ERR_NETWORK_CHANGED`-Fehler kommen dagegen **nicht** vom Server (siehe Abschnitt 6).

---

## 3. Backups

- **Nächtlich:** DB-Backups laufen über 1Panel auf dem Server.
- **Vor jedem Deploy:** Dump aller Datenbanken des laufenden Releases (siehe Abschnitt 2).
- **Manuell:** Dumps und Restores macht der Admin auf dem Server.

> **Nicht im DB-Backup:** die Uploads unter `App_Data`, nur die Datenbanken.

---

## 4. Konfiguration und Secrets

Secrets und Env-Werte liegen **nur auf dem Server**, nie im Repo (Doppel-Unterstrich als Trenner):

- `ConnectionStrings__ProductionConnection` (`Server=db;...`)
- `Authentication__Discord__ClientId` / `__ClientSecret`
- `Bootstrap__AdminDiscordId`
- `Llm__ApiKey` (OpenRouter), `Llm__DeepSeek__ApiKey` (DeepSeek direkt); der KI-Eigner wählt den Anbieter unter
  `/einstellungen?tab=ki-anbieter`. `Ki__OwnerDiscordId` bestimmt den KI-Eigner.
- `TZ=Europe/Berlin` — **zwingend**: In Blazor Server nutzt `.ToLocalTime()` die Server-Zeitzone. Ohne `TZ` läuft
  der Server in UTC und alle Zeiten sind 2 h zu früh (über Mitternacht sogar der falsche Tag).

Nach einer Änderung der Env muss der Container neu erstellt werden (`restart` liest die Env nicht neu; `TimeZoneInfo.Local`
ist pro Prozess gecacht) — also den Deploy-Workflow laufen lassen.

**Discord-Login:** Im Discord Developer Portal → OAuth2 → Redirects eintragen: `https://noose.info/signin-discord`
(Demo: `https://demo.noose.info/signin-discord`).

---

## 5. Image-Build per GitHub Action

Das Container-Image baut `.github/workflows/image.yml` („Container-Image“): bei einem PR nur Build (prüft das
Dockerfile), bei einem Push auf `master` Build **und** Push nach GHCR
(`ghcr.io/nullradix-dev/noose-website:<volle-commit-sha>` plus `latest`). Das `Dockerfile` baut mit `sdk:10.0`
und läuft auf `aspnet:10.0` (Ubuntu 24.04) als `www-data`; die Quill-Assets werden im Build geprüft.

Die Action deployt **nicht** — das bleibt ein bewusster Schritt mit dem Workflow „Deploy“ (Abschnitt 2).

---

## 6. Troubleshooting

| Symptom | Ursache & Lösung |
|---------|------------------|
| **`Connect Timeout expired`** beim Start, Container im Neustart-Loop | DB nicht erreichbar. Der Connection-String muss auf den Compose-Dienst `db` zeigen (`Server=db`); läuft der DB-Container? Gemanagte Fremd-DBs sind vom Server aus nicht erreichbar. |
| **`Kein Connection-String konfiguriert`** | Weder `ProductionConnection` noch `DefaultConnection` gesetzt/erreichbar → Server-Env prüfen, dann Deploy-Workflow laufen lassen (der Container muss neu erstellt werden). |
| **`Failed to determine the https port for redirect`** (Log) | Harmlos. Tritt nur bei direkten http-Anfragen an Kestrel auf; über den Reverse-Proxy mit TLS verschwindet die Warnung. |
| **Login: „invalid redirect_uri"** | Im Discord Developer Portal `https://noose.info/signin-discord` als Redirect eintragen. |
| **Zeiten 2 h zu früh / falscher Tag** | Server läuft in UTC. `TZ=Europe/Berlin` in der Server-Env ergänzen, dann Deploy-Workflow laufen lassen (Container muss neu erstellt werden). |
| **502 Bad Gateway** | App läuft nicht → App-Logs auf dem Server prüfen (Betriebsdoku); ein Startfehler beim Deploy lässt das vorherige Release weiterlaufen. |
| **Deploy scheitert: Image nicht gefunden** | Der Workflow „Container-Image“ für den Commit ist noch nicht fertig oder fehlgeschlagen → abwarten bzw. prüfen, dann Deploy erneut starten. |
| **Nutzer nach jedem Deploy ausgeloggt** | Data-Protection-Schlüssel weg → `App_Data` muss als Volume eingebunden bleiben und darf **nicht** gelöscht werden. |
| **Konsole: `ERR_NAME_NOT_RESOLVED` / `ERR_NETWORK_CHANGED`, WebSocket schließt mit `1006`, danach automatische Erholung** | Praktisch immer **clientseitig**: Der Browser konnte `noose.info` nicht auflösen bzw. hat abgebrochen, weil sich die Netzwerkschnittstelle geändert hat (WLAN-Wechsel, VPN, Adapter-Reset, Standby). Die Anfrage hat den Server nie erreicht. Gegenprobe Client: `nslookup noose.info 1.1.1.1` aus einem anderen Netz (Handy-Hotspot). Bei echtem App-Ausfall käme **502**, bei totem Host `ERR_CONNECTION_REFUSED`/`ERR_CONNECTION_TIMED_OUT` — nie `ERR_NAME_NOT_RESOLVED`. Das Reconnect-Modal fängt das ab; nichts zu tun. |
| **`ERR_CONNECTION_RESET` auf einzelne GUID-benannte Requests** (Uploads/Anhänge aus `App_Data/uploads`) | **Nur zusammen mit** dem Muster oben clientseitig (abgerissene Verbindung beim Netzwechsel). **Isoliert** — also ohne `ERR_NAME_NOT_RESOLVED` und bei stabiler WebSocket-Verbindung — serverseitig prüfen: App-Logs auf Exceptions im Datei-Endpoint, Proxy-Log auf `upstream prematurely closed connection`. |
