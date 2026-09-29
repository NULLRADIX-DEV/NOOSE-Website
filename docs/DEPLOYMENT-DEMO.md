# Demo-Instanz aufsetzen — `demo.noose.info`

Zweite, **read-only** NOOSE-Instanz auf **demselben Server**, eigene DB, eigener Port, eigene Domain.
Produktiv (`noose.info`) bleibt komplett unberührt. Vom **Main-PC** abarbeiten — alle Befehle in **PowerShell**, aus dem **Repo-Root** (das Skript liegt in `scripts\`).

| | Produktiv | Demo |
|--|-----------|------|
| Domain | `noose.info` | `demo.noose.info` |
| Port (Kestrel) | `5000` | `5001` |
| DB | `noose` | `noose_demo` |
| systemd-Dienst | `noose` | `noose-demo` |
| App-Verzeichnis | `/var/www/noose` | `/var/www/noose-demo` |
| Env-Datei | `/etc/noose/noose.env` | `/etc/noose-demo/noose-demo.env` |

> Es ist **dasselbe Binary**. Unterschied nur: andere DB + anderer Port (Env) + andere Domain (nginx).
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
  per STRATO-VNC-Konsole nach `~/.ssh/authorized_keys`, oder über einen Rechner, der schon Zugang hat.

---

## Schritt 1 — DNS prüfen

```powershell
nslookup demo.noose.info
```
Muss `62.169.28.155` zurückgeben. Wenn nicht → DNS noch nicht propagiert, kurz warten (TLS in Schritt 4 braucht das).

---

## Schritt 2 — Server vorbereiten (Backup + DB + Env + Dienst + nginx)

Aktuellen Branch ziehen (enthält `setup-demo.sh`), Skript hochladen und ausführen:

```powershell
git pull
scp setup-demo.sh root@62.169.28.155:/tmp/setup-demo.sh
ssh root@62.169.28.155 "sed -i 's/\r$//' /tmp/setup-demo.sh && bash /tmp/setup-demo.sh"
```

Das Skript macht (idempotent, mehrfach ausführbar):
1. **Backup** der Produktiv-DB → `/root/backups/noose-prod-<datum>.sql.gz`
2. Demo-DB `noose_demo` + Rechte
3. Env `/etc/noose-demo/noose-demo.env` — Secrets aus Prod übernommen, nur Port→5001 & DB→`noose_demo`
4. App-Verzeichnis `/var/www/noose-demo`
5. systemd-Dienst `noose-demo` (registriert, Start kommt mit dem Deploy)
6. nginx-Site `demo.noose.info`

> Produktiv-DB/-Dienst/-Env/-nginx werden **nicht** verändert — nur gelesen (Backup) + nginx neu geladen.

---

## Schritt 3 — Demo-Instanz deployen

```powershell
.\scripts\deploy.ps1 -AppDir /var/www/noose-demo -Service noose-demo
```

Publisht das Binary, lädt es nach `/var/www/noose-demo`, startet `noose-demo`. Beim Start migriert die App
die leere `noose_demo` automatisch. (Für künftige Updates der **Produktiv**-Seite weiter einfach `.\scripts\deploy.ps1`.)

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
ssh root@62.169.28.155 "systemctl status noose-demo --no-pager | head -n 5"
ssh root@62.169.28.155 "curl -s -o /dev/null -w 'demo health: HTTP %{http_code}\n' http://127.0.0.1:5001/health"
```
Erwartet: Dienst `active (running)` und `HTTP 200`.

---

## Schritt 6 — Daten + Demo-Modus scharfschalten (einmalig)

> **Besucher loggen sich NIE ein.** Sobald der Demo-Modus an ist, schaltet die App jeden anonymen
> Besucher automatisch auf den read-only Demo-Agenten — alles sichtbar, kein Login, kein Discord.
> Der folgende Login ist **ein einziges Mal** nötig, nur für dich als Admin, zum Befüllen + Anschalten.

1. `https://demo.noose.info` öffnen → **als Admin via Discord einloggen** (deine Discord-ID ist via
   `Bootstrap__AdminDiscordId` aus der Prod-Env übernommen → du bist auf der Demo automatisch Admin).
2. Admin → System → **„Demo-Daten einspielen"** (3-Stufen-Bestätigung) → ~14 Fraktionen + 40 Personen.
3. Admin → System → **„Demo-Modus aktivieren"** (3-Stufen-Bestätigung).
4. Fertig. Ausloggen — ab jetzt ist alles öffentlich read-only, niemand muss sich mehr einloggen.

*(Optional, falls du sogar diesen einen Login vermeiden willst: ein Auto-Seed-Startflag wäre möglich —
das ist eine kleine Code-Änderung + Deploy. Bei Bedarf sagen.)*

---

## Updates später

- **Produktiv:** `.\scripts\deploy.ps1`
- **Demo:** `.\scripts\deploy.ps1 -AppDir /var/www/noose-demo -Service noose-demo`

## Nützliche Befehle

```powershell
ssh root@62.169.28.155 "journalctl -u noose-demo -f"          # Live-Logs Demo
ssh root@62.169.28.155 "systemctl restart noose-demo"          # Neustart Demo
ssh root@62.169.28.155 "ls -lh /root/backups"                  # Backups ansehen
```

## Demo wieder entfernen (falls je nötig)

```bash
systemctl disable --now noose-demo
rm -f /etc/systemd/system/noose-demo.service /etc/nginx/sites-enabled/noose-demo /etc/nginx/sites-available/noose-demo
systemctl daemon-reload && systemctl reload nginx
rm -rf /var/www/noose-demo /etc/noose-demo
mysql -e "DROP DATABASE noose_demo;"
```
