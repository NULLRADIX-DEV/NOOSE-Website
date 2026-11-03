# AGENTS.md — NOOSE-Website

Blazor Web App (.NET 10, Interactive Server only) on EF Core 9 + Pomelo → MariaDB/MySQL.
Identifiers and comments are English; domain vocabulary, UI text and docs are German.
Live: https://fibhq.com (the FIB, until November 2026 the NOOSE; the old name stays in repo, image and identifiers)

## Where the rules live

- `CLAUDE.md` is always loaded and is authoritative for conventions, architecture and gotchas.
- Before changing a specific area, read the matching file in `../NOOSE-Doc/claude-memory/` (table in
  `CLAUDE.md`) — it explains *why* a rule exists and what broke when it was ignored. `NOOSE-Doc` is a
  private neighbour repo; clone it next to this one.

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

# Deploy: GitHub Actions → Deploy → Run workflow (.github/workflows/deploy.yml, Kopie des Plattform-Templates)
# Image baut vorher der Workflow "Container-Image" (ghcr.io/nullradix-dev/noose-website:<sha>) — für den Commit abwarten
# Input environment = fib (the FIB at fibhq.com; the workflow default is still production, so pick fib explicitly)
# production and demo are the frozen NOOSE release on noose.info: never deploy master there, it would rewrite NOOSE data into FIB
# Commit leer = aktueller master, sonst SHA; Häkchen "rollback" = vorheriges Release
# Server prüft vor dem Umschalten, behält bei Startfehler das alte Release und dumpt vorher alle DBs. Server-Details: private Betriebsdoku
```

CI only builds the container image (`.github/workflows/image.yml`: PR = build check, master = push to GHCR); tests do not run in CI — local build + tests are the gate. Deploy is the manual workflow "Deploy" (see above).

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
  (`App_Data/keys`); losing it logs everyone out. It is a volume outside the image and survives every deploy.
- Culture is de-DE globally; times use `ToLocalTime()` against the server TZ.
- A user-visible feature needs a line in `Infrastructure/Changelog/ChangelogContent.cs`; a new menu
  entry needs a handbook article — tests enforce both.
- **The rename NOOSE → FIB is text only.** Namespace `NOOSE_Website`, classes `Noose*`/`Noosei*`, claim types
  `noose:*`, CSS classes `noose-*`, seed keys and the database name keep their names because they are stored or
  referenced elsewhere. User-visible text says FIB and FIBI.
- **`Rank` is persisted as int** (`Trainee=1` … `SpecialAgentInCharge=4`, `SectionChief=5`, `AssistantDirector=6`,
  `DeputyDirector=7`, `Director=8`); never reorder or insert. The home division is stored (`Agent.Division`), the
  Board is derived from the rank (`DivisionRules.Of`) and has no enum value. Every write of a rank goes through
  `DivisionRules.Apply` and `PromotionRules.RequireMayAssign`.
- **Case numbers** take their prefix from `CaseNumberService.AgencyPrefix`. Any lookup that accepts a typed or
  printed number goes through `CaseNumbers.Normalize`, so old `NOOSE-…` links and posters still resolve.
- **NOOSE → FIB wording swaps live in one table**, `Infrastructure/BrandSweep.cs`. The data migration
  (`Phase109_FibTexte`) builds its SQL from it; a new text column must be swept or listed as excluded in
  `BrandSweepPlan` — a test checks every string column of the model.
