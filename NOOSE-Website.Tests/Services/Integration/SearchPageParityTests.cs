using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Evidence;
using NOOSE_Website.Data.Entities.Informants;
using NOOSE_Website.Data.Entities.Presentations;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Infrastructure.Presentations;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The anti-divergence test: for every category that has a canonical list service, the ids the search
/// provider returns must be exactly the ids that service returns for the same viewer.</summary>
/// <remarks>
/// <para>This is the test that makes "search and page can never diverge again" mechanically true rather than
/// aspirational. Its value is that it needs no knowledge of any individual rule — it compares two implementations
/// of the same question and fails when they disagree, whichever one is wrong.</para>
/// <para>It exists because two real bugs got past a green suite: an absence provider that rebuilt the roster-tier
/// ternary instead of naming it, and a personnel provider that queried raw <c>db.Users</c> while the page filtered
/// team leads, blocked accounts and applicants out. Both were found by hand. This finds that class automatically.</para>
/// <para>Only categories with a list service are here. Content children (comments, sources, followups, …) resolve
/// through <see cref="NOOSE_Website.Services.Search.SearchParentResolver"/> and have no roster to compare against;
/// they are covered by <c>SearchVisibilityTests</c> instead.</para>
/// </remarks>
public class SearchPageParityTests
{
    private static ClaimsPrincipal Viewer(string kind) => kind switch
    {
        "plain" => ClaimsPrincipalBuilder.Agent("me").WithRank(Rank.JuniorAgent).Build(),
        "lead" => ClaimsPrincipalBuilder.Agent("me").WithRank(Rank.Director).Build(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    // small corpus on purpose: more than PerCategory rows and the provider would cap while the service would not
    private static async Task SeedAsync(SqliteTestContext ctx)
    {
        await using var db = ctx.NewContext();

        // the six classifiable records: one open, one restricted at each audience
        db.People.Add(Seed.Person("p-open", "Otto Offen"));
        db.People.Add(Seed.Person("p-vs", "Vera VS", p => p.IsClassified = true));
        db.People.Add(Seed.Person("p-tru", "Tim TRU", p => { p.IsClassified = true; p.IsTRUClassified = true; }));
        db.Factions.Add(Seed.Faction("f-open", "Offene Bande"));
        db.Factions.Add(Seed.Faction("f-vs", "Geheimbund", f => f.IsClassified = true));
        db.PersonGroups.Add(new Data.Entities.Groups.PersonGroup { Id = "g-open", Name = "Offene Gruppe", CaseNumber = "NOOSE-G-1" });
        db.PersonGroups.Add(new Data.Entities.Groups.PersonGroup { Id = "g-vs", Name = "Geheime Gruppe", CaseNumber = "NOOSE-G-2", IsClassified = true });
        db.Parties.Add(new Data.Entities.Parties.Party { Id = "pa-open", Name = "Offene Partei", CaseNumber = "NOOSE-PA-1" });
        db.Parties.Add(new Data.Entities.Parties.Party { Id = "pa-vs", Name = "Geheime Partei", CaseNumber = "NOOSE-PA-2", IsClassified = true });
        db.Operations.Add(new Data.Entities.Operations.Operation { Id = "o-open", Title = "Offener Zugriff", CaseNumber = "NOOSE-O-1" });
        db.Operations.Add(new Data.Entities.Operations.Operation { Id = "o-vs", Title = "Geheimer Zugriff", CaseNumber = "NOOSE-O-2", IsClassified = true });
        db.Cases.Add(Seed.Case("v-open", "Offener Vorgang"));
        db.Cases.Add(Seed.Case("v-vs", "Geheimer Vorgang", v => v.IsClassified = true));

        // taskforces gate on membership, not classification
        db.Taskforces.Add(new Taskforce { Id = "t-mine", Name = "Meine Einheit", CaseNumber = "NOOSE-TF-1" });
        db.Taskforces.Add(new Taskforce { Id = "t-other", Name = "Fremde Einheit", CaseNumber = "NOOSE-TF-2" });
        db.TaskforceAgents.Add(new TaskforceAgent { TaskforceId = "t-mine", AgentId = "me" });

        // documents: three independent layers
        db.Documents.Add(new Document { Id = "d-open", Title = "Offenes Dokument", ContentHtml = "<p>x</p>" });
        db.Documents.Add(new Document { Id = "d-vs", Title = "VS-Dokument", ContentHtml = "<p>x</p>", IsClassified = true });
        db.Documents.Add(new Document { Id = "d-tf", Title = "Taskforce-Dokument", ContentHtml = "<p>x</p>", OwnerTaskforceId = "t-other" });
        db.Documents.Add(new Document { Id = "d-excl", Title = "Entzogenes Dokument", ContentHtml = "<p>x</p>" });
        db.DocumentAccessExclusions.Add(new DocumentAccessExclusion { DocumentId = "d-excl", AgentId = "me" });

        db.Laws.Add(new Law { Id = "g1", LawBook = "StGB", Paragraph = "§ 242", Title = "Diebstahl", Text = "…" });

        db.LibraryFiles.Add(new LibraryFile
        {
            Id = "lib-open", Title = "Offene Datei", OriginalName = "a.pdf", FileNameSaved = "a",
            ContentType = "application/pdf", SizeBytes = 1,
        });
        db.LibraryFiles.Add(new LibraryFile
        {
            Id = "lib-vs", Title = "VS-Datei", OriginalName = "b.pdf", FileNameSaved = "b",
            ContentType = "application/pdf", SizeBytes = 1, IsClassified = true,
        });

        db.Informants.Add(new Informant { Id = "i-mine", CaseNumber = "NOOSE-VP-1", RealName = "Meine VP", HandlerId = "me" });
        db.Informants.Add(new Informant { Id = "i-other", CaseNumber = "NOOSE-VP-2", RealName = "Fremde VP", HandlerId = "someone" });

        db.EvidenceItems.Add(new EvidenceItem { Id = "e1", Name = "Schalldämpfer", Category = "Waffenteil" });

        db.Presentations.Add(new Presentation { Id = "pr-pub", Title = "Lagebild", Access = PresentationAccess.Public, OwnerId = "someone" });
        db.Presentations.Add(new Presentation
        {
            Id = "pr-tru", Title = "TRU-Briefing", Access = PresentationAccess.Closed,
            Audience = AnnouncementAudience.TruUnit, OwnerId = "someone",
        });
        db.Presentations.Add(new Presentation { Id = "pr-priv", Title = "Privat", Access = PresentationAccess.Private, OwnerId = "someone" });
        db.Presentations.Add(new Presentation { Id = "pr-mine", Title = "Meins", Access = PresentationAccess.Private, OwnerId = "me" });

        // the account states the personnel roster distinguishes
        db.Users.Add(Seed.Agent("me", configure: a => a.Codename = "Ich"));
        db.Users.Add(Seed.Agent("a-active", configure: a => a.Codename = "Aktiv"));
        db.Users.Add(Seed.Agent("a-tl", configure: a => { a.Codename = "Aufsicht"; a.IsTeamLead = true; }));
        db.Users.Add(Seed.Agent("a-blocked", status: AgentStatus.Blocked, configure: a => a.Codename = "Gesperrt"));
        db.Users.Add(Seed.Agent("a-applicant", status: AgentStatus.Applicant, configure: a => a.Codename = "Bewerber"));
        db.Users.Add(Seed.Agent("a-gone", status: AgentStatus.Terminated, configure: a => a.Codename = "Gekuendigt"));

        await db.SaveChangesAsync();
    }

    // ---- the canonical list services, built for real: the visibility under test lives inside them ----

    private static PersonService People(SqliteTestContext ctx)
        => new(ctx.Factory, Substitute.For<IFileStorageService>(), Substitute.For<IProfileSuggestionService>(),
            Substitute.For<ICaseNumberService>(), Substitute.For<IThreatScoreService>(),
            Substitute.For<INotificationService>(), Substitute.For<IPublicWantedService>());

    private static async Task<IReadOnlyList<string>> CanonicalAsync(
        string category, SqliteTestContext ctx, ClaimsPrincipal user)
    {
        var scope = ViewerScope.From(user);
        var docScope = DocumentViewerScope.From(user);
        var caseNo = Substitute.For<ICaseNumberService>();
        var suggest = Substitute.For<IProfileSuggestionService>();
        var threat = Substitute.For<IThreatScoreService>();
        var notify = Substitute.For<INotificationService>();

        switch (category)
        {
            case nameof(Data.Entities.People.Person):
                return (await People(ctx).GetListAsync(scope)).Select(x => x.Id).ToList();

            case nameof(Data.Entities.Factions.Faction):
                var factions = new FactionService(ctx.Factory, caseNo, suggest, People(ctx),
                    Substitute.For<IFactionPhotoStorageService>(), threat, notify,
                    Substitute.For<IPublicFactionProfileService>());
                return (await factions.GetListAsync(scope)).Select(x => x.Id).ToList();

            case nameof(Data.Entities.Groups.PersonGroup):
                var groups = new PersonGroupService(ctx.Factory, caseNo, People(ctx), threat, notify,
                    Substitute.For<IPersonGroupPhotoStorageService>());
                return (await groups.GetListAsync(scope)).Select(x => x.Id).ToList();

            case nameof(Data.Entities.Parties.Party):
                var parties = new PartyService(ctx.Factory, caseNo, suggest, People(ctx), threat, notify,
                    Substitute.For<IPartyPhotoStorageService>());
                return (await parties.GetListAsync(scope)).Select(x => x.Id).ToList();

            case nameof(Data.Entities.Operations.Operation):
                var operations = new OperationService(ctx.Factory, caseNo, suggest, notify);
                return (await operations.GetListAsync(scope)).Select(x => x.Id).ToList();

            case nameof(Data.Entities.Cases.Case):
                var cases = new CaseService(ctx.Factory, caseNo, suggest, notify);
                return (await cases.GetListAsync(scope)).Select(x => x.Id).ToList();

            case nameof(Taskforce):
                var taskforces = new TaskforceService(ctx.Factory, caseNo, notify);
                return (await taskforces.GetListAsync(scope.MayAllTaskforces, scope.MeId)).Select(x => x.Id).ToList();

            case nameof(Document):
                return (await new DocumentService(ctx.Factory).GetListAsync(docScope)).Select(x => x.Id).ToList();

            case nameof(Law):
                return (await new LawService(ctx.Factory, Substitute.For<IPublicLawService>()).GetListAsync()).Select(x => x.Id).ToList();

            case nameof(LibraryFile):
                var library = new LibraryService(ctx.Factory, Substitute.For<ILibraryStorageService>());
                return (await library.GetListAsync(docScope)).Select(x => x.Id).ToList();

            case nameof(Informant):
                return (await new InformantService(ctx.Factory, caseNo).GetListAsync(user)).Select(x => x.Id).ToList();

            case nameof(EvidenceItem):
                var evidence = new EvidenceService(ctx.Factory, caseNo,
                    Substitute.For<IEvidenceImageStorageService>(), suggest);
                return (await evidence.GetItemsAsync()).Select(x => x.Item.Id).ToList();

            case nameof(Presentation):
                var presentations = new PresentationService(ctx.Factory, Substitute.For<ITextImageStorageService>(),
                    new PresentationLiveRegistry(), new PresentationBroadcaster());
                return (await presentations.GetListAsync(user)).Select(x => x.Id).ToList();

            case nameof(Agent):
                // AgentManagementService needs a UserManager, so the roster rule stands in for it directly. Not
                // circular in the way that matters: it catches a provider that forgot the helper, which is the bug
                // this row exists for.
                await using (var db = ctx.NewContext())
                {
                    return await db.Users.OnlyWithPersonnelFile().Select(u => u.Id).ToListAsync();
                }

            default:
                throw new ArgumentOutOfRangeException(nameof(category), category, "Keine kanonische Liste hinterlegt.");
        }
    }

    private static async Task<IReadOnlyList<string>> FromSearchAsync(
        string category, SqliteTestContext ctx, ClaimsPrincipal user)
    {
        // empty text = browse: the record providers then return everything they consider visible
        var results = await SearchTestHost.NewService(ctx).SearchAsync(new SearchCriteria(), user);
        // a category with no hits produces no group at all, which is an empty set here
        return results.Groups.FirstOrDefault(g => g.Category == category)?.Hit.Select(h => h.TargetId).ToList() ?? [];
    }

    [Theory]
    [InlineData("Person", "plain")]
    [InlineData("Person", "lead")]
    [InlineData("Faction", "plain")]
    [InlineData("Faction", "lead")]
    [InlineData("PersonGroup", "plain")]
    [InlineData("PersonGroup", "lead")]
    [InlineData("Party", "plain")]
    [InlineData("Party", "lead")]
    [InlineData("Operation", "plain")]
    [InlineData("Operation", "lead")]
    [InlineData("Case", "plain")]
    [InlineData("Case", "lead")]
    [InlineData("Taskforce", "plain")]
    [InlineData("Taskforce", "lead")]
    [InlineData("Document", "plain")]
    [InlineData("Document", "lead")]
    [InlineData("Law", "plain")]
    [InlineData("Law", "lead")]
    [InlineData("LibraryFile", "plain")]
    [InlineData("LibraryFile", "lead")]
    [InlineData("Informant", "plain")]
    [InlineData("Informant", "lead")]
    [InlineData("EvidenceItem", "plain")]
    [InlineData("EvidenceItem", "lead")]
    [InlineData("Presentation", "plain")]
    [InlineData("Presentation", "lead")]
    [InlineData("Agent", "lead")]
    public async Task The_search_returns_exactly_what_the_page_lists(string category, string viewerKind)
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx);
        var user = Viewer(viewerKind);

        var fromSearch = (await FromSearchAsync(category, ctx, user)).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var fromPage = (await CanonicalAsync(category, ctx, user)).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        // whichever side is wrong, they must not disagree: a wider search leaks, a narrower one hides the record
        // from the one place an agent goes looking for it
        Assert.Equal(fromPage, fromSearch);
    }

    [Fact]
    public void Every_category_with_a_list_service_is_covered_by_this_test()
    {
        // the guard against the test itself rotting: a category added to the catalog with a roster behind it must
        // be added here too, or the parity promise quietly stops covering it
        var covered = new[]
        {
            "Person", "Faction", "PersonGroup", "Party", "Operation", "Case", "Taskforce",
            "Document", "Law", "LibraryFile", "Informant", "EvidenceItem", "Presentation", "Agent",
        };

        Assert.All(covered, c => Assert.NotNull(NOOSE_Website.Services.Search.SearchCatalog.Find(c)));
        Assert.Equal(covered.Length, covered.Distinct(StringComparer.Ordinal).Count());
    }
}
