using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Blocked content: an agency never sees it, whatever record, child, rule or account release exists.</summary>
public sealed class PartnerContentBlockTests : IDisposable
{
    private const PartnerAgency Parlament = PartnerAgency.Parlament;
    private const string Me = "partner";
    private static readonly DateTime T0 = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    private static ViewerScope PartnerScope(PartnerAgency agency = Parlament)
        => new(MayClassifiedRead: false, MayAllTaskforces: false, MeId: Me, PartnerAgency: agency);

    private PersonDocService Docs()
        => new(_ctx.Factory, Substitute.For<IPersonService>(), Substitute.For<IThreatScoreService>(), Substitute.For<INotificationService>());

    private void Add(params object[] entities)
    {
        using var db = _ctx.NewContext();
        db.AddRange(entities);
        db.SaveChanges();
    }

    private static PartnerShare Share(string type, string id, PartnerAgency agency = Parlament, bool whole = false, string? agentId = null)
        => new() { EntityType = type, EntityId = id, Agency = agency, IncludesChildren = whole, PartnerAgentId = agentId };

    private static PartnerAgencyProfile Blocking(PartnerContent blocked, PartnerAgency agency = Parlament)
        => new() { Agency = agency, BlockedContent = blocked };

    private static PersonDoc Doc(string id, string personId, string? orgId = null)
        => new() { Id = id, PersonId = personId, Timestamp = T0, Outcome = MeasureOutcome.RunningStill, OrgType = orgId is null ? null : nameof(Faction), OrgId = orgId };

    [Fact]
    public async Task BlockedDoc_IsHidden_DespiteWholeRecordChildAndAccountReleases()
    {
        Add(Seed.Person("p1"), Blocking(PartnerContent.Docs));
        Add(Doc("d1", "p1"),
            Share(nameof(Person), "p1", whole: true),
            Share(nameof(Person), "p1", whole: true, agentId: Me),
            Share(nameof(PersonDoc), "d1"),
            Share(nameof(PersonDoc), "d1", agentId: Me));

        Assert.Empty(await Docs().GetForPersonAsync("p1", PartnerScope()));
        await using var db = _ctx.NewContext();
        Assert.False(await PartnerVisibility.IsChildVisibleToPartnerAsync(db, nameof(Person), "p1", nameof(PersonDoc), "d1", Parlament, Me));
    }

    [Fact]
    public async Task BlockedDoc_IsHidden_DespiteARuleWithChildren()
    {
        Add(Seed.Person("p1"), Blocking(PartnerContent.Docs),
            new PartnerReleaseRule { Agency = Parlament, EntityType = nameof(Person), Scope = PartnerRuleScope.All, IncludesChildren = true });
        Add(Doc("d1", "p1"));

        Assert.Empty(await Docs().GetForPersonAsync("p1", PartnerScope()));
    }

    [Fact]
    public async Task OrgDocs_BlockedForTheAgency_AreHidden()
    {
        Add(Seed.Faction("f1"), Seed.Person("p1"), Blocking(PartnerContent.Docs));
        Add(Doc("d1", "p1", "f1"), Share(nameof(Faction), "f1", whole: true), Share(nameof(Person), "p1", whole: true));

        Assert.Empty(await Docs().GetForOrgAsync(nameof(Faction), "f1", PartnerScope()));
    }

    [Fact]
    public async Task OrgDocs_WithoutBlock_ShowOnlyDocsTheAgencyMaySee()
    {
        // shell-released person: its docs need their own release, also on the faction's doks tab
        Add(Seed.Faction("f1"), Seed.Person("p1"));
        Add(Doc("d-open", "p1", "f1"), Doc("d-closed", "p1", "f1"),
            Share(nameof(Faction), "f1", PartnerAgency.DoJ, whole: true),
            Share(nameof(Person), "p1", PartnerAgency.DoJ),
            Share(nameof(PersonDoc), "d-open", PartnerAgency.DoJ));

        var docs = await Docs().GetForOrgAsync(nameof(Faction), "f1", PartnerScope(PartnerAgency.DoJ));

        Assert.Equal(new[] { "d-open" }, docs.Select(d => d.Doc.Id));
    }

    [Fact]
    public async Task BatchedChildFilter_HonoursTheBlock()
    {
        Add(Seed.Person("p1"), Blocking(PartnerContent.Observations));
        Add(Share(nameof(Person), "p1", whole: true));

        await using var db = _ctx.NewContext();
        var visible = await PartnerVisibility.VisibleChildIdsAsync(db, nameof(Observation),
            new[] { (nameof(Person), "p1", "o1") }, Parlament, Me);
        var comments = await PartnerVisibility.VisibleChildIdsAsync(db, nameof(Comment),
            new[] { (nameof(Person), "p1", "c1") }, Parlament, Me);

        Assert.Empty(visible);
        Assert.Equal(new[] { "c1" }, comments);
    }

    [Fact]
    public async Task Block_OfAnotherAgency_DoesNotApply()
    {
        Add(Seed.Person("p1"), Blocking(PartnerContent.Docs, PartnerAgency.DoJ));
        Add(Doc("d1", "p1"), Share(nameof(Person), "p1", whole: true));

        Assert.Single(await Docs().GetForPersonAsync("p1", PartnerScope()));
    }
}
