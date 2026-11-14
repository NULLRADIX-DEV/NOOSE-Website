using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Investigations;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Data.Entities.Warrants;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.CurrentUser;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Investigations;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Write rules of investigations that the access resolution alone does not cover.</summary>
public sealed class InvestigationServiceTests : IDisposable
{
    private const string Tf = "tf-1";
    private const string Inv = "inv-1";

    private readonly SqliteTestContext _ctx = new();
    private readonly InvestigationService _svc;

    public InvestigationServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_ctx.Connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.AmbientTransactionWarning))
            .AddInterceptors(new AuditSaveChangesInterceptor(new CurrentUser()))
            .Options;
        _svc = new InvestigationService(new TestDbContextFactory(options), Substitute.For<ICaseNumberService>(),
            Substitute.For<INotificationService>());

        using var db = _ctx.NewContext();
        db.Users.AddRange(
            Seed.Agent("lead", Rank.SpecialAgent),
            Seed.Agent("manager", Rank.SpecialAgent),
            Seed.Agent("other", Rank.SpecialAgent),
            Seed.Agent("sid", configure: a =>
            {
                a.PartnerAgency = PartnerAgency.DoJ;
                a.PartnerUnitId = PartnerUnits.DojSid;
            }));
        db.Taskforces.Add(new Taskforce { Id = Tf, Name = "TF", CaseNumber = "FIB-TF-2026-0001", Scope = TaskforceScope.CrossAgency });
        db.TaskforcePartnerAgencies.Add(new TaskforcePartnerAgency { TaskforceId = Tf, Agency = PartnerAgency.DoJ });
        db.Investigations.Add(new Investigation { Id = Inv, TaskforceId = Tf, Title = "Akte", CaseNumber = "FIB-ER-2026-0001" });
        db.InvestigationParticipants.AddRange(
            new InvestigationParticipant { Id = "row-lead", InvestigationId = Inv, AgentId = "lead", Role = InvestigationRole.Lead },
            // may manage participants, but is no lead
            new InvestigationParticipant { Id = "row-manager", InvestigationId = Inv, AgentId = "manager", Role = InvestigationRole.Investigator,
                TabAccessJson = """{"beteiligte":2}""" },
            new InvestigationParticipant { Id = "row-other", InvestigationId = Inv, AgentId = "other", Role = InvestigationRole.Investigator },
            new InvestigationParticipant { Id = "row-sid", InvestigationId = Inv, AgentId = "sid", Role = InvestigationRole.Investigator,
                TabAccessJson = """{"tatvorwurf":1,"tathergang":1,"personen":1,"beweismittel":1,"vernehmungen":1,"aufgaben":1,"notizen":1,"anordnungen":1,"beteiligte":1,"verlauf":1}""" });
        db.SaveChanges();
    }

    public void Dispose() => _ctx.Dispose();

    /// <summary>Acting agent for the audit stamp.</summary>
    private sealed class CurrentUser : ICurrentUserService
    {
        public Task<CurrentUserInfo> GetAsync() => Task.FromResult(Get());
        public CurrentUserInfo Get() => new("lead", "lead", false, false, false);
    }

    private static ClaimsPrincipal Internal(string id) => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.SpecialAgent).Build();

    private static InvestigationParticipantInput Row(string id, string agentId, InvestigationRole role)
        => new(id, agentId, null, role, new Dictionary<string, InvestigationAccessLevel>());

    [Fact]
    public async Task A_manager_cannot_make_themselves_lead()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _svc.SaveParticipantAsync(Inv, Row("row-manager", "manager", InvestigationRole.Lead), Internal("manager")));
    }

    [Fact]
    public async Task A_manager_cannot_change_their_own_rights()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _svc.SaveParticipantAsync(Inv, Row("row-manager", "manager", InvestigationRole.Observer), Internal("manager")));
    }

    [Fact]
    public async Task A_manager_cannot_demote_or_remove_the_lead()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _svc.SaveParticipantAsync(Inv, Row("row-lead", "lead", InvestigationRole.Investigator), Internal("manager")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _svc.RemoveParticipantAsync(Inv, "row-lead", Internal("manager")));
    }

    [Fact]
    public async Task A_manager_still_changes_others()
    {
        await _svc.SaveParticipantAsync(Inv, Row("row-other", "other", InvestigationRole.Observer), Internal("manager"));

        await using var db = _ctx.NewContext();
        Assert.Equal(InvestigationRole.Observer, db.InvestigationParticipants.Find("row-other")!.Role);
    }

    [Fact]
    public async Task The_last_lead_cannot_step_down()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _svc.SaveParticipantAsync(Inv, Row("row-lead", "lead", InvestigationRole.Investigator), Internal("lead")));
    }

    [Fact]
    public async Task The_history_hides_entries_limited_to_others()
    {
        var chargeId = await _svc.SaveChargeAsync(Inv,
            new InvestigationChargeInput(null, null, "§ 99", "StGB", "Geheim", InvestigationChargeCheck.Open), Internal("lead"));
        await _svc.SetRestrictionAsync(Inv, nameof(InvestigationCharge), chargeId,
            [new InvestigationTarget("other", null, "other")], Internal("lead"));
        var sid = ClaimsPrincipalBuilder.Agent("sid").AsPartnerUnit(PartnerAgency.DoJ, PartnerUnits.DojSid).Build();

        Assert.DoesNotContain(await _svc.GetHistoryAsync(Inv, sid), h => h.What.Contains("§ 99"));
        Assert.Contains(await _svc.GetHistoryAsync(Inv, Internal("lead")), h => h.What.Contains("§ 99"));

        // still hidden once the entry is deleted
        await _svc.DeleteEntryAsync(Inv, nameof(InvestigationCharge), chargeId, Internal("lead"));
        Assert.DoesNotContain(await _svc.GetHistoryAsync(Inv, sid), h => h.What.Contains("§ 99"));
    }

    [Fact]
    public async Task An_investigation_with_an_open_warrant_cannot_be_deleted()
    {
        await using (var db = _ctx.NewContext())
        {
            db.People.Add(Seed.Person("p-1"));
            db.Warrants.Add(new Warrant
            {
                Id = "w-1", InvestigationId = Inv, PersonId = "p-1", Status = WarrantStatus.Requested,
                CaseNumber = "FIB-AN-2026-0001", Subject = "Haus", Justification = "Grund",
            });
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.DeleteAsync(Inv, Internal("lead")));
    }
}
