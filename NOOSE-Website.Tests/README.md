# NOOSE-Website.Tests

xUnit test project for the NOOSE-Website Blazor app. Targets `net10.0`, references the web
project, and covers the business logic in `Services/`, `Authorization/`, and `Models/`.

## Layout

- `Infrastructure/` — shared test helpers:
  - `SqliteTestContext` — an open in-memory SQLite database with the full EF schema
    (`EnsureCreated`), FK enforcement off (the app runs on MySQL; tests exercise logic, not
    referential integrity). Exposes `Factory` (an `IDbContextFactory<AppDbContext>`) and
    `NewContext()`.
  - `Seed` — factories for the most-referenced entities (each `.CaseNumber` is unique).
  - `ClaimsPrincipalBuilder` — fluent builder for agent/partner `ClaimsPrincipal`s.
  - `TestParallelization.cs` — disables cross-collection parallelism (shared in-memory
    connections + a global seed counter are not parallel-safe).
- `Authorization/`, `Services/`, `Models/` — pure-logic unit tests.
- `Services/Integration/` — service tests backed by `SqliteTestContext`, with collaborators
  substituted via NSubstitute (`ICaseNumberService` is stubbed because the real one uses
  MySQL-only `ON DUPLICATE KEY` SQL).

## Run the tests

```bash
dotnet test NOOSE-Website.Tests/NOOSE-Website.Tests.csproj
```

## Browser tests (NOOSE-Website.E2E)

`NOOSE-Website.E2E` drives a real Chromium (Playwright) against a running app in demo mode
(`Demo:AutoSetup`, signed in as the demo Director; no other roles are tested). Without
`NOOSE_E2E_BASEURL` every test is skipped, so a plain `dotnet test` stays green.

- `MobileOverflowTests.Every_static_route_fits_375px` — every `[Route]` without parameters (read by
  reflection, minus `/Account/*`, `/Error`, `/not-found` and print routes) at 375×812 must not be
  wider than the viewport. The message lists the outermost elements that stick out.
- `MobileOverflowTests.Record_detail_fits_375px` — opens each list in `RouteCatalog.DetailSeeds`,
  follows the first record link and checks the detail page the same way.
- `DesktopBaselineTests.Desktop_matches_master` — at 1280×900 a fixed set of routes (plus `/personen/{id}/druck` and a
  certificate with print media emulation) is screenshotted on the branch and on a `master` build
  (`NOOSE_E2E_BASELINE_URL`) and compared pixel by pixel (at most 0.1 % differing pixels). Times, dates, relative
  times and the build version are masked on both sides; lists whose order depends on equal timestamps are sorted by
  case number first. On a difference both shots and a diff image go to the artifacts folder. Skipped without both URLs.

Screenshots of failing routes go to `NOOSE_E2E_ARTIFACTS` (default `bin/.../e2e-artifacts`).

### Run locally

Needs Docker, bash (Git Bash on Windows) and PowerShell 7 (`pwsh`, for the Playwright installer):

```bash
scripts/handy-tests-local.sh                                  # all browser tests
scripts/handy-tests-local.sh "DisplayName~/personen"          # a dotnet test --filter expression
scripts/handy-tests-local.sh 'DisplayName~%22/dashboard%22|DisplayName~%22/personen%22\)'   # exactly these two routes
```

A case is named like `Every_static_route_fits_375px(route: "/personen")`, so `DisplayName~/personen` also hits
`/personen/neu` and every other route below it. To match one route exactly, include the quotes: write them as `%22`
(a plain `"` breaks the MSBuild property `dotnet test` passes the filter in) and escape `(`/`)` as `\(`/`\)`.

The script starts (or reuses) a throwaway MariaDB container `noose-e2e-db` on `127.0.0.1:3317`
(root password `e2e`, test only), creates the database, stops whatever listens on the test port,
builds the app and the test project, installs Chromium, starts the app in demo mode on
`http://127.0.0.1:5080`, waits for `/health` and runs the tests with `--no-build` (on Windows the
running app locks its own `.exe`, so nothing may rebuild it during the run). The app log is in
`$TMPDIR/noose-e2e-app-<port>.log`. Several checkouts can run side by side with their own app and
database: `NOOSE_E2E_PORT=5082 NOOSE_E2E_DB=noose_e2e_2 scripts/handy-tests-local.sh`. If an app
answers on `:5081`, its address is passed as `NOOSE_E2E_BASELINE_URL` (a `master` build for the
desktop comparison).

### CI

`.github/workflows/handy-tests.yml` runs on every pull request and on `master`: job `unit` runs this
project, job `handy` publishes the app, starts it in demo mode against a fresh `mariadb:10.11`
service and runs `NOOSE-Website.E2E`; the TRX file, the screenshots and the app log are uploaded as
the artifact `handy-tests`. Job `desktop` publishes `master` and the branch side by side (databases
`noose_base` and `noose_head` in one MariaDB service, apps on 5081 and 5080, both in demo mode) and runs
`DesktopBaselineTests`; results go to the artifact `desktop-tests`. Until the mobile refactor is finished,
`handy` and `desktop` only report (`continue-on-error: true`).

## Coverage

Coverage uses the built-in **Microsoft Code Coverage** engine — coverlet is not used because its
Mono.Cecil build cannot instrument .NET 10 assemblies.

```bash
dotnet test NOOSE-Website.Tests/NOOSE-Website.Tests.csproj \
    --collect "Code Coverage;Format=cobertura"
python NOOSE-Website.Tests/coverage/coverage-summary.py \
    <path printed above, e.g. NOOSE-Website.Tests/TestResults/<guid>/*.cobertura.xml>
```

`coverage-summary.py` reports line coverage for `Services` / `Authorization` / `Models` only.

### Scope & exclusions

Coverage is measured over the hand-written logic namespaces. Excluded by design:

- **Generated / non-logic:** EF migrations, Razor components, `Program.cs`, plain entity/DTO
  types.
- **`[ExcludeFromCodeCoverage]` in the web project** (genuinely not unit-testable):
  `AuthorizationRegistration` (DI composition), `DiscordWebhookService` (outbound HTTP),
  `DemoDataSeed` / `DemoDataService` (dev-only demo fixtures).

### Latest result

| Namespace     | Line coverage |
| ------------- | ------------- |
| Services      | 91.4 %        |
| Authorization | 100 %         |
| Models        | 91.3 %        |
| **Total**     | **91.4 %**    |
