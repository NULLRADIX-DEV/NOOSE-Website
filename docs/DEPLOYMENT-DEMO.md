# Demo-Instanz aufsetzen — `demo.noose.info`

Zweite, **read-only** NOOSE-Instanz auf **demselben Server**, eigene DB, eigener Port, eigene Domain.
Produktiv (`noose.info`) bleibt komplett unberührt. Läuft als Container (Compose-Dienst `noose-demo`, siehe `deploy/compose.yml`). Vom **Main-PC** abarbeiten — alle Befehle in **PowerShell**, aus dem **Repo-Root** (das Skript liegt in `scripts\`).

| | Produktiv | Demo |
|--|-----------|------|
| Domain | `noose.info` | `demo.noose.info` |
| Port (Kestrel) | `5000` | `5001` |
| DB | `noose` | `noose_demo` |
| Container | `noose` | `noose-demo` |
| App_Data (Volume) | `/opt/noose/data/prod` | `/opt/noose/data/demo` |
| Env-Datei | `/etc/noose/noose.env` | `/etc/noose-demo/noose-demo.env` |

> Es ist **dasselbe Image** (Tag `DEMO_TAG` statt `NOOSE_TAG`). Unterschied nur: andere DB + anderer Port (Env) + andere Domain (nginx). Beide Datenbanken liegen in derselben MariaDB (`noose-db`).
> Der Demo-Modus selbst ist nur ein Flag in der jeweiligen DB — auf `noose_demo` isoliert, kann Produktiv nie treffen.

---

## Voraussetzungen (sollten schon erledigt sein)

- [x] **DNS:** A-Record `demo` → `62.169.28.155`  (kein AAAA, außer noose.info läuft auch über IPv6)
- [x] **Discord:** Redirect `https://demo.noose.info/signin-discord` im Developer-Portal ergänzt
- [ ] **SSH-Zugang vom Main-PC** (siehe Schritt 0)

---

## Schritt 0 — SSH-Zugang prüfen

```powershell
ssh root@62.169.28.155 "hostname"
```

- Gibt **ohne Passwortabfrage** den Servernamen aus → passt, weiter mit Schritt 1.
- `Permission denied (publickey)` → der Main-PC hat keinen gültigen Key auf dem Server. Dann zuerst Key hinterlegen:
  ```powershell
  # erzeugt einen Key, falls noch keiner da ist (Enter fuer Default, leere Passphrase)
  ssh-keygen -t ed25519
  ```
  Den Public Key (`type $env:USERPROFILE\.ssh\id_ed25519.pub`) auf den Server bringen — entweder
  per Contabo-VNC-Konsole nach `~/.ssh/authorized_keys`, oder über einen Rechner, der schon Zugang hat.

---

## Schritt 1 — DNS prüfen

```powershell
nslookup demo.noose.info
```
Muss `62.169.28.155` zurückgeben. Wenn nicht → DNS noch nicht propagiert, kurz warten (TLS in Schritt 4 braucht das).

---

## Schritt 2 — Server vorbereiten

Nichts mehr per Skript: Docker, `/opt/noose/compose.yml`, die MariaDB (Datenbank `noose_demo`), die Env-Datei
`/etc/noose-demo/noose-demo.env` (u. a. `Demo__AutoSetup=true`) und die nginx-Site `demo.noose.info` sind Teil der
bestehenden Server-Einrichtung. Das frühere Einrichtungs-Skript (`setup-demo`, systemd-Dienst, `/var/www/noose-demo`) gibt es nicht mehr.

> Produktiv-DB/-Container/-Env/-nginx werden durch die Demo **nicht** verändert.

---

## Schritt 3 — Demo-Instanz deployen

```powershell
.\scripts\deploy.ps1 -Target demo
```

Vorher muss die GitHub Action „Container-Image“ für den Commit fertig sein. Das Skript zieht das Image, setzt `DEMO_TAG`
in `/opt/noose/.env`, startet `noose-demo` neu und prüft `/health`. Beim Start migriert die App
`noose_demo` automatisch. (Für die **Produktiv**-Seite: `.\scripts\deploy.ps1`.)

---

## Schritt 4 — TLS-Zertifikat

Sobald DNS (Schritt 1) aufgelöst ist:

```powershell
ssh root@62.169.28.155 "certbot --nginx -d demo.noose.info --non-interactive --agree-tos -m tristan.atze@gmail.com --redirect"
```
(Alternativ ohne Flags interaktiv: `ssh root@62.169.28.155 "certbot --nginx -d demo.noose.info"` und Fragen beantworten.)

---

## Schritt 5 — Health-Check

```powershell
ssh root@62.169.28.155 "cd /opt/noose && docker compose ps noose-demo"
ssh root@62.169.28.155 "curl -s -o /dev/null -w 'demo health: HTTP %{http_code}\n' http://127.0.0.1:5001/health"
```
Erwartet: Container `running` und `HTTP 200`.

---

## Schritt 6 — Daten + Demo-Modus (automatisch)

Mit `Demo__AutoSetup=true` in `/etc/noose-demo/noose-demo.env` spielt die App die Demo-Daten (~14 Fraktionen + 40 Personen)
beim Start selbst ein und schaltet den Demo-Modus an (`DemoAutoSetup.cs`). **Besucher loggen sich NIE ein:** im Demo-Modus
wird jeder anonyme Besucher automatisch der read-only Demo-Agent, alles ist sichtbar, kein Login, kein Discord.
Manuelle Schritte im Admin-Bereich sind nicht nötig.

Kontrolle nach dem Deploy:
```powershell
ssh root@62.169.28.155 "docker logs noose-demo 2>&1 | grep 'Demo-AutoSetup'"
```
Erwartet: `Demo-AutoSetup: <n> Datensaetze geseedet.` und `Demo-AutoSetup: Demo-Modus aktiviert.` (bei bereits
befüllter Demo evtl. nur die zweite Zeile). Bei `Demo-AutoSetup: Seeding fehlgeschlagen` startet die Instanz ohne neue Beispieldaten.

---

## Updates später

- **Produktiv:** `.\scripts\deploy.ps1`
- **Demo:** `.\scripts\deploy.ps1 -Target demo`

## Nützliche Befehle

```powershell
ssh root@62.169.28.155 "docker logs -f noose-demo"                          # Live-Logs Demo
ssh root@62.169.28.155 "cd /opt/noose && docker compose restart noose-demo"  # Neustart Demo
ssh root@62.169.28.155 "ls -lh /root/backups"                  # Backups ansehen
```

## Demo wieder entfernen (falls je nötig)

```bash
cd /opt/noose && docker compose stop noose-demo && docker compose rm -f noose-demo
# danach den Dienst noose-demo aus /opt/noose/compose.yml (deploy/compose.yml) entfernen
rm -f /etc/nginx/sites-enabled/noose-demo /etc/nginx/sites-available/noose-demo
systemctl reload nginx
rm -rf /etc/noose-demo /opt/noose/data/demo
docker exec noose-db mariadb -e "DROP DATABASE noose_demo;"
```
