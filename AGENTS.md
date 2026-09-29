# AGENTS.md — NOOSE-Website

Blazor Web App (.NET 10, Interactive Server only) on EF Core 9 + Pomelo → MariaDB/MySQL.
Identifiers and comments are English; domain vocabulary, UI text and docs are German.
Live: https://noose.info

## Where the rules live

- `CLAUDE.md` is always loaded and is authoritative for conventions, architecture and gotchas.
- Before changing a specific area, read the matching file in `claude-memory/` (table in `CLAUDE.md`) —
  it explains *why* a rule exists and what broke when it was ignored.
- `docs/superpowers/{specs,plans}/` are dated design history, not current truth (e.g. an early plan
  says "do NOT add a test project" — `NOOSE-Website.Tests` exists). Trust code over those docs.

## Commands

All from the repo root unless noted.

```powershell
dotnet build NOOSE-Website.slnx                       # stop the dev server first (bin lock)

dotnet run   --project NOOSE-Website/NOOSE-Website.csproj   # http://localhost:5174
dotnet watch --project NOOSE-Website/NOOSE-Website.csproj run   # Hot Reload

# Tests: xunit + in-memory SQLite — no database required
dotnet test NOOSE-Website.Tests/NOOSE-Website.Tests.csproj
dotnet test NOOSE-Website.Tests/NOOSE-Website.Tests.csproj --filter "FullyQualifiedName~SearchCatalog"
# Coverage (no coverlet — it cannot instrument .NET 10):
dotnet test NOOSE-Website.Tests/NOOSE-Website.Tests.csproj --collect "Code Coverage" --settings docs/coverage.runsettings

# Deploy nach Produktion (root@62.169.28.155, Docker-Container 'noose', /opt/noose)
# Image baut GitHub Actions (ghcr.io/nullradix-dev/noose-website:<sha>) — Action "Container-Image" für den Commit vorher abwarten
.\scripts\deploy.ps1                # Prod: aktueller origin/master → compose.yml hochladen → Image ziehen → Container neu → /health-Check
.\scripts\deploy.ps1 -Target demo   # Demo-Instanz (noose-demo)
.\scripts\deploy.ps1 -Tag <sha>     # bestimmter Commit / Rollback (kurze SHA geht)
.\scripts\deploy.ps1 -NoPause       # ohne "Enter zum Schließen" (CI/Terminal)
```

CI only builds the container image (`.github/workflows/image.yml`: PR = build check, master = push to GHCR); tests do not run in CI — local build + tests are the gate.

### EF migrations (manifest lives in `scripts/`)

`dotnet tool restore` / `dotnet ef` **fail from the repo root** (no manifest there). Run them from
`scripts/`, and pass both projects because the startup project defaults to the current directory:

```powershell
cd scripts
dotnet tool restore                  # before every 'dotnet ef' — dotnet-ef is local, pinned 9.0.17
dotnet ef migrations add PhaseNN_<Name> `
    --project ../NOOSE-Website/NOOSE-Website.csproj `
    --startup-project ../NOOSE-Website/NOOSE-Website.csproj
```

Migrations are applied on app start (`db.Database.MigrateAsync()`), so `dotnet ef database update`
is normally unnecessary. The design-time factory pins EF tools to the local connection — migrations
can never hit production. Stop the dev server before adding a migration.

## Architecture

- `Program.cs` is the composition root — all DI registrations inline, grouped by build phase.
- `Services/` holds business logic **and the real authorization** (`Permission.Require*`, `Visibility`,
  `*Visibility`, `RecordsReference`). UI `AuthorizeView`s are not a substitute; write methods take a
  `ClaimsPrincipal actor` and call the guard first.
- One `AppDbContext`; all Fluent configuration lives in `OnModelCreating` (no `IEntityTypeConfiguration`).
- Always inject `IDbContextFactory<AppDbContext>` and create a short-lived context per operation —
  a circuit-long scoped context throws "A second operation was started on this context".
- `NOOSE-Website.Tests` cannot test `.razor` components (no bUnit) — put testable logic in the service layer.
- Routes/Docs are German (`/personen`, DB columns via `[Column]`); C# members are English.

## Traps

- **Comments:** English only, inline `//` with 2–3 words, no block comments, no "Phase X" references.
- **New/edit pages share one `*Editor.razor` file** → load in `OnParametersSetAsync` with a `_loadedId`
  guard, never in `OnInitializedAsync`.
- **Route changes need a real app start**, not just a build: two components on the same `@page` only
  throw at runtime when the routing table is built.
- **`ExecuteUpdate`/`ExecuteDelete`/raw SQL bypass the interceptors** → they skip audit stamping and the
  read-only barrier; call `Permission.RequireWriteAccess` and write a `ManualAudit.Row(...)` yourself.
- **Do not bump EF Core/Identity packages to 10.x** — Pomelo 9 only supports EF Core 9.
- **Never delete `NOOSE-Website/App_Data/`** — it holds uploads and the Data-Protection keys
  (`App_Data/keys`); losing it logs everyone out. `deploy.ps1` excludes it.
- Culture is de-DE globally; times use `ToLocalTime()` against the server TZ.
- A user-visible feature needs a line in `Infrastructure/Changelog/ChangelogContent.cs`; a new menu
  entry needs a handbook article — tests enforce both.
