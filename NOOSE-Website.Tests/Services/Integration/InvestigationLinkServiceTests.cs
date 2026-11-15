using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Investigations;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.CurrentUser;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;
using NSubstitute;
using W = NOOSE_Website.Tests.Services.Integration.InvestigationReferenceWorld;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Links between a file and other records: both ends filtered per viewer, never through the link engine.</summary>
public sealed class InvestigationLinkServiceTests : IDisposable
{
    private const string Open = "dddddddd-0000-4000-8000-000000000001";
    private const string Secret = "dddddddd-0000-4000-8000-000000000002";
    private const string Shared = "dddddddd-0000-4000-8000-000000000003";
    private const string SecretName = "Vera Verschluss";

    private readonly W _world = new();
    private readonly DbContextOptions<AppDbContext> _audited;
    private readonly InvestigationLinkService _svc;

    public InvestigationLinkServiceTests()
    {
        // the audit interceptor writes the history rows and turns a delete into a soft delete
        _audited = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_world.Ctx.Connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.AmbientTransactionWarning))
            .AddInterceptors(new AuditSaveChangesInterceptor(new CurrentUser()))
            .Options;
        _svc = new InvestigationLinkService(new TestDbContextFactory(_audited));

        using var db = _world.Ctx.NewContext();
        db.People.AddRange(
            Seed.Person(Open, "Olaf Offen"),
            Seed.Person(Secret, SecretName, p => p.IsClassified = true),
            Seed.Person(Shared, "Sina Freigabe"));
        db.PartnerShares.Add(new PartnerShare { EntityType = "Person", EntityId = Shared, Agency = PartnerAgency.DoJ, IncludesChildren = true });
        db.SaveChanges();
    }

    public void Dispose() => _world.Dispose();

    private sealed class CurrentUser : ICurrentUserService
    {
        public Task<CurrentUserInfo> GetAsync() => Task.FromResult(Get());
        public CurrentUserInfo Get() => new("lead", "lead", false, false, false);
    }

    private void Link(string investigationId, string type, string id, string? label = null)
    {
        using var db = _world.Ctx.NewContext();
        db.InvestigationLinks.Add(new InvestigationLink { InvestigationId = investigationId, RecordType = type, RecordId = id, Label = label });
        db.SaveChanges();
    }

    [Fact]
    public void Every_linkable_type_has_a_german_name_and_no_entry_type_is_linkable()
    {
        foreach (var type in InvestigationLinkService.RecordTypes.Where(t => t != nameof(Investigation)))
        {
            Assert.NotNull(NOOSE_Website.Services.Search.SearchCatalog.Find(type));
        }
        Assert.DoesNotContain(InvestigationLinkService.RecordTypes,
            t => t != nameof(Investigation) && InvestigationReferences.IsType(t));
        Assert.Equal(InvestigationTabs.Overview, InvestigationTabs.ForChildType(nameof(InvestigationLink)));
    }

    // ---- the record's end ----

    [Fact]
    public async Task A_person_file_shows_the_link_only_to_who_sees_the_investigation()
    {
        Link(W.Inv, "Person", Shared, "Hauptverdächtige");

        Assert.Empty(await _svc.GetForRecordAsync("Person", Shared, W.Viewer("outsider")));
        Assert.Empty(await _svc.GetForRecordAsync("Person", Shared, W.Viewer("member")));

        var row = Assert.Single(await _svc.GetForRecordAsync("Person", Shared, W.Viewer("inv")));
        Assert.Equal(W.Inv, row.InvestigationId);
        Assert.Equal($"{W.CaseNumber} · Akte Messer", row.Display);
        Assert.Equal($"/ermittlungen/{W.Inv}", row.Href);
        Assert.Equal("Hauptverdächtige", row.Label);
        Assert.Single(await _svc.GetForRecordAsync("Person", Shared, W.Viewer("chief")));
    }

    [Fact]
    public async Task A_partner_sees_the_link_only_with_participation_and_a_released_record()
    {
        Link(W.Inv, "Person", Shared);
        Link(W.Inv, "Person", Open);

        // the state attorney unit takes part; the DoJ chief unit does not
        Assert.Single(await _svc.GetForRecordAsync("Person", Shared, W.Viewer("sta")));
        Assert.Empty(await _svc.GetForRecordAsync("Person", Shared, W.Viewer("dojchief")));
        // participation alone does not open an unreleased person file
        Assert.Empty(await _svc.GetForRecordAsync("Person", Open, W.Viewer("sta")));
    }

    [Fact]
    public async Task A_trashed_investigation_drops_off_the_record()
    {
        Link(W.Inv, "Person", Open);
        using (var db = _world.Ctx.NewContext())
        {
            db.Investigations.Find(W.Inv)!.IsDeleted = true;
            db.SaveChanges();
        }

        Assert.Empty(await _svc.GetForRecordAsync("Person", Open, W.Viewer("chief")));
    }

    // ---- the file's end ----

    [Fact]
    public async Task Inside_the_file_a_classified_person_drops_out_for_a_participant_without_the_right()
    {
        Link(W.Inv, "Person", Open);
        Link(W.Inv, "Person", Secret);
        Link(W.Inv, "Person", Shared);

        var inv = await _svc.GetForInvestigationAsync(W.Inv, W.Viewer("inv"));
        Assert.Equal([Open, Shared], inv.Select(r => r.Id).Order().ToArray());
        Assert.DoesNotContain(inv, r => r.Display.Contains(SecretName));

        Assert.Equal(3, (await _svc.GetForInvestigationAsync(W.Inv, W.Viewer("chief"))).Count);

        // a partner sees released records only
        var sta = await _svc.GetForInvestigationAsync(W.Inv, W.Viewer("sta"));
        Assert.Equal(Shared, Assert.Single(sta).Id);

        // nobody outside the file sees anything
        Assert.Empty(await _svc.GetForInvestigationAsync(W.Inv, W.Viewer("outsider")));
    }

    [Fact]
    public async Task A_file_to_file_link_shows_on_both_files_only_with_sight_of_the_other()
    {
        // lead takes part in both
        await _svc.CreateAsync(W.Inv, nameof(Investigation), W.Other, null, W.Viewer("lead"));

        Assert.Equal(W.Other, Assert.Single(await _svc.GetForInvestigationAsync(W.Inv, W.Viewer("lead"))).Id);
        var back = Assert.Single(await _svc.GetForInvestigationAsync(W.Other, W.Viewer("lead")));
        Assert.Equal(W.Inv, back.Id);
        Assert.Equal(nameof(Investigation), back.Type);
        // the label belongs to the file that made the link
        Assert.False(back.MayEditLabel);

        // inv sees only Inv, outsider only Other
        Assert.Empty(await _svc.GetForInvestigationAsync(W.Inv, W.Viewer("inv")));
        Assert.Empty(await _svc.GetForInvestigationAsync(W.Other, W.Viewer("outsider")));

        // the reverse direction counts as the same pair
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _svc.CreateAsync(W.Other, nameof(Investigation), W.Inv, null, W.Viewer("lead")));
    }

    // ---- creating ----

    [Fact]
    public async Task Creating_needs_edit_on_the_cover_sheet()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _svc.CreateAsync(W.Inv, "Person", Open, null, W.Viewer("obs")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _svc.CreateAsync(W.Inv, "Person", Open, null, W.Viewer("outsider")));

        await _svc.CreateAsync(W.Inv, "Person", Open, "Zeugin", W.Viewer("inv"));
        await using var db = _world.Ctx.NewContext();
        var row = Assert.Single(db.InvestigationLinks);
        Assert.Equal(("Person", Open, "Zeugin"), (row.RecordType, row.RecordId, row.Label));
    }

    [Fact]
    public async Task Creating_refuses_a_target_the_actor_cannot_see()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _svc.CreateAsync(W.Inv, "Person", Secret, null, W.Viewer("inv")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _svc.CreateAsync(W.Inv, "Person", W.Missing, null, W.Viewer("inv")));
        // a file the actor does not take part in
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _svc.CreateAsync(W.Inv, nameof(Investigation), W.Other, null, W.Viewer("inv")));
    }

    [Fact]
    public async Task Creating_refuses_doubles_self_links_and_entry_types()
    {
        await _svc.CreateAsync(W.Inv, "Person", Open, null, W.Viewer("inv"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _svc.CreateAsync(W.Inv, "Person", Open, null, W.Viewer("inv")));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _svc.CreateAsync(W.Inv, nameof(Investigation), W.Inv, null, W.Viewer("lead")));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _svc.CreateAsync(W.Inv, nameof(InvestigationNote), W.Note, null, W.Viewer("lead")));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _svc.CreateAsync(W.Inv, nameof(InvestigationEvidence), W.Evidence, null, W.Viewer("lead")));
    }

    [Fact]
    public async Task No_row_reaches_the_link_engine()
    {
        await _svc.CreateAsync(W.Inv, "Person", Open, null, W.Viewer("inv"));
        await _svc.CreateAsync(W.Inv, nameof(Investigation), W.Other, null, W.Viewer("lead"));

        await using var db = _world.Ctx.NewContext();
        Assert.Empty(db.Links.IgnoreQueryFilters());
        Assert.Equal(2, db.InvestigationLinks.Count());
    }

    // ---- removing ----

    [Fact]
    public async Task The_record_end_removes_with_write_on_the_record_and_sight_of_the_file()
    {
        var id = await _svc.CreateAsync(W.Inv, "Person", Open, null, W.Viewer("inv"));

        // sees neither the file nor its links
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _svc.RemoveAsync(id, W.Viewer("outsider")));
        // reads the file only, but writes the person file
        await _svc.RemoveAsync(id, W.Viewer("obs"));

        await using var db = _world.Ctx.NewContext();
        Assert.True(db.InvestigationLinks.IgnoreQueryFilters().Single(l => l.Id == id).IsDeleted);
        Assert.Empty(await _svc.GetForRecordAsync("Person", Open, W.Viewer("inv")));
    }

    [Fact]
    public async Task Only_the_owning_file_changes_the_label()
    {
        var id = await _svc.CreateAsync(W.Inv, "Person", Open, null, W.Viewer("inv"));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _svc.UpdateLabelAsync(id, "x", W.Viewer("obs")));
        await _svc.UpdateLabelAsync(id, "  Zeugin  ", W.Viewer("inv"));

        await using var db = _world.Ctx.NewContext();
        Assert.Equal("Zeugin", db.InvestigationLinks.Single().Label);
    }

    // ---- history and merge ----

    [Fact]
    public async Task The_history_names_the_kind_of_record_never_its_title()
    {
        // leadership reads the classified person and has full access to the file; the lead reads its history
        var id = await _svc.CreateAsync(W.Inv, "Person", Secret, null, W.Viewer("chief"));
        await _svc.RemoveAsync(id, W.Viewer("chief"));

        var investigations = new InvestigationService(new TestDbContextFactory(_audited), Substitute.For<ICaseNumberService>(),
            Substitute.For<INotificationService>(), Substitute.For<IInvestigationPhotoStorageService>());
        var history = await investigations.GetHistoryAsync(W.Inv, W.Viewer("lead"));

        var rows = history.Where(h => h.EntityType == nameof(InvestigationLink)).ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, h =>
        {
            Assert.Equal(InvestigationService.LinksSection, h.Tab);
            Assert.EndsWith("Verknüpfung mit Person", h.What);
            Assert.DoesNotContain(SecretName, h.What);
        });
        Assert.DoesNotContain(history, h => (h.What + h.Changes).Contains(SecretName) || (h.What + h.Changes).Contains(Secret));
    }

    private InvestigationService Investigations() => new(new TestDbContextFactory(_audited), Substitute.For<ICaseNumberService>(),
        Substitute.For<INotificationService>(), Substitute.For<IInvestigationPhotoStorageService>());

    [Fact]
    public async Task The_other_files_history_shows_a_file_link_only_to_who_sees_the_linking_file()
    {
        const string label = "Hauptverdächtiger im Fall Y";
        // lead takes part in both files and links Inv to Other; outsider leads Other but has no part in Inv
        var id = await _svc.CreateAsync(W.Inv, nameof(Investigation), W.Other, null, W.Viewer("lead"));
        await _svc.UpdateLabelAsync(id, label, W.Viewer("lead"));

        var outsider = await Investigations().GetHistoryAsync(W.Other, W.Viewer("outsider"));
        Assert.DoesNotContain(outsider, h => h.EntityType == nameof(InvestigationLink));
        Assert.DoesNotContain(outsider, h => (h.What + h.Changes + h.Who).Contains(label));

        // who sees both files gets the rows, but never the label the other team wrote
        var lead = await Investigations().GetHistoryAsync(W.Other, W.Viewer("lead"));
        var rows = lead.Where(h => h.EntityType == nameof(InvestigationLink)).ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, h => Assert.Null(h.Changes));
    }

    [Fact]
    public async Task The_history_never_shows_a_link_label()
    {
        const string label = "V-Person, Deckname K.";
        // leadership links the classified person; inv reads every tab of Inv but not the person
        var id = await _svc.CreateAsync(W.Inv, "Person", Secret, null, W.Viewer("chief"));
        await _svc.UpdateLabelAsync(id, label, W.Viewer("chief"));

        var history = await Investigations().GetHistoryAsync(W.Inv, W.Viewer("inv"));

        var changed = Assert.Single(history, h => h.EntityType == nameof(InvestigationLink) && h.What.StartsWith("Geändert"));
        Assert.Null(changed.Changes);
        Assert.DoesNotContain(history, h => (h.What + h.Changes).Contains(label));
    }

    [Fact]
    public async Task The_file_picker_offers_an_older_editable_file_behind_newer_read_only_and_linked_ones()
    {
        var observed = new List<string>();
        using (var db = _world.Ctx.NewContext())
        {
            // more newer files than the picker lists, all of them read-only for lead
            for (var i = 0; i < 15; i++)
            {
                var file = Guid.NewGuid().ToString();
                observed.Add(file);
                db.Investigations.Add(new Investigation
                {
                    Id = file, TaskforceId = W.Tf, Title = $"Akte Neu {i}", CaseNumber = $"ER-8{i:000}", OpenedAt = new DateTime(2026, 11, 10).AddHours(i),
                });
                db.InvestigationParticipants.Add(new InvestigationParticipant { InvestigationId = file, AgentId = "lead", Role = InvestigationRole.Observer });
            }
            db.SaveChanges();
        }

        var editable = await _svc.FindInvestigationsAsync(null, W.Viewer("lead"), editableOnly: true);
        Assert.Equal([W.Inv, W.Other], editable.Select(p => p.Id).Order().ToArray());

        // linked files leave the list before its cap, so an older one still gets a slot
        var rest = await _svc.FindInvestigationsAsync(null, W.Viewer("lead"), editableOnly: false, exclude: [.. observed, W.Other]);
        Assert.Equal(W.Inv, Assert.Single(rest).Id);
    }

    [Fact]
    public async Task A_person_merge_moves_the_links_and_drops_doubles()
    {
        const string source = "eeeeeeee-0000-4000-8000-000000000001";
        using (var db = _world.Ctx.NewContext())
        {
            db.People.Add(Seed.Person(source, "Olaf Doppelt"));
            db.SaveChanges();
        }
        Link(W.Inv, "Person", source);
        Link(W.Other, "Person", source);
        Link(W.Other, "Person", Open);

        await new PersonMergeService(_world.Ctx.Factory, Substitute.For<IPublicWantedService>())
            .MergeAsync(source, Open, ClaimsPrincipalBuilder.Agent("chief").WithRank(Rank.Director).Build());

        await using var read = _world.Ctx.NewContext();
        var live = read.InvestigationLinks.Where(l => l.RecordType == "Person").ToList();
        Assert.DoesNotContain(live, l => l.RecordId == source);
        Assert.Equal([W.Inv, W.Other], live.Where(l => l.RecordId == Open).Select(l => l.InvestigationId).Order().ToArray());
    }
}
