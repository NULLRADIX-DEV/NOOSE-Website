# CLAUDE.md — NOOSE-Website

Zentrale Akten-/Intelligence-Datenbank für die **NOOSE** (National Office of Security Enforcement),
eine fiktive Behörde auf einem FiveM/GTA-RP-Server. Die Seite ersetzt verstreute Discord-Threads
durch eine zentrale, durchsuchbare, bidirektional verlinkte Akten-Datenbank: pro Person und pro
Fraktion eine Akte, in der alles zusammenläuft. **Codebase ist anglisiert (englische Identifier),
aber Domänen-Vokabular, UI-Texte, Kommentare und Planungsdocs sind Deutsch.** Live: https://noose.info

## Detailwissen (`claude-memory/`)

Diese Datei hält, was für **jede** Änderung gilt. Bereichswissen — vor allem die Begründungen
hinter den Regeln des öffentlichen Bereichs — liegt in `claude-memory/`. **Lies die passende
Datei, bevor du im jeweiligen Bereich etwas änderst**; sie erklärt, warum eine Regel existiert,
und genau das verhindert die Fehler, die dort schon einmal passiert sind.

| Datei | Lies sie, wenn du … |
|---|---|
| [`oeffentlich-grundlagen.md`](claude-memory/oeffentlich-grundlagen.md) | **irgendetwas** unter `Components/Pages/Public\|Portal/` oder `Services/Public/` anfasst: Bürgerkonto, Modul-Schalter, Not-Aus, `PublicVisibility`, `PublicRoutes`, redaktionelle Seiten, Migrationsnamen |
| [`oeffentlich-fahndung.md`](claude-memory/oeffentlich-fahndung.md) | an `OeffentlicheFahndung`, `PublicWantedService`, `/gesucht`, `/gefasst`, Poster, Foto-Endpoint, Sachfahndung oder Einspruch arbeitest |
| [`oeffentlich-geld.md`](claude-memory/oeffentlich-geld.md) | Kopfgeld oder Belohnung anfasst (`FahndungKopfgeldAnteile`, `IBountyService`, `IRewardService`, Kassenbuchung, Beleg) |
| [`oeffentlich-buergerkontakt.md`](claude-memory/oeffentlich-buergerkontakt.md) | an Hinweisen, Triage, Übernahme, Ticket-Chat oder Bürger-Vorlagen arbeitest — **inkl. der Anonymitätszusage** |
| [`oeffentlich-redaktion.md`](claude-memory/oeffentlich-redaktion.md) | Organisationsprofile, Presse, Warnungen, Gesetzesfreigabe, Lageberichte, Gefahrenlage-Ampel oder die öffentlichen Zahlen anfasst |
| [`oeffentlich-suche.md`](claude-memory/oeffentlich-suche.md) | einen öffentlichen Suchprovider, `PublicSearchService`, `/suche-oeffentlich` oder die Außen-KPIs anfasst |
| [`noosei.md`](claude-memory/noosei.md) | am Gateway, an einem NOOSEI-Werkzeug, an Kontingenten oder an den KI-Panels arbeitest |
| [`services-details.md`](claude-memory/services-details.md) | `AgentSelection` „aufräumen" willst, an der Bestenliste arbeitest oder eine Suchkategorie hinzufügst |
| [`ui-details.md`](claude-memory/ui-details.md) | eine Route entfernst/verschiebst (Legacy-Redirects) oder `NavMenu.razor` anfasst |

## Rules for comments
- **English only** — no German, ever
- **Inline `//`** — 2–3 words; describe the *why*, not the what
- **`catch { }` blocks** — `/* best effort */` or `/* ignore */`
- **XML `/// <summary>`** — one short English line: `/// <summary>Set classification on target.</summary>`
- **No block comments** — collapse multi-line explanations to a single short line or delete them
- **No "Phase X" references** in comments — just describe what the code does

## Tech-Stack

- **.NET 10** (`net10.0`), Single-Project-Solution (`NOOSE-Website.slnx` → `NOOSE-Website/NOOSE-Website.csproj`)
- **Blazor Web App, nur Interactive Server** (SignalR) — kein WebAssembly/Auto
- **MudBlazor 9.5** (Dark-Theme „Anthrazit + Cyan", **nur Dark-Mode**)
- **EF Core 9** via **Pomelo.EntityFrameworkCore.MySql 9.0** → lokal MariaDB/XAMPP, Prod MariaDB 10.11 (Container)
- **ASP.NET Core Identity** (User-Entity = `Agent`) + **Discord-OAuth** (`AspNet.Security.OAuth.Discord`)
- Weiteres: HtmlSanitizer, Quill 1.3.7 (RichText), vis-network (Graph), FullCalendar (Kalender) — alle self-hosted unter `wwwroot/lib`

> ⚠️ **EF/Identity NICHT auf 10.x heben.** Pomelo 9 unterstützt nur EF Core 9; 10.0.x würde EF Core 10 ziehen und kollidieren. Bewusst auf der 9.0.x-Linie auf .NET-10-Runtime bleiben (siehe csproj-Kommentar).

## Befehle

Alle Befehle aus dem **Repo-Root** ausführen — außer den EF-Befehlen, die aus `scripts/` laufen
(dort liegt das Tool-Manifest, siehe unten).

```bash
# Build
dotnet build NOOSE-Website.slnx

# Lokal starten  →  http://localhost:5174  (Profil "https": https://localhost:7063)
dotnet run   --project NOOSE-Website/NOOSE-Website.csproj
dotnet watch --project NOOSE-Website/NOOSE-Website.csproj run   # Hot Reload

# EF-Migrationen — aus scripts/ ausführen (Manifest scripts/dotnet-tools.json, gepinnt auf 9.0.17).
# Aus dem Repo-Root schlagen 'dotnet tool restore'/'dotnet ef' fehl ("dotnet-ef nicht vorhanden").
cd scripts
dotnet tool restore                                             # vor jedem 'dotnet ef'
dotnet ef migrations add PhaseNN_<Name> `
    --project ../NOOSE-Website/NOOSE-Website.csproj `
    --startup-project ../NOOSE-Website/NOOSE-Website.csproj     # Startup-Default ist das aktuelle Verzeichnis
cd ..
# 'dotnet ef database update' ist i.d.R. UNNÖTIG — Migrationen werden beim App-Start
# automatisch via db.Database.MigrateAsync() angewendet (Program.cs).

# Deploy: GitHub Actions → Deploy → Run workflow (.github/workflows/deploy.yml, Kopie des Plattform-Templates)
# Image baut vorher der Workflow "Container-Image" (ghcr.io/nullradix-dev/noose-website:<sha>) — für den Commit abwarten
# Input environment = production (Prod, Default) | demo; Commit leer = aktueller master, sonst SHA; Häkchen "rollback" = vorheriges Release
# Server prüft vor dem Umschalten, behält bei Startfehler das alte Release und dumpt vorher alle DBs. Server-Details: private Betriebsdoku
```

- **Test-Projekt `NOOSE-Website.Tests`** (xunit, ~3.5k Tests): `dotnet test NOOSE-Website.Tests/NOOSE-Website.Tests.csproj` — läuft auf In-Memory-SQLite, braucht **keine** Datenbank. Helfer in `Tests/Infrastructure/`: `SqliteTestContext` (In-Memory-SQLite + `IDbContextFactory`), `Seed.*` (Entity-Fabriken), `ClaimsPrincipalBuilder` (Rang/Flags/Claims). **Kein bUnit** → `.razor`-Komponenten sind nicht testbar; testbare Logik gehört in den Service-Layer.
- Deploy nur über den Workflow „Deploy“ (Actions → Deploy → Run workflow). Das Image muss vorher vom Workflow „Container-Image“ gebaut sein. Der Server prüft alles vor dem Umschalten, behält bei Startfehler das vorherige Release und dumpt vorher alle Datenbanken (ein Rollback macht EF-Migrationen nicht rückgängig). Prod (`deploy/compose.yml`) und Demo (`deploy/demo/compose.yml`) sind zwei getrennte Apps mit eigenem Linux-User, Docker, MariaDB 10.11 und Speicherlimit. Server-Details (Pfade, Ports, Benutzer, Backups) stehen in der privaten Betriebsdoku.

### Secrets & Config

`appsettings.json` enthält **nur leere Platzhalter**. Echte Werte kommen aus der Source-Control heraus:

- **Lokal:** User-Secrets (`UserSecretsId d41f8a93-2c7b-4e16-9a55-0b3e7c1f6d28`)
- **Produktion:** Env-Vars liegen nur auf dem Server, nie im Repo (Doppel-Unterstrich: `ConnectionStrings__ProductionConnection`, `Authentication__Discord__ClientId`)
- **Prüfung:** Der Workflow „Secret-Scan“ prüft die ganze Git-History mit gitleaks (`.gitleaks.toml`: Standardregeln, dazu keine öffentlichen IPv4-Adressen). Neue Funde nie in `.gitleaksignore` eintragen, sondern den Wert entfernen und ein Secret wechseln.

Benötigte Keys: `ConnectionStrings:DefaultConnection` (oder `:ProductionConnection`), `Authentication:Discord:ClientId` + `:ClientSecret`, `Bootstrap:AdminDiscordId`.
Ohne erreichbaren DB-Connection-String wirft die App beim Start. `DatabaseConnectionResolver` bevorzugt `ProductionConnection` (5s-Reachability-Probe), sonst Fallback auf `DefaultConnection` → derselbe Build läuft lokal und auf dem Server ohne Edit.

## Architektur

Schichten innerhalb von `NOOSE-Website/`:

| Ordner | Inhalt |
|--------|--------|
| `Components/` | Razor-Pages + UI (dünn). `Pages/<Feature>/`, `Layout/`, `Common/Shared/`, `Account/` |
| `Navigation/` | `NavCatalog` (Einträge), `NavEntry`/`NavSection` (Policy-Achse), `NavArea` (Darstellungs-Achse), `LegacyRoutes`, `MergedPageSections` |
| `Data/` | `AppDbContext`, `Entities/<Domain>/`, `Migrations/` (~51, Phase-Präfix) |
| `Models/` | DTOs/View-Models, `Enums/`, `Abstractions/` (Marker-Interfaces) |
| `Services/` | Business-Logik **+ die echte Authorization-Durchsetzung**; Subordner `Graph/`, `Statistics/`, `Threat/` |
| `Authorization/` | Policies, Requirements, Handler, `ClaimsPrincipal`-Extensions |
| `Infrastructure/` | Interceptors, File-Storage, Audit, Background-Worker, CurrentUser, Broadcaster |

- **`Program.cs`** ist Composition-Root (Top-Level-Statements): alle DI-Registrierungen inline, nach Build-„Phase" gruppiert/kommentiert.
- **Render-Mode** wird pro Seite in `App.razor` gesetzt: `InteractiveServer`, außer `[ExcludeFromInteractiveRouting]` (Error, NotFound, Login, Pending, Blocked, Legal) → statisch.
- **Culture global auf de-DE** fixiert (`UseRequestLocalization` + `CultureInfo.DefaultThread*`).
- **Middleware-Reihenfolge** (load-bearing): `UseForwardedHeaders` (zuerst, vertraut nur Loopback/nginx) → `RequestLocalization` → `PublicIndexingMiddleware` (noindex außerhalb der öffentlichen Routen; **vor** dem ExceptionHandler, damit re-executete Fehlerseiten den Header behalten) → (nur Prod) `ExceptionHandler`+`HSTS` → `StatusCodePagesWithReExecute("/not-found")` → `HttpsRedirection` → `Authentication` → `DemoModeMiddleware` → `Authorization` → `Antiforgery` → `RateLimiter` (**nach** Antiforgery: ein POST ohne Token darf kein Permit verbrauchen, sonst hält ein anonymer Besucher die Anmeldung dauerhaft auf 429) → `MapStaticAssets` → `/health` → `MapRazorComponents<App>` → `Map*Endpoints`-Gruppen.
- **SignalR Hub:** `MaximumReceiveMessageSize = 25 MB` (für den RichTextEditor, der volles HTML inkl. base64-Bildern über SignalR streamt — nicht zurücksetzen).
- **Background-Worker** (`AddHostedService`): `FollowupDueWorker` (Wiedervorlagen), `ThreatScoreSweepWorker` (tägl. Score-Decay, seedet Fraktionen beim ersten Start), `SituationReportWorker` (monatl. Lageberichte). Laufen pro Host-Instanz → keine Multi-Instanz gegen eine DB.
- **Health-Check** `/health` (`AddDbContextCheck`) — von Deploy-Skript und Status-Seite genutzt.

## Datenmodell (EF Core)

- **Ein** `AppDbContext : IdentityDbContext<Agent>` (~60 DbSets); **alle** Fluent-Configs in `OnModelCreating` (keine `IEntityTypeConfiguration`-Klassen). Annotations nur für `[Table]`/`[Column]` (deutsche Namen) und `[NotMapped]`.
- **DbContext-Factory:** Immer `IDbContextFactory<AppDbContext>` injizieren und pro Operation einen kurzlebigen Context erzeugen (`await using var db = await dbFactory.CreateDbContextAsync(ct)`). Ein zirkuit-langer scoped Context wirft in Blazor Server *„A second operation was started on this context"*.
- **3 SaveChanges-Interceptors, Reihenfolge zählt:** `ReadOnlyBarrierInterceptor` (zuerst!) → `AuditSaveChangesInterceptor` → `WatchlistChangeInterceptor`.
- **PKs:** meist `string` GUID (`Id = Guid.NewGuid().ToString()`); `AuditLog` nutzt `long`; `Agent` erbt Identity-`string`-Key.
- **Cross-Cutting via Marker-Interfaces** (`Models/Abstractions/`): `IAuditable` (CreatedAt/By, ModifiedAt/By — vom Interceptor gestempelt) und `ISoftDelete` (globaler Query-Filter `!IsDeleted`, automatisch per Reflection angewandt). Neue Entität → einfach Interface implementieren.
- **Soft-Delete ist Norm:** Löschen über EF rewritet `Deleted` → `Modified`. Papierkorb-Queries: `IgnoreQueryFilters().Where(x => x.IsDeleted)`.
- **DB-Spalten Deutsch, C#-Member Englisch:** z. B. `Person.CaseNumber` → Spalte `Aktenzeichen`, Tabelle `Personen`, `IsDeleted` → `IstGeloescht`.
- **Polymorphe Assoziationen** (Quellen, Kommentare, Tags, Links, Followups, ClassificationHistory, …) über `(EntityType string via nameof(T), EntityId string)` — **kein echter FK**, schneller Pfad ist der Composite-Index.
- **`DeleteBehavior.Restrict`** (statt Cascade) bei `PersonRelation` und `*Member`-Tabellen, um MySQL-„multiple cascade paths" zu vermeiden; FKs auf die Identity-`Agent`-Tabelle nie Cascade.
- **`longtext`** für HTML/JSON-Spalten (`Document.ContentHtml`, `*Json`, `SystemSetting.Value`) — kein `HasMaxLength` darauf.
- **Aktenzeichen** (z. B. `NOOSE-P-2026-0001`) race-safe über `CaseNumberCounter` (Composite-Key `Prefix`,`Year`) in einer Transaktion.
- **Design-Time** (`AppDbContextDesignTimeFactory`) zwingt EF-Tools immer auf lokale `DefaultConnection` → Migrationen können **nie** Produktion treffen.

## Services-Layer

- **Interface-first:** jeder DI-Service ist `I<Name>Service` + `<Name>Service`, `AddScoped`. Implementierungen nutzen **Primary Constructors**. Jede public-async-Methode hat ein trailing `CancellationToken cancellationToken = default`.
- **Live-Updates per Singleton-Broadcaster/Dispatcher:** scoped Service schreibt die Row, ruft dann den Singleton (`NotificationBroadcaster`, `TaskforceChatBroadcaster`, `SharesBroadcaster`, `AcknowledgmentBroadcaster`, `WatchlistDispatcher`) zum Push an verbundene Circuits.
- **Authorization wird IM Service-Layer durchgesetzt**, nicht nur in der UI: statische Guards `Permission.Require*` (werfen `UnauthorizedAccessException`), Sichtbarkeit zentral in statischem `Visibility`/`*Visibility`/`RecordsReference`. Write-Methoden nehmen `ClaimsPrincipal actor` und rufen den Guard als erste Anweisung.
- **Statische Helfer in `Services/`** (NICHT DI-registriert): `Permission`, `Visibility`, `AgentSelection`, `ClassificationHelper`, `TextSimilarity`, `RecordsReference`, `MentionParser`, `HtmlCleanup`, `TrashProjection`, `DepartmentRules`, `Public/PublicModules`, `Public/PublicVisibility`, `Public/PublicRoutes`. Geteilte Logik dorthin extrahieren statt kopieren.
- **Wer in eine Agenten-Auswahlliste darf, entscheidet ausschließlich `Services/AgentSelection.cs`.**
  `db.Users.OnlySelectable()` = `Active && !IsTeamLead && PartnerAgency == null` für **jeden** Picker,
  Dropdown, Roster und Roster-Fan-out (also überall, wo Empfänger *aus dem Gesamtbestand* gewählt werden);
  `OnlyListable()` = `Codename != "" && !IsTeamLead && PartnerAgency == null` **nur** für die Log-Filter
  (`AgentDirectory`), die Gekündigte/Gesperrte bewusst behalten. `IsSelectable(agent)` ist der
  In-Memory-Zwilling. Nie `db.Users` von Hand nach Status/Flags filtern — und den **Schreibpfad mit
  demselben Prädikat absichern** (`OnlySelectable().AnyAsync(...)`), sonst bleibt der SignalR-Pfad offen.
  Einzige Ausweitung: der Audit-Viewer (`/nachweis`, `AuditLogQueryService.GetFilterOptionsAsync`) nutzt
  `OnlyAuditFilterable()` = `Codename != ""` (via `AgentDirectory.AllForAuditAsync`) und listet damit
  bewusst auch Teamleitungen und Partner — mit Marker-Suffix im Dropdown —, damit deren Log-Zeilen
  filterbar sind.
- **Bewusste Ausnahmen von `AgentSelection`** (MentionService, Admin-Roster, Partner-Inverse,
  ID→Codename-Wörterbücher, Fan-out über übergebene Empfängerlisten, …) stehen in
  [`claude-memory/services-details.md`](claude-memory/services-details.md) — **nicht „aufräumen"**.
- **Bestenliste: der Rang-Boden liegt NICHT in `AgentSelection`** (`GamificationService.LeadershipFloor`
  partitioniert eine schon autorisierte Menge auf einer zweiten Achse). Ihn zu verschieben leert jeden
  Picker → [`services-details.md`](claude-memory/services-details.md).
- **Ein Picker, der eine gespeicherte Agenten-ID auflöst, muss auf `FindAsync` zurückfallen**, wenn die ID
  nicht mehr auswählbar ist (`FollowupDialog`, `ObservationDialog`). Sonst bleibt das Objekt `null` und der
  Speichern-Pfad *löscht die Zuordnung still* bzw. schreibt sie auf den Bearbeiter um. Auflösen **außerhalb**
  der Angebotsliste, damit niemand die Person neu auswählen kann.
- **`ITrashService`** fächert den globalen Papierkorb über alle 13 Record-Dienste auf. Alle haben dieselbe
  Signatur (`GetTrashAsync(ct)` / `RestoreAsync(id, actor, ct)`), also bindet Restore als **Methodengruppe** —
  kein generischer EF-Pfad, kein Bulk-SQL, Permission-Guard und Audit-Interceptor laufen weiter mit.
  Ein neuer löschbarer Typ wird in `TrashService` als eine Zeile registriert + eine `TrashProjection`-Methode.
- **Globale Suche:** `SearchService` ist nur noch **Orchestrator**. Je Kategorie ein `ISearchProvider`
  (`Services/Search/Providers/`, ~67), registriert über `AddSearchProviders()`. Der **`SearchCatalog`
  (`Services/Search/SearchCatalog.cs`) ist die einzige Wahrheit** für Label, Icon, Route, Trefferform, Traits,
  Facetten-Reihenfolge und das `suche_akten`-Enum — eine neue Kategorie ist **eine Zeile dort + ein Provider**.
  In-Memory-Levenshtein (`TextSimilarity`) bleibt, weil MySQL/Pomelo keine Edit-Distance übersetzt.
  Die sechs Regeln dahinter (Sichtbarkeits-Prädikate benennen, zwei Wellen unter Wanduhr-Budget,
  Trefferzahlen, keine Vorfilter, `SearchParentResolver`, Partner-Deckel) →
  [`services-details.md`](claude-memory/services-details.md).
- **Maintenance/Banner/Theme/Logo:** `SystemSettingService` über Key/Value-Tabelle, 10s `IMemoryCache`. Logo/Uploads liegen **außerhalb wwwroot** unter `App_Data/uploads`, ausgeliefert über autorisierte Minimal-API-Endpoints.
- **Vier getrennte Token-Systeme, nie vermischen:** `PlaceholderService` (`{{Name}}`, `{{Aktenzeichen}}`, `{{Datum}}`, `{{Uhrzeit}}`, `{{Agent}}`, `{{Dienstgrad}}` — Dokument-/Aktivitäts-/Personal-Vorlagen) · `BewerbungTemplateRenderer` (bare `NAME`/`BEWERBER`/`DATUM`/`UHRZEIT`/`DIENSTGRAD`, nur Bewerbungs-Anschreiben) · `MentionParser` (`@{Typ:GUID}`, aufgelöst über `MentionService.ResolveManyAsync` → `<MentionText>`) ·
  `PublicTemplateRenderer` (bare `BUERGER`/`AKTENZEICHEN`/`DATUM`/`UHRZEIT`/`NAME`, nur Bürger-Nachrichten,
  **ohne** HTML-Encoding — siehe Phase 11 in [`claude-memory/oeffentlich-buergerkontakt.md`](claude-memory/oeffentlich-buergerkontakt.md)).
- **Platzhalter werden NUR beim Anwenden einer Vorlage expandiert**, nicht beim Speichern: Vorlagen-*Editoren* und der Edit-Modus gespeicherter Records lassen Tokens bewusst roh stehen (dort sind sie der Payload).

## Authorization, Ränge & Rollen

Drei orthogonale Achsen: **(1) Rang** (`Models/Enums/Rank.cs`, int-backed `JuniorAgent=1 … Director=6`), **(2) Boolean-Flags** auf `Agent` (`IsAdmin`, `IsTRU`, `IsHRB`, `IsTeamLead`), **(3) Policies** (kombinieren Rang+Flags).

- **Berechtigungslogik existiert an genau zwei Stellen** — nirgends sonst rohe Claim-Checks:
  - `Authorization/AgentPrincipalExtensions.cs` — `ClaimsPrincipal`-Extensions (`IsAdmin`, `IsLeadership`, `IsOnlyReader`, `MayWrite`, `MayRealNameSee`, `MayHighestClassification`, …) für UI/Policies/Read-Gates.
  - `Services/Permission.cs` — statische `Require*`-Guards für Service-Writes.
- **Führung (Leadership)** = Rang ≥ `SupervisorySpecialAgent(4)` **oder** Admin. **`HöchsteEinstufung`** ≥ `SeniorSpecialAgent(3)`, **`BeförderungEntscheiden`** ≥ `DeputyDirector(5)`.
- **TRU und HRB gibt es erst ab `Rank.SpecialAgent`** — Dienstverordnung §2.4.1, zentral in
  `Services/DepartmentRules.cs` (`MinimumRank`/`MayHold`/`RequireMayHold`). Die Regel betrifft den **Ziel**-Agenten,
  nicht den Handelnden, und gehört deshalb *nicht* in `Permission`. Drei Dinge hängen zusammen: der Guard in
  `AgentManagementService` (`TruSetAsync`/`HrbSetAsync`/`ReleaseAsync`/`PromoteApplicantToAgentAsync`), das
  **Fallenlassen** der Kennzeichen bei einer Herabstufung (`DropDepartmentsBelowThreshold`, gerufen von
  `RankChangeAsync` und `PromotionDecideAsync` — sonst behält ein degradierter Agent sein HRB-Tor) und die drei
  UI-Stellen, die dasselbe Prädikat lesen müssen (`Admin/Agents.razor`, `Admin/Shares.razor`,
  `Recruiting/Shared/BewerbungPromotePanel.razor`). **Entfernen bleibt auf jedem Rang erlaubt**, sonst säße ein
  Altbestand fest.
- **Admin = Boolean-Flag** (`Agent.IsAdmin` / Claim `noose:admin`), **nicht** der Rang und **nicht** die geseedete Identity-Rolle „Admin" (die ist ungenutzt). Admin short-circuited jedes `RankRequirement`.
- **Nur-Lese-Aufsicht (`OnlyReader`)** = `IsTeamLead && !IsAdmin` (abgeleitet, kein Flag): liest alles (inkl. VS), schreibt **nichts** (vom `ReadOnlyBarrierInterceptor` hart vetoed), sieht **nie** Klarnamen. `IsTeamLead` allein gewährt sonst keine Rechte; TeamLeads sind RP-weit unsichtbar.
  - **`IsTeamLead` entfernt den Account aus jeder Auswahlliste** (`AgentSelection`), auch mit `IsAdmin` obendrauf. Deshalb zeigt die **Einsichtsliste eines VS-Dokuments die Aufsicht nicht**, obwohl `DocumentViewerScope.CanSee` ihr den Lesezugriff weiterhin gewährt — die Liste ist absichtlich unvollständig, sonst würde sie die Existenz der Aufsicht verraten. Nicht „reparieren".
- **Claims werden beim Login** in den Cookie geschrieben (`AgentClaimsPrincipalFactory`) → keine DB-Hits pro Request. Rang-/Rollen-/Status-Änderung rotiert den `SecurityStamp` (`Save(agent, newStamp: true)`) → erzwingt Re-Login (`SecurityStampValidator` revalidiert alle 30s).
- **Zwei Policies für HRB+Führung, und die Wahl ist load-bearing.** `Policies.HrbOrLeadership` ist das
  **Zugangs**-Gate (Bewerbungswesen) und prüft **nicht** aufs Schreiben; `Policies.HrbOrLeadershipWrite`
  spiegelt `Permission.RequireHrbOrLeadershipWrite` (`MayWrite() && IsHrbOrLeadership()`) und gehört an jedes
  Redaktions-`AuthorizeView`. Mit der falschen sieht der Demo-Besucher (trägt HRB **und** Director) die
  Bearbeiten-Knöpfe des Handbuchs und bekommt nach dem Ausfüllen eines Dialogs eine Absage.
- **Neue Policy anlegen:** Konstante in `Policies.cs` → registrieren in `AuthorizationRegistration.AddNooseAuthorization` (`RankRequirement` für Rang-Gate **oder** `RequireAssertion(ctx => ctx.User.SomeExtension())`) → ggf. Extension in `AgentPrincipalExtensions.cs`. **Policy-Strings nie hardcoden** — immer `Policies.*`.
- **Account-Flow:** Discord-Login → `Agent` mit `Status=Pending` → Freigabe durch Führung/Admin (`AgentManagementService.ReleaseAsync`) setzt `Active` + Rang + Flags. Bootstrap-Admins via `Bootstrap:AdminDiscordId(s)`.
- **Zwei VS-Achsen:** `Classification` (Einstufung Person/Fraktion: `ReviewCase`/Prüffall → `SuspicionCase`/Verdachtsfall → `SecuredStateThreatening`/Gesichert staatsgefährdend) **und** `DocumentClassification` (Bibliotheks-VS-Stufe: `None`/`Leadership`/`Tru`/`Hrb`). VS-Sichtbarkeit wird **server-seitig** über `DocumentViewerScope.CanSee` durchgesetzt, nicht über die `Classified`-Policy (reserviert/ungenutzt).
- **Ausbildungsmodule: Abhaken ist nicht Verwalten.** `TrainingModuleService.MarkCompletedAsync`/`UnmarkCompletedAsync`
  tragen `Permission.RequireHrbOrLeadershipWrite` (HRB darf ohne Führungsrang); `CreateAsync`/`UpdateAsync`/`DeleteAsync`
  bleiben auf `RequireLeadership` — `DeleteAsync` nimmt die Haken **aller** Agenten mit. Das UI-Gate ist **kein**
  `AuthorizeView`, sondern das private `ModulesPanel._mayTick`, gelesen an **zwei** Stellen (Checkbox-`ReadOnly` und
  `ToggleAsync`); nur die Checkbox zu sperren lässt den SignalR-Pfad offen. Das Prädikat muss den Guard spiegeln
  (`MayWrite() && IsHrbOrLeadership()`) — `Policies.HrbOrLeadership` allein hat keine Schreibprüfung und ließe
  Nur-Lese-Aufsicht und Demo-Principal eine aktive Checkbox sehen.
- **Keine DoJ/LSPD/LSMD-Accounts/-Ränge** — jeder User ist ein NOOSE-`Agent`. Partner-Lesezugriff (Phase 9) ist noch nicht gebaut.

## UI / Blazor-Komponenten

- **Ein Feature-Ordner je Bereich** unter `Components/Pages/` (Account, Admin, Board, Calendar, Cases, Factions, Graph, Groups, Jobs, Laws, Operations, OrgChart, Parties, People, Personnel, Search, Statistics, Taskforces, Tips, Wanted, Watchlist). Pro Feature: `*List`/`*Editor`/`*Detail`/`*Print` + `Shared/`. Cross-Feature → `Components/Common/Shared/`.
- **Deutsche Routen:** `/personen`, `/fraktionen`, `/vorgaenge`, `/aufgaben`, `/operationen`, `/parteien`, `/personengruppen`, `/taskforces`, `/kalender`, `/organigramm`, `/statistik`, `/brett`, `/gesetze`, `/suche`, `/graph`, `/fahndung`, `/hinweise`. CRUD-Subroutes `/{feature}/neu`, `/{Id}`, `/{Id}/bearbeiten`, `/{Id}/druck`.
- **Neu-/Bearbeiten liegen in EINER Datei** (`*Editor.razor` mit zwei `@page`-Direktiven). Beide Routen binden
  denselben Komponententyp → Blazor recycelt die Instanz beim Wechsel. **Laden gehört deshalb in
  `OnParametersSetAsync` mit `_loadedId`-Guard, nie in `OnInitializedAsync`** (läuft sonst nicht erneut).

### V1.5: zusammengefasste Seiten

Seit V1.5 gibt es statt vieler Einzelseiten sieben Sammelseiten mit `RecordSectionRail`:

| Route | ersetzt |
|---|---|
| `/einstellungen` | 13 Admin-Seiten (System, Discord, Status, Tags, Custom-Felder, Aktualität, Bedrohungs-Score, Vorlagen ×4, Module, Einladungen, Partner, Basisdaten) |
| `/nachweis` | `/chronik`, `/einstellungen?tab=protokoll` (Änderungen + Zugriffe), `?tab=gegenaufklaerung`, `?tab=gegenaufklaerung-regeln` |
| `/papierkorb` | 12 `*Trash.razor`-Seiten, getrieben von `ITrashService` |
| `/fahndung` | `/observationen`, `/doks` |
| `/abmeldungen` | `/abmeldungen/uebersicht`, `/abmeldungen/papierkorb` |
| `/bewerbungen` | `/bewerbungen/sperren`, `/bewerbungs-vorlagen`, `/bewerbungs-tests` |
| `/statistik` | `/lageberichte` |

- **Alte Routen leben weiter** in **einer** Shell (`Components/Common/Navigation/LegacyRouteRedirect.razor`
  + `Navigation/LegacyRoutes.cs`); `MergedPageSections.cs` hält die Slug-Listen; strengere Abschnitte
  werden in `<AuthorizeView>` gewickelt. Mechanik und Fallstricke →
  [`claude-memory/ui-details.md`](claude-memory/ui-details.md).
- **Globales `[Authorize(Policy = Policies.ActiveAgent)]` in `_Imports.razor`** → jede neue Seite ist standardmäßig auth-pflichtig. Öffentliche Seiten brauchen explizit `[AllowAnonymous]` (Login, Pending, Blocked, Error, NotFound, Legal).
- **Strengere Seiten:** `@attribute [Authorize(Policy = Policies.LeadershipPage|AdminPage|HighestClassificationPage)]`. Feingranular per `<AuthorizeView Policy="@Policies.X" Context="...">` (explizite `Context`-Namen bei Verschachtelung). `*Page`-Policies lassen bewusst auch den `OnlyReader` rein — nicht zu Rang-Requirements „vereinfachen".
- **Kein Code-Behind** (`*.razor.cs` existiert nicht) — Logik in inline `@code`. Private Felder `_camelCase`.
- **Dark-Mode hardcoded** (`IsDarkMode="true"`, nur `PaletteDark` in `Theme/NooseTheme.cs`). Admin-Akzentfarben zur Laufzeit über `/einstellungen?tab=system` (`NooseTheme.WithColours(...)`).
- **JS-Interop** ist self-hosted + lazy-loaded je Seite mit `?v=`-Cache-Buster: `graph.js` (vis-network), `kalender.js` (FullCalendar), `richtext.js` (Quill 1.3.7); `app.js` (Strg+K Command-Palette) ist das einzige global geladene Modul. Interop-Komponenten: `IAsyncDisposable`, Import in `OnAfterRenderAsync(firstRender)`, `[JSInvokable]`-Callbacks, alles in `try/catch` gegen `JSDisconnectedException`.

### Gemeinsame UI-Bausteine (`Components/Common/Shared/`)

Vor dem Bauen einer neuen Seite hier nachsehen — diese Bausteine sind bereits überall im Einsatz:

| Komponente | Zweck |
|---|---|
| `PageHeader` | Kopfzeile jeder Seite (Icon, Titel, Untertitel, `BackHref`, `Actions`) |
| `EmptyState` | Leerzustand für `NoRecordsContent` |
| `StatTile` / `HazardList` | KPI-Kachel (klickbar über `Href`) und Gefährdungsliste des Dashboards |
| `RecordSectionRail` + `RecordSection` | vertikale, gruppierte Abschnittsleiste; hält den Abschnitt in `?tab=` |
| `QueryState` | Listenfilter in der URL halten (`Read`/`ReadEnum`/`ReadFlag`/`WriteAsync`) |

- `QueryState.WriteAsync` und `TabUrlState` schreiben über `window.nooseReplaceState` (in `App.razor`) —
  **`replaceState` löst kein `LocationChanged` aus**, deshalb aktualisiert der Rail `_activeSlug` selbst.
- `TabUrlState.ParameterName` ist ein hartkodiertes `"tab"` → **kein `MudTabs` mit URL-Sync innerhalb
  eines `RecordSectionRail`** (Query-Kollision). Abschnitte stattdessen flach ziehen.

### Drawer: Icon-Leiste + Panel

`Components/Layout/NavMenu.razor` ist zweispaltig; ein Icon-Klick **navigiert nicht**, er wechselt
nur das Panel. Die sieben Regeln dazu (zwei orthogonale Achsen `NavSection`/`NavArea`, `MudNavMenu`-Scope,
handgebaute Leiste, `aria-current`, Policy-Snapshot, tote `CollapsedGroups`) →
[`claude-memory/ui-details.md`](claude-memory/ui-details.md).

## Wichtige Gotchas

- **Nach Route-Änderungen die App wirklich starten, nicht nur bauen.** Zwei Komponenten auf derselben
  `@page` sind kein Compilerfehler — sie werfen erst beim Aufbau der Routing-Tabelle zur Laufzeit.
- **`dotnet tool restore` vor jedem `dotnet ef`** — `dotnet-ef` ist lokal-gepinnt (9.0.17), nicht global. Beides aus `scripts/` ausführen (dort liegt `dotnet-tools.json`); aus dem Repo-Root schlägt der Aufruf fehl.
- **EF/Identity nicht auf 10.x** (Pomelo-9-Kollision).
- **Vor `dotnet ef migrations add` den Dev-Server stoppen** (bin-Lock), dann neu bauen.
- **`App_Data` beim Deploy nie löschen** — enthält Uploads **und** Data-Protection-Keys (`App_Data/keys`); Verlust loggt alle User bei jedem Restart aus. `App_Data` ist nicht im Image, sondern ein Volume auf dem Server und überlebt jeden Deploy.
- **Deploy baut nichts lokal** — das Image kommt aus GitHub Actions (`.github/workflows/image.yml`, Push auf master), ausgerollt wird per Workflow „Deploy“.
- **`TZ=Europe/Berlin` in der Server-Env** nötig — Blazor Server rechnet `ToLocalTime()` in der Server-TZ; ohne TZ sind alle Zeiten (inkl. 20-Min-„Tot"-Fenster) verschoben. `TimeZoneInfo.Local` ist prozess-gecached → Container neu erstellen (Deploy-Workflow; `restart` liest die Env-Datei nicht neu).
- **`?v=` bumpen bei JS-Modul-Edits** (`graph.js?v=8`, `kalender.js?v=7`, `richtext.js?v=21`, `entwurf.js?v=1`, `textbild.js?v=1`, `app.js?v=4`) — dynamische ES-Imports umgehen Blazors Asset-Fingerprinting. **Alle** Importstellen eines Moduls mitziehen: `app.js` wird von `CommandPalette.razor`, `KeyboardShortcuts.razor` **und** `FinancingCatalogPanel.razor` geladen, und zwei verschiedene `?v=` holen zwei Kopien.
- **Ablehnen, Schließen und eine nicht bestandene Sicherheitsüberprüfung sperren 14 Tage.** Die Dauer, das
  Aktiv-Prädikat (`IstBlacklist || GesperrtBis > jetzt`, es gibt keine `IstAktiv`-Spalte) und die
  Lokal→UTC-Umrechnung des `MudDatePicker` liegen zusammen in `Services/BewerbungssperreRules.cs`. Die Sperre
  hängt am **Discord-Konto**, nicht am Namen. Es gibt keinen Interceptor dafür: ein **neuer Abschlusspfad muss
  `IBewerbungssperreService.BanAsync` selbst rufen** — und zwar vor `broadcaster.Report`, sonst liest das
  Sperr-Panel den Zustand davor. Entscheidungs-Methoden tragen `Permission.RequireRecruitingDecision`
  (Schreib- **vor** Rang-Prüfung), weil die Sperre ein Folge-Write ist, dessen Fehlschlag nur geloggt wird.
- **Bewerbungs-Platzhalter sind groß-/kleinschreibungsabhängig.** `BewerbungTemplateRenderer` matcht `\bNAME\b`
  case-sensitiv; aus `NAME` ein `Name` zu machen schaltet die `███████`-Schwärzung für jede daraus gebaute
  Nachricht still ab. `TextAssistService` lehnt eine NOOSEI-Korrektur deshalb hart ab, wenn Anzahl **oder**
  Schreibweise dieser Tokens abweicht (Kontext `RecruitingTemplate`).
- **Eine ungelesene Benachrichtigung hält ihr Ziel offen.** `WatchlistFanout` unterdrückt „Akte geändert“,
  solange zur selben Akte eine ungelesen ist; `NotifyOnceAsync`/`NotifyManyOnceAsync` (nur die Ticket-Wege)
  falten eine neue Meldung gleicher Art und gleichen `Href` auf die jüngste ungelesene und setzen deren
  `CreatedAt` neu. Alle anderen Arten (`NotifyAsync`, Erwähnungen) legen einfach eine weitere Zeile an. Wer im
  Posteingang (`/benachrichtigungen`) **wieder als ungelesen** markiert, macht die alte Meldung damit wieder
  offen — bei einer beobachteten Akte kommt bis zum Lesen keine neue dazu. Gewollt, im Handbuch erklärt. Der Posteingang ist nur intern (`Policies.InternalAgent`); die Glocke hängt auch
  im öffentlichen Kopf und zeigt den Link deshalb nur bei `IsInternalAgent()`. Die Seitenzahl steht bewusst
  **nicht** in der Adresse: eine gemerkte Ansicht würde sie mitnehmen.
- **Eine Drogenroute gehört genau einer Fraktion.** „Dieselbe“ Route heißt gleicher Schlüssel
  (`Services/DrugRouteRules.Key`: Groß-/Kleinschreibung und Leerzeichen zählen nicht); halten tun nur aktive
  Fraktionen (Papierkorb und Archiv geben frei). `FactionService.CreateAsync`/`RefreshAsync` prüfen unter einem
  prozessweiten Schloss (`RouteGate`, beim Anlegen bis zum Commit) und werfen `DrugRouteConflictException`, solange
  ein Konflikt nicht in `FactionInput.ConfirmedRouteTakeovers` steht; eine bestätigte Übernahme entfernt die Zeile
  beim Halter im selben Save und schreibt `ManualAudit` an **beide** Akten (Routenzeilen sind nicht auditiert).
  Einen Halter, den der Handelnde nicht sehen darf, nennt die Prüfung nicht und lässt ihn nicht übernehmen.
  `RestoreAsync`/`UnarchiveAsync` streichen, was inzwischen eine andere Fraktion hält; das Umbenennen im Katalog
  lehnt ein Zusammenlegen ab. **Kein eindeutiger Index**: die Zeilen einer Fraktion im Papierkorb bleiben stehen,
  und Altbestand darf doppelt sein, bis jemand speichert. Ein neuer Schreibweg auf `FraktionDrogenrouten` muss
  durch dasselbe Schloss.
- **`NOOSE-Website/BuildNumber.txt` erhöht sich automatisch bei jedem echten Build** (`dotnet build`/`watch`/`publish`, MSBuild-Target in der `.csproj`; IDE-Design-Time-Builds sind ausgenommen) und wird lokal als `1.0.<Zahl>` angezeigt. Die Prod-Version kommt aus der GitHub Action: Build-Nummer `1222 + Run-Nummer` geht als Build-Arg `BUILD_NUMBER` ans Dockerfile, das `N-1` in die Datei schreibt (das Target zählt selbst hoch und überschreibt ein `-p:Version`), heraus kommt `1.0.N` unter Einstellungen → Status und als Build im neuesten Changelog-Eintrag (der Seeder stempelt beim ersten Start nach einem Deploy). Die lokale Datei ist **gitignored** (`.gitignore` Zeile 386) → taucht nie in `git status` auf und wird nicht mitcommittet; sie zählt nur lokale Builds (Deploy baut nicht lokal).
- **Das Dockerfile muss zusammen mit allen Quellen restoren, nicht zuerst nur mit der `.csproj`.** Sonst fehlt `_framework/blazor.web.js` (Web SDK), Blazor startet nie (tote Navbar), während `/health` grün bleibt. Das Dockerfile prüft die Datei beim Build.
- **Bild-Paste in ein Plaintext-Feld legt die Datei ab und schreibt nur ein Token.** `MentionInput` nimmt per
  Strg+V ein Clipboard-Bild an, sobald `ImageOwnerType`/`ImageOwnerId` gesetzt sind (Opt-in wie der @-Picker);
  `TextImageService` speichert nach `App_Data/uploads/textbilder`, legt eine `Textbilder`-Zeile an und gibt
  `@{TextImage:GUID}` zurück. Die **Trägerakte entscheidet über die Sichtbarkeit**, nicht der Token-Besitzer:
  `RecordsReference` löst das Bild wie eine Quelle über den Träger auf (Soft-Delete und fremde Taskforce fallen
  damit automatisch raus), Sonderfälle sind `Document` (drei VS-Flags + Entzug ⇒ `Documents.OnlyVisible`) und
  `Agent` (Personalakte trägt kein Flag ⇒ Führung). Ein Feld, das seine Trägerakte nicht benennen kann, darf
  kein Bild annehmen. `textbild.js` mit `?v=` bumpen. Verdrahtet: Kommentare (polymorph, auch im Bearbeiten-Modus),
  Taskforce-Chat und die Person-Dialoge `DocDialog`/`ObservationDialog` — die hängen das Bild an die
  **Personenakte**, weil Dok und Observation kein eigenes VS-Flag tragen und beim Einfügen noch keine Id haben.
  `DocCreateDialog` bleibt bewusst außen vor: dort wird die Person erst im Dialog gewählt.
- **`graph.js`-JSON-Keys = englische CLR-Typnamen** (`nameof`), nicht die deutschen Display-Namen; C#- und JS-Map müssen synchron bleiben.
- **Die Quill-Werkzeugleiste ist `position: sticky` und hängt an der Höhe der `MudAppBar`.**
  `app.css` setzt `top: var(--noose-rte-toolbar-top, 64px)`; im Dialog überschreibt
  `.mud-dialog .ql-toolbar.ql-snow` die Variable auf `0px`, unter dem sm-Breakpoint auf `56px`. Wird die
  App-Bar höher, klebt die Leiste darunter fest — beides gehört zusammen geändert. Ein ausgeklapptes
   Expansion-Panel macht seine Hülle zum Scrollport; die `.mud-collapse-entered .mud-collapse-wrapper:has(.ql-toolbar)`-
   Ausnahme stellt für Editoren den Seiten-Scrollport wieder her. Kein `?v=` nötig: `app.css` läuft über
   `@Assets["app.css"]` und wird von `MapStaticAssets` gefingerprinted.
- **Der `RichTextEditor` ist eine Fläche, kein einfaches Feld.** Er trägt Suchen/Ersetzen, Vollbild, Gliederung,
  Slash-Menü, Markdown-Kürzel und die Entwurfswiederherstellung (Browser-**IndexedDB**, Schlüssel = Agent +
  `DraftKey`). **Jede neue Editorstelle gibt einen stabilen `DraftKey` mit** — sonst gibt es still gar keine
  Wiederherstellung; mehrere Editoren auf einer Seite brauchen unterschiedliche Schlüssel (Handbuch
  `:anleitung`/`:rollenspiel`, Tagesordnung `tagesordnung:{item.Id}`). Nach erfolgreichem Speichern
  `MarkSavedAsync()` rufen, sonst bietet der nächste Aufruf den gespeicherten Text erneut an. `Compact="true"`
  (nur Bewerberchat) = schlanke Leiste, kein Entwurf, kein Slash-Menü.
- **Einfache Textfelder sichern ihren Entwurf genauso — über `DraftKey` an `MentionInput`** (bzw. ein
  `<TextDraft>` um ein `MudTextField`). Ablage, Sieben-Tage-Grenze und Löschung beim Abmelden teilen sich Editor
  und Felder in `wwwroot/js/entwurf.js` (von `richtext.js` importiert, `?v=` an **beiden** Stellen gleich).
  Ein Formular vergibt einen **Bereich** (`DraftKeys.In(DraftScope, "feld")`), und wer speichert, verwirft ihn
  **nach** dem erfolgreichen Schreiben: `TextDraft.DiscardScopeAsync(JS, agentId, scope)` — bei Dialogen der
  Aufrufer, weil der Dialog vor dem Speichern schließt. Nur Speichern oder *Verwerfen* löschen einen Entwurf,
  *Abbrechen* nicht. Bürgerportal und öffentliche Seiten sichern nichts (Anonymitätszusage).
  `TextDraftScanTests` verlangt für jedes Feld ab drei Zeilen einen Schlüssel oder einen Ausnahmegrund.
- **Checklisten, Einzüge und Ausrichtung sind Quill-Klassen im gespeicherten HTML** (`data-checked`,
  `ql-indent-N`, `ql-align-*`). `HtmlCleanup` erlaubt genau `data-checked` — keine pauschale `data-*`-Freigabe.
  Reine Leseansichten laden kein `quill.snow.css`; die Darstellung steht deshalb zusätzlich in `app.css` unter
  `.dokument-html`. Ein neues Blockformat braucht damit immer drei Stellen: Toolbar, Sanitizer, Lese-CSS.
- **Bilder im Fließtext liegen als Datei auf der Trägerakte, nicht als Base64 in der Spalte.**
  `RichTextHtmlInterceptor` (nach der Schreib-Sperre, **vor** dem Audit — die neuen `TextImage`-Zeilen brauchen
  ihren Stempel) hebt jedes `data:image/...` in die Dateiablage, schreibt `/dateien/textbilder/{id}` und reinigt
  danach erneut. `RichTextImageFields` ist die **Allowlist** — Schreib- **und** Lesepfad: `TextImageService`
  antwortet für registrierte Typen über `Visibility.IsRecordVisibleAsync`, alles andere bleibt unsichtbar.
  Öffentliche Träger (Presse, Warnungen, FAQ, Seiten, Lageberichte, Fraktionsprofil) stehen bewusst **nicht**
  drin, ihr Endpunkt ist intern. Zu groß/fremder Typ/kein Gate ⇒ Base64 bleibt stehen.
- **Beschriftung: Der Editor tippt eine Zeile, die Akte trägt eine `<figure>`.** `richtext.js` markiert die
  Zeile mit `noose-bildtext` (eigenes Zeilenformat), `RichTextFigure.ToStored`/`ToEditor` falten sie beim
  Speichern bzw. entfalten beim Laden — beide Richtungen in C# und getestet, weil ein verlustbehafteter
  Rundlauf still die Beschriftung frisst. Breite ist das Quill-`width`-Attribut (Prozent), gelesen über
  `img[width="…"]` in `app.css`; `figure`/`figcaption` stehen im Sanitizer. Ein Klick aufs Bild öffnet
  `ImageFormatDialog`.
- **Einfügen wird gegen dieselbe Allowlist geputzt wie das Speichern.** `HtmlCleanup.Profile` liefert die
  Listen, `initRichText` bekommt sie als Parameter; der Paste-Pfad wirft unbekannte Tags/Attribute/Styles
  schon im Browser weg, macht aus Word-Listen echte Listen und aus `font-weight`/`font-style`/`text-decoration`
  die Formate `<strong>`/`<em>`/`<u>`/`<s>`. Diese Übersetzung ist der Grund, warum die CSS-Allowlist des
  Sanitizers kein `font-weight` braucht. Strg+Shift+V fügt reinen Text ein. Ein neuer Sanitizer-Eintrag landet
  damit automatisch auch im Editor — die Listen **nicht** im JS duplizieren.
- **Struktur-Bausteine sind Klassen bzw. Blöcke, kein Markup-Zoo:** `noose-kasten-hinweis|warnung|info`
  (Zeilenformat, Text bleibt tippbar), `trenner` (`<hr>`-Block-Embed), `noose-inhaltsverzeichnis` (Liste).
  Heading-Ids vergibt `RichTextAnchors.ToStored` beim Speichern; `BuildToc` nutzt **dieselbe** Slug-Regel,
  sonst läuft der Link ins Leere. `id`/`hr` stehen deshalb im Sanitizer, `scroll-margin-top` hält den Anker
  unter der App-Bar frei. Die Auswahl-Blase ist eigenes JS (`.noose-auswahl`), Rückgängig/Wiederholen hängen
  am History-Modul, Strg+S nur an `OnSaveRequested` (Seiten verdrahten ihren Speichern-Knopf).
- **Connection-Strings nie in `appsettings.json`** — nur User-Secrets/Env.
- **Discord-Redirect** muss im Developer-Portal als `https://noose.info/signin-discord` registriert sein.
- **Score-Writes gehen via `ExecuteUpdateAsync`**, um den Audit-Interceptor zu umgehen (sonst stempelt jeder Recompute `GeaendertAm` → bricht die Aktualitäts-Ampel). **Bulk-/Raw-SQL umgeht generell die Interceptors** → `Permission.RequireWriteAccess` dann explizit aufrufen. Dokumentierte Ausnahmen von dieser Guard-Pflicht: `FactionRecency.StampAsync`, `PublicWantedService.CountViewAsync`, `TipPriorityService` und `RecomputeConfirmedTipsAsync` — abgeleitete Werte hinter einem schon abgesicherten Schreibpfad.
- **Fraktions-Aktualität hängt NICHT an `GeaendertAm`**, sondern an vier eigenen Stempeln auf `Fraktionen`
  (`MitgliederAktualisiertAm`, `BestaendeAktualisiertAm`, `AktivitaetenAktualisiertAm`, `DoksAktualisiertAm`).
  Der **älteste** davon bestimmt die Ampel; Stammdaten-Edits setzen sie nicht zurück. Alles zentral in
  `Services/FactionRecency.cs` (`Reference`/`Oldest`/`Facets`/`ReferenceBefore`/`StampAsync`) — Lesepfade
  (Liste, Karte, Druck, Dashboard, Statistik, `LeadService`) gehen ausschließlich darüber. Neuer Schreibpfad
  auf Mitglieder/Bestände/Aktivitäten/Doks ⇒ `FactionRecency.StampAsync` **nach** dem `SaveChanges` aufrufen
  (Raw-Update, damit der Stempel selbst kein `GeaendertAm`/Audit-Eintrag erzeugt).
- **Nachvollziehbarkeit:** Ein Schreibpfad, der den Interceptor umgeht (`ExecuteUpdate/Delete`/Raw-SQL) **oder** eine nicht-`IAuditable`-Zuordnung ändert (z. B. `TagMapping`), muss selbst eine Zeile via `ManualAudit.Row(entityType, entityId, …)` schreiben — gegen die **Akte** geloggt (⇒ Zeitstrahl + Chronik + Protokoll), bei reinen Config-Aktionen gegen einen Config-Typ (nur Protokoll). `ChangesJson` folgt der `{Feld:[alt,neu]}`-Form (`ManualAudit.Change`), sonst rendert `AuditDisplay.Parse` nichts.
- **Ein Feld, dessen Inhalt nicht ins Änderungsprotokoll gehört, wird in `Infrastructure/Audit/AuditRedaction.cs` eingetragen** (Registry aus `(CLR-Typname, Property)`, vom `AuditSaveChangesInterceptor` beim **Schreiben** gefragt). Grund: **`/nachweis` liest jeder interne Agent** (`Policies.InternalAgent`) und `AuditLogQueryService` filtert nicht nach Typ — Ticket-Inhalte sind sonst führungs-only, ein Hinweis trägt eine Anonymitätszusage. Display-seitig zu filtern reicht nicht: `AuditDisplay.Parse` kennt den Entitätstyp nicht, und `Comment.Text` **soll** dort sichtbar bleiben. Preis: der alte Wert ist danach nirgends mehr rekonstruierbar (Wer/Wann bleiben).
- **Neue Kind-/Anhang-Tabelle einer Akte** (auditiert, aber unsichtbar auf dem Zeitstrahl) → Fall in `TimelineService.AuditSourceAsync` (Fan-out per FK bzw. polymorph über `EntityType/EntityId`) **und** einen Titel in `TimelineDisplay.MapAudit` ergänzen — sonst erscheint sie generisch als „Akte geändert" oder gar nicht.
- **„Neu seit deinem letzten Besuch" liest den Zeitstrahl, nicht die Abschnitte.** Eine neue Zeitstrahl-Quelle
  gibt ihrem `Raw` die **`ActorId`** mit (sonst meldet die Akte dem Agenten sein eigenes Tun als neu) und, wenn
  sie nach dem Ereignis- statt dem Erfassungszeitpunkt sortiert (wie Observation und Aktivität), auch
  **`RecordedAt`** (sonst ist ein Nachtrag nie neu). Wo der Handelnde verborgen wird (`TipAnonymity`), bleibt
  auch die Id leer. Der „letzte Besuch" kommt aus dem Zugriffsprotokoll: Aufrufe mit weniger als 30 Minuten
  Abstand sind **ein** Besuch (`RecordVisits`), damit Neuladen und Prerendern die Markierungen nicht löschen.
  Eine Akte bekommt die Zeile nur, wenn sie ihre Besuche über `LogViewAsync` protokolliert und einen
  `historie`-Abschnitt hat; `SinceLastVisitScanTests` hält die Verdrahtung.
- **Mehrfachauswahl in der Suche: die Typregeln stehen in `Services/RecordBatch.cs`, nicht auf der Seite.**
  `LinkService.CreateManyAsync`, `TagService.AddManyAsync` und `WatchlistService.FollowManyAsync` erzwingen sie
  selbst (`Linkable`, `LinkAnchors`/`LinkTargetsFor`, `Taggable` = die `Tagged`-Kategorien, `Followable`), weil
  `Visibility.IsRecordVisibleAsync` einen unbekannten Typ als sichtbar beantwortet und die Personalakte nur am Rang
  misst — `RecordBatch.ExistsAndVisibleAsync` schließt beides. Jede Sammelaktion beginnt mit
  `Permission.RequireWriteAccess` und speichert **einmal**: acht Saves hießen acht Beobachter-Wellen, die gegen die
  „schon ungelesen"-Prüfung rennen. Nur `TagMapping` bekommt `ManualAudit.Row`; `Link` und `WatchlistEntry` laufen
  durch den Audit-Interceptor — eine zusätzliche Zeile wäre doppelt. Die gewählte Akte ist die **Quelle** jeder
  neuen Verknüpfung; ein Bürgerhinweis ist bewusst kein Anker (Hinweis→Person heißt „übernommen").
- **Bewerbungs-Anschreiben nie auf `{{...}}` „normalisieren"** — `BewerbungTemplateRenderer` schwärzt `\bNAME\b` zu `███████`, damit der Agent gegenüber Bewerbern anonym bleibt; `{{Agent}}` würde stattdessen den Codename ausliefern. `DocumentTemplates` ist dieselbe Tabelle für Bibliothek **und** Bewerbung → Consumer müssen nach `Category` (`RecruitingSeeder.TemplateCategory`) filtern.
- **`SearchNavigation.For` gibt `null` statt zu raten.** Der alte `_ => "/personen/{id}"`-Fallback öffnete für einen
  Kommentar an einer Fraktion eine *Personenakte mit der Fraktions-Id* — eine falsche Akte, lautlos. Ein Treffer ohne
  Route ist nicht klickbar; wer eine Route braucht, prüft `SearchCatalog.IsRoutable`.
- **Deutsches Label, Plural und Icon eines Akten-Typs kommen ausschließlich aus `Services/RecordTypeDisplay.cs`**
  (`Name`/`Plural`/`Icon`, eine Fassade über `SearchCatalog` — keine zweite Tabelle). Jedes Verknüpfungs-Panel
  delegiert dorthin. Vorher lag dasselbe Paar in **acht** Razor-Dateien kopiert und driftete: `CaseContentPanel`
  kannte `Law`/`Meeting`/`Job` nicht (Gruppenüberschrift „Law"), `PrintLinks` und `LawView` schrieben den rohen
  CLR-Namen. Ein neuer verknüpfbarer Typ ist damit **eine `SearchCatalog`-Zeile**, kein Rundgang durch die Panels;
  `LinkCitizenContactTests` hält `LinkService.KnownTypes` gegen den Katalog.
- **Neue Suchkategorie = eine `SearchCatalog`-Zeile + ein `ISearchProvider` + eine Registrierungszeile.** Fehlt eins,
  schlägt `SearchCatalogTests`/`SearchCoverageTests` fehl. Der `Assistant`-Trait wird **zusammen mit dem Provider**
  gesetzt; `NooseiRecordTypes` leitet Name, Plural und die durchsuchbare Menge daraus ab, eine zweite Tabelle gibt
  es nicht mehr. Alle Kategorien tragen den Trait — er entscheidet nur, ob das Modell **eingrenzen** darf;
  Treffer daraus kamen bei einer unbeschränkten `suche_akten` ohnehin an, nur ohne `id=` zum Weiterverfolgen.
- **`SearchIndexBackfillWorker.Version` hochzählen**, wenn `SearchIndexProjection` einen Typ dazubekommt. Sonst
  bekommen Bestandsinstallationen **null** Index-Zeilen für den neuen Typ, und die phonetische Suche wirkt „flaky".
- **Suchtests konstruieren mit `MaxConcurrency = 1`** (`SearchTestHost`): `SqliteTestContext` gibt jedem Context
  dieselbe offene `SqliteConnection`, und zwei gleichzeitige Kommandos darauf sind undefiniert.
- **Stale Docs:** `Authorization/README.md` und `Infrastructure/README.md` sind veraltete „Phase 0"-Stubs; viele `<see cref>`-Tags zeigen auf alte deutsche Typnamen. Quelle ist der Code, nicht die READMEs.

## NOOSEI (KI-Integration)

**Ein einziger Weg zum Modell:** `INooseiGateway.AskAsync` prüft Recht und Kontingent, führt aus,
bucht **genau einmal** ab und protokolliert. `ILlmService` ist reiner Transport und bleibt DB-frei.
Werkzeuge (`Services/Llm/Tools/`) lesen nur, filtern **jeder für sich** über den `ViewerScope`, und
`NooseiToolResult.NotFound()` ist für „existiert nicht" und „darfst du nicht sehen" absichtlich
identisch. **Echtes Geld sieht ausschließlich der KI-Eigner**, alle anderen rechnen in Kontingent-Token.

→ Werkzeug-Register, Sichtbarkeits-Gates, Kontingent-Mathematik, Betriebsspalten und Editor-Korrektur:
**[`claude-memory/noosei.md`](claude-memory/noosei.md)** — lies das, bevor du dort etwas änderst.

## Öffentlicher Bereich

Vollständig gebaut, Phase 1–18: Bürgerkonten, Modulgerüst, redaktionelle Seiten,
Fahndung samt Ausbau und Sachfahndung, Kopfgeld, Bürgerhinweise mit Triage/Übernahme, Belohnung,
Ticket-Chat, Bürger-Vorlagen, Organisationsprofile, Einspruch, Presse, Warnungen, Gesetzesauszüge,
Lageberichte, Gefahrenlage-Ampel, öffentliche Zahlen, die Suchanbindung, Führungsprofile und die
**Ergreifungsmeldung** (ein Bürger meldet, dass er eine gesuchte Person selbst gestellt hat).

**Fünf Regeln gelten überall dort — der Rest steht in `claude-memory/oeffentlich-*.md`:**

- **Ein Bürger ist ein `Agent` mit `Status = Civilian`**, nicht mit Rechten. Zugang (`MayUseCitizenPortal()`),
  Status (`IsCitizen()`) und Einreichen (`MayCitizenSubmit()`) sind drei verschiedene Fragen — **Partner und
  Nur-Lese-Aufsicht dürfen einreichen** (Zivil-Identität, Ticket, Hinweis), nur der Demo-Besucher nicht.
- **Modul-Aus wirkt im Service, nicht in der UI** (`RequireEnabledAsync` wirft); der Not-Aus schlägt jedes
  Einzelmodul, ohne eine gespeicherte Wahl zu verändern. Publizieren braucht ein lebendes Modul,
  ***De*publizieren nie.**
- **`IgnoreQueryFilters()` gilt für die ganze Kompilierung, nicht für den Operanden.** Der
  Unterdrückungsgürtel ist deshalb immer eine **zweite Abfrage**, nie eine Unterabfrage — sonst geht eine
  soft-gelöschte Akte anonym live (nachgemessen, nicht vermutet).
- **Schreib-Guard vor Rang-Guard.** Sonst prägen Nur-Lese-Aufsicht und Demo-Principal Aktenzeichen und
  Foto-Kopien, bevor der `ReadOnlyBarrierInterceptor` das Speichern verweigert.
- **Eine öffentliche Seite schreibt ihren Kopf über `<LinkPreview>`** (`Components/Common/Shared/`) und
  nirgends sonst. Zwei `<HeadContent>`-Blöcke auf einer Seite addieren sich nicht — die Ausgabestelle behält
  den zuletzt registrierten —, deshalb ist `noindex` ein **Parameter** der Vorschau. Ohne die Zeile wird der
  geteilte Link zur nackten Adresse; `PublicPageScanTests` fordert beides ein.

→ Einstieg immer über **[`claude-memory/oeffentlich-grundlagen.md`](claude-memory/oeffentlich-grundlagen.md)**,
dann die Themendatei aus der Tabelle oben.

## Handbuch pflegen (`/handbuch`)

Der Erstbestand liegt unter `NOOSE-Website/Infrastructure/Handbook/` — `HandbookContent.cs` hält nur
die Formen und setzt das Buch zusammen, der Text steht **eine Datei je Kapitel** in `Content/`
(plus `GlossaryContent.cs`). Der Seeder schreibt ihn beim Start ein und fasst **nie** an, was jemand
redaktionell bearbeitet hat (`IstAngepasst`). Dieselbe Konfliktregel wie beim Changelog.

Bestand: 9 Kapitel, 97 Artikel, 169 Glossarbegriffe, 15 Schaubilder, 41 Schritt-Karten.

- **Ton:** direkte Anrede, kurze Sätze, Klicknamen kursiv. Ein Fachwort beim ersten Auftreten erklären —
  genau dort greift später auch die Erklär-Blase aus dem Glossar.
- **Zwei Sorten Inhalt, bewusst getrennt:** `ContentHtml` ist die Anleitung, `RollenspielHtml` der
  abgesetzte Kasten „Im Rollenspiel bedeutet das …". So lassen sich RP-Regeln ändern, ohne die
  Anleitung anzufassen.
- **Diagramme gehören nicht in den Text.** `HtmlCleanup` erlaubt kein `<svg>` und würde ein
  eingebettetes beim ersten Speichern strippen. Ein Artikel nennt nur einen `DiagrammSchluessel`;
  die Zeichnung ist eine Razor-Komponente unter `Components/Pages/Handbook/Diagrams/` und wird in
  `HandbookDiagram.Known` registriert. `HandbookTests.Every_shipped_diagram_key_names_a_drawing` hält das.
- **Schritt-Karten sind Zeilen, kein Markup.** Sie werden mit ihrem Artikel **komplett ersetzt**, nicht
  einzeln geschlüsselt.
- **`NavSchluessel` verbindet einen Artikel mit einem Menü-Eintrag** (`NavEntry.Key`) — daraus speist sich
  der „?"-Knopf (`Components/Common/Shared/HandbookHelpButton.razor`, gerendert von `PageHeader`). Ein
  Tippfehler dort lässt den Knopf still verschwinden; `Every_shipped_nav_key_names_a_menu_entry` fängt ihn ab.
  **`NavCatalog.ByRoute` nimmt das längste Präfix**, also fallen alle Unterrouten auf ihren Listen-Eintrag
  (`/personen/{id}/bearbeiten` ⇒ `personen`) und `?tab=` wird verworfen — die sieben Sammelseiten haben je
  **einen** Artikel. Wo das falsch ist: `HelpNavKey` setzen. Wo der Knopf auf die Seite zeigen würde, auf
  der man steht: `ShowHelp="false"` (so machen es `/handbuch` und `/handbuch/{slug}`).
- **Der Nav-Key-Lesepfad ist gecacht** (`HandbookService`, Generationszähler im `IMemoryCache`), weil er auf
  36 Seiten je Aufruf läuft und Prerendering ihn verdoppelt. **Jeder Schreibpfad ruft `Evict()`** — sonst
  sieht ein Redakteur seine eigene Änderung zehn Minuten lang nicht;
  `An_edited_article_is_visible_to_the_help_button_at_once` hält das.
- **Jede Änderung an einer bestehenden Zeile ⇒ `HandbookContent.Revision` hochzählen** — Text, Titel,
  Slug, Kurzbeschreibung, Diagramm- oder Nav-Schlüssel. Ein neuer Artikel braucht das nicht; er wird an
  seinem fehlenden Key erkannt. **Reihenfolge und Kapitelzugehörigkeit sind davon ausgenommen**: die zieht
  der Seeder auch ohne Bump nach, weil sie Struktur sind und nicht Text. Vorher standen sie hinter dem
  Revisions-Guard, und ein mitten in ein Kapitel eingefügter Artikel bekam damit dieselbe
  `Reihenfolge` wie der, den er verdrängte — die Sortierung zwischen den beiden war danach undefiniert.
  Eine redaktionell angefasste Zeile (`IstAngepasst`) bleibt auch davon unberührt.
- **Slugs im Erstbestand schon sauber schreiben** (Kleinbuchstaben, Bindestriche, keine Umlaute). Der
  Seeder schreibt sie roh, der Editor bereinigt — `Every_shipped_slug_is_already_url_clean` hält beide zusammen.
- **Eine neue Seite braucht einen Artikel.** `Every_menu_entry_has_an_article` fordert je `NavEntry` einen
  Artikel mit passendem `NavSchluessel`; die einzige Ausnahmeliste im Test sind Zweit-Einträge auf eine
  Seite, die schon einen Artikel hat (ein Artikel trägt genau einen Schlüssel).
- **Erklär-Blasen laufen auf dem Renderpfad, nie beim Speichern.** `GlossaryHtml.Annotate` (aufgerufen von
  `RichHtml`, **nach** dem Erwähnungs-Durchlauf) schreibt `<span class="glossar" data-glossar="…">` — ein
  Attribut, das `HtmlCleanup` gar nicht kennt. Käme diese Ausgabe je in einen Speicherpfad, bliebe ein toter
  dekorierter Span in der Akte zurück. Reihenfolge zählt: umgekehrt zerschnitte der Glossar-Durchlauf den
  Textknoten, den `MentionParser` mit absoluten Offsets liest.
- **Der Glossar-Durchlauf fasst nur Textknoten an** und überspringt `a`, `code`, `pre` sowie alles unter
  `.erwaehnung`/`.glossar` — die Erwähnungs-Ausgabe ist ein **Span**, kein Link, und „Verschlusssache" ist
  selbst ein Begriff. Längster Treffer zuerst (sonst „Senior Special ⟨Agent⟩"), Wortgrenzen (sonst leuchtet
  „Fahndung" in „Fahndungsliste"), jeder Begriff **einmal je Block**.
  Wortgrenzen zählen **Buchstaben, Ziffern und kombinierende Zeichen** — ohne Letztere reißt ein zerlegtes
  „Akte&#x0300;" seinen Akzent aus der Blase heraus.
  **Die Wortgrenze endet nicht am Textknoten.** `GlossaryHtml.Neighbour` reicht das Zeichen links und rechts
  **über Inline-Tags hinweg** an `LongestAt` weiter; ein Block, ein `<br>` oder ein Bild beendet das Wort.
  Ohne das leuchtete „Fahndung" in `Fahndung<b>sliste</b>` — was der Editor schreibt, sobald jemand eine
  Silbe fettet —, also wieder eine **falsche** Erklärung mitten im Wort statt einer fehlenden.
  **Whitespace im Begriff ist tolerant** (`GlossaryMatcher.MatchLength`): der Editor schreibt für einen
  doppelten oder abschließenden Leerschritt ein `&nbsp;`, und ein strenger Vergleich verfehlte dann den
  langen Begriff und setzte die Blase auf das Wort *darin* — „Senior&nbsp;Special&nbsp;⟨Agent⟩", also eine
  **falsche** Erklärung, keine fehlende. Deshalb misst `LongestAt` jeden Kandidaten, statt den ersten Treffer
  zu nehmen: mit flexiblem Whitespace verbraucht der längere Begriff nicht zwangsläufig mehr Zeichen.
  Alles davon hängt an `GlossaryHtmlTests` — 34 Fälle, weil jeder Fehler dort stumm ist.
- **Auf Papier nie.** Die Blasen hängen an `RichHtml.Plain`, und `PrintRichHtmlScanTests` fordert für jede
  `RichHtml`-Stelle in einer `@layout PrintLayout`-Seite ein `Plain="true"`. Routen-Schnüffeln wäre falsch:
  `/lageberichte/{Id}` nutzt das Drucklayout ohne `/druck`-Adresse.
- **Ein Begriff ist unique indexiert**, nicht nur sein Seed-Key — `Every_shipped_term_is_written_only_once`
  fängt die Dublette ab, die sonst erst den ersten Start sprengt.
- **Schreiben dürfen Führung und HRB** (`Permission.RequireHrbOrLeadershipWrite` — die Schreib-Variante
  des vorhandenen `RequireHrbOrLeadership`, das nur den Zugang zum Bewerbungswesen regelt).
- **Handbuch und Glossar sind zwei Suchkategorien** (`Quick | SideIndexed | Assistant`). **Kein `Heavy`** —
  nicht weil `Quick` das verböte (`Document` und `Meeting` tragen beides), sondern weil der Artikeltext
  longtext ist und draußen bleiben soll: gefunden wird über Titel, Kurzbeschreibung und Kapitel, so wie es
  auch das Suchfeld im Handbuch tut.
- **Der Slug ist der Schlüssel des Artikels im ganzen Suchpfad** — Treffer, Index-Eintrag und `ResolveIdsAsync`.
  `/handbuch/{Slug}` ist die Adresse, eine Id dort ergibt 404. Und die Zweitwelle entdoppelt ihre Kandidaten
  gegen die schon gefundenen `TargetId`s: mit zwei verschiedenen Schlüsseln kam **jeder** Artikel, den die
  erste Welle gefunden hatte, ein zweites Mal zurück. `SourceId` bleibt die Zeilen-Id, damit ein Umbenennen
  den Eintrag umschreibt statt den alten Slug liegen zu lassen (so macht es auch der `PersonAlias`-Zweig).
  `HandbookSideIndexTests` hält das.
- **Nicht jede `Quick`-Kategorie kann eine Erwähnung tragen.** Der @-Picker speist sich aus
  `ISearchService.QuickSearchAsync`, und der Token ist `@{Typ:GUID}` — ein Slug passt nicht auf die Regex,
  der Token käme **roh in den Kommentar** und würde wörtlich angezeigt. `MentionService.CandidatesAsync`
  filtert deshalb auf `LinkService.KnownTypes` (die Typen, die überhaupt als Referenz auflösen); für alle
  Kategorien, die es vor dem Handbuch gab, ist der Filter ein No-Op.
- **Der Glossarbegriff hat keine eigene Seite** und wird über `/handbuch?begriff={Id}` geöffnet, aufgelöst in
  `Handbook.OpenTermFromQuery()`. **Über die Id, nicht den Namen:** `SearchCatalog.Route` füllt die Vorlage mit
  `string.Format` und kodiert **nicht** — ein Begriff trägt Leerzeichen und Umlaute.
- **NOOSEI liest das Handbuch über `schlage_nach`**, nicht über `lies_akte`: beide Typen stehen in
  `NooseiRecordTypes.ReachableWithoutRead`, nicht in `Uses`. Ein Artikel ist eine Antwort auf eine Frage, keine
  Akte. Das Werkzeug bewertet erst Titel/Kurzbeschreibung/Kapitel und lädt **nur für die Besten** den Text —
  gegen jeden Rumpf zu scoren hieße achtzig longtext-Spalten für eine Frage zu lesen. Damit das stimmt,
  **projiziert `GetChaptersAsync`** auf die Kartenfelder statt Entitäten zu laden; vorher zog schon die
  Indexseite jeden Artikeltext mit. Der Textabruf ist zusätzlich **eigenständig gedeckelt**
  (`MaxArticleBodies`), sonst kostete ein `max: 40` über hundert Rundreisen; die Quellen-Chips ebenso
  (`MaxRefs`). Das **Glossar steht vor den Artikeln**, weil der Clip das Ende abschneidet — sonst fiele eine
  Definition weg, während ihr Chip stehen bliebe.
- **`SearchIndexBackfillWorker.Version` steht auf 4.** Wer die Projektion um einen Typ erweitert, zählt hoch
  **und** ergänzt die `IndexAllAsync`-Zeile — sonst bekommt eine Bestandsinstallation null Index-Zeilen.

## Einarbeitungs-Checkliste

Sechs Schritte, die sich selbst abhaken. Die Regeln stehen **einmal** in `Services/Onboarding.cs` (statischer
Helfer, wie `Permission`); der Zustand liegt als Schlüsselmenge in `NavPreferences.OnboardingDone`.

- **Fünf Schritte werden gestempelt, wo sie passieren** (`INavPreferencesService.MarkOnboardingStepAsync`):
  `/profil`, `/handbuch` und `/handbuch/{slug}` (dort zusätzlich `kapitel:<slug>` — es ist die einzige Stelle,
  die das Kapitel eines Artikels kennt), die Suchseite (es gibt kein Suchprotokoll) und `RecentsTracker`
  für „erste Akte". **Der sechste ist abgeleitet** (Menü angepasst): die Präferenzen sind schon die Antwort.
- **`Recents` taugt nicht als Nachweis** — die Liste ist auf 15 gedeckelt, ein daraus gelesener Schritt würde
  sich nach fünfzehn weiteren Besuchen selbst zurücknehmen. Deshalb wird beim Ereignis gestempelt.
- **Ein Schritt darf keine Abfrage kosten.** Alles kommt aus dem Präferenzen-Blob, den der Drawer ohnehin
  geladen und 30 s gecacht hat. Ein Schritt, dessen „erledigt" Zeilen zählen müsste, gehört nicht auf die Liste.
- **Schreiben läuft über `ExecuteUpdateAsync`** und umgeht damit den `ReadOnlyBarrierInterceptor` bewusst
  (reine UI-Präferenz, kein Audit-Eintrag). Jeder Stempel steht in `try/catch` — er darf nie die Seite kosten.
- **Je Stempel ein winziger, idempotenter Schreibvorgang.** `MutateAsync` ist ein Read-Modify-Write über den
  **ganzen** Blob, und jede Navigation schreibt parallel `PushRecentAsync`. Zwei überlappende Mutationen
  verlieren deshalb eine der beiden Änderungen — der „profil"-Stempel wurde bei **jedem** Besuch geschrieben
  und sofort wieder überschrieben, der Schritt konnte nie abhaken. Dagegen steht jetzt ein **Schloss je Agent**
  (gestreift, statisch) um Lesen-Ändern-Schreiben **und** den Cache-Write. Im Testharnisch ist das unsichtbar:
  `SqliteTestContext` gibt jedem Context dieselbe offene Verbindung und serialisiert von selbst.
- **Auch `GetAsync` füllt den Cache unter demselben Schloss**, mit Doppelprüfung darin. Ein Lesefehlschlag
  holt den Blob sonst außerhalb des Schlosses, eine Mutation schreibt dazwischen, und der Leser legt seinen
  Stand von **vor** der Mutation wieder obenauf — dieselbe verlorene Änderung wie oben, nur von der Leseseite
  aus betreten. Der Schnellpfad (Cache-Treffer) läuft weiter ohne Schloss.

## Changelog pflegen (`/neuerungen`)

**Jedes Feature, das ein Agent bemerkt, bekommt eine Zeile** in
`NOOSE-Website/Infrastructure/Changelog/ChangelogContent.cs` — das ist Teil des Features, nicht Nacharbeit.

- **Ton:** ein kurzer Satz in Alltagssprache, aus Sicht des Lesers. Keine Technik: kein Dienst, keine Tabelle,
  kein Interceptor, kein Commit. `ChangelogTests.No_shipped_line_talks_about_the_technology` hält eine
  Sperrliste dagegen. Aus *„Partition the rate limiters"* wird **„Anmeldeseite lässt sich nicht mehr von außen
  blockieren"**.
- **Nichts eintragen** für Umbenennungen, Tests, Doku, Refactorings — alles, was von außen unsichtbar ist.
- **Neue Zeile:** an die passende `SeededRelease` anhängen. `Key` ist stabil und einmalig im Format
  `<major>.<minor>.<laufende-zweistellige-nummer>-<kurz>`; die dritte Zahl startet je Fassung bei `00` und
  steigt pro Eintrag um eins (`2.1.00-name`, `2.1.01-name`).
  Eine neue Zeile braucht **keine** Revisions-Erhöhung; sie wird an ihrem fehlenden Key erkannt.
- **Bestehende Zeile umformulieren:** `ChangelogContent.Revision` **hochzählen**. Der Seeder schreibt dann
  unberührte Zeilen neu — und lässt redaktionell bearbeitete (`IstAngepasst`) für immer in Ruhe. Ohne Bump
  passiert nichts.
- **Neue Fassung:** `SeededRelease` mit Version im Format `X.X.XX` anlegen. **Die Build-Nummer nicht eintragen** —
  `BuildNumber.txt` ist gitignored und beim Schreiben unbekannt; `ChangelogSeeder` stempelt sie beim ersten
  Start nach dem Deploy auf die neueste Fassung ohne Stempel.
- **Seeden nur über einen Kontext mit Audit-Interceptor.** Das von ihm gestempelte `ErstelltAm` **jeder Zeile**
  ist das, womit das Neuerungen-Fenster vergleicht; ohne Interceptor meldet es still und dauerhaft nichts.
- **Das Neuerungen-Fenster zählt Zeilen, nicht Fassungen.** `ChangelogNewsPrompt` (im `MainLayout`, erst nach
  dem ersten interaktiven Render, nicht über der Wartungsseite) holt über `GetNewsSinceAsync` jede sichtbare
  Zeile mit `ErstelltAm` nach `NeuerungenLastSeenUtc` und öffnet `ChangelogNewsDialog`. Eine Fassung sammelt
  Zeilen über mehrere Deploys (2.2.06–2.2.10 kamen einzeln in 2.2.00); die alte Karte verglich die Fassung und
  übersah deshalb jede angehängte Zeile. Umformulieren (Revision) und Umziehen (`LegacyKey`) lassen `ErstelltAm`
  stehen und melden nichts. Gestempelt wird beim Schließen, mit der **Lesezeit**; `/neuerungen` stempelt selbst
  und bekommt kein Fenster; ein erster Besuch überhaupt stempelt still. Wie viel das Fenster zeigt, steht in
  `ChangelogNewsFlash.PreviewLines` (Rest: „… und N weitere").
  **Zwei Einstellungen, die nicht zurückgedreht werden dürfen:** `CloseOnNavigation = false` am Dialog — MudBlazor
  schließt Dialoge sonst bei jedem Pfadwechsel mit *Abbrechen*, und das Fenster wäre ungelesen gestempelt; und
  `SetNeuerungenLastSeenAsync` rückt den Stempel **nur vor**, sonst setzt ein spät geschlossenes Fenster in einem
  Tab die schon gelesene Seite im anderen zurück. Der Prompt steht außerhalb der `ErrorBoundary`; jede Ausnahme
  darin muss gefangen werden, sonst endet der Circuit.
- **Eine Zeile in eine andere Fassung verschieben:** ihr neuer `Key` muss zur neuen Fassung passen
  (`Every_shipped_version_and_key_uses_sequential_two_digit_updates` fordert das), und genau deshalb darf
  man ihn nicht einfach umschreiben: der Seeder fände die alte Zeile nicht wieder, ließe sie in der alten
  Fassung stehen und schriebe eine zweite daneben. Die Zeile nennt deshalb ihren alten Schlüssel selbst
  (fünftes Argument von `Neu`/`Besser`/`Fix`, `SeededEntry.LegacyKey`) — dann zieht der Seeder dieselbe
  Zeile um. `LegacyVersion` an der Fassung kann das nicht: es schreibt nur den Präfix aller Schlüssel
  einer Fassung um, und eine umziehende Zeile bekommt eine neue laufende Nummer.

## Domänen-Glossar

| Begriff | Bedeutung |
|---------|-----------|
| **NOOSE** | National Office of Security Enforcement — fiktive Geheimdienst-Behörde |
| **Personenakte / Person** | Zentrale Akte je Person (Tabelle `Personen`) |
| **Personen-Dok** | Verhör-/Maßnahmen-Protokoll an einer Person; Ausgang Spritze/offiziell/erschossen/laufen |
| **Steckbrief** | Erweiterte Person-Daten (Aliase, Telefon, Fahrzeuge, Waffen) |
| **Fraktion / Partei / Personengruppe** | Gruppierungs-Akten mit eigenen Mitgliedern/Rängen/Konflikten |
| **Einstufung / Classification** | Prüffall → Verdachtsfall → Gesichert staatsgefährdend |
| **Verschlusssache (VS) / `IsClassified`** | Führungs-only-Sichtbarkeit; VS-Stufen für Doks: None/Leadership/TRU/HRB |
| **Personalakte** | Dienstgrad-Verlauf, Notizen, Beförderungen, Ausbildungsmodule je Agent |
| **Beförderung** | Antrags-/Entscheidungs-Workflow (`AgentPromotionRequest`) |
| **Taskforce** | Einheit mit Genehmigung; Scope innerbehördlich/überbehördlich |
| **EHK-Score / Bedrohungs-Score** | Automatischer Gefährdungswert (0–100) je Fraktion/Person |
| **Aktenzeichen** | Menschenlesbare ID, z. B. `NOOSE-P-2026-0001` |
| **Wartungsmodus** | In `MainLayout.razor` erzwungen (keine Middleware); Admins behalten Zugriff |
| **Klarname / Codename** | Realname (führungs-/nicht-OnlyReader-only) vs. Dienst-Codename |
| **TRU / HRB** | Tactical Response Unit / Human Resource Branch — Flags neben dem Dienstgrad (laut DVO erst ab Special Agent) + VS-Stufen |

## Weiterführende Docs

- `README.md` — Funktionsübersicht (Features-Sektion, intern + öffentlich), Schnellstart, Deployment
- `docs/DEPLOYMENT.md` — Plattform-Setup, Deploy-Workflow (Prod, Demo, Rollback), Backups, Troubleshooting
- `docs/CODE_REVIEW_TODO.md` — bekannte Tech-Debt-/Review-Findings
- `IdeenBacklog.md` — Feature-Roadmap: 66 bewertete Vorschläge, einzeln entschieden (28 angenommen,
  31 vorgemerkt, 3 abgelehnt). Je Vorschlag **die Dateien, an denen er ansetzt**. Vor einem neuen
  Feature dort nachsehen — die Analyse ist gemacht, und ein abgelehnter Punkt trägt seinen Grund.
- `claude-memory/` — Detailwissen je Bereich (Tabelle oben). **Warum** eine Regel existiert, nicht nur dass sie gilt.
