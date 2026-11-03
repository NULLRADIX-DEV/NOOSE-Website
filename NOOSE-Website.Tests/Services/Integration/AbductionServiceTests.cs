using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Abductions;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Abductions;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="AbductionService"/> over in-memory SQLite.</summary>
public sealed class AbductionServiceTests
{
    private static readonly DateTime T0 = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    private static ClaimsPrincipal Junior()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).Build();

    private static ClaimsPrincipal OnlyReader()
        => ClaimsPrincipalBuilder.Agent("tl").WithRank(Rank.SpecialAgent).AsTeamLead().Build();

    private static (AbductionService Svc, IThreatScoreService Threat, ICaseNumberService Case) Build(SqliteTestContext ctx)
    {
        var threat = Substitute.For<IThreatScoreService>();
        var caseNo = Substitute.For<ICaseNumberService>();
        var seq = 0;
        caseNo.NextAsync(Arg.Any<AppDbContext>(), "ENT", Arg.Any<CancellationToken>())
            .Returns(_ => $"FIB-ENT-2026-{++seq:0000}");
        var svc = new AbductionService(ctx.Factory, caseNo, threat, Substitute.For<INotificationService>());
        return (svc, threat, caseNo);
    }

    private static AbductionInput Input(string victimId, string perpType, string perpId, Action<AbductionInput>? cfg = null)
    {
        var i = new AbductionInput
        {
            VictimAgentId = victimId,
            PerpetratorType = perpType,
            PerpetratorId = perpId,
            Timestamp = T0,
            Outcome = AbductionOutcome.Released,
        };
        cfg?.Invoke(i);
        return i;
    }

    [Fact]
    public async Task CreateAsync_AssignsCaseNumber_PersistsAndRecomputesFactionThreat()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("victim"));
            db.Factions.Add(Seed.Faction("fac", "Ballas"));
            db.SaveChanges();
        }
        var (svc, threat, _) = Build(ctx);

        var created = await svc.CreateAsync(Input("victim", nameof(Faction), "fac"), Leader());

        Assert.Equal("FIB-ENT-2026-0001", created.CaseNumber);
        using (var db = ctx.NewContext())
        {
            Assert.Single(db.AgentAbductions);
        }
        await threat.Received(1).NewCalculateAsync("fac", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_AllocatesCaseNumber_InsideTransaction()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("victim"));
            db.Factions.Add(Seed.Faction("fac"));
            db.SaveChanges();
        }
        bool? txPresent = null;
        var caseNo = Substitute.For<ICaseNumberService>();
        // CaseNumberService.NextAsync fails fast without an enclosing transaction; assert we provide one
        caseNo.NextAsync(Arg.Any<AppDbContext>(), "ENT", Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                txPresent = ((AppDbContext)ci[0]).Database.CurrentTransaction is not null;
                return "FIB-ENT-2026-0001";
            });
        var svc = new AbductionService(ctx.Factory, caseNo,
            Substitute.For<IThreatScoreService>(), Substitute.For<INotificationService>());

        await svc.CreateAsync(Input("victim", nameof(Faction), "fac"), Leader());

        Assert.True(txPresent, "NextAsync must be called within an active DB transaction.");
    }

    [Fact]
    public async Task CreateAsync_PersonPerpetrator_RecomputesPersonThreat()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("victim"));
            db.People.Add(Seed.Person("p1"));
            db.SaveChanges();
        }
        var (svc, threat, _) = Build(ctx);

        await svc.CreateAsync(Input("victim", nameof(Person), "p1"), Leader());

        await threat.Received(1).NewCalculatePersonScoreAsync("p1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_WithoutLeak_ZeroesLeakFields()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("victim"));
            db.Factions.Add(Seed.Faction("fac"));
            db.SaveChanges();
        }
        var (svc, _, _) = Build(ctx);

        // categories/severity set but the leak flag is off → must be cleared
        var created = await svc.CreateAsync(Input("victim", nameof(Faction), "fac", i =>
        {
            i.InformationLeaked = false;
            i.LeakCategories = LeakCategory.Informants | LeakCategory.Operations;
            i.LeakSeverity = LeakSeverity.Critical;
            i.Compromises.Add(new CompromiseTargetInput { TargetType = nameof(Operation), TargetId = "op1" });
        }), Leader());

        Assert.Equal(LeakCategory.None, created.LeakCategories);
        Assert.Equal(LeakSeverity.None, created.LeakSeverity);
        // no leak → compromises are not persisted either
        using var check = ctx.NewContext();
        Assert.Empty(check.AbductionCompromises);
    }

    [Fact]
    public async Task CreateAsync_PersistsPickedCompromises()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("victim"));
            db.Factions.Add(Seed.Faction("fac"));
            db.SaveChanges();
        }
        var (svc, _, _) = Build(ctx);

        var created = await svc.CreateAsync(Input("victim", nameof(Faction), "fac", i =>
        {
            i.InformationLeaked = true;
            i.LeakSeverity = LeakSeverity.High;
            i.Compromises.Add(new CompromiseTargetInput { TargetType = nameof(Operation), TargetId = "op1" });
            i.Compromises.Add(new CompromiseTargetInput { TargetType = nameof(Person), TargetId = "p1" });
            // duplicate must be de-duplicated, not throw on the unique index
            i.Compromises.Add(new CompromiseTargetInput { TargetType = nameof(Operation), TargetId = "op1" });
        }), Leader());

        var rows = await svc.GetCompromisesForAbductionAsync(created.Id);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal(CompromiseStatus.Compromised, r.Status));
    }

    [Fact]
    public async Task UpdateAsync_ReconcilesCompromises_AddsAndRemoves()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("victim"));
            db.Factions.Add(Seed.Faction("fac"));
            db.SaveChanges();
        }
        var (svc, _, _) = Build(ctx);
        var created = await svc.CreateAsync(Input("victim", nameof(Faction), "fac", i =>
        {
            i.InformationLeaked = true;
            i.Compromises.Add(new CompromiseTargetInput { TargetType = nameof(Operation), TargetId = "op1" });
        }), Leader());

        // edit: drop op1, add p2
        await svc.UpdateAsync(created.Id, Input("victim", nameof(Faction), "fac", i =>
        {
            i.InformationLeaked = true;
            i.Compromises.Add(new CompromiseTargetInput { TargetType = nameof(Person), TargetId = "p2" });
        }), Leader());

        var rows = await svc.GetCompromisesForAbductionAsync(created.Id);
        var row = Assert.Single(rows);
        Assert.Equal(nameof(Person), row.TargetType);
        Assert.Equal("p2", row.TargetId);
    }

    [Fact]
    public async Task UpdateAsync_NoLeak_RemovesAllCompromises()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("victim"));
            db.Factions.Add(Seed.Faction("fac"));
            db.SaveChanges();
        }
        var (svc, _, _) = Build(ctx);
        var created = await svc.CreateAsync(Input("victim", nameof(Faction), "fac", i =>
        {
            i.InformationLeaked = true;
            i.Compromises.Add(new CompromiseTargetInput { TargetType = nameof(Operation), TargetId = "op1" });
        }), Leader());

        await svc.UpdateAsync(created.Id, Input("victim", nameof(Faction), "fac", i => i.InformationLeaked = false), Leader());

        Assert.Empty(await svc.GetCompromisesForAbductionAsync(created.Id));
    }

    [Fact]
    public async Task CreateAsync_MissingPerpetrator_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync(Input("victim", nameof(Faction), ""), Leader()));
    }

    [Fact]
    public async Task CreateAsync_OnlyReader_Denied()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateAsync(Input("victim", nameof(Faction), "fac"), OnlyReader()));
    }

    [Fact]
    public async Task AddCompromise_ThenClear_FlipsStatusAndDropsFromBadgeLookup()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("victim"));
            db.Factions.Add(Seed.Faction("fac"));
            db.SaveChanges();
        }
        var (svc, _, _) = Build(ctx);
        var abd = await svc.CreateAsync(Input("victim", nameof(Faction), "fac"), Leader());

        var comp = await svc.AddCompromiseAsync(abd.Id, nameof(Operation), "op1", "Op enttarnt", Leader());
        Assert.Equal(CompromiseStatus.Compromised, comp.Status);

        var flaggedBefore = await svc.GetCompromisedTargetIdsAsync(nameof(Operation), new[] { "op1" });
        Assert.Contains("op1", flaggedBefore);

        await svc.SetCompromiseStatusAsync(comp.Id, CompromiseStatus.Cleared, Leader());

        var flaggedAfter = await svc.GetCompromisedTargetIdsAsync(nameof(Operation), new[] { "op1" });
        Assert.DoesNotContain("op1", flaggedAfter);

        using var check = ctx.NewContext();
        var row = check.AbductionCompromises.Single();
        Assert.Equal(CompromiseStatus.Cleared, row.Status);
        Assert.NotNull(row.ClearedAt);
    }

    [Fact]
    public async Task AddCompromise_Duplicate_ReactivatesInsteadOfStacking()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("victim"));
            db.Factions.Add(Seed.Faction("fac"));
            db.SaveChanges();
        }
        var (svc, _, _) = Build(ctx);
        var abd = await svc.CreateAsync(Input("victim", nameof(Faction), "fac"), Leader());

        var first = await svc.AddCompromiseAsync(abd.Id, nameof(Person), "p9", null, Leader());
        await svc.SetCompromiseStatusAsync(first.Id, CompromiseStatus.Cleared, Leader());
        var second = await svc.AddCompromiseAsync(abd.Id, nameof(Person), "p9", null, Leader());

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(CompromiseStatus.Compromised, second.Status);
        using var check = ctx.NewContext();
        Assert.Single(check.AbductionCompromises);
    }

    [Fact]
    public async Task GetTrashAsync_ReturnsOnlyDeleted_AndRestoreClearsFlag()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("v"));
            db.AgentAbductions.Add(new AgentAbduction
            {
                Id = "del", CaseNumber = "FIB-ENT-2026-9001", VictimAgentId = "v",
                PerpetratorType = nameof(Faction), PerpetratorId = "fac", Timestamp = T0,
                IsDeleted = true, DeletedAt = T0,
            });
            db.AgentAbductions.Add(new AgentAbduction
            {
                Id = "live", CaseNumber = "FIB-ENT-2026-9002", VictimAgentId = "v",
                PerpetratorType = nameof(Faction), PerpetratorId = "fac", Timestamp = T0,
            });
            db.SaveChanges();
        }
        var (svc, _, _) = Build(ctx);

        var trash = await svc.GetTrashAsync();
        Assert.Single(trash);
        Assert.Equal("del", trash[0].Id);

        await svc.RestoreAsync("del", Leader());

        using var check = ctx.NewContext();
        var restored = check.AgentAbductions.IgnoreQueryFilters().Single(a => a.Id == "del");
        Assert.False(restored.IsDeleted);
    }

    [Fact]
    public async Task RestoreAsync_NonLeadership_Denied()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RestoreAsync("x", Junior()));
    }

    [Fact]
    public async Task GetForVictim_And_GetForPerpetrator_Filter()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("victimA"));
            db.Users.Add(Seed.Agent("victimB"));
            db.Factions.Add(Seed.Faction("fac"));
            db.SaveChanges();
        }
        var (svc, _, _) = Build(ctx);
        await svc.CreateAsync(Input("victimA", nameof(Faction), "fac"), Leader());
        await svc.CreateAsync(Input("victimB", nameof(Faction), "fac"), Leader());

        var forA = await svc.GetForVictimAsync("victimA");
        Assert.Single(forA);
        Assert.Equal("victimA", forA[0].Abduction.VictimAgentId);

        var forFaction = await svc.GetForPerpetratorAsync(nameof(Faction), "fac");
        Assert.Equal(2, forFaction.Count);
    }
}
