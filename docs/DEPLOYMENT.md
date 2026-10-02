# Deployment

NOOSE läuft als Container auf einer gemeinsamen Server-Plattform. Prod (`noose.info`) und Demo (`demo.noose.info`) sind zwei getrennte Apps, jede mit eigener MariaDB 10.11 und eigenem Speicherlimit. Hier steht, wie du einen Stand ausrollst, was die App von ihrer Umgebung erwartet und was hinter den typischen Fehlern steckt. Wie der Server selbst eingerichtet ist, steht in der privaten Betriebsdoku.

## Wie es zusammenhängt

```
Browser ──HTTPS──> Reverse-Proxy der Plattform (TLS, WebSockets für Blazor/SignalR)
                     ▼
                   App-Container (Kestrel)  ──>  MariaDB-Container "db"
```

Das Image `ghcr.io/nullradix-dev/noose-website:<commit-sha>` baut GitHub Actions. Prod nutzt `deploy/compose.yml`, die Demo `deploy/demo/compose.yml`. Image-Tag, Port und Netz setzt der Server selbst über `APP_COMMIT`, `APP_PORT` und `APP_SUBNET`, deshalb steht keine Server-Nummer im Repo. Secrets und die übrige Umgebung liegen nur auf dem Server.

Beim Start erledigt die App einiges selbst:

- Sie wählt ihre Verbindung, zuerst `ConnectionStrings:ProductionConnection`, sonst `DefaultConnection` (`Data/DatabaseConnectionResolver.cs`). Auf dem Server zeigt `ProductionConnection` auf den Compose-Dienst `db`.
- Sie wendet ausstehende EF-Migrationen an. Ein `dotnet ef database update` gegen Prod ist nie nötig.
- Sie vertraut dem Reverse-Proxy über `UseForwardedHeaders()`, zusätzlich dem Netz der App aus `ForwardedHeaders:KnownNetworks`, das die Compose-Datei setzt.
- Sie hält ihre Data-Protection-Schlüssel in `App_Data/keys`, neben den Uploads. `App_Data` liegt deshalb nicht im Image, sondern als Volume auf dem Server und überlebt jeden Deploy. Lösch es nie, sonst sind nach jedem Neustart alle Nutzer ausgeloggt.

## Einen Stand ausrollen

1. Die Änderung per Pull Request nach `master` bringen, der Branch ist geschützt.
2. Warten, bis der Workflow „Container-Image“ (`.github/workflows/image.yml`) für den Merge-Commit fertig ist.
3. Actions → „Deploy“ → „Run workflow“. Bei `environment` steht `production` für Prod oder `demo` für die [Demo](DEPLOYMENT-DEMO.md). Commit leer lassen für den aktuellen `master`, sonst den Commit-SHA eintragen. „rollback“ bleibt aus.
4. Danach im Browser Strg+F5, damit er die neuen Assets lädt.

Der Server prüft vor dem Umschalten alles und lässt bei einem Startfehler das vorherige Release weiterlaufen. Vor jedem Wechsel sichert er alle Datenbanken des laufenden Releases. Ob die neue Version läuft, zeigt `GET /health`. Es prüft auch die Datenbank und liefert dann `200 Healthy`.

Während des Umschaltens sehen eingeloggte Nutzer kurz das Reconnect-Modal, und `/_blazor/negotiate` antwortet mit 502. Das ist erwartet und dauert etwa 10 bis 25 Sekunden, bei Releases mit großen Migrationen länger.

Für einen Rollback startest du denselben Workflow mit dem Häkchen „rollback“. Er macht EF-Migrationen nicht rückgängig. Dafür gibt es die Sicherung vor jedem Wechsel, die der Admin auf dem Server zurückspielen kann.

## Konfiguration

Secrets und Umgebung liegen nur auf dem Server, nie im Repo. Die Schlüssel schreibt man mit doppeltem Unterstrich:

- `ConnectionStrings__ProductionConnection` mit `Server=db;...`
- `Authentication__Discord__ClientId` und `Authentication__Discord__ClientSecret`
- `Bootstrap__AdminDiscordId`
- `Llm__ApiKey` für OpenRouter und `Llm__DeepSeek__ApiKey` für DeepSeek direkt. Welchen Anbieter die App nutzt, wählt der KI-Eigner unter `/einstellungen?tab=ki-anbieter`. Wer das ist, bestimmt `Ki__OwnerDiscordId`.
- `TZ=Europe/Berlin`. Ohne diese Zeile läuft der Container in UTC. Weil `.ToLocalTime()` in Blazor Server die Zeitzone des Servers nimmt, sind dann alle Zeiten 2 Stunden zu früh, kurz nach Mitternacht sogar am falschen Tag.

Prod setzt `Demo__AutoSetup=false` fest in `deploy/compose.yml`.

Nach einer Änderung der Umgebung muss der Container neu erstellt werden, ein Neustart reicht nicht. `restart` liest die Env-Datei nicht neu, und `TimeZoneInfo.Local` merkt sich der Prozess. Lass also den Deploy-Workflow laufen.

Für den Login braucht Discord im Developer Portal unter OAuth2 → Redirects die Adresse `https://noose.info/signin-discord`, für die Demo `https://demo.noose.info/signin-discord`.

## Das Image

`.github/workflows/image.yml` baut bei einem Pull Request nur, um das Dockerfile zu prüfen. Bei einem Push auf `master` baut es und lädt das Image nach GHCR, mit dem vollen Commit-SHA als Tag und zusätzlich `latest`. Das `Dockerfile` baut mit `sdk:10.0` und läuft auf `aspnet:10.0` (Ubuntu 24.04) als `www-data`, die Quill-Assets prüft es schon im Build. Ausgerollt wird nie automatisch, das bleibt der bewusste Schritt mit „Deploy“.

## Wenn etwas nicht geht

- Bricht der Start mit `Connect Timeout expired` ab und startet der Container immer wieder neu, ist die Datenbank nicht erreichbar. Der Connection String muss auf den Compose-Dienst `db` zeigen (`Server=db`), und der DB-Container muss laufen. Eine fremde, gemanagte Datenbank erreicht der Server nicht.
- Meldet die App `Kein Connection-String konfiguriert`, ist weder `ProductionConnection` noch `DefaultConnection` gesetzt. Der Admin ergänzt die Umgebung, danach läuft der Deploy-Workflow.
- `Failed to determine the https port for redirect` im Log ist harmlos. Die Warnung kommt nur bei direkten http-Anfragen an Kestrel, über den Reverse-Proxy mit TLS taucht sie nicht auf.
- Sagt Discord beim Login „invalid redirect_uri“, fehlt `https://noose.info/signin-discord` im Developer Portal.
- Sind alle Zeiten 2 Stunden zu früh oder am falschen Tag, fehlt `TZ=Europe/Berlin` in der Umgebung. Nach dem Ergänzen läuft der Deploy-Workflow, damit der Container neu entsteht.
- 502 Bad Gateway heißt, die App läuft nicht. Der Admin sieht in ihre Logs. Ist ein Deploy am Start gescheitert, läuft das vorherige Release weiter.
- Scheitert der Deploy, weil das Image fehlt, ist der Workflow „Container-Image“ für den Commit noch nicht fertig oder fehlgeschlagen. Abwarten oder nachsehen, dann neu starten.
- Sind Nutzer nach jedem Deploy ausgeloggt, sind die Data-Protection-Schlüssel weg. `App_Data` muss als Volume eingebunden bleiben.
- Zeigt die Konsole `ERR_NAME_NOT_RESOLVED` oder `ERR_NETWORK_CHANGED`, schließt der WebSocket mit `1006` und erholt sich danach von selbst, liegt es fast immer am Client. Der Browser konnte `noose.info` nicht auflösen oder hat abgebrochen, weil sich das Netz geändert hat, etwa durch WLAN-Wechsel, VPN oder Standby. Die Anfrage kam nie beim Server an. Prüfen lässt sich das mit `nslookup noose.info 1.1.1.1` aus einem anderen Netz, etwa dem Hotspot des Handys. Fällt die App wirklich aus, kommt 502, bei einem toten Host `ERR_CONNECTION_REFUSED` oder `ERR_CONNECTION_TIMED_OUT`, aber nie `ERR_NAME_NOT_RESOLVED`. Das Reconnect-Modal fängt den Fall ab, zu tun ist nichts.
- Bekommen einzelne Anfragen mit GUID im Namen `ERR_CONNECTION_RESET`, das sind Uploads und Anhänge aus `App_Data/uploads`, gehört das zusammen mit dem Fall davor zum Client. Kommt es allein vor, bei stabiler WebSocket-Verbindung und ohne `ERR_NAME_NOT_RESOLVED`, sieht der Admin nach: in den App-Logs nach Exceptions im Datei-Endpoint und im Log des Proxys nach `upstream prematurely closed connection`.
