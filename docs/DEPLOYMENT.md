# Deployment

Das FIB läuft als Container auf einer gemeinsamen Server-Plattform. Es gibt drei getrennte Apps, jede mit eigener MariaDB 10.11 und eigenem Speicherlimit. `fib` (`fibhq.com`) ist die aktuelle Fassung. `production` (`noose.info`) und `demo` (`demo.noose.info`) laufen weiter mit der NOOSE-Fassung vor der Umstellung und bekommen keine neuen Stände mehr. Hier steht, wie du einen Stand ausrollst, was die App von ihrer Umgebung erwartet und was hinter den typischen Fehlern steckt. Wie der Server selbst eingerichtet ist, steht in der privaten Betriebsdoku.

Die FIB-Instanz ist eine eigene App, weil die Umstellung Daten umschreibt und sich nicht zurückdrehen lässt: Sie ersetzt NOOSE durch FIB in allen Texten und Aktenzeichen und rechnet die Dienstgrade auf die neue Leiter um. Ihre Datenbank begann deshalb als Kopie der NOOSE-Datenbank. Die NOOSE-Instanzen bleiben unberührt, solange niemand dort ein FIB-Image ausrollt.

## Wie es zusammenhängt

```
Browser ──HTTPS──> Reverse-Proxy der Plattform (TLS, WebSockets für Blazor/SignalR)
                     ▼
                   App-Container (Kestrel)  ──>  MariaDB-Container "db"
```

Das Image `ghcr.io/nullradix-dev/noose-website:<commit-sha>` baut GitHub Actions, für alle drei Apps dasselbe. Jede App hat ihre Compose-Datei: `fib` nutzt `deploy/fib/compose.yml`, `production` `deploy/compose.yml` und `demo` `deploy/demo/compose.yml`. Die Datei der FIB-Instanz entspricht der von Prod. Image-Tag, Port und Netz setzt der Server selbst über `APP_COMMIT`, `APP_PORT` und `APP_SUBNET`, deshalb steht keine Server-Nummer im Repo. Secrets und die übrige Umgebung liegen nur auf dem Server.

Beim Start erledigt die App einiges selbst:

- Sie wählt ihre Verbindung, zuerst `ConnectionStrings:ProductionConnection`, sonst `DefaultConnection` (`Data/DatabaseConnectionResolver.cs`). Auf dem Server zeigt `ProductionConnection` auf den Compose-Dienst `db`.
- Sie wendet ausstehende EF-Migrationen an. Ein `dotnet ef database update` gegen den Server ist nie nötig. Beim ersten Start der FIB-Instanz laufen dabei `Phase108_FibAbteilungen` (Spalte für die Abteilung, Tabelle für den Schusswaffengebrauch), `Phase109_FibTexte` (ein Durchgang über alle Textspalten, die Tauschtabelle steht in `Infrastructure/BrandSweep.cs`) und `Phase110_FibRaenge` (Dienstgrade, Abteilungen, Einstellungen). Sie laufen vor dem ersten Request und brauchen länger als ein gewöhnlicher Start, warte also, bis `GET /health` antwortet.
- Sie vertraut dem Reverse-Proxy über `UseForwardedHeaders()`, zusätzlich dem Netz der App aus `ForwardedHeaders:KnownNetworks`, das die Compose-Datei setzt.
- Sie hält ihre Data-Protection-Schlüssel in `App_Data/keys`, neben den Uploads. `App_Data` liegt deshalb nicht im Image, sondern als Volume auf dem Server und überlebt jeden Deploy. Lösch es nie, sonst sind nach jedem Neustart alle Nutzer ausgeloggt.

## Einen Stand ausrollen

1. Die Änderung per Pull Request nach `master` bringen, der Branch ist geschützt.
2. Warten, bis der Workflow „Container-Image“ (`.github/workflows/image.yml`) für den Merge-Commit fertig ist.
3. Actions → „Deploy“ → „Run workflow“. Trag bei `environment` ausdrücklich `fib` ein. Der Vorgabewert des Workflows ist weiter `production`, und ein Lauf mit den Vorgaben würde den FIB-Stand auf die NOOSE-Instanz rollen. Commit leer lassen für den aktuellen `master`, sonst den Commit-SHA eintragen. „rollback“ bleibt aus.
4. Danach im Browser Strg+F5, damit er die neuen Assets lädt.

Der Server prüft vor dem Umschalten alles und lässt bei einem Startfehler das vorherige Release weiterlaufen. Vor jedem Wechsel sichert er alle Datenbanken des laufenden Releases. Ob die neue Version läuft, zeigt `GET /health`. Es prüft auch die Datenbank und liefert dann `200 Healthy`.

`production` und `demo` rollst du von `master` nicht mehr aus. Ein FIB-Image würde beim Start die NOOSE-Daten umschreiben, und dort gibt es kein Zurück außer der Sicherung. Der Workflow hindert dich nicht daran, die Regel liegt bei dir. Muss an einer dieser Instanzen doch etwas geschehen, nimm den SHA eines Commits aus der Zeit vor der Umstellung, nie das leere Commit-Feld.

Während des Umschaltens sehen eingeloggte Nutzer kurz das Reconnect-Modal, und `/_blazor/negotiate` antwortet mit 502. Das ist erwartet und dauert etwa 10 bis 25 Sekunden, bei Releases mit großen Migrationen länger.

Für einen Rollback startest du denselben Workflow mit dem Häkchen „rollback“. Er macht EF-Migrationen nicht rückgängig. Dafür gibt es die Sicherung vor jedem Wechsel, die der Admin auf dem Server zurückspielen kann. Auf `fib` rollst du nie einen Commit aus der Zeit vor der Umstellung aus: Er kennt die Dienstgrade 7 und 8 nicht und liest die umgeschriebenen Daten nur teilweise.

## Konfiguration

Secrets und Umgebung liegen nur auf dem Server, nie im Repo. Die Schlüssel schreibt man mit doppeltem Unterstrich:

- `ConnectionStrings__ProductionConnection` mit `Server=db;...`
- `Authentication__Discord__ClientId` und `Authentication__Discord__ClientSecret`
- optional `GoogleDrive__ClientId`, `GoogleDrive__ApiKey` und `GoogleDrive__AppId` für den Import über die Google-Anmeldung (siehe unten). Fehlt einer der drei Werte, blendet die App den Weg aus.
- `Bootstrap__AdminDiscordId`
- `Llm__ApiKey` für OpenRouter und `Llm__DeepSeek__ApiKey` für DeepSeek direkt. Welchen Anbieter die App nutzt, wählt der KI-Eigner unter `/einstellungen?tab=ki-anbieter`. Wer das ist, bestimmt `Ki__OwnerDiscordId`.
- `TZ=Europe/Berlin`. Ohne diese Zeile läuft der Container in UTC. Weil `.ToLocalTime()` in Blazor Server die Zeitzone des Servers nimmt, sind dann alle Zeiten 2 Stunden zu früh, kurz nach Mitternacht sogar am falschen Tag.

Prod und die FIB-Instanz setzen `Demo__AutoSetup=false` fest in ihrer Compose-Datei.

Die Discord-Benachrichtigungen schaltet ein Admin unter `/einstellungen?tab=discord` ein. In der Datenbank, die aus NOOSE kopiert wurde, sind sie zunächst aus, damit beide Instanzen nicht in denselben Kanal schreiben.

Nach einer Änderung der Umgebung muss der Container neu erstellt werden, ein Neustart reicht nicht. `restart` liest die Env-Datei nicht neu, und `TimeZoneInfo.Local` merkt sich der Prozess. Lass also den Deploy-Workflow laufen.

Für den Login braucht Discord im Developer Portal unter OAuth2 → Redirects die Adresse `https://fibhq.com/signin-discord`. Die NOOSE-Instanzen behalten ihre eigenen Einträge, `https://noose.info/signin-discord` und `https://demo.noose.info/signin-discord`, solange sie laufen.

### Google-Anmeldung für den Import

Google meldet niemanden beim FIB an. Agents verbinden Google nur, um ein Dokument oder eine Präsentation zu importieren, die nicht per Link freigegeben ist. Die App fragt nur den Scope `drive.file` an: Sie liest damit allein die Datei, die der Agent im Google-Fenster wählt. Ein Token speichert sie nicht.

Einrichtung in der [Google Cloud Console](https://console.cloud.google.com/):

1. Ein Projekt anlegen. Die **Projektnummer** (nur Ziffern, auf der Startseite des Projekts) wird `GoogleDrive__AppId`.
2. Unter „APIs & Dienste → Bibliothek“ die **Google Picker API** und die **Google Drive API** aktivieren.
3. Unter „Google Auth Platform“ den Zustimmungsbildschirm einrichten: Typ „Extern“, App-Name, Support-Adresse und die Domain `fibhq.com`. Als Scope nur `.../auth/drive.file` eintragen. Er gilt nicht als sensibel, deshalb entfällt Googles Sicherheitsprüfung; nur ein hochgeladenes Logo lässt Google das Branding prüfen. Unter „Branding“ Startseite `https://fibhq.com`, Datenschutzerklärung `https://fibhq.com/datenschutz`, Nutzungsbedingungen `https://fibhq.com/nutzungsbedingungen` und die autorisierte Domain `fibhq.com` eintragen; den Datenschutz-Link verlangt Google für öffentliche Apps, und die Erklärung beschreibt unter Ziffer 9 den Import. Fordert Google, die Domain zu bestätigen, geschieht das in der Google Search Console (DNS-Eintrag). Danach die App auf „In Produktion“ stellen, sonst kommen nur eingetragene Testnutzer hinein.
4. Unter „Clients“ einen OAuth-Client vom Typ „Webanwendung“ anlegen. Autorisierte JavaScript-Quelle ist `https://fibhq.com`. Eine Weiterleitungs-URI braucht er nicht. Die Client-ID wird `GoogleDrive__ClientId`.
5. Unter „Anmeldedaten“ einen API-Schlüssel anlegen und einschränken: Anwendungen auf „Websites“ mit `https://fibhq.com/*` **und** `https://docs.google.com/*` (der Picker läuft in einem Rahmen von docs.google.com, ohne diesen Eintrag meldet er „API developer key is invalid“), APIs auf die Google Picker API und die Google Drive API. Er wird `GoogleDrive__ApiKey`.

Für lokale Tests nicht den Prod-Client erweitern, sondern im selben Projekt einen zweiten Client und Schlüssel anlegen: JavaScript-Quellen `http://localhost` und `http://localhost:5174`, Schlüssel für `http://localhost/*` und `https://docs.google.com/*`. Die Werte kommen in die User-Secrets.

Läuft die NOOSE-Instanz noch mit Google-Import, bleiben ihre Adressen in Branding, Client und Schlüssel daneben eingetragen. Das Projekt kennt dann beide Domains.

Client-ID, API-Schlüssel und Projektnummer sind keine Geheimnisse, der Browser bekommt sie ohnehin. Trotzdem stehen sie nur in der Env-Datei auf dem Server, damit die Instanzen getrennt bleiben.

Setzt ein Reverse-Proxy eine Content-Security-Policy, braucht sie:

- `script-src`: `https://accounts.google.com/gsi/client` und `https://apis.google.com`
- `connect-src`: `https://accounts.google.com/gsi/`
- `style-src`: `https://accounts.google.com/gsi/style`
- `frame-src`: `https://docs.google.com` und `https://accounts.google.com/gsi/`

Eine `Cross-Origin-Opener-Policy` muss `same-origin-allow-popups` sein, sonst bleibt das Google-Fenster leer.

## Das Image

`.github/workflows/image.yml` baut bei einem Pull Request nur, um das Dockerfile zu prüfen. Bei einem Push auf `master` baut es und lädt das Image nach GHCR, mit dem vollen Commit-SHA als Tag und zusätzlich `latest`. Das `Dockerfile` baut mit `sdk:10.0` und läuft auf `aspnet:10.0` (Ubuntu 24.04) als `www-data`, die Quill-Assets prüft es schon im Build. Ausgerollt wird nie automatisch, das bleibt der bewusste Schritt mit „Deploy“.

## Wenn etwas nicht geht

- Bricht der Start mit `Connect Timeout expired` ab und startet der Container immer wieder neu, ist die Datenbank nicht erreichbar. Der Connection String muss auf den Compose-Dienst `db` zeigen (`Server=db`), und der DB-Container muss laufen. Eine fremde, gemanagte Datenbank erreicht der Server nicht.
- Meldet die App `Kein Connection-String konfiguriert`, ist weder `ProductionConnection` noch `DefaultConnection` gesetzt. Der Admin ergänzt die Umgebung, danach läuft der Deploy-Workflow.
- `Failed to determine the https port for redirect` im Log ist harmlos. Die Warnung kommt nur bei direkten http-Anfragen an Kestrel, über den Reverse-Proxy mit TLS taucht sie nicht auf.
- Sagt Discord beim Login „invalid redirect_uri“, fehlt `https://fibhq.com/signin-discord` im Developer Portal.
- Sind alle Zeiten 2 Stunden zu früh oder am falschen Tag, fehlt `TZ=Europe/Berlin` in der Umgebung. Nach dem Ergänzen läuft der Deploy-Workflow, damit der Container neu entsteht.
- 502 Bad Gateway heißt, die App läuft nicht. Der Admin sieht in ihre Logs. Ist ein Deploy am Start gescheitert, läuft das vorherige Release weiter.
- Scheitert der Deploy, weil das Image fehlt, ist der Workflow „Container-Image“ für den Commit noch nicht fertig oder fehlgeschlagen. Abwarten oder nachsehen, dann neu starten.
- Sind Nutzer nach jedem Deploy ausgeloggt, sind die Data-Protection-Schlüssel weg. `App_Data` muss als Volume eingebunden bleiben.
- Zeigt die Konsole `ERR_NAME_NOT_RESOLVED` oder `ERR_NETWORK_CHANGED`, schließt der WebSocket mit `1006` und erholt sich danach von selbst, liegt es fast immer am Client. Der Browser konnte `fibhq.com` nicht auflösen oder hat abgebrochen, weil sich das Netz geändert hat, etwa durch WLAN-Wechsel, VPN oder Standby. Die Anfrage kam nie beim Server an. Prüfen lässt sich das mit `nslookup fibhq.com 1.1.1.1` aus einem anderen Netz, etwa dem Hotspot des Handys. Fällt die App wirklich aus, kommt 502, bei einem toten Host `ERR_CONNECTION_REFUSED` oder `ERR_CONNECTION_TIMED_OUT`, aber nie `ERR_NAME_NOT_RESOLVED`. Das Reconnect-Modal fängt den Fall ab, zu tun ist nichts.
- Bekommen einzelne Anfragen mit GUID im Namen `ERR_CONNECTION_RESET`, das sind Uploads und Anhänge aus `App_Data/uploads`, gehört das zusammen mit dem Fall davor zum Client. Kommt es allein vor, bei stabiler WebSocket-Verbindung und ohne `ERR_NAME_NOT_RESOLVED`, sieht der Admin nach: in den App-Logs nach Exceptions im Datei-Endpoint und im Log des Proxys nach `upstream prematurely closed connection`.
