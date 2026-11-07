#!/usr/bin/env bash
# Runs the browser tests (NOOSE-Website.E2E) against a local build in demo mode.
# Needs Docker: a throwaway MariaDB container "noose-e2e-db" on 127.0.0.1:3317 (root password "e2e", test-only).
# The app under test runs on :5080; a master build for the desktop comparison may run on :5081 (NOOSE_E2E_BASELINE_URL).
# Usage: scripts/handy-tests-local.sh [dotnet test --filter expression]
# Parallel runs: set NOOSE_E2E_PORT and NOOSE_E2E_DB per checkout (each gets its own app and database).
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PORT="${NOOSE_E2E_PORT:-5080}"
DB_PORT=3317
DB_NAME="${NOOSE_E2E_DB:-noose_e2e}"
LOG="${TMPDIR:-/tmp}/noose-e2e-app-${PORT}.log"

if ! docker ps --format '{{.Names}}' | grep -qx noose-e2e-db; then
  docker start noose-e2e-db >/dev/null 2>&1 || docker run -d --name noose-e2e-db -e MARIADB_ROOT_PASSWORD=e2e \
    -e MARIADB_DATABASE=noose_e2e -p "127.0.0.1:${DB_PORT}:3306" mariadb:10.11 \
    --character-set-server=utf8mb4 --collation-server=utf8mb4_general_ci >/dev/null
fi

for _ in $(seq 1 30); do docker exec noose-e2e-db mariadb -uroot -pe2e -e "CREATE DATABASE IF NOT EXISTS \`${DB_NAME}\` CHARACTER SET utf8mb4;" 2>/dev/null && break; sleep 2; done

# stop whatever listens on the test port (a previous run)
if command -v powershell.exe >/dev/null 2>&1; then
  powershell.exe -NoProfile -Command "Get-NetTCPConnection -LocalPort $PORT -State Listen -ErrorAction SilentlyContinue | ForEach-Object { Stop-Process -Id \$_.OwningProcess -Force -ErrorAction SilentlyContinue }" || true
else
  fuser -k "${PORT}/tcp" 2>/dev/null || true
fi

# build everything before the app starts: a running app locks its own .exe on Windows, so the test run must not rebuild it
if [ -d "$ROOT/NOOSE-Website.E2E" ]; then
  dotnet build "$ROOT/NOOSE-Website.E2E" -v q -nologo
  # Chromium for Playwright (no-op when already installed)
  pwsh "$ROOT/NOOSE-Website.E2E/bin/Debug/net10.0/playwright.ps1" install chromium
else
  dotnet build "$ROOT/NOOSE-Website/NOOSE-Website.csproj" -v q -nologo
fi
(
  cd "$ROOT/NOOSE-Website"
  ConnectionStrings__DefaultConnection="Server=127.0.0.1;Port=${DB_PORT};Database=${DB_NAME};User ID=root;Password=e2e;SslMode=None;" \
  Demo__AutoSetup=true ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://127.0.0.1:${PORT}" \
    nohup dotnet run --no-build --no-launch-profile >"$LOG" 2>&1 &
)

for _ in $(seq 1 60); do
  if [ "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:${PORT}/health")" = "200" ]; then break; fi
  sleep 2
done
curl -sf "http://127.0.0.1:${PORT}/health" >/dev/null || { echo "app did not start, see $LOG"; tail -40 "$LOG"; exit 1; }

export NOOSE_E2E_BASEURL="http://127.0.0.1:${PORT}"
if [ "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:5081/health)" = "200" ]; then
  export NOOSE_E2E_BASELINE_URL="http://127.0.0.1:5081"
fi

if [ -d "$ROOT/NOOSE-Website.E2E" ]; then
  if [ $# -gt 0 ]; then
    dotnet test "$ROOT/NOOSE-Website.E2E" --no-build --filter "$1"
  else
    dotnet test "$ROOT/NOOSE-Website.E2E" --no-build
  fi
fi
