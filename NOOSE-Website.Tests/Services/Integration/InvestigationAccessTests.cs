using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities.Investigations;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Who sees an investigation and what they may do in it; list and point check must agree.</summary>
public sealed class InvestigationAccessTests : IDisposable
{
    private const string Tf = "tf-1";
    private const string Inv = "inv-1";

    private readonly SqliteTestContext _ctx = new();

    public InvestigationAccessTests()
    {
        using var db = _ctx.NewContext();
        db.Users.AddRange(
            Seed.Agent("lead", Rank.SpecialAgent),
            Seed.Agent("member", Rank.SpecialAgent),
            Seed.Agent("outsider", Rank.SpecialAgent),
            Seed.Agent("tf-lead", Rank.SpecialAgent),
            Seed.Agent("chief", Rank.SectionChief),
            Seed.Agent("sid", configure: a => Partner(a, PartnerUnits.DojSid, lead: false)),
            Seed.Agent("sid-lead", configure: a => Partner(a, PartnerUnits.DojSid, lead: true)),
            Seed.Agent("sta", configure: a => Partner(a, PartnerUnits.DojStateAttorney, lead: false)));
        db.Taskforces.Add(new Taskforce { Id = Tf, Name = "TF", CaseNumber = "FIB-TF-2026-0001", Scope = TaskforceScope.CrossAgency });
        db.TaskforceAgents.Add(new TaskforceAgent { TaskforceId = Tf, AgentId = "tf-lead", Role = TaskforceRole.LeadInvestigator });
        db.TaskforceAgents.Add(new TaskforceAgent { TaskforceId = Tf, AgentId = "member", Role = TaskforceRole.Member });
        db.TaskforcePartnerAgencies.Add(new TaskforcePartnerAgency { TaskforceId = Tf, Agency = PartnerAgency.DoJ });
        db.Investigations.Add(new Investigation { Id = Inv, TaskforceId = Tf, Title = "Akte", CaseNumber = "FIB-ER-2026-0001" });
        db.InvestigationParticipants.Add(new InvestigationParticipant { InvestigationId = Inv, AgentId = "lead", Role = InvestigationRole.Lead });
        db.SaveChanges();
    }

    private static void Partner(NOOSE_Website.Data.Entities.Agent a, string unitId, bool lead)
    {
        a.PartnerAgency = PartnerAgency.DoJ;
        a.PartnerUnitId = unitId;
        a.IsPartnerUnitLead = lead;
    }

    public void Dispose() => _ctx.Dispose();

    private static ClaimsPrincipal Internal(string id, Rank rank = Rank.SpecialAgent)
        => ClaimsPrincipalBuilder.Agent(id).WithRank(rank).Build();

    private static ClaimsPrincipal Doj(string id, string unitId, bool lead = false)
        => ClaimsPrincipalBuilder.Agent(id).AsPartnerUnit(PartnerAgency.DoJ, unitId, lead).Build();

    private void AddRow(string? agentId, string? unitId, InvestigationRole role, string? tabs = null)
    {
        using var db = _ctx.NewContext();
        db.InvestigationParticipants.Add(new InvestigationParticipant
        {
            InvestigationId = Inv, AgentId = agentId, PartnerUnitId = unitId, Role = role, TabAccessJson = tabs,
        });
        db.SaveChanges();
    }

    private void SetUnitDefaults(string unitId, string json)
    {
        using var db = _ctx.NewContext();
        db.PartnerUnits.Find(unitId)!.InvestigationDefaultsJson = json;
        db.SaveChanges();
    }

    private async Task<(bool Listed, bool Point, InvestigationAccessView Access)> Check(ClaimsPrincipal user)
    {
        await using var db = _ctx.NewContext();
        var scope = ViewerScope.From(user);
        var listed = await db.Investigations.OnlyVisible(db, scope).AnyAsync(i => i.Id == Inv);
        var point = await InvestigationAccess.IsVisibleAsync(db, Inv, scope);
        var access = await InvestigationAccess.ResolveAsync(db, Inv, user);
        return (listed, point, access);
    }

    [Fact]
    public async Task Outsider_SeesNothing()
    {
        var (listed, point, access) = await Check(Internal("outsider"));
        Assert.False(listed);
        Assert.False(point);
        Assert.False(access.CanSee);
    }

    [Fact]
    public async Task PlainTaskforceMember_IsNotEnough()
    {
        var (listed, point, access) = await Check(Internal("member"));
        Assert.False(listed);
        Assert.False(point);
        Assert.False(access.CanSee);
    }

    [Fact]
    public async Task InvestigationLead_HasFullAccess()
    {
        var (listed, _, access) = await Check(Internal("lead"));
        Assert.True(listed);
        Assert.True(access.FullAccess);
        Assert.True(access.CanEdit(InvestigationTabs.Participants));
    }

    [Fact]
    public async Task TaskforceLead_HasFullAccess_WithoutRow()
    {
        var (listed, point, access) = await Check(Internal("tf-lead"));
        Assert.True(listed);
        Assert.True(point);
        Assert.True(access.FullAccess);
    }

    [Fact]
    public async Task Leadership_SeesAndEditsEverything()
    {
        var (listed, _, access) = await Check(Internal("chief", Rank.SectionChief));
        Assert.True(listed);
        Assert.True(access.FullAccess);
    }

    [Fact]
    public async Task Supervision_ReadsButNeverEdits()
    {
        var user = ClaimsPrincipalBuilder.Agent("outsider").WithRank(Rank.SpecialAgent).AsTeamLead().Build();
        var (listed, _, access) = await Check(user);
        Assert.True(listed);
        Assert.True(access.CanRead(InvestigationTabs.Evidence));
        Assert.False(access.CanEdit(InvestigationTabs.Evidence));
    }

    [Fact]
    public async Task Investigator_EditsContent_ButOnlyReadsParticipants()
    {
        AddRow("member", null, InvestigationRole.Investigator);
        var (listed, _, access) = await Check(Internal("member"));
        Assert.True(listed);
        Assert.True(access.CanEdit(InvestigationTabs.Evidence));
        Assert.True(access.CanRead(InvestigationTabs.Participants));
        Assert.False(access.CanEdit(InvestigationTabs.Participants));
        Assert.False(access.FullAccess);
    }

    [Fact]
    public async Task RowOverride_HidesATab_AndThereforeTheHistory()
    {
        AddRow("member", null, InvestigationRole.Investigator, """{"notizen":0}""");
        var (_, _, access) = await Check(Internal("member"));
        Assert.False(access.CanRead(InvestigationTabs.Notes));
        Assert.False(access.CanRead(InvestigationTabs.History));
        Assert.True(access.CanRead(InvestigationTabs.Overview));
    }

    [Fact]
    public async Task UnitRow_GivesEveryAccountOfTheUnit_TheUnitDefaults_Live()
    {
        AddRow(null, PartnerUnits.DojSid, InvestigationRole.Investigator);
        SetUnitDefaults(PartnerUnits.DojSid, """{"personen":2,"beweismittel":1}""");

        var (listed, point, access) = await Check(Doj("sid", PartnerUnits.DojSid));
        Assert.True(listed);
        Assert.True(point);
        Assert.True(access.CanEdit(InvestigationTabs.People));
        Assert.True(access.CanRead(InvestigationTabs.Evidence));
        Assert.False(access.CanEdit(InvestigationTabs.Evidence));
        Assert.False(access.CanRead(InvestigationTabs.Notes));

        // a changed default applies at once
        SetUnitDefaults(PartnerUnits.DojSid, """{"beweismittel":2}""");
        (_, _, access) = await Check(Doj("sid", PartnerUnits.DojSid));
        Assert.True(access.CanEdit(InvestigationTabs.Evidence));
        Assert.False(access.CanRead(InvestigationTabs.People));
    }

    [Fact]
    public async Task UnitRow_DoesNotReachAnotherUnit()
    {
        AddRow(null, PartnerUnits.DojSid, InvestigationRole.Investigator);
        var (listed, point, access) = await Check(Doj("sta", PartnerUnits.DojStateAttorney));
        Assert.False(listed);
        Assert.False(point);
        Assert.False(access.CanSee);
    }

    [Fact]
    public async Task UnitLead_EditsEverything_OnlyWhileTheAgencyTakesPart()
    {
        var (listed, _, access) = await Check(Doj("sid-lead", PartnerUnits.DojSid, lead: true));
        Assert.True(listed);
        Assert.True(access.CanEdit(InvestigationTabs.Evidence));
        Assert.True(access.CanEdit(InvestigationTabs.Participants));
        Assert.True(access.SeesAllEntries);
        // deleting the file and handing out its lead stay with full access
        Assert.False(access.FullAccess);

        await using (var db = _ctx.NewContext())
        {
            await db.TaskforcePartnerAgencies.Where(x => x.TaskforceId == Tf).ExecuteDeleteAsync();
        }
        (listed, _, access) = await Check(Doj("sid-lead", PartnerUnits.DojSid, lead: true));
        Assert.False(listed);
        Assert.False(access.CanSee);
    }

    [Fact]
    public async Task UnitLead_OfAUnitThatMayNot_SeesNothingExtra()
    {
        await using (var db = _ctx.NewContext())
        {
            db.PartnerUnits.Find(PartnerUnits.DojSid)!.LeadSeesAllInvestigations = false;
            await db.SaveChangesAsync();
        }
        var (listed, _, access) = await Check(Doj("sid-lead", PartnerUnits.DojSid, lead: true));
        Assert.False(listed);
        Assert.False(access.CanSee);
    }

    [Fact]
    public async Task Prosecutor_MaySign_OnlyInAProsecutionUnit()
    {
        AddRow("sta", null, InvestigationRole.Prosecutor);
        var (_, _, access) = await Check(Doj("sta", PartnerUnits.DojStateAttorney));
        Assert.True(access.MaySignWarrants);

        // moved to SID: the row stays, the signature goes
        await using (var db = _ctx.NewContext())
        {
            db.Users.Find("sta")!.PartnerUnitId = PartnerUnits.DojSid;
            await db.SaveChangesAsync();
        }
        (_, _, access) = await Check(Doj("sta", PartnerUnits.DojSid));
        Assert.False(access.MaySignWarrants);
    }

    [Fact]
    public async Task InternalProsecutorRole_NeverSigns()
    {
        AddRow("member", null, InvestigationRole.Prosecutor);
        var (_, _, access) = await Check(Internal("member"));
        Assert.False(access.MaySignWarrants);
    }

    [Fact]
    public async Task Restriction_HidesTheEntry_FromOthers_ButNotFromFullAccess()
    {
        AddRow("member", null, InvestigationRole.Investigator);
        AddRow("sid", null, InvestigationRole.Investigator);
        await using (var db = _ctx.NewContext())
        {
            db.InvestigationItemRestrictions.Add(new InvestigationItemRestriction
            {
                InvestigationId = Inv, ItemType = "InvestigationNote", ItemId = "note-1", AgentId = "member",
            });
            await db.SaveChangesAsync();
        }

        async Task<HashSet<string>> Hidden(ClaimsPrincipal user)
        {
            await using var db = _ctx.NewContext();
            var access = await InvestigationAccess.ResolveAsync(db, Inv, user);
            return await InvestigationAccess.HiddenItemIdsAsync(db, Inv, "InvestigationNote", access);
        }

        Assert.Empty(await Hidden(Internal("member")));
        Assert.Contains("note-1", await Hidden(Doj("sid", PartnerUnits.DojSid)));
        Assert.Empty(await Hidden(Internal("lead")));
        // supervision reads everything, restricted entries included
        Assert.Empty(await Hidden(ClaimsPrincipalBuilder.Agent("outsider").WithRank(Rank.SpecialAgent).AsTeamLead().Build()));
    }

    [Fact]
    public async Task Prosecutor_WithoutReadOnWarrants_MayNotSign()
    {
        AddRow("sta", null, InvestigationRole.Prosecutor, """{"anordnungen":0}""");
        var (_, _, access) = await Check(Doj("sta", PartnerUnits.DojStateAttorney));
        Assert.False(access.MaySignWarrants);
    }

    [Fact]
    public async Task Demo_ReadsButNeverWrites()
    {
        var user = ClaimsPrincipalBuilder.Agent("chief").WithRank(Rank.Director).AsDemo().Build();
        var (_, _, access) = await Check(user);
        Assert.True(access.CanRead(InvestigationTabs.Evidence));
        Assert.False(access.CanEdit(InvestigationTabs.Evidence));
        Assert.False(access.FullAccess);
    }
}
