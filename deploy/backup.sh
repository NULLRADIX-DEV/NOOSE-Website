#!/bin/bash
# Taegliches Backup der NOOSE-Datenbanken (Prod + Demo) aus dem Container noose-db.
# Liegt auf dem Server unter /opt/noose/backup.sh, Cron (root):
#   15 4 * * * /opt/noose/backup.sh >> /var/log/noose-backup.log 2>&1
# Ergebnis: /root/backups/<db>-JJJJ-MM-TT.sql.gz, Aufbewahrung 30 Tage.
# Restore:  gunzip < /root/backups/noose-<datum>.sql.gz | docker exec -i noose-db mariadb noose
set -euo pipefail

DIR=/root/backups
DAYS=30
DATABASES="noose noose_demo"

umask 077
mkdir -p "$DIR"
# halbfertige Dumps nie liegen lassen, auch wenn das Skript abbricht
trap 'rm -f "$DIR"/*.sql.gz.tmp' EXIT
log() { echo "[$(date '+%F %T')] $*"; }
log "Backup startet"

failed=0
for db in $DATABASES; do
    f="$DIR/$db-$(date +%F).sql.gz"
    # Nur vollstaendige Dumps behalten (mariadb-dump schreibt am Ende "-- Dump completed")
    if docker exec noose-db mariadb-dump --single-transaction --quick --routines --events "$db" | gzip > "$f.tmp" \
       && zcat "$f.tmp" | tail -n 1 | grep -q "Dump completed"; then
        mv "$f.tmp" "$f"
        log "$db -> $f ($(du -h "$f" | cut -f1))"
    else
        log "FEHLER: Dump von $db fehlgeschlagen oder unvollstaendig"
        failed=1
    fi
done

# Nur die taeglichen Dumps dieses Skripts rotieren (manuelle Dumps aus backup-db.ps1 heissen <db>-<datum>_<zeit>)
find "$DIR" -maxdepth 1 -type f \( -name "noose-????-??-??.sql.gz" -o -name "noose_demo-????-??-??.sql.gz" \) -mtime +"$DAYS" -delete
[ "$failed" = 0 ] || { log "Backup mit Fehlern beendet"; exit 1; }
echo "[$(date '+%F %T')] Backup fertig"
