# Architektur

NOOSE ist eine einzige Blazor-Web-App mit einer MariaDB dahinter. Der interne Aktenbereich und der öffentliche Bereich laufen im selben Prozess und teilen sich die Datenbank. Getrennt sind sie über Dienste und Sichtbarkeitsregeln, nicht über eigene Anwendungen.

## Tech-Stack

| Bereich | Technologie | Version |
|---------|-------------|---------|
| Runtime | .NET | `net10.0` |
| UI | Blazor Web App, nur Interactive Server (SignalR) | - |
| Komponenten | MudBlazor, nur Dark-Mode („Anthrazit + Cyan“) | 9.5.0 |
| ORM | Pomelo.EntityFrameworkCore.MySql, zieht EF Core 9 mit | 9.0.0 |
| Identity | Microsoft.AspNetCore.Identity.EntityFrameworkCore | 9.0.16 |
| EF Design | Microsoft.EntityFrameworkCore.Design | 9.0.16 |
| OAuth | AspNet.Security.OAuth.Discord | 10.0.0 |
| HTML-Bereinigung | HtmlSanitizer | 9.0.892 |
| Markdown | Markdig | 1.3.2 |
| Health-Checks | Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore | 9.0.16 |
| EF-Tool | dotnet-ef, lokal gepinnt | 9.0.17 |

EF Core und Identity bleiben bewusst auf der Linie 9.0.x. Pomelo 9.0.0 kann nur EF Core 9, ein Upgrade auf 10.0.x würde EF Core 10 ziehen und mit Pomelo kollidieren. Die Pakete 9.0.x laufen ohne Probleme auf der Runtime von .NET 10.

Quill 1.3.7, vis-network 9.1.9, FullCalendar 6.1.15 und ECharts liegen selbst gehostet unter `wwwroot/lib` und werden erst bei Bedarf über JS-Interop geladen. Dasselbe gilt für die Module unter `wwwroot/js`, etwa `graph.js`, `kalender.js`, `richtext.js`, `entwurf.js` oder `statistik-charts.js`. Global geladen wird nur `wwwroot/app.js`.

Lokal läuft die App gegen MariaDB oder MySQL, etwa aus XAMPP, in Produktion gegen MariaDB 10.11 im Container. Die Engine erkennt EF über `ServerVersion.AutoDetect()` selbst.

## Aufbau

| Ordner in `NOOSE-Website/` | Inhalt |
|--------|--------|
| `Components/` | Razor-Seiten und UI, dünn gehalten, je Feature ein Ordner |
| `Data/` | `AppDbContext`, `Entities/<Domain>/`, `Migrations/` |
| `Models/` | DTOs, `Enums/`, `Abstractions/` mit den Marker-Interfaces |
| `Services/` | Fachlogik und Durchsetzung der Rechte, mit Unterordnern wie `Public/`, `Search/`, `Threat/` oder `Llm/` |
| `Authorization/` | Policies, Requirements, Handler, Erweiterungen für `ClaimsPrincipal` |
| `Infrastructure/` | Interceptors, Broadcaster, Hintergrund-Worker, Audit, Dateiablage, aktueller Nutzer, Seeder |
| `Navigation/` | Katalog der Navigation, Weiterleitungen alter Routen, Personalisierung der Navigation |
| `Theme/` | `NooseTheme.cs` mit der dunklen Palette, `ChartPalette.cs` für die Diagramme |

## Muster, die tragen

Den Render-Mode entscheidet `App.razor` zentral über `AcceptsInteractiveRouting()`. Jede Seite läuft als `InteractiveServer`, außer sie trägt `[ExcludeFromInteractiveRouting]` und rendert dann statisch. Statisch rendern alle Seiten unter `Pages/Public`, damit sie ohne Circuit auskommen. Das Bürgerportal unter `Pages/Portal` läuft interaktiv.

Ein Dienst injiziert immer `IDbContextFactory<AppDbContext>` und erzeugt je Operation einen kurzlebigen Context. Ein Context, der am Circuit hängt, teilen sich zwei Operationen. Dann bricht EF mit „A second operation was started on this context“ ab oder mit einer `ObjectDisposedException`, wenn ein Dialog oder eine Navigation den Scope schon beendet hat.

Fünf SaveChanges-Interceptors laufen in fester Reihenfolge (`Program.cs`), und die Reihenfolge zählt:

1. `ReadOnlyBarrierInterceptor` lehnt Schreibzugriffe von Konten ab, die nur lesen dürfen, etwa Partnerbehörden, bis auf wenige Ausnahmen. Er kommt zuerst, damit nichts anderes vorher läuft.
2. `RichTextHtmlInterceptor` verschiebt base64-Bilder aus den Rich-Text-Spalten in Dateien und legt dafür eigene Bild-Zeilen an. Er läuft vor dem Audit, weil diese Zeilen den Stempel noch brauchen.
3. `AuditSaveChangesInterceptor` stempelt alles mit `IAuditable` und macht aus einem harten Löschen ein Soft-Delete.
4. `WatchlistChangeInterceptor` merkt sich Änderungen für die Follower einer Akte.
5. `SearchIndexInterceptor` baut die Indexzeilen der geänderten Datensätze aus dem Endzustand neu und kommt deshalb zuletzt.

Soft-Delete und Audit hängen an Marker-Interfaces (`ISoftDelete`, `IAuditable`). Ein globaler Query-Filter auf `!IsDeleted` entsteht per Reflection. Eine neue Entity implementiert das passende Interface, mehr braucht es nicht.

Live-Updates laufen über Singleton-Broadcaster. Ein Scoped-Dienst schreibt die Zeile und ruft danach den Broadcaster, der die verbundenen Circuits benachrichtigt, etwa `NotificationBroadcaster`, `TaskforceChatBroadcaster`, `TipsBroadcaster` oder `TicketBroadcaster`. Dazu kommt der `WatchlistDispatcher`. Daneben laufen Hintergrund-Worker als `AddHostedService`, zum Beispiel für Wiedervorlagen, die tägliche Neuberechnung der Bedrohungs-Scores, den Lagebericht und den Ablauf von Eignungstests und öffentlichen Ausschreibungen.

Auch die Middleware hat eine feste Reihenfolge. `PublicIndexingMiddleware` setzt außerhalb der öffentlichen Routen `noindex` und sitzt vor dem ExceptionHandler, damit auch eine Fehlerseite den Header behält. Der RateLimiter läuft nach Antiforgery, damit ein POST ohne Token kein Kontingent verbraucht.

Nach der Migration beim Start legen idempotente Seeder fehlende Zeilen an, etwa `PublicModuleSeeder` oder `PublicTemplateSeeder`. Eine gespeicherte Wahl überschreiben sie nie.

Rechte setzt der Service-Layer durch, nicht die Oberfläche. Jede schreibende Methode nimmt `ClaimsPrincipal actor` und ruft als erste Anweisung einen Guard `Permission.Require*`. Die Sichtbarkeit steht zentral in `Visibility`, für den öffentlichen Bereich in `Public/PublicVisibility`. Berechtigungslogik gibt es nur in `Authorization/AgentPrincipalExtensions.cs` und `Services/Permission.cs`.

## Datenmodell

Es gibt genau einen `AppDbContext : IdentityDbContext<Agent>`, alle Fluent-Konfigurationen stehen in `OnModelCreating`. Spalten und Tabellen heißen deutsch, die C#-Member englisch. `Person.CaseNumber` liegt also in der Spalte `Aktenzeichen` der Tabelle `Personen`, `IsDeleted` in `IstGeloescht`.

Aktenzeichen sind für Menschen lesbar, etwa `NOOSE-P-2026-0001`, und entstehen ohne Kollision über `CaseNumberCounter`. Jeder Bereich hat sein eigenes Präfix, etwa `P` für Personen, `FA` für öffentliche Ausschreibungen, `H` für Bürgerhinweise, `T` für Tickets, `EIN` für Einsprüche, `BEL` für Belohnungsbelege, `PM` für Pressemitteilungen, `KAS` für Kassenbuchungen, `ASS` für Asservate und `ENT` für Entführungen.

Quellen, Kommentare, Tags, Verknüpfungen und Wiedervorlagen hängen polymorph an jeder Akte, über das Paar `(EntityType, EntityId)` und ohne echten Fremdschlüssel.

Der öffentliche Bereich hat eine eigene Domäne in `Data/Entities/Public/`, darunter Ausschreibung mit Warnhinweisen, Kopfgeld-Anteil, Einspruch, Hinweis mit Nachricht und Belohnung, Ticket mit Beteiligten und Nachrichten, Pressemitteilung, Warnung, Lagebericht, Fraktions- und Führungsprofil, Seite, FAQ, Modul, Vorlage und Bürgerprofil. Was von welcher Tabelle nach außen gehen darf, und warum der Rest nie hinausgeht, steht ausnahmslos in `Services/Public/PublicVisibility.cs`. Ein Test prüft über alle DbSets, dass keine Tabelle fehlt.

| Aktentyp | Tabelle | Einstufung | Bedrohungs-Score |
|----------|---------|------------|------------------|
| Person | `Personen` | ✓ | 0–100 |
| Fraktion | `Fraktionen` | ✓ | 0–100, `null` = nicht bewertet |
| Partei | `Parteien` | ✓ | - |
| Personengruppe | `Personengruppen` | ✓ | - |
| Operation | `Operationen` | ✓ | - |
| Taskforce | `Taskforces` | - | - |

Es gibt zwei Arten von Einstufung, und der Server setzt beide durch. `Classification` ist der Status einer Akte, `DocumentClassification` die VS-Stufe eines Dokuments:

| Wert | `Classification` | `DocumentClassification` |
|---|---|---|
| 0 | `Unknown`, nicht eingestuft | `None`, für alle |
| 1 | `ReviewCase`, Prüffall | `Leadership` |
| 2 | `SuspicionCase`, Verdachtsfall | `Tru` |
| 3 | `SecuredStateThreatening`, gesichert staatsgefährdend | `Hrb` |

Die höchste Einstufung einer Akte setzt direkt, wer ab Senior Special Agent ist oder Admin. Alle anderen stellen dafür einen Antrag.

Der Bedrohungs-Score gilt für Personen und Fraktionen und liegt zwischen 0 und 100. Sein Anker folgt der Einstufung, 75 für gesichert staatsgefährdend, 50 für einen Verdachtsfall, 12 für einen Prüffall und 0 ohne Einstufung. Ein optionaler Konfidenzwert bildet Datenlücken ab, die Begründung liegt strukturiert in `BedrohungsDetailJson`. `null` heißt nicht bewertet, etwa bei Fraktionen des Staates. `ThreatScoreSweepWorker` rechnet täglich alles neu.

## Rollen und Rechte

Rechte ergeben sich aus drei Achsen, die unabhängig voneinander sind: dem Dienstgrad, einigen Flags am Konto und den Policies.

| Wert | Dienstgrad | Hinweis |
|------|------|---------|
| 1 | JuniorAgent | |
| 2 | SpecialAgent | |
| 3 | SeniorSpecialAgent | darf die höchste Einstufung setzen |
| 4 | SupervisorySpecialAgent | ab hier Führung |
| 5 | DeputyDirector | entscheidet über Beförderungen |
| 6 | Director | |

Zur Führung gehört, wer mindestens Supervisory Special Agent ist oder `IsAdmin` trägt. Im Code fragt das `IsLeadership()` aus `Authorization/AgentPrincipalExtensions.cs` ab.

| Flag am `Agent` | Bedeutung |
|------|-----------|
| `IsAdmin` | Vollzugriff, zählt als Führung und überspringt jede Anforderung an den Dienstgrad |
| `IsTRU` | Tactical Response Unit, Zugriff auf VS-Dokumente der TRU |
| `IsHRB` | Human Resources Branch, Zugriff auf VS-Dokumente der HRB und auf die Verwaltung des Recruitings |
| `IsTeamLead` | ein Marker ohne eigene Rechte |

Nicht jedes Konto ist ein Agent. Den Unterschied macht `AgentStatus`:

| Wert | Status | Bedeutung |
|------|--------|-----------|
| 0 | Pending | angemeldet, wartet auf Freigabe |
| 1 | Active | freigegebener interner Agent, über `PartnerAgency` auch eine Partnerbehörde |
| 2 | Blocked | ausgesperrt, eine laufende Sitzung endet nach höchstens 30 Sekunden |
| 3 | Applicant | Bewerber mit Zugang nur zum Bewerber-Portal `/portal` |
| 4 | Terminated | gekündigt |
| 5 | Civilian | Bürger, über Discord angemeldet, ohne jedes Behördenrecht, nur im öffentlichen Bereich und unter `/buerger` |

Alle Policies stehen in `Authorization/Policies.cs` und werden in `AuthorizationRegistration` registriert. Darunter sind `CitizenPortal` als Zugang zum Bürgerbereich, `ApplicantPortal`, `InternalAgent`, `DocumentAuthor` und `AiOwner`. `AiOwner` ist eine eigene Achse neben dem Admin und entscheidet allein über die Kontingente von NOOSEI und die Anzeige der echten Kosten.
