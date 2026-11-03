using System.Security.Claims;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Informants;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Informants;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Security-critical tests for informant record visibility plus person and faction linking.</summary>
public sealed class InformantServiceTests
{
    private static ClaimsPrincipal Leader() => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();
    private static ClaimsPrincipal Handler(string id) => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.SpecialAgent).Build();
    private static ClaimsPrincipal Stranger() => ClaimsPrincipalBuilder.Agent("stranger").WithRank(Rank.SpecialAgent).Build();
    private static ClaimsPrincipal OnlyReader() => ClaimsPrincipalBuilder.Agent("or").AsTeamLead().Build();
    private static ClaimsPrincipal Junior() => ClaimsPrincipalBuilder.Agent("j").WithRank(Rank.Trainee).Build();
    private static ClaimsPrincipal Partner() => ClaimsPrincipalBuilder.Agent("p")
        .AsPartner(PartnerAgency.DoJ, PartnerRank.Chief).Build();

    // shared across every service instance in one test: case numbers are unique-indexed
    private int _caseNumber;

    private InformantService Svc(SqliteTestContext ctx)
    {
        SeedHandlers(ctx);
        var caseNumbers = Substitute.For<ICaseNumberService>();
        caseNumbers.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult($"FIB-VP-2026-{++_caseNumber:0000}"));
        return new InformantService(ctx.Factory, caseNumbers);
    }

    private static InformantInput NewInput(string handlerId = "handler1", string? realName = "Max Mustermann",
        string? personId = null, string? factionId = null)
        => new(realName, personId, factionId, "Kontakt im Hafen", InformantReliability.B, InformantStatus.Active,
            handlerId, "0900-123", "Vorsicht");

    private async Task<string> SeedAsync(SqliteTestContext ctx, string handlerId = "handler1")
        => await Svc(ctx).CreateAsync(NewInput(handlerId), Leader());

    // The write path validates handler ids against OnlySelectable(), so they have to be real accounts.
    private static void SeedHandlers(SqliteTestContext ctx)
    {
        using var db = ctx.NewContext();
        foreach (var id in new[] { "handler1", "handler2" })
        {
            if (!db.Users.Any(u => u.Id == id))
            {
                db.Users.Add(Seed.Agent(id));
            }
        }
        db.SaveChanges();
    }

    // Seeds a person record and returns its id.
    private static string SeedPerson(SqliteTestContext ctx, string name = "Klara Klarname", bool classified = false)
    {
        using var db = ctx.NewContext();
        var person = Seed.Person(name: name, configure: p => p.IsClassified = classified);
        db.People.Add(person);
        db.SaveChanges();
        return person.Id;
    }

    // Seeds a faction record and returns its id.
    private static string SeedFaction(SqliteTestContext ctx, string name = "Ballas")
    {
        using var db = ctx.NewContext();
        var faction = Seed.Faction(name: name);
        db.Factions.Add(faction);
        db.SaveChanges();
        return faction.Id;
    }

    // ---- GetHandlerOptionsAsync ----

    [Fact]
    public async Task GetHandlerOptionsAsync_ExcludesTeamLeadsAndPartners()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("ok"));
            db.Users.Add(Seed.Agent("tl", configure: a => a.IsTeamLead = true));
            // not even with the admin flag on top
            db.Users.Add(Seed.Agent("tl-adm", configure: a => { a.IsTeamLead = true; a.IsAdmin = true; }));
            db.Users.Add(Seed.Agent("partner", configure: a => a.PartnerAgency = PartnerAgency.LSPD));
            db.Users.Add(Seed.Agent("pending", status: AgentStatus.Pending));
            db.Users.Add(Seed.Agent("gone", status: AgentStatus.Terminated));
            db.SaveChanges();
        }

        var svc = Svc(ctx);

        // open to every internal writer now, not just leadership
        var options = await svc.GetHandlerOptionsAsync(Junior());
        Assert.Contains(options, o => o.Id == "ok");
        Assert.DoesNotContain(options, o => o.Id is "tl" or "tl-adm" or "partner" or "pending" or "gone");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.GetHandlerOptionsAsync(OnlyReader()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.GetHandlerOptionsAsync(Partner()));
    }

    [Fact]
    public async Task Create_Throws_ForAHandlerWhoIsNotSelectable()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("tl", configure: a => a.IsTeamLead = true));
            db.SaveChanges();
        }

        // the SignalR path must not reach past the picker and surface a hidden account
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Svc(ctx).CreateAsync(NewInput("tl"), Junior()));
    }

    [Fact]
    public async Task Update_Throws_WhenReassigningToANonSelectableHandler()
    {
        using var ctx = new SqliteTestContext();
        var id = await SeedAsync(ctx, "handler1");
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("tl", configure: a => a.IsTeamLead = true));
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Svc(ctx).UpdateAsync(id, NewInput("tl"), Junior()));
    }

    // ==================== record visibility ====================

    [Fact]
    public async Task Handler_SeesOwnInformant_WithContactData()
    {
        using var ctx = new SqliteTestContext();
        var id = await SeedAsync(ctx, "handler1");
        var svc = Svc(ctx);

        Assert.Contains(await svc.GetListAsync(Handler("handler1")), i => i.Id == id);
        var detail = await svc.GetDetailAsync(id, Handler("handler1"));
        Assert.NotNull(detail);
        Assert.Equal("Max Mustermann", detail!.Name);
        Assert.Equal("0900-123", detail.ContactInfo);
    }

    [Fact]
    public async Task Leadership_SeesContactData()
    {
        using var ctx = new SqliteTestContext();
        var id = await SeedAsync(ctx);

        var detail = await Svc(ctx).GetDetailAsync(id, Leader());
        Assert.NotNull(detail);
        Assert.Equal("Max Mustermann", detail!.Name);
        Assert.Equal("0900-123", detail.ContactInfo);
    }

    [Fact]
    public async Task OnlyReader_SeesTheWholeRecord_IncludingContactData()
    {
        using var ctx = new SqliteTestContext();
        var id = await SeedAsync(ctx);
        var svc = Svc(ctx);

        // record access is all-or-nothing: no second tier hides the name or the contact channel
        Assert.Contains(await svc.GetListAsync(OnlyReader()), i => i.Id == id);
        var detail = await svc.GetDetailAsync(id, OnlyReader());
        Assert.NotNull(detail);
        Assert.Equal("Max Mustermann", detail!.Name);
        Assert.Equal("0900-123", detail.ContactInfo);
        Assert.Equal("Vorsicht", detail.Notes);
    }

    [Fact]
    public async Task EveryInternalAgent_SeesEveryInformant_WithContactData()
    {
        using var ctx = new SqliteTestContext();
        var id = await SeedAsync(ctx, "handler1");
        var svc = Svc(ctx);

        foreach (var actor in new[] { Stranger(), Junior(), Handler("handler2") })
        {
            Assert.Contains(await svc.GetListAsync(actor), i => i.Id == id);
            var detail = await svc.GetDetailAsync(id, actor);
            Assert.NotNull(detail);
            Assert.Equal("Max Mustermann", detail!.Name);
            Assert.Equal("0900-123", detail.ContactInfo);
            Assert.Equal("Vorsicht", detail.Notes);
            Assert.True(detail.MayEdit);
        }
    }

    [Fact]
    public async Task Partner_SeesNothing()
    {
        using var ctx = new SqliteTestContext();
        var id = await SeedAsync(ctx, "handler1");
        var svc = Svc(ctx);

        Assert.Empty(await svc.GetListAsync(Partner()));
        Assert.Null(await svc.GetDetailAsync(id, Partner()));
        Assert.Empty(await svc.GetMeetingsAsync(id, Partner()));
    }

    [Fact]
    public async Task Partner_IsRefusedByTheCentralRecordGate()
    {
        using var ctx = new SqliteTestContext();
        var id = await SeedAsync(ctx, "handler1");

        // the gate every polymorphic child, anchor and link resolution goes through
        await using var db = ctx.NewContext();
        Assert.False(await Visibility.IsRecordVisibleAsync(
            db, nameof(Informant), id, ViewerScope.From(Partner())));
        Assert.True(await Visibility.IsRecordVisibleAsync(
            db, nameof(Informant), id, ViewerScope.From(Junior())));
    }

    // ==================== name source ====================

    [Fact]
    public async Task LinkedPerson_ProvidesTheName_AndClearsFreeText()
    {
        using var ctx = new SqliteTestContext();
        var personId = SeedPerson(ctx, "Klara Klarname");
        var svc = Svc(ctx);

        var id = await svc.CreateAsync(NewInput(realName: "Wird ignoriert", personId: personId), Leader());

        var detail = await svc.GetDetailAsync(id, Leader());
        Assert.Equal("Klara Klarname", detail!.Name);
        Assert.Equal(personId, detail.PersonId);

        using var db = ctx.NewContext();
        Assert.Null(db.Informants.Single(i => i.Id == id).RealName);
    }

    [Fact]
    public async Task RenamingLinkedPerson_RenamesTheInformant()
    {
        using var ctx = new SqliteTestContext();
        var personId = SeedPerson(ctx, "Alter Name");
        var svc = Svc(ctx);
        var id = await svc.CreateAsync(NewInput(realName: null, personId: personId), Leader());

        using (var db = ctx.NewContext())
        {
            db.People.Single(p => p.Id == personId).Name = "Neuer Name";
            db.SaveChanges();
        }

        Assert.Equal("Neuer Name", (await svc.GetDetailAsync(id, Leader()))!.Name);
    }

    [Fact]
    public async Task Unlinking_CarriesThePersonNameOver()
    {
        using var ctx = new SqliteTestContext();
        var personId = SeedPerson(ctx, "Klara Klarname");
        var svc = Svc(ctx);
        var id = await svc.CreateAsync(NewInput(realName: null, personId: personId), Leader());

        await svc.UpdateAsync(id, NewInput(realName: null, personId: null), Leader());

        var detail = await svc.GetDetailAsync(id, Leader());
        Assert.Null(detail!.PersonId);
        Assert.Equal("Klara Klarname", detail.Name);
    }

    [Fact]
    public async Task Create_Throws_WithoutNameOrPerson()
    {
        using var ctx = new SqliteTestContext();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Svc(ctx).CreateAsync(NewInput(realName: null), Leader()));
    }

    [Fact]
    public async Task Create_Throws_WhenPersonAlreadyLinked()
    {
        using var ctx = new SqliteTestContext();
        var personId = SeedPerson(ctx);
        var svc = Svc(ctx);
        await svc.CreateAsync(NewInput(realName: null, personId: personId), Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync(NewInput(realName: null, personId: personId), Leader()));
    }

    [Fact]
    public async Task Create_Throws_ForUnknownPerson()
    {
        using var ctx = new SqliteTestContext();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Svc(ctx).CreateAsync(NewInput(realName: null, personId: "does-not-exist"), Leader()));
    }

    // ==================== person marker ====================

    [Fact]
    public async Task PersonMarker_IsVisibleToEveryInternalAgent()
    {
        using var ctx = new SqliteTestContext();
        var personId = SeedPerson(ctx);
        var svc = Svc(ctx);
        var id = await svc.CreateAsync(NewInput("handler1", realName: null, personId: personId), Leader());

        foreach (var actor in new[] { Stranger(), Junior(), Handler("handler1"), Leader(), OnlyReader() })
        {
            var marker = await svc.GetPersonMarkerAsync(personId, actor);
            Assert.NotNull(marker);
            Assert.Equal(id, marker!.InformantId);
        }
    }

    [Fact]
    public async Task PersonMarker_IsNullForPartnersAndUnlinkedPeople()
    {
        using var ctx = new SqliteTestContext();
        var personId = SeedPerson(ctx);
        var otherPersonId = SeedPerson(ctx, "Ohne Informant");
        var svc = Svc(ctx);
        await svc.CreateAsync(NewInput(realName: null, personId: personId), Leader());

        Assert.Null(await svc.GetPersonMarkerAsync(personId, Partner()));
        Assert.Null(await svc.GetPersonMarkerAsync(otherPersonId, Leader()));
    }

    // ==================== faction link ====================

    [Fact]
    public async Task FactionLink_IsResolvedForDisplay()
    {
        using var ctx = new SqliteTestContext();
        var factionId = SeedFaction(ctx, "Ballas");
        var svc = Svc(ctx);

        var id = await svc.CreateAsync(NewInput(factionId: factionId), Leader());

        var detail = await svc.GetDetailAsync(id, Leader());
        Assert.Equal(factionId, detail!.FactionId);
        Assert.Equal("Ballas", detail.FactionName);
    }

    [Fact]
    public async Task FactionLink_IsNotExclusive_SeveralInformantsPerFaction()
    {
        using var ctx = new SqliteTestContext();
        var factionId = SeedFaction(ctx);
        var svc = Svc(ctx);
        await svc.CreateAsync(NewInput(realName: "Erster", factionId: factionId), Leader());
        await svc.CreateAsync(NewInput(realName: "Zweiter", factionId: factionId), Leader());

        var entries = await svc.GetForFactionAsync(factionId, Leader());
        Assert.Equal(2, entries.Count);
        Assert.Equal(new[] { "Erster", "Zweiter" }, entries.Select(e => e.Name));
    }

    [Fact]
    public async Task FactionRoster_NamesTheInformant_ForEveryInternalAgent()
    {
        using var ctx = new SqliteTestContext();
        var factionId = SeedFaction(ctx);
        var svc = Svc(ctx);
        await svc.CreateAsync(NewInput("handler1", factionId: factionId), Leader());

        var entry = Assert.Single(await svc.GetForFactionAsync(factionId, Stranger()));
        Assert.Equal("Max Mustermann", entry.Name);
    }

    [Fact]
    public async Task FactionRoster_IsEmptyForPartners()
    {
        using var ctx = new SqliteTestContext();
        var factionId = SeedFaction(ctx);
        var svc = Svc(ctx);
        await svc.CreateAsync(NewInput(factionId: factionId), Leader());

        Assert.Empty(await svc.GetForFactionAsync(factionId, Partner()));
    }

    [Fact]
    public async Task Create_Throws_ForUnknownFaction()
    {
        using var ctx = new SqliteTestContext();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Svc(ctx).CreateAsync(NewInput(factionId: "does-not-exist"), Leader()));
    }

    [Fact]
    public async Task FactionLink_CanBeCleared()
    {
        using var ctx = new SqliteTestContext();
        var factionId = SeedFaction(ctx);
        var svc = Svc(ctx);
        var id = await svc.CreateAsync(NewInput(factionId: factionId), Leader());

        await svc.UpdateAsync(id, NewInput(factionId: null), Leader());

        Assert.Null((await svc.GetDetailAsync(id, Leader()))!.FactionId);
        Assert.Empty(await svc.GetForFactionAsync(factionId, Leader()));
    }

    // ==================== write guards ====================

    [Fact]
    public async Task Create_IsAllowedForAnyInternalAgent()
    {
        using var ctx = new SqliteTestContext();
        var svc = Svc(ctx);

        var id = await svc.CreateAsync(NewInput(), Junior());

        Assert.Equal("Max Mustermann", (await svc.GetDetailAsync(id, Stranger()))!.Name);
    }

    [Fact]
    public async Task Create_Throws_ForSupervisionAndPartners()
    {
        using var ctx = new SqliteTestContext();
        var svc = Svc(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateAsync(NewInput(), OnlyReader()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateAsync(NewInput(), Partner()));
    }

    [Fact]
    public async Task AddMeeting_AllowedForAnyInternalAgent_DeniedForSupervisionAndPartners()
    {
        using var ctx = new SqliteTestContext();
        var id = await SeedAsync(ctx, "handler1");
        var svc = Svc(ctx);
        var meeting = new InformantMeetingInput(DateTime.UtcNow, "Hafen", "Übergabe beobachtet");

        await svc.AddMeetingAsync(id, meeting, Junior()); // not the handler, still allowed
        Assert.Single(await svc.GetMeetingsAsync(id, Stranger()));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.AddMeetingAsync(id, meeting, OnlyReader()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.AddMeetingAsync(id, meeting, Partner()));
    }

    [Fact]
    public async Task Update_IsAllowedForANonHandlerAgent()
    {
        using var ctx = new SqliteTestContext();
        var id = await SeedAsync(ctx, "handler1");
        var svc = Svc(ctx);

        await svc.UpdateAsync(id, new InformantInput("Max Mustermann", null, null, "Neu bewertet",
            InformantReliability.A, InformantStatus.Burned, "handler1", "0900-123", "Vorsicht"), Junior());

        var detail = await svc.GetDetailAsync(id, Junior());
        Assert.Equal("Neu bewertet", detail!.Description);
        Assert.Equal(InformantStatus.Burned, detail.Status);
    }

    [Fact]
    public async Task Update_LetsAnyInternalAgentReassignTheHandler()
    {
        using var ctx = new SqliteTestContext();
        var id = await SeedAsync(ctx, "handler1");
        var svc = Svc(ctx);

        await svc.UpdateAsync(id, NewInput("handler2"), Junior());

        Assert.Equal("handler2", (await svc.GetDetailAsync(id, Junior()))!.HandlerId);
    }

    [Fact]
    public async Task Update_Throws_ForSupervisionAndPartners()
    {
        using var ctx = new SqliteTestContext();
        var id = await SeedAsync(ctx, "handler1");
        var svc = Svc(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.UpdateAsync(id, NewInput(), OnlyReader()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.UpdateAsync(id, NewInput(), Partner()));
    }

    // ==================== delete / trash / restore ====================

    [Fact]
    public async Task Delete_IsAllowedForAnyInternalAgent_AndHidesTheRecordEverywhere()
    {
        using var ctx = new SqliteTestContext();
        var factionId = SeedFaction(ctx);
        var svc = Svc(ctx);
        var id = await svc.CreateAsync(NewInput(factionId: factionId), Leader());

        await svc.DeleteAsync(id, Junior());

        Assert.Empty(await svc.GetListAsync(Leader()));
        Assert.Null(await svc.GetDetailAsync(id, Leader()));
        Assert.Empty(await svc.GetForFactionAsync(factionId, Leader()));
    }

    [Fact]
    public async Task Delete_Throws_ForSupervisionAndPartners()
    {
        using var ctx = new SqliteTestContext();
        var id = await SeedAsync(ctx);
        var svc = Svc(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.DeleteAsync(id, OnlyReader()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.DeleteAsync(id, Partner()));
    }

    // The SaveChanges interceptors are not registered in the test context, so Remove would hard-delete.
    // Marking the row is what the interceptor does in production, and what the other trash tests do here.
    private static void MarkDeleted(SqliteTestContext ctx, string id)
    {
        using var db = ctx.NewContext();
        var inf = db.Informants.IgnoreQueryFilters().Single(i => i.Id == id);
        inf.IsDeleted = true;
        inf.DeletedAt = DateTime.UtcNow;
        db.SaveChanges();
    }

    [Fact]
    public async Task Trash_ListsTheDeletedRecord_WithItsName()
    {
        using var ctx = new SqliteTestContext();
        var personId = SeedPerson(ctx, "Klara Klarname");
        var svc = Svc(ctx);
        var byName = await svc.CreateAsync(NewInput(), Leader());
        var byPerson = await svc.CreateAsync(NewInput(realName: null, personId: personId), Leader());
        MarkDeleted(ctx, byName);
        MarkDeleted(ctx, byPerson);

        var trash = await svc.GetTrashAsync();

        // the name follows the same source rule as the file itself: linked person first, then free text
        Assert.Equal("Max Mustermann", trash.Single(t => t.Id == byName).Name);
        Assert.Equal("Klara Klarname", trash.Single(t => t.Id == byPerson).Name);
    }

    [Fact]
    public async Task Restore_BringsTheRecordAndItsMeetingsBack_ButOnlyForLeadership()
    {
        using var ctx = new SqliteTestContext();
        var id = await SeedAsync(ctx);
        var svc = Svc(ctx);
        await svc.AddMeetingAsync(id, new InformantMeetingInput(DateTime.UtcNow, "Hafen", "Übergabe"), Junior());
        MarkDeleted(ctx, id);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RestoreAsync(id, Junior()));

        await svc.RestoreAsync(id, Leader());

        Assert.NotNull(await svc.GetDetailAsync(id, Junior()));
        Assert.Single(await svc.GetMeetingsAsync(id, Junior()));
        Assert.Empty(await svc.GetTrashAsync());
    }

    [Fact]
    public async Task ADeletedInformant_StillBlocksItsPersonLink_UntilRestoredOrTheLinkIsFreed()
    {
        using var ctx = new SqliteTestContext();
        var personId = SeedPerson(ctx);
        var svc = Svc(ctx);
        var id = await svc.CreateAsync(NewInput(realName: null, personId: personId), Leader());
        MarkDeleted(ctx, id);

        // the unique index counts soft-deleted rows too, so the message has to name the trash
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync(NewInput(realName: null, personId: personId), Leader()));
        Assert.Contains("Papierkorb", ex.Message);
    }

    [Fact]
    public async Task Update_WritesEveryField()
    {
        using var ctx = new SqliteTestContext();
        var id = await SeedAsync(ctx, "handler1");
        var svc = Svc(ctx);

        await svc.UpdateAsync(id, new InformantInput("Neuer Name", null, null, "Neu", InformantReliability.A,
            InformantStatus.Inactive, "handler1", "0700-999", "Neue Notiz"), Leader());

        var detail = await svc.GetDetailAsync(id, Handler("handler1"));
        Assert.Equal("Neuer Name", detail!.Name);
        Assert.Equal("0700-999", detail.ContactInfo);
        Assert.Equal("Neue Notiz", detail.Notes);
        Assert.Equal(InformantStatus.Inactive, detail.Status);
    }
}
