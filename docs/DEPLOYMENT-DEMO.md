# Demo-Instanz — `demo.noose.info`

Zweite, **read-only** NOOSE-Instanz. Sie ist eine **eigene App** auf der Server-Plattform mit eigenem Linux-User,
eigenem rootless Docker, eigener MariaDB 10.11 (Compose-Dienst `db`) und eigenem Speicherlimit (`deploy/demo/compose.yml`).
Produktiv (`noose.info`, `deploy/compose.yml`) bleibt komplett unberührt.

> Server-Details (Pfade, Ports, Benutzer, Backups) stehen in der privaten Betriebsdoku.

| | Produktiv | Demo |
|--|-----------|------|
| Domain | `noose.info` | `demo.noose.info` |
| Compose-Datei | `deploy/compose.yml` | `deploy/demo/compose.yml` |
| Deploy-Input `environment` | `production` | `demo` |
| Datenbank | eigene MariaDB der Prod-App | eigene MariaDB der Demo-App |

> Es ist **dasselbe Image** wie in Prod. Unterschied nur: eigene App mit eigener DB, eigener Env und eigener Domain.
> Ob eine Instanz die Demo ist, entscheidet nur die Konfiguration (`Demo__AutoSetup=true`), nie ein Flag in der DB. Prod setzt `false` fest in der Compose-Datei und zeigt im Panel keinen Demo-Schalter.

---

## Voraussetzungen

- **DNS:** A-Record `demo` zeigt auf den Server (kein AAAA, außer noose.info läuft auch über IPv6)
- **Discord:** Redirect `https://demo.noose.info/signin-discord` im Developer-Portal eingetragen
- Server-Env der Demo-App (nur auf dem Server, nie im Repo) mit Connection-String `Server=db;...` und `Demo__AutoSetup=true`

---

## Deploy

**Actions → Deploy → Run workflow** mit `environment` = `demo` (`.github/workflows/deploy.yml`).

- Commit leer = aktueller `master`, sonst Commit-SHA.
- Häkchen „rollback“ = zurück auf das vorherige Release.
- Vorher muss der Workflow „Container-Image“ für den Commit fertig sein.
- Der Server prüft vor dem Umschalten alles, behält bei einem Startfehler das vorherige Release und dumpt vorher alle
  Datenbanken des laufenden Releases (ein Rollback macht EF-Migrationen nicht rückgängig).
- Beim Start migriert die App die Demo-DB automatisch.
- Für die **Produktiv**-Seite: derselbe Workflow mit `environment` = `production`.

---

## Daten + Demo-Modus (automatisch)

`Demo__AutoSetup` kommt aus der Server-Env der Demo-App (`true`). Prod erzwingt dagegen `Demo__AutoSetup=false` in
`deploy/compose.yml`. Mit `true` spielt die App die Demo-Daten (~14 Fraktionen + 40 Personen) beim Start selbst ein und
schaltet den Demo-Modus an (`DemoAutoSetup.cs`). **Besucher loggen sich NIE ein:** im Demo-Modus wird jeder anonyme
Besucher automatisch der read-only Demo-Agent, alles ist sichtbar, kein Login, kein Discord. Manuelle Schritte im
Admin-Bereich sind nicht nötig.

Kontrolle nach dem Deploy: in den App-Logs der Demo nach `Demo-AutoSetup` suchen. Erwartet:
`Demo-AutoSetup: <n> Datensaetze geseedet.` und `Demo-AutoSetup: Demo-Modus aktiviert.` (bei bereits befüllter Demo
evtl. nur die zweite Zeile). Bei `Demo-AutoSetup: Seeding fehlgeschlagen` startet die Instanz ohne neue Beispieldaten.
Health: `GET /health` der Demo-Domain liefert `200 Healthy`.

---

## Betrieb

- **Logs, Neustart, Backups:** Sache des Admins auf dem Server, siehe private Betriebsdoku. DB-Backups laufen nächtlich
  über 1Panel; Dumps und Restores macht der Admin.
- **Demo wieder entfernen:** App auf der Plattform abbauen (Admin), danach `deploy/demo/compose.yml` und den
  Demo-Eintrag im Deploy-Workflow aus dem Repo entfernen.
