using NOOSE_Website.Data.Entities.Investigations;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Search;
using W = NOOSE_Website.Tests.Services.Integration.InvestigationReferenceWorld;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>A reference to an investigation resolves exactly as far as the viewer could open that file or entry.</summary>
public sealed class InvestigationReferenceTests : IDisposable
{
    private readonly W _world = new();

    public void Dispose() => _world.Dispose();

    private async Task<Dictionary<(string Type, string Id), RecordsReference.Resolution>> Resolve(
        ViewerScope scope, params (string Type, string Id)[] refs)
    {
        await using var db = _world.Ctx.NewContext();
        return await InvestigationReferences.ResolveAsync(db, refs.Length == 0 ? W.All.ToArray() : refs, scope);
    }

    private const string AllTypes = "all";
    private const string None = "none";

    public static TheoryData<string, string> Matrix => new()
    {
        { "chief", AllTypes },
        { "sup", AllTypes },
        { "tf-lead", AllTypes },
        { "lead", AllTypes },
        { "inv", AllTypes },
        { "obs", AllTypes },
        { "manager", AllTypes },
        { "noevid", "Investigation,InvestigationCharge,InvestigationPerson,InvestigationInterrogation,InvestigationTask,InvestigationNote" },
        { "member", None },
        { "outsider", None },
        { "sid", AllTypes },
        { "sta", "Investigation,InvestigationEvidence" },
        { "sid-lead", AllTypes },
        { "dojchief", None },
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public async Task Each_viewer_resolves_what_the_file_shows_them(string viewer, string expected)
    {
        var map = await Resolve(W.Scope(viewer));

        var want = expected switch
        {
            AllTypes => InvestigationReferences.Types.ToArray(),
            None => Array.Empty<string>(),
            _ => expected.Split(','),
        };
        Assert.Equal(want.Order(), map.Keys.Select(k => k.Type).Order());
    }

    [Fact]
    public async Task A_tab_set_to_none_hides_its_entries_but_not_the_file()
    {
        var map = await Resolve(W.Scope("noevid"), (nameof(Investigation), W.Inv), (nameof(InvestigationEvidence), W.Evidence));

        Assert.True(map.ContainsKey((nameof(Investigation), W.Inv)));
        Assert.False(map.ContainsKey((nameof(InvestigationEvidence), W.Evidence)));
    }

    [Theory]
    [InlineData("inv", false)]
    [InlineData("sid", false)]
    [InlineData("obs", true)]
    [InlineData("sta", true)]
    [InlineData("manager", true)]
    [InlineData("lead", true)]
    [InlineData("chief", true)]
    public async Task A_restricted_entry_resolves_only_for_the_named_and_who_sees_all(string viewer, bool resolves)
    {
        var map = await Resolve(W.Scope(viewer), (nameof(InvestigationEvidence), W.Restricted));

        Assert.Equal(resolves, map.ContainsKey((nameof(InvestigationEvidence), W.Restricted)));
    }

    [Fact]
    public async Task A_deleted_entry_is_gone_for_leadership_too_and_comes_back_on_restore()
    {
        await SetDeleted<InvestigationEvidence>(W.Evidence, true);
        Assert.Empty(await Resolve(W.Scope("chief"), (nameof(InvestigationEvidence), W.Evidence)));

        await SetDeleted<InvestigationEvidence>(W.Evidence, false);
        Assert.Single(await Resolve(W.Scope("chief"), (nameof(InvestigationEvidence), W.Evidence)));
    }

    [Fact]
    public async Task A_trashed_file_takes_its_entries_with_it()
    {
        await using (var db = _world.Ctx.NewContext())
        {
            var inv = await db.Investigations.FirstAsync(i => i.Id == W.Inv);
            inv.IsDeleted = true;
            await db.SaveChangesAsync();
        }

        Assert.Empty(await Resolve(W.Scope("chief")));
        Assert.Empty(await Resolve(W.Scope("lead")));
    }

    [Fact]
    public async Task A_restriction_survives_delete_and_restore()
    {
        await SetDeleted<InvestigationEvidence>(W.Restricted, true);
        await SetDeleted<InvestigationEvidence>(W.Restricted, false);

        Assert.Empty(await Resolve(W.Scope("inv"), (nameof(InvestigationEvidence), W.Restricted)));
        Assert.Single(await Resolve(W.Scope("obs"), (nameof(InvestigationEvidence), W.Restricted)));
    }

    [Fact]
    public async Task A_guid_under_the_wrong_type_or_an_unknown_guid_stays_absent()
    {
        var map = await Resolve(W.Scope("chief"),
            (nameof(InvestigationCharge), W.Evidence), (nameof(InvestigationEvidence), W.Missing), (nameof(Investigation), W.Evidence));

        Assert.Empty(map);
    }

    [Fact]
    public async Task Labels_name_the_file_and_never_carry_note_text_or_tokens()
    {
        var map = await Resolve(W.Scope("lead"));

        Assert.Equal($"{W.CaseNumber} · Akte Messer", map[(nameof(Investigation), W.Inv)].Display);
        Assert.Equal($"{W.CaseNumber} · Tatvorwurf § 12 StGB", map[(nameof(InvestigationCharge), W.Charge)].Display);
        Assert.Equal($"{W.CaseNumber} · Max Mustermann (Beschuldigter)", map[(nameof(InvestigationPerson), W.Person)].Display);
        Assert.Equal($"{W.CaseNumber} · Beweismittel #3 Messer", map[(nameof(InvestigationEvidence), W.Evidence)].Display);
        Assert.Equal($"{W.CaseNumber} · Vernehmung Erika Muster", map[(nameof(InvestigationInterrogation), W.Interrogation)].Display);

        var task = map[(nameof(InvestigationTask), W.Task)].Display;
        Assert.StartsWith($"{W.CaseNumber} · Aufgabe: Prüfen", task);
        Assert.DoesNotContain("@{", task);

        var note = map[(nameof(InvestigationNote), W.Note)].Display;
        Assert.StartsWith($"{W.CaseNumber} · Notiz vom ", note);
        Assert.DoesNotContain(W.NoteText, note);
        Assert.DoesNotContain("Geheim", note);

        Assert.All(map.Values, v => Assert.False(v.Classified));
    }

    [Fact]
    public async Task Hrefs_open_the_file_or_the_entry_on_its_tab()
    {
        var map = await Resolve(W.Scope("lead"));

        Assert.Equal($"/ermittlungen/{W.Inv}", map[(nameof(Investigation), W.Inv)].Href);
        Assert.Equal($"/ermittlungen/{W.Inv}?tab=beweismittel&eintrag={W.Evidence}", map[(nameof(InvestigationEvidence), W.Evidence)].Href);
        Assert.Equal($"/ermittlungen/{W.Inv}?tab=notizen&eintrag={W.Note}", map[(nameof(InvestigationNote), W.Note)].Href);
        Assert.Equal($"/ermittlungen/{W.Inv}?tab=personen&eintrag={W.Person}", map[(nameof(InvestigationPerson), W.Person)].Href);
    }

    [Fact]
    public async Task More_than_the_cap_of_files_leaves_the_rest_unresolved()
    {
        var ids = new List<string>();
        await using (var db = _world.Ctx.NewContext())
        {
            for (var i = 0; i < InvestigationReferences.MaxInvestigations + 1; i++)
            {
                var id = Guid.NewGuid().ToString();
                ids.Add(id);
                db.Investigations.Add(new Investigation { Id = id, TaskforceId = W.Tf, Title = $"Akte {i}", CaseNumber = $"ER-9{i:000}" });
                db.InvestigationParticipants.Add(new InvestigationParticipant { InvestigationId = id, AgentId = "lead", Role = InvestigationRole.Lead });
            }
            await db.SaveChangesAsync();
        }

        var map = await Resolve(W.Scope("lead"), ids.Select(id => (nameof(Investigation), id)).ToArray());

        Assert.Equal(InvestigationReferences.MaxInvestigations, map.Count);
        Assert.False(map.ContainsKey((nameof(Investigation), ids[^1])));
    }

    [Fact]
    public async Task No_account_resolves_nothing()
    {
        Assert.Empty(await Resolve(new ViewerScope(true, true, null, null)));
        Assert.Empty(await Resolve(new ViewerScope(true, true, "gibt-es-nicht", null)));
    }

    [Fact]
    public async Task A_partner_scope_without_its_agency_cannot_take_the_leadership_arm()
    {
        // the caller forgot the agency and claimed leadership; the account row still says partner
        Assert.Empty(await Resolve(new ViewerScope(true, true, "dojchief", null)));
        // the unit row still counts for a partner that really takes part
        var sta = await Resolve(new ViewerScope(true, true, "sta", null));
        Assert.Equal(new[] { nameof(Investigation), nameof(InvestigationEvidence) }, sta.Keys.Select(k => k.Type).Order());
    }

    [Fact]
    public async Task IsEntryVisible_matches_the_resolver()
    {
        await using var db = _world.Ctx.NewContext();
        Assert.True(await InvestigationReferences.IsEntryVisibleAsync(db, nameof(InvestigationEvidence), W.Evidence, W.Scope("inv")));
        Assert.False(await InvestigationReferences.IsEntryVisibleAsync(db, nameof(InvestigationEvidence), W.Restricted, W.Scope("inv")));
        Assert.False(await InvestigationReferences.IsEntryVisibleAsync(db, nameof(InvestigationEvidence), W.Evidence, W.Scope("outsider")));
        Assert.False(await InvestigationReferences.IsEntryVisibleAsync(db, nameof(InvestigationEvidencePhoto), W.Photo, W.Scope("chief")));
    }

    // ---- candidates ----

    private async Task<List<NOOSE_Website.Models.Common.MentionHit>> Candidates(string viewer, string text, string? context = null)
    {
        await using var db = _world.Ctx.NewContext();
        return await InvestigationReferences.CandidatesAsync(db, text, W.Scope(viewer), context);
    }

    [Fact]
    public async Task Candidates_reach_a_readable_older_entry_behind_newer_unreadable_ones()
    {
        // noevid may not read the evidence tab of Inv, but takes part in an older file whose evidence it reads
        const string old = "aaaaaaaa-0000-4000-8000-0000000000a1";
        const string oldEvidence = "aaaaaaaa-0000-4000-8000-0000000000a2";
        await using (var db = _world.Ctx.NewContext())
        {
            db.Investigations.Add(new Investigation { Id = old, TaskforceId = W.Tf, Title = "Akte Alt", CaseNumber = "ER-0001", OpenedAt = new DateTime(2026, 1, 1) });
            db.InvestigationParticipants.Add(new InvestigationParticipant { InvestigationId = old, AgentId = "noevid", Role = InvestigationRole.Investigator });
            db.InvestigationEvidence.Add(new InvestigationEvidence
            {
                Id = oldEvidence, InvestigationId = old, Number = 1, Name = "Klappmesser", CreatedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            });
            // more newer hits than the picker fetches per type, all on a tab noevid cannot read
            for (var i = 0; i < 15; i++)
            {
                db.InvestigationEvidence.Add(new InvestigationEvidence
                {
                    InvestigationId = W.Inv, Number = 100 + i, Name = $"Messer {i}", CreatedAt = new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i),
                });
            }
            await db.SaveChangesAsync();
        }

        // matches "Klappmesser" and every "Messer …" alike (SQLite compares case-sensitively)
        var hits = await Candidates("noevid", "esser");

        Assert.Contains(hits, h => h.Type == nameof(InvestigationEvidence) && h.Id == oldEvidence);
        Assert.DoesNotContain(hits, h => h.Type == nameof(InvestigationEvidence) && h.Id != oldEvidence);
    }

    [Fact]
    public async Task The_batch_resolution_matches_the_single_one_for_every_viewer()
    {
        await using var db = _world.Ctx.NewContext();
        string[] files = [W.Inv, W.Other, W.Missing];
        foreach (var name in new[] { "chief", "sup", "tf-lead", "member", "lead", "inv", "obs", "noevid", "manager", "outsider", "sid", "sid-lead", "sta", "dojchief" })
        {
            var user = W.Viewer(name);
            var many = await InvestigationAccess.ResolveManyAsync(db, files, user);
            var readMany = await InvestigationAccess.ResolveReadManyAsync(db, files, W.Scope(name));
            foreach (var id in files)
            {
                Same(await InvestigationAccess.ResolveAsync(db, id, user), many.GetValueOrDefault(id) ?? InvestigationAccessView.None, $"{name}/{id}");
                Same(await InvestigationAccess.ResolveReadAsync(db, id, W.Scope(name)), readMany.GetValueOrDefault(id) ?? InvestigationAccessView.None, $"{name}/{id} read");
            }
        }

        static void Same(InvestigationAccessView single, InvestigationAccessView batch, string what)
        {
            Assert.True((single.CanSee, single.FullAccess, single.MayManage, single.MaySignWarrants, single.MayWrite, single.ReadsAll)
                == (batch.CanSee, batch.FullAccess, batch.MayManage, batch.MaySignWarrants, batch.MayWrite, batch.ReadsAll), what);
            Assert.True(InvestigationTabs.Slugs.All(s => single.Level(s) == batch.Level(s)), what);
        }
    }

    [Fact]
    public async Task Candidates_are_always_a_subset_of_what_resolves()
    {
        foreach (var viewer in new[] { "inv", "noevid", "sta", "outsider", "dojchief", "lead" })
        {
            foreach (var text in new[] { "Akte", "Messer", "Pistole", "#3", "#4", "Max", "Erika", "Prüfen", "§", "Notiz", "ER-" })
            {
                var hits = await Candidates(viewer, text);
                var map = await Resolve(W.Scope(viewer), hits.Select(h => (h.Type, h.Id)).ToArray());
                Assert.All(hits, h => Assert.Equal(map[(h.Type, h.Id)].Display, h.Display));
            }
        }
    }

    [Fact]
    public async Task Candidates_find_evidence_by_number_and_never_offer_a_restricted_entry()
    {
        Assert.Contains(await Candidates("inv", "#3"), h => h.Type == nameof(InvestigationEvidence) && h.Id == W.Evidence);
        Assert.DoesNotContain(await Candidates("inv", "Pistole"), h => h.Id == W.Restricted);
        Assert.Contains(await Candidates("obs", "Pistole"), h => h.Id == W.Restricted);
    }

    [Fact]
    public async Task Notes_are_not_searchable()
        => Assert.DoesNotContain(await Candidates("lead", W.NoteText), h => h.Type == nameof(InvestigationNote));

    [Fact]
    public async Task Candidates_rank_the_context_file_first()
    {
        var hits = await Candidates("lead", "Akte", W.Other);
        Assert.Equal(W.Other, hits[0].Id);
        hits = await Candidates("lead", "Akte", W.Inv);
        Assert.Equal(W.Inv, hits[0].Id);
    }

    // ---- guards ----

    [Fact]
    public void Types_are_the_file_plus_every_entry_type()
    {
        Assert.Equal(InvestigationService.EntryTabs.Keys.Append(nameof(Investigation)).Order(), InvestigationReferences.Types.Order());
        foreach (var (type, tab) in InvestigationService.EntryTabs)
        {
            Assert.Equal(tab, InvestigationTabs.ForChildType(type));
        }
    }

    [Fact]
    public void No_scope_free_path_knows_the_types()
    {
        var partnerReleasable = (string[])typeof(MentionService)
            .GetField("PartnerReleasableMentionTypes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;
        var catalog = SearchCatalog.Categories.Select(c => c.Clr).ToHashSet();
        foreach (var type in InvestigationReferences.Types)
        {
            Assert.DoesNotContain(type, LinkService.KnownTypes);
            Assert.DoesNotContain(type, LinkService.ReferenceTypes);
            Assert.DoesNotContain(type, RecordBatch.Linkable);
            Assert.DoesNotContain(type, partnerReleasable);
            Assert.DoesNotContain(type, catalog);
            Assert.False(PartnerVisibility.IsReleasableType(type));
        }
    }

    [Fact]
    public async Task RecordsReference_never_resolves_them()
    {
        await using var db = _world.Ctx.NewContext();
        Assert.Empty(await RecordsReference.ResolveAsync(db, W.All.ToList()));
        Assert.Empty(await RecordsReference.ResolveAsync(db, W.All.ToList(), mayAllTaskforces: true, meId: "chief"));
    }

    [Fact]
    public void Every_type_has_a_label_and_its_own_icon()
    {
        foreach (var type in InvestigationReferences.Types)
        {
            Assert.StartsWith("Ermittlung", MentionVisuals.Label(type));
            Assert.NotEqual(MudBlazor.Icons.Material.Filled.Link, MentionVisuals.Symbol(type));
        }
        Assert.Equal(MudBlazor.Icons.Material.Filled.Link, MentionVisuals.Symbol(null));
        Assert.Equal(string.Empty, MentionVisuals.Label(null));
    }

    // ---- hardened gates ----

    [Fact]
    public async Task Visibility_answers_entries_like_the_file_does()
    {
        await using var db = _world.Ctx.NewContext();
        foreach (var (type, id) in W.Entries)
        {
            Assert.False(await Visibility.IsRecordVisibleAsync(db, type, id, W.Scope("outsider")));
            Assert.False(await Visibility.IsRecordVisibleAsync(db, type, id, W.Scope("member")));
            Assert.True(await Visibility.IsRecordVisibleAsync(db, type, id, W.Scope("inv")));
            Assert.True(await Visibility.IsRecordVisibleAsync(db, type, id, W.Scope("sid")));
            Assert.False(await Visibility.IsRecordVisibleAsync(db, type, id, W.Scope("dojchief")));
            Assert.True(await PartnerVisibility.IsRecordVisibleToPartnerAsync(db, type, id, PartnerAgency.DoJ, "sid"));
            Assert.False(await PartnerVisibility.IsRecordVisibleToPartnerAsync(db, type, id, PartnerAgency.DoJ, "dojchief"));
        }
        Assert.False(await Visibility.IsRecordVisibleAsync(db, nameof(InvestigationEvidence), W.Restricted, W.Scope("inv")));
        Assert.False(await Visibility.IsRecordVisibleAsync(db, nameof(InvestigationEvidence), W.Evidence, W.Scope("noevid")));
        Assert.False(await PartnerVisibility.IsRecordVisibleToPartnerAsync(db, nameof(InvestigationEvidence), W.Restricted, PartnerAgency.DoJ, "sid"));
    }

    [Theory]
    [InlineData(nameof(InvestigationEvidencePhoto), W.Photo)]
    [InlineData(nameof(InvestigationInterrogationEntry), W.QaEntry)]
    [InlineData(nameof(InvestigationParticipant), W.LeadRow)]
    [InlineData(nameof(InvestigationItemRestriction), W.RestrictionRow)]
    [InlineData(nameof(InvestigationLink), W.Missing)]
    public async Task The_files_machinery_is_never_visible_on_its_own(string type, string id)
    {
        await using var db = _world.Ctx.NewContext();
        Assert.False(await Visibility.IsRecordVisibleAsync(db, type, id, W.Scope("chief")));
        Assert.False(await Visibility.IsRecordVisibleAsync(db, type, id, W.Scope("lead")));
        Assert.False(await Visibility.IsRecordVisibleAsync(db, type, id, W.Scope("sid")));
        Assert.False(await PartnerVisibility.IsRecordVisibleToPartnerAsync(db, type, id, PartnerAgency.DoJ, "sid"));
    }

    private async Task SetDeleted<T>(string id, bool deleted) where T : InvestigationRow
    {
        await using var db = _world.Ctx.NewContext();
        var row = await db.Set<T>().IgnoreQueryFilters().FirstAsync(r => r.Id == id);
        row.IsDeleted = deleted;
        await db.SaveChangesAsync();
    }
}
