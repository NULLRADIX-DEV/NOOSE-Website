# Demo-Instanz

`demo.noose.info` ist die zweite NOOSE-Instanz, die jeder ohne Login ansehen kann und in der niemand etwas ändern kann. Seit der Umstellung auf das FIB läuft sie unverändert mit der NOOSE-Fassung und bekommt keine neuen Stände mehr. Eine Demo mit dem FIB-Stand gibt es nicht, `demo.fibhq.com` ist bewusst nicht eingerichtet. Dieses Dokument hält fest, wie die Demo aufgebaut ist und was der Demo-Modus im Code tut, falls später wieder eine entsteht.

Alle drei Instanzen laufen mit demselben Image-Aufbau, aber als eigene Apps der Plattform, jede mit eigener MariaDB 10.11, eigener Umgebung und eigenem Speicherlimit.

| | Prod | Demo | FIB |
|--|------|------|-----|
| Domain | `noose.info` | `demo.noose.info` | `fibhq.com` |
| Stand | NOOSE, eingefroren | NOOSE, eingefroren | aktuell |
| Compose-Datei | `deploy/compose.yml` | `deploy/demo/compose.yml` | `deploy/fib/compose.yml` |
| `environment` im Deploy | `production` | `demo` | `fib` |

Ob eine Instanz die Demo ist, entscheidet nur die Konfiguration (`Demo__AutoSetup=true`), nie ein Flag in der Datenbank. Prod und die FIB-Instanz setzen den Wert in der Compose-Datei fest auf `false`, deshalb zeigt das Panel dort auch keinen Demo-Schalter.

## Ausrollen

Gar nicht mehr von `master`. Das Environment `demo` gehört der NOOSE-Demo, und ein FIB-Image würde ihre Daten beim Start umschreiben (siehe [Deployment](DEPLOYMENT.md#einen-stand-ausrollen)). Muss an der NOOSE-Demo etwas geschehen, rollst du den SHA eines Commits aus der Zeit vor der Umstellung aus.

## Eine neue Demo aufsetzen

Soll es wieder eine Demo geben, braucht sie eine eigene App mit eigenem Environment und eigenem Deploy-Schlüssel, `demo` ist vergeben. `deploy/demo/compose.yml` dient als Muster. Die Umgebung der App enthält den Connection String `Server=db;...` und `Demo__AutoSetup=true`. Die Domain braucht einen DNS-Eintrag per A-Record, einen AAAA-Eintrag nur, wenn die Hauptdomain auch über IPv6 erreichbar ist. In Discord gehört der Redirect `https://<domain>/signin-discord` ins Developer Portal.

## Was beim Start passiert

Mit `Demo__AutoSetup=true` spielt die App beim Start die Demo-Daten ein, wenn sie noch fehlen, und schaltet den Demo-Modus an (`DemoAutoSetup.cs`). Im Demo-Modus wird jeder anonyme Besucher automatisch zum Demo-Agenten, der alles sehen, aber nichts ändern darf. Niemand loggt sich ein, Discord wird nicht gebraucht, und im Admin-Bereich ist nichts zu tun. Die Beispieldaten sprechen von FIB.

Ob das geklappt hat, zeigen die Logs der Demo. Dort stehen `Demo-AutoSetup: <n> Datensaetze geseedet.` und `Demo-AutoSetup: Demo-Modus aktiviert.`, bei einer schon befüllten Demo vielleicht nur die zweite Zeile. Steht dort `Demo-AutoSetup: Seeding fehlgeschlagen`, startet die Instanz ohne neue Beispieldaten. `GET /health` auf der Demo-Domain liefert `200 Healthy`.

## Betrieb

Logs, Neustarts und Sicherungen erledigt der Admin auf dem Server. Soll die NOOSE-Demo wieder weg, baut der Admin ihre App auf der Plattform ab. Danach kommen `deploy/demo/compose.yml` und der Demo-Eintrag im Deploy-Workflow aus dem Repo.
