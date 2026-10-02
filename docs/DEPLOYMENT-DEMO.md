# Demo-Instanz

`demo.noose.info` ist eine zweite NOOSE-Instanz, die jeder ohne Login ansehen kann und in der niemand etwas ändern kann. Sie läuft mit demselben Image wie Prod, aber als eigene App auf der Plattform, mit eigener MariaDB 10.11, eigener Umgebung und eigenem Speicherlimit (`deploy/demo/compose.yml`). Prod bleibt davon unberührt.

| | Prod | Demo |
|--|-----------|------|
| Domain | `noose.info` | `demo.noose.info` |
| Compose-Datei | `deploy/compose.yml` | `deploy/demo/compose.yml` |
| `environment` im Deploy | `production` | `demo` |
| Datenbank | eigene MariaDB der Prod-App | eigene MariaDB der Demo-App |

Ob eine Instanz die Demo ist, entscheidet nur die Konfiguration (`Demo__AutoSetup=true`), nie ein Flag in der Datenbank. Prod setzt den Wert in der Compose-Datei fest auf `false`, deshalb zeigt das Panel dort auch keinen Demo-Schalter.

## Voraussetzungen

- Der DNS-Eintrag `demo` zeigt per A-Record auf den Server. Einen AAAA-Eintrag braucht es nur, wenn auch `noose.info` über IPv6 erreichbar ist.
- In Discord ist der Redirect `https://demo.noose.info/signin-discord` eingetragen.
- Die Umgebung der Demo-App auf dem Server enthält den Connection String `Server=db;...` und `Demo__AutoSetup=true`.

## Ausrollen

Genau wie Prod ([Deployment](DEPLOYMENT.md#einen-stand-ausrollen)), nur mit `environment` = `demo`. Die App migriert ihre Datenbank beim Start selbst.

## Was beim Start passiert

Mit `Demo__AutoSetup=true` spielt die App beim Start die Demo-Daten ein, wenn sie noch fehlen, und schaltet den Demo-Modus an (`DemoAutoSetup.cs`). Im Demo-Modus wird jeder anonyme Besucher automatisch zum Demo-Agenten, der alles sehen, aber nichts ändern darf. Niemand loggt sich ein, Discord wird nicht gebraucht, und im Admin-Bereich ist nichts zu tun.

Ob das geklappt hat, zeigen die Logs der Demo. Dort stehen `Demo-AutoSetup: <n> Datensaetze geseedet.` und `Demo-AutoSetup: Demo-Modus aktiviert.`, bei einer schon befüllten Demo vielleicht nur die zweite Zeile. Steht dort `Demo-AutoSetup: Seeding fehlgeschlagen`, startet die Instanz ohne neue Beispieldaten. `GET /health` auf der Demo-Domain liefert `200 Healthy`.

## Betrieb

Logs, Neustarts und Sicherungen erledigt der Admin auf dem Server. Soll die Demo wieder weg, baut der Admin ihre App auf der Plattform ab. Danach kommen `deploy/demo/compose.yml` und der Demo-Eintrag im Deploy-Workflow aus dem Repo.
