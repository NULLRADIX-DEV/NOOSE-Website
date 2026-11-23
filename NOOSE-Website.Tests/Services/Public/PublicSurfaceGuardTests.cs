using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NOOSE_Website.Authorization;
using Microsoft.Extensions.Logging;
using NOOSE_Website.Data;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Infrastructure.Notifications;
using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Navigation;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Tests.Services.Public;

/// <summary>The registries nothing used to enforce.</summary>
/// <remarks>
/// Each of these has burnt someone once. A new audited entity showed its raw CLR name in the German UI of /nachweis
/// (BuergerProfil, until phase 2). A public route that was not a child of /gesucht would have been missed in the demo
/// middleware. And a partner saw "not released for partner agencies" on a page he can open logged out.
/// </remarks>
public class PublicSurfaceGuardTests
{
    private static string DisplayFile([CallerFilePath] string here = "")
    {
        var file = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..",
            "NOOSE-Website", "Services", "AuditEntityDisplay.cs"));
        Assert.True(File.Exists(file), $"AuditEntityDisplay nicht gefunden: {file}");
        return file;
    }

    private static IEnumerable<Type> EntityTypes()
        => typeof(AppDbContext).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType.IsGenericType
                && p.PropertyType.GetGenericTypeDefinition() == typeof(Microsoft.EntityFrameworkCore.DbSet<>))
            .Select(p => p.PropertyType.GetGenericArguments()[0])
            .Distinct();

    [Fact]
    public void EveryAuditablePublicAreaEntity_HasALabelAndARoute()
    {
        // The interceptor stamps the CLR name; without an arm here it is what /nachweis shows a German-speaking user
        // — BuergerProfil went out that way until phase 2.
        // the label is checked against the source rather than against Label(name) != name: "Warnhinweis" happens to
        // read the same in both languages, so a value comparison would call the correct arm a miss
        var source = File.ReadAllText(DisplayFile());
        var offenders = EntityTypes()
            .Where(t => t.Namespace == "NOOSE_Website.Data.Entities.Public")
            .Where(t => typeof(IAuditable).IsAssignableFrom(t))
            .Where(t => !source.Contains($"\"{t.Name}\" =>", StringComparison.Ordinal)
                || AuditEntityDisplay.Route(t.Name, "id") is null)
            .Select(t => t.Name)
            .Order()
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Jede auditierte Entität des öffentlichen Bereichs braucht Label und Route in AuditEntityDisplay: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void EverySettingsRouteOfTheAuditDisplay_NamesAnExistingSection()
    {
        // A route into /einstellungen is only useful if the rail actually has that section. Read out of the source
        // rather than driven by EntityTypes(): the display also answers for config types that have no DbSet at all
        // ("PublicArea", "PublicSituation"), and those were exactly the arms the reflection version could not see.
        var slugs = Regex.Matches(File.ReadAllText(DisplayFile()), @"/einstellungen\?tab=([a-z0-9-]+)")
            .Select(m => m.Groups[1].Value)
            .Distinct(StringComparer.Ordinal)
            .Order()
            .ToArray();
        // a renamed route prefix would otherwise leave this green forever
        Assert.NotEmpty(slugs);

        var offenders = slugs
            .Where(slug => !MergedPageSections.Settings.Contains(slug, StringComparer.Ordinal))
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Jede ?tab=-Route zeigt auf einen vorhandenen Abschnitt: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheSituationPage_RendersItsAssessmentAsText()
    {
        // The assessment never goes through HtmlCleanup, so a single MarkupString here would turn it into an
        // anonymously reachable, unfiltered markup surface. Scoped to this one file on purpose: the warning hub and
        // the press article render published, cleaned HTML and use MarkupString legitimately.
        var file = Path.Combine(ProjectRoot(), "Components", "Pages", "Public", "SituationPage.razor");
        Assert.True(File.Exists(file), $"Lage-Seite nicht gefunden: {file}");

        Assert.DoesNotContain("MarkupString", WithoutComments(File.ReadAllText(file)), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryAuditablePublicAreaEntity_IsDecidedInTheWatchlistRollup()
    {
        // the rollup's default arm only warns, so an unlisted type is a log line per write and nothing else — every
        // public-area table had been running that way since phase 1, found by reading the server log
        var logger = new CountingLogger();
        var offenders = EntityTypes()
            .Where(t => t.Namespace == "NOOSE_Website.Data.Entities.Public")
            .Where(t => typeof(IAuditable).IsAssignableFrom(t))
            .Where(t =>
            {
                logger.Warnings = 0;
                WatchlistRecordRollup.Map(Activator.CreateInstance(t)!, logger);
                return logger.Warnings > 0;
            })
            .Select(t => t.Name)
            .Order()
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Jede auditierte Entität des öffentlichen Bereichs ist im Watchlist-Rollup entschieden: "
            + string.Join(", ", offenders));
    }

    private sealed class CountingLogger : ILogger
    {
        public int Warnings { get; set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
            {
                Warnings++;
            }
        }
    }

    [Fact]
    public void EveryPublicRoutePrefix_IsExcludedFromDemoMode()
    {
        // /gesucht used to be listed by hand; /gefasst is a sibling route, not a child of it, and would have been
        // missed the same way — an anonymous archive visitor would then carry the demo agent
        var offenders = PublicRoutes.Prefixes
            .Where(p => !DemoModeMiddleware.ExcludedPrefixes.Contains(p, StringComparer.OrdinalIgnoreCase))
            .Order()
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Jede öffentliche Route bleibt im Demo-Modus anonym: " + string.Join(", ", offenders));
    }

    [Fact]
    public void PartnerRoutes_NeverBlockAPublicRoute()
    {
        // a partner can open the same page logged out, so the refusal would claim a restriction that does not exist
        var offenders = PublicRoutes.Prefixes
            .Where(p => !PartnerRoutes.IsAllowed(p))
            .Order()
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Eine öffentliche Route ist für einen Partner nie gesperrt: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheStatisticsService_NeverWrites()
    {
        // It is the one public service without an invalidation path, and that is only sound because it has no write
        // path either: the figures simply expire and are counted again. A write here would need a drop site, and the
        // sentence in the interface that says there is none would quietly become false.
        var file = Path.Combine(ProjectRoot(), "Services", "Public", "PublicStatisticsService.cs");
        Assert.True(File.Exists(file), $"Zahlen-Dienst nicht gefunden: {file}");

        var code = WithoutComments(File.ReadAllText(file));
        string[] writes = ["SaveChangesAsync", "ExecuteUpdate", "ExecuteDelete", "ExecuteSql", "cache.Remove"];
        var offenders = writes.Where(w => code.Contains(w, StringComparison.Ordinal)).Order().ToArray();

        Assert.True(offenders.Length == 0,
            "Der Zahlen-Dienst liest nur: " + string.Join(", ", offenders));
    }

    [Fact]
    public void ThePubliclyShownPayout_IsReadNowhereButTheStatisticsService()
    {
        // It is a roleplay display figure, not money: nothing was booked and no receipt exists for it. Every money
        // path in the app lives under Services/ — the KPI panel divides real payouts by real arrests, the cash book
        // balances real accounts, the reward service issues receipts — and letting the facade reach any of them
        // would corrupt a figure the agency works with. So exactly two services may name it: the wanted service
        // writes it, the statistics service reads it. Same discipline as RewardsPaidBaseline.
        string[] allowed = ["PublicWantedService.cs", "IPublicWantedService.cs", "PublicStatisticsService.cs"];

        var services = Path.Combine(ProjectRoot(), "Services");
        var offenders = Directory.EnumerateFiles(services, "*.cs", SearchOption.AllDirectories)
            .Where(f => !allowed.Contains(Path.GetFileName(f), StringComparer.Ordinal))
            .Where(f => WithoutComments(File.ReadAllText(f)).Contains("PublicPaidOut", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order()
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Die Anzeigezahl darf keinen Geldpfad erreichen: " + string.Join(", ", offenders));
    }

    [Fact]
    public void NoPublicPage_RendersAClickableStatTile()
    {
        // StatTile navigates from an @onclick handler. Public pages render statically, so the handler never runs and
        // an Href would only give the tile a pointer cursor and a link role that lead nowhere — the same class of
        // silently dead affordance as PrintFrame on the wanted poster.
        var pages = Path.Combine(ProjectRoot(), "Components", "Pages", "Public");
        Assert.True(Directory.Exists(pages), $"Öffentliche Seiten nicht gefunden: {pages}");

        var offenders = Directory.EnumerateFiles(pages, "*.razor", SearchOption.AllDirectories)
            .Where(f => Regex.IsMatch(WithoutComments(File.ReadAllText(f)),
                @"<StatTile\b[^>]*\bHref\s*=", RegexOptions.Singleline))
            .Select(Path.GetFileName)
            .Order()
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Eine statisch gerenderte Seite hat keine klickbare Kachel: " + string.Join(", ", offenders));
    }

    [Fact]
    public void ThePublicSearchService_NeverWritesAndHoldsNoContext()
    {
        // It composes the cached snapshots of the services that own the published tables. Holding a context would
        // put the suppression belt one careless line away, and a write would need a drop site the interface says
        // does not exist. Wider than the figures guard by exactly that: this one owns no data at all.
        var file = Path.Combine(ProjectRoot(), "Services", "Public", "PublicSearchService.cs");
        Assert.True(File.Exists(file), $"Suchdienst nicht gefunden: {file}");

        var code = WithoutComments(File.ReadAllText(file));
        string[] forbidden =
        [
            "SaveChangesAsync", "ExecuteUpdate", "ExecuteDelete", "ExecuteSql", "cache.Remove", "cache.Set",
            // IMemoryCache by name too: the rule is "holds no cache", and the three cache.* spellings above only
            // catch a field that happens to be called "cache"
            "IMemoryCache", "IDbContextFactory", "AppDbContext", "IgnoreQueryFilters",
        ];
        var offenders = forbidden.Where(w => code.Contains(w, StringComparison.Ordinal)).Order().ToArray();

        Assert.True(offenders.Length == 0,
            "Der öffentliche Suchdienst liest nur, und zwar durch die Dienste: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheKpiService_NeverWrites()
    {
        // Same sentence as the figures service, same reason: it is an evaluation, so there is nothing to invalidate
        // — and the day someone writes here, the promise in the interface quietly stops being true.
        var file = Path.Combine(ProjectRoot(), "Services", "Public", "PublicKpiService.cs");
        Assert.True(File.Exists(file), $"Kennzahlen-Dienst nicht gefunden: {file}");

        var code = WithoutComments(File.ReadAllText(file));
        string[] writes = ["SaveChangesAsync", "ExecuteUpdate", "ExecuteDelete", "ExecuteSql", "cache.Remove"];
        var offenders = writes.Where(w => code.Contains(w, StringComparison.Ordinal)).Order().ToArray();

        Assert.True(offenders.Length == 0,
            "Der Kennzahlen-Dienst liest nur: " + string.Join(", ", offenders));
    }

    /// <summary>Names that would put a citizen's identity into a search result row.</summary>
    /// <remarks>
    /// Only the two that are unambiguous. <c>FirstName</c>/<c>LastName</c> are generic enough that a scan on them
    /// would either be false-red on the agent provider or get defused on the next reading — the failure this file
    /// warns about for MentionParser.
    /// </remarks>
    private static readonly string[] CitizenIdentity =
    [
        "BuergerProfil", "BuergerProfile", "CitizenProfile", "CitizenProfileId", "CitizenName", "WantsAnonymity",
        "AnonymityResolvedAt",
    ];

    [Fact]
    public void NoSearchProvider_ProjectsACitizenIdentity()
    {
        // A tip and a ticket became searchable with the public-area hookup, and the anonymity promise cannot ride on
        // the projection alone: typing a name and seeing whether a tip comes back defeats it without reading a flag.
        // the providers only: SearchCatalog names BuergerProfil as an exclusion key, which is the opposite of a leak
        var providers = Path.Combine(ProjectRoot(), "Services", "Search", "Providers");
        Assert.True(Directory.Exists(providers), $"Suchprovider nicht gefunden: {providers}");

        var offenders = Directory.EnumerateFiles(providers, "*.cs", SearchOption.AllDirectories)
            .Select(f => (File: Path.GetFileName(f), Text: WithoutComments(File.ReadAllText(f))))
            .SelectMany(f => CitizenIdentity
                .Where(name => Regex.IsMatch(f.Text, $@"\b{name}\b"))
                .Select(name => $"{f.File}: {name}"))
            .Order()
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Kein Suchtreffer nennt den Bürger hinter einer Einreichung: " + string.Join(", ", offenders));
    }

    private static string ProjectRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "NOOSE-Website"));

    /// <summary>Names that would turn the advertised sum into a breakdown of who paid what.</summary>
    private static readonly string[] BountyInternals =
    [
        "IBountyService", "BountyShareRow", "BountySummary", "BountyCoverage",
        "BountyOrigin", "BountyShareStatus", "KassenKonto", "DonorAgentId", "KassenBuchungId",
        // a payout breakdown is the same class of leak: who was paid how much out of whose money
        "IRewardService", "RewardRow", "RewardDraft",
    ];

    [Fact]
    public void NoAnonymousPage_NamesTheBountyBreakdown()
    {
        // The outside gets one number. Origin, donor, account and share count are the part that would say which agent
        // staked his own money on whom — a structural rule (PublicBounty cannot carry them) plus this scan, because
        // a page could still reach past the record and query the service itself.
        var pages = Path.Combine(ProjectRoot(), "Components", "Pages", "Public");
        Assert.True(Directory.Exists(pages), $"Öffentliche Seiten nicht gefunden: {pages}");

        var offenders = Directory.EnumerateFiles(pages, "*.razor", SearchOption.AllDirectories)
            .Select(f => (File: Path.GetFileName(f), Text: File.ReadAllText(f)))
            .SelectMany(f => BountyInternals
                .Where(name => f.Text.Contains(name, StringComparison.Ordinal))
                .Select(name => $"{f.File}: {name}"))
            .Order()
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Nach außen geht nur die Summe: " + string.Join(", ", offenders));
    }

    [Fact]
    public void EveryWriterOfTheBountyTable_DropsThePublicSnapshot()
    {
        // The share table feeds the cached snapshot but is owned by another service, so the one thing that must not be
        // forgettable is the invalidation. PublicWantedService satisfies this by declaring the method itself.
        var offenders = Directory
            .EnumerateFiles(ProjectRoot(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.Combine("Data", "Migrations"), StringComparison.Ordinal))
            // AgentManagementService only nulls the donor pointer when an account is hard-deleted. The advertised
            // sum counts statuses, never donors, so no snapshot can go stale from it - and its write is an
            // ExecuteUpdate, matched here only because the file holds a SaveChangesAsync elsewhere.
            .Where(f => Path.GetFileName(f) != "AgentManagementService.cs")
            // WithoutComments like its siblings: prose that explains why a file does NOT invalidate contains the
            // very method name this scan looks for, which silently excused TipPriorityService
            .Select(f => (File: Path.GetFileName(f), Text: WithoutComments(File.ReadAllText(f))))
            .Where(f => f.Text.Contains("FahndungKopfgeldAnteile", StringComparison.Ordinal)
                && f.Text.Contains("SaveChangesAsync", StringComparison.Ordinal)
                && !f.Text.Contains("InvalidatePublicViewAsync", StringComparison.Ordinal))
            .Select(f => f.File)
            .Order()
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Wer Kopfgeld-Anteile schreibt, verwirft den öffentlichen Snapshot: " + string.Join(", ", offenders));
    }

    /// <summary>Names that would put an agent behind a citizen-facing message.</summary>
    private static readonly string[] DeskInternals =
    [
        "AuthorAgentId", "AuthorCodename", "TicketMessageRow", "TicketRow", "TicketDetail",
        "TipMessageRow", "TipRow", "TipDetail",
        // the objection desk: ObjectionDetail carries the citizen's block flag and the decider's codename
        "ObjectionRow", "ObjectionDetail", "DecidedByCodename",
        // the press desk: PressEdit names the publishing agent, and PressDraft is the unpublished body
        "PressEdit", "PressDraft", "PublishedByName",
        // the warning and law desks: the same shape, plus the paragraphs that stay in
        "WarningEdit", "WarningDraft", "LawReleaseRow",
        // the report desk: the edit row names the publishing agent and the internal anchor
        "PublicReportEdit", "PublicReportDraft", "PublicReportAnchor",
    ];

    [Fact]
    public void NoCitizenPage_NamesTheHandlerSideOfAConversation()
    {
        // The agency answers under one constant name. The outward records structurally carry no author, but a
        // portal page could still reach past them and ask the service for the handler projection.
        var pages = Path.Combine(ProjectRoot(), "Components", "Pages", "Portal");
        Assert.True(Directory.Exists(pages), $"Bürgerseiten nicht gefunden: {pages}");

        // whole identifiers only: the outward records are named CitizenTicketDetail and CitizenTipDetail, and a
        // substring match would report the very types that keep the promise
        var offenders = Directory.EnumerateFiles(pages, "*.razor", SearchOption.AllDirectories)
            .Select(f => (File: Path.GetFileName(f), Text: File.ReadAllText(f)))
            .SelectMany(f => DeskInternals
                .Where(name => System.Text.RegularExpressions.Regex.IsMatch(f.Text, $@"\b{name}\b"))
                .Select(name => $"{f.File}: {name}"))
            .Order()
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Der Absender nach außen ist eine Konstante, kein Agent: " + string.Join(", ", offenders));
    }

    /// <summary>Token systems that must not be applied on the citizen-facing path.</summary>
    /// <remarks>
    /// MentionParser is deliberately absent: the public services call it to <em>reject</em> outward text that carries
    /// a mention, which is the opposite of using it as a token system. A scan that forbade it would be defused on the
    /// next reading rather than understood.
    /// </remarks>
    private static readonly string[] ForeignTokenSystems =
    [
        "BewerbungTemplateRenderer", "IBewerbungTemplateService",
        "PlaceholderService", "IPlaceholderService", "IDocumentTemplateService",
    ];

    [Fact]
    public void NoPublicPath_AppliesAForeignTokenSystem()
    {
        // Three systems already exist and none of them fits a citizen message: the recruiting renderer HTML-encodes
        // and validates against the applicant token set, the placeholder service resolves record fields, and both
        // would travel half-expanded to the outside. The fourth set has its own renderer, and this keeps it that way.
        var root = ProjectRoot();
        var folders = new[]
        {
            Path.Combine(root, "Services", "Public"),
            Path.Combine(root, "Components", "Pages", "Public"),
            Path.Combine(root, "Components", "Pages", "Portal"),
            Path.Combine(root, "Components", "Pages", "Tickets"),
            Path.Combine(root, "Components", "Pages", "Tips"),
        };
        foreach (var folder in folders)
        {
            Assert.True(Directory.Exists(folder), $"Ordner nicht gefunden: {folder}");
        }

        var offenders = folders
            .SelectMany(f => Directory.EnumerateFiles(f, "*.*", SearchOption.AllDirectories))
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
            .Select(f => (File: Path.GetFileName(f), Text: WithoutComments(File.ReadAllText(f))))
            .SelectMany(f => ForeignTokenSystems
                .Where(name => System.Text.RegularExpressions.Regex.IsMatch(f.Text, $@"\b{name}\b"))
                .Select(name => $"{f.File}: {name}"))
            .Order()
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Der öffentliche Pfad nutzt ausschließlich PublicTemplateRenderer: " + string.Join(", ", offenders));
    }

    /// <summary>Internal stacks that must never hang off a service an anonymous page constructs.</summary>
    /// <remarks>
    /// Not about reading, about the object graph: a public page injects its service, so every constructor parameter of
    /// that service is built for an anonymous visitor. PublicReportService took ISituationReportService for its anchor
    /// picker once, which pulled statistics, funding statistics and notifications onto /berichte — and the marker list
    /// in PublicPageScanTests, which keeps that name off the pages themselves, could not see it one layer down.
    /// </remarks>
    private static readonly string[] InternalStacks =
    [
        "ISituationReportService", "IStatisticsService", "IFinancingStatisticsService", "IFoodStatisticsService",
        // the global search orchestrator: resolving it builds all ~60 providers, every one with its own context
        // factory, for a visitor who is only allowed to see what was published
        "ISearchService",
    ];

    [Fact]
    public void NoServiceInjectedByAPublicPage_PullsInAnInternalStack()
    {
        var root = ProjectRoot();
        var pages = Path.Combine(root, "Components", "Pages", "Public");
        Assert.True(Directory.Exists(pages), $"Öffentliche Seiten nicht gefunden: {pages}");

        // resolved by the house convention IFooService -> FooService.cs; a service the pages inject but that cannot be
        // resolved is reported rather than skipped, so a renamed file does not quietly empty this scan
        var injected = Directory.EnumerateFiles(pages, "*.razor", SearchOption.AllDirectories)
            .SelectMany(f => Regex.Matches(WithoutComments(File.ReadAllText(f)), @"@inject\s+(I[A-Za-z]+Service)\b")
                .Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .Order()
            .ToArray();
        Assert.NotEmpty(injected);

        var offenders = new List<string>();
        foreach (var name in injected)
        {
            var file = Directory.EnumerateFiles(Path.Combine(root, "Services"), name[1..] + ".cs",
                SearchOption.AllDirectories).FirstOrDefault();
            if (file is null)
            {
                offenders.Add($"{name}: Implementierung nicht gefunden");
                continue;
            }
            var code = WithoutComments(File.ReadAllText(file));
            offenders.AddRange(InternalStacks
                .Where(stack => Regex.IsMatch(code, $@"\b{stack}\b"))
                .Select(stack => $"{Path.GetFileName(file)}: {stack}"));
        }

        Assert.True(offenders.Count == 0,
            "Ein Dienst, den eine anonyme Seite injiziert, zieht keinen internen Stack in seinen Objektgraphen: "
            + string.Join(", ", offenders.Order()));
    }

    /// <summary>Strips comments: the rule is about code, and prose has to be allowed to name what it rules out.</summary>
    /// <remarks>
    /// Otherwise the next reader deletes the sentence explaining why the recruiting renderer is not reused, just to
    /// make this test green — the same defusing this file warns about for MentionParser.
    /// </remarks>
    private static string WithoutComments(string text)
    {
        const RegexOptions overLines = RegexOptions.Singleline;
        text = Regex.Replace(text, @"@\*.*?\*@", " ", overLines);
        text = Regex.Replace(text, @"/\*.*?\*/", " ", overLines);
        return Regex.Replace(text, @"//[^\r\n]*", " ");
    }
}
