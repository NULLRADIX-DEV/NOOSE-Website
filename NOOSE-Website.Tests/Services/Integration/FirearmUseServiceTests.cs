using System.Security.Claims;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Personnel;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="FirearmUseService"/> over in-memory SQLite.</summary>
public class FirearmUseServiceTests
{
    // The soft-delete rewrite interceptor is DI-only and absent here, so DeleteAsync HARD-deletes.

    private const string Secret = "Warnschuss auf dem Parkdeck";

    private sealed record Sent(IReadOnlyCollection<string> Recipients, NotificationType Type, string Title,
        string? Href, string? TriggerId);

    private static (FirearmUseService Svc, List<Sent> Sent) Build(SqliteTestContext ctx)
    {
        var notifications = Substitute.For<INotificationService>();
        var sent = new List<Sent>();
        notifications.NotifyManyInAppAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<NotificationType>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                sent.Add(new Sent(call.ArgAt<IReadOnlyCollection<string>>(0), call.ArgAt<NotificationType>(1),
                    call.ArgAt<string>(2), call.ArgAt<string?>(3), call.ArgAt<string?>(4)));
                return Task.CompletedTask;
            });
        return (new FirearmUseService(ctx.Factory, notifications), sent);
    }

    private static void SeedAgent(SqliteTestContext ctx, string id, Rank rank, Division? division = null,
        Action<Agent>? configure = null)
    {
        using var db = ctx.NewContext();
        if (db.Users.Any(u => u.Id == id))
        {
            return;
        }
        db.Users.Add(Seed.Agent(id, rank, configure: a =>
        {
            a.Division = division;
            configure?.Invoke(a);
        }));
        db.SaveChanges();
    }

    private static FirearmUse SeedUse(SqliteTestContext ctx, string agentId, DateTime? timestamp = null,
        string reason = Secret, Action<FirearmUse>? configure = null)
    {
        var row = new FirearmUse
        {
            AgentId = agentId,
            Timestamp = timestamp ?? new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
            Reason = reason,
        };
        configure?.Invoke(row);

        using var db = ctx.NewContext();
        db.FirearmUses.Add(row);
        db.SaveChanges();
        return row;
    }

    private static int Count(SqliteTestContext ctx)
    {
        using var db = ctx.NewContext();
        return db.FirearmUses.IgnoreQueryFilters().Count();
    }

    private static ClaimsPrincipalBuilder Trainee(string id) => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee);

    private static ClaimsPrincipalBuilder Leader(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.SpecialAgentInCharge);

    private static ClaimsPrincipal OnlyReader(string id = "tl") => ClaimsPrincipalBuilder.Agent(id).AsTeamLead().Build();

    // ---------- ReportAsync ----------

    [Fact]
    public async Task ReportAsync_StampsActorAndNow_AndKeepsTheTrimmedReason()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "a1", Rank.Trainee, Division.Cid);
        var (svc, _) = Build(ctx);
        var before = DateTime.UtcNow;

        var row = await svc.ReportAsync("  " + Secret + "  ", Trainee("a1").Build());

        Assert.Equal("a1", row.AgentId);
        Assert.Equal(Secret, row.Reason);
        Assert.InRange(row.Timestamp, before, DateTime.UtcNow);
        Assert.Equal(1, Count(ctx));
    }

    [Fact]
    public async Task ReportAsync_TakesNoAgentFromTheCaller()
    {
        // the interface has no agent parameter at all: nobody reports for somebody else
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "a1", Rank.Trainee);
        SeedAgent(ctx, "a2", Rank.Trainee);
        var (svc, _) = Build(ctx);

        await svc.ReportAsync(Secret, Trainee("a2").Build());

        using var db = ctx.NewContext();
        Assert.Equal("a2", Assert.Single(db.FirearmUses).AgentId);
    }

    [Fact]
    public async Task ReportAsync_RefusesEveryoneWhoMayNotWriteOrIsNotInHouse()
    {
        using var ctx = new SqliteTestContext();
        var (svc, sent) = Build(ctx);
        ClaimsPrincipal[] refused =
        [
            // read-only supervision: in house, but may not write
            OnlyReader(),
            // partner agency
            Trainee("p1").AsPartner(PartnerAgency.LSPD, PartnerRank.Member).Build(),
            // the synthetic demo visitor carries a rank and an active status
            Leader("demo").AsDemo().Build(),
            // a signed-in citizen has no rank and no agent status
            ClaimsPrincipalBuilder.Agent("c1").WithStatus(AgentStatus.Civilian).Build(),
            ClaimsPrincipalBuilder.Agent("w1").WithStatus(AgentStatus.Pending).Build(),
            ClaimsPrincipalBuilder.Anonymous(),
        ];

        foreach (var actor in refused)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.ReportAsync(Secret, actor));
        }

        Assert.Equal(0, Count(ctx));
        Assert.Empty(sent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ReportAsync_RefusesAnEmptyReason(string reason)
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "a1", Rank.Trainee);
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ReportAsync(reason, Trainee("a1").Build()));

        Assert.Equal(0, Count(ctx));
    }

    [Fact]
    public async Task ReportAsync_RefusesAReasonLongerThanTheColumn()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "a1", Rank.Trainee);
        var (svc, _) = Build(ctx);

        await svc.ReportAsync(new string('x', FirearmUseRules.MaxReasonLength), Trainee("a1").Build());
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ReportAsync(new string('x', FirearmUseRules.MaxReasonLength + 1), Trainee("a1").Build()));

        Assert.Equal(1, Count(ctx));
    }

    // ---------- the bell ----------

    [Fact]
    public async Task ReportAsync_NotifiesTheDivisionsLeadershipAndTheBoard_NotTheReporterNorForeignDivisions()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "rep", Rank.Trainee, Division.Ctd, a => a.Codename = "Falke");
        SeedAgent(ctx, "sac-ctd", Rank.SpecialAgentInCharge, Division.Ctd);
        SeedAgent(ctx, "chief-ctd", Rank.SectionChief, Division.Ctd);
        SeedAgent(ctx, "sac-cid", Rank.SpecialAgentInCharge, Division.Cid);
        SeedAgent(ctx, "chief-hrt", Rank.SectionChief, Division.Hrt);
        // Board members take the report whatever their home
        SeedAgent(ctx, "ad", Rank.AssistantDirector, Division.Cid);
        SeedAgent(ctx, "director", Rank.Director, Division.Hrt);
        // below the leadership floor
        SeedAgent(ctx, "senior-ctd", Rank.SeniorSpecialAgent, Division.Ctd);
        SeedAgent(ctx, "trainee-ctd", Rank.Trainee, Division.Ctd);
        // never selectable: read-only supervision, partner, closed account
        SeedAgent(ctx, "tl", Rank.SpecialAgentInCharge, Division.Ctd, a => a.IsTeamLead = true);
        SeedAgent(ctx, "partner", Rank.SpecialAgentInCharge, Division.Ctd, a => a.PartnerAgency = PartnerAgency.LSPD);
        SeedAgent(ctx, "blocked", Rank.SpecialAgentInCharge, Division.Ctd, a => a.Status = AgentStatus.Blocked);
        var (svc, sent) = Build(ctx);

        await svc.ReportAsync(Secret, Trainee("rep").Build());

        var notice = Assert.Single(sent);
        Assert.Equal(NotificationType.FirearmUseReported, notice.Type);
        Assert.Equal(["ad", "chief-ctd", "director", "sac-ctd"], notice.Recipients.Order(StringComparer.Ordinal));
        Assert.Equal("/schusswaffengebrauch", notice.Href);
        Assert.Equal("rep", notice.TriggerId);
    }

    [Fact]
    public async Task ReportAsync_TheTitleNamesAgentAndTimeButNeverTheReason()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "rep", Rank.Trainee, Division.Cid, a => a.Codename = "Falke");
        SeedAgent(ctx, "sac", Rank.SpecialAgentInCharge, Division.Cid);
        var (svc, sent) = Build(ctx);

        await svc.ReportAsync(Secret, Trainee("rep").Build());

        var title = Assert.Single(sent).Title;
        Assert.Contains("Falke", title, StringComparison.Ordinal);
        Assert.Contains("Uhr", title, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, title, StringComparison.Ordinal);
        Assert.DoesNotContain("Parkdeck", title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReportAsync_ALeadershipReporterDoesNotNotifyThemself()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "sac1", Rank.SpecialAgentInCharge, Division.Cid);
        SeedAgent(ctx, "sac2", Rank.SpecialAgentInCharge, Division.Cid);
        var (svc, sent) = Build(ctx);

        await svc.ReportAsync(Secret, Leader("sac1").Build());

        Assert.Equal(["sac2"], Assert.Single(sent).Recipients);
    }

    [Fact]
    public async Task ReportAsync_ADerivedHomeCountsLikeAStoredOne()
    {
        // an old row with no stored division and the TRU flag is a member of the HRT
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "rep", Rank.SpecialAgent, null, a => a.IsTRU = true);
        SeedAgent(ctx, "chief-hrt", Rank.SectionChief, null, a => a.IsTRU = true);
        SeedAgent(ctx, "chief-cid", Rank.SectionChief, Division.Cid);
        var (svc, sent) = Build(ctx);

        await svc.ReportAsync(Secret, ClaimsPrincipalBuilder.Agent("rep").WithRank(Rank.SpecialAgent).Build());

        Assert.Equal(["chief-hrt"], Assert.Single(sent).Recipients);
    }

    [Fact]
    public async Task ReportAsync_StaysSavedWhenTheBellFails()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "rep", Rank.Trainee, Division.Cid);
        SeedAgent(ctx, "sac", Rank.SpecialAgentInCharge, Division.Cid);
        var notifications = Substitute.For<INotificationService>();
        notifications.NotifyManyInAppAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<NotificationType>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Glocke defekt")));

        await new FirearmUseService(ctx.Factory, notifications).ReportAsync(Secret, Trainee("rep").Build());

        Assert.Equal(1, Count(ctx));
    }

    // ---------- reading ----------

    [Fact]
    public async Task GetListAsync_LeadershipAndSupervisionSeeEveryone_NewestFirst_AnAgentSeesNothing()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "a1", Rank.Trainee);
        SeedAgent(ctx, "a2", Rank.SpecialAgent);
        SeedUse(ctx, "a1", new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc));
        SeedUse(ctx, "a2", new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc));
        var (svc, _) = Build(ctx);

        var asLeader = await svc.GetListAsync(new FirearmUseFilter(), Leader().Build());
        var asReader = await svc.GetListAsync(new FirearmUseFilter(), OnlyReader());

        Assert.Equal(["a2", "a1"], asLeader.Select(r => r.AgentId));
        Assert.Equal(2, asReader.Count);
        Assert.All(asLeader, r => Assert.Equal(Secret, r.Reason));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.GetListAsync(new FirearmUseFilter(), Trainee("a1").Build()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.GetListAsync(new FirearmUseFilter(),
                Leader("p1").AsPartner(PartnerAgency.DoJ, PartnerRank.Chief).Build()));
    }

    [Fact]
    public async Task GetListAsync_FiltersByAgentAndByPeriod()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "a1", Rank.Trainee);
        SeedAgent(ctx, "a2", Rank.Trainee);
        SeedUse(ctx, "a1", new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc));
        SeedUse(ctx, "a1", new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc));
        SeedUse(ctx, "a2", new DateTime(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc));
        var (svc, _) = Build(ctx);

        var byAgent = await svc.GetListAsync(new FirearmUseFilter(AgentId: "a1"), Leader().Build());
        // the end is exclusive: the caller passes the day after an inclusive "Bis"
        var byPeriod = await svc.GetListAsync(
            new FirearmUseFilter(FromUtc: new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
                ToUtc: new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc)),
            Leader().Build());

        Assert.Equal(2, byAgent.Count);
        Assert.All(byAgent, r => Assert.Equal("a1", r.AgentId));
        Assert.Equal("a1", Assert.Single(byPeriod).AgentId);
    }

    [Fact]
    public async Task GetListAsync_FiltersByHomeDivision_DerivedHomesAndTheBoardIncluded()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "cid", Rank.Trainee, Division.Cid);
        SeedAgent(ctx, "hrt", Rank.SpecialAgent, Division.Hrt);
        SeedAgent(ctx, "hrt-old", Rank.SpecialAgent, null, a => a.IsTRU = true);
        SeedAgent(ctx, "board", Rank.AssistantDirector, Division.Ctd);
        foreach (var id in new[] { "cid", "hrt", "hrt-old", "board" })
        {
            SeedUse(ctx, id);
        }
        var (svc, _) = Build(ctx);

        var hrt = await svc.GetListAsync(new FirearmUseFilter(Division: Division.Hrt), Leader().Build());
        var ctd = await svc.GetListAsync(new FirearmUseFilter(Division: Division.Ctd), Leader().Build());
        var cid = await svc.GetListAsync(new FirearmUseFilter(Division: Division.Cid), Leader().Build());

        Assert.Equal(["hrt", "hrt-old"], hrt.Select(r => r.AgentId).Order(StringComparer.Ordinal));
        // a Board member keeps the home division they return to
        var board = Assert.Single(ctd);
        Assert.Equal("board", board.AgentId);
        Assert.True(board.IsBoard);
        Assert.Equal(Division.Ctd, board.Home);
        Assert.Equal("cid", Assert.Single(cid).AgentId);
    }

    [Fact]
    public async Task GetForAgentAsync_OwnFileForTheAgent_AnyFileForTheLeadershipRead_NobodyElse()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "a1", Rank.Trainee);
        SeedAgent(ctx, "a2", Rank.Trainee);
        SeedUse(ctx, "a1");
        var (svc, _) = Build(ctx);

        var own = await svc.GetForAgentAsync("a1", Trainee("a1").Build());
        var byLeader = await svc.GetForAgentAsync("a1", Leader().Build());
        var bySupervision = await svc.GetForAgentAsync("a1", OnlyReader());

        Assert.Equal(Secret, Assert.Single(own).Reason);
        Assert.Single(byLeader);
        Assert.Single(bySupervision);
        // a colleague below the leadership, a partner and a citizen read neither the file nor its absence
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.GetForAgentAsync("a1", Trainee("a2").Build()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.GetForAgentAsync("a1", Trainee("a1").AsPartner(PartnerAgency.LSMD, PartnerRank.Member).Build()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.GetForAgentAsync("a1", ClaimsPrincipalBuilder.Agent("a1").WithStatus(AgentStatus.Civilian).Build()));
    }

    [Fact]
    public async Task GetForAgentAsync_ADeletedReportIsNotListed()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "a1", Rank.Trainee);
        SeedUse(ctx, "a1");
        SeedUse(ctx, "a1", configure: r => { r.IsDeleted = true; r.DeletedAt = DateTime.UtcNow; });
        var (svc, _) = Build(ctx);

        Assert.Single(await svc.GetForAgentAsync("a1", Trainee("a1").Build()));
    }

    [Fact]
    public async Task GetAgentOptionsAsync_ListsOnlyAgentsWithAReport_ForTheLeadershipRead()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "a1", Rank.Trainee);
        SeedAgent(ctx, "a2", Rank.Trainee);
        SeedUse(ctx, "a1");
        var (svc, _) = Build(ctx);

        var options = await svc.GetAgentOptionsAsync(Leader().Build());

        Assert.Equal("a1", Assert.Single(options).Id);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.GetAgentOptionsAsync(Trainee("a1").Build()));
    }

    // ---------- deleting ----------

    [Fact]
    public async Task DeleteAsync_LeadershipRemovesAnotherAgentsReport_AndTheRowSaysSo()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "a1", Rank.Trainee);
        SeedAgent(ctx, "lead", Rank.SpecialAgentInCharge);
        var row = SeedUse(ctx, "a1");
        var (svc, _) = Build(ctx);

        Assert.True(Assert.Single(await svc.GetListAsync(new FirearmUseFilter(), Leader().Build())).MayDelete);
        await svc.DeleteAsync(row.Id, Leader().Build());

        Assert.Equal(0, Count(ctx));
    }

    [Fact]
    public async Task DeleteAsync_NeverReachesTheLeadersOwnReport()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "lead", Rank.SpecialAgentInCharge);
        SeedAgent(ctx, "other", Rank.SpecialAgentInCharge);
        var own = SeedUse(ctx, "lead");
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.DeleteAsync(own.Id, Leader("lead").Build()));
        // the button is gone for the owner and there for a colleague of the leadership
        Assert.False(Assert.Single(await svc.GetListAsync(new FirearmUseFilter(), Leader("lead").Build())).MayDelete);
        Assert.True(Assert.Single(await svc.GetListAsync(new FirearmUseFilter(), Leader("other").Build())).MayDelete);
        Assert.Equal(1, Count(ctx));

        // an admin is no exception to the rule for their own report
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.DeleteAsync(own.Id, Leader("lead").AsAdmin().Build()));
        await svc.DeleteAsync(own.Id, Leader("other").Build());
        Assert.Equal(0, Count(ctx));
    }

    [Fact]
    public async Task DeleteAsync_RefusesWhoIsNotLeadershipWithWriteAccess()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "a1", Rank.Trainee);
        SeedAgent(ctx, "a2", Rank.Trainee);
        var row = SeedUse(ctx, "a1");
        var (svc, _) = Build(ctx);
        ClaimsPrincipal[] refused =
        [
            Trainee("a2").Build(),
            // the report's own author is no leadership either
            Trainee("a1").Build(),
            // read-only supervision reads the list but never deletes
            OnlyReader(),
            Leader("demo").AsDemo().Build(),
            Leader("p1").AsPartner(PartnerAgency.DoJ, PartnerRank.Chief).Build(),
        ];

        foreach (var actor in refused)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.DeleteAsync(row.Id, actor));
        }

        Assert.Equal(1, Count(ctx));
        Assert.False(Assert.Single(await svc.GetListAsync(new FirearmUseFilter(), OnlyReader())).MayDelete);
    }

    [Fact]
    public async Task DeleteAsync_AnUnknownReportIsNotFound()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DeleteAsync("gibtsnicht", Leader().Build()));
    }

    // ---------- trash ----------

    [Fact]
    public async Task GetTrashAsync_ReturnsOnlyDeletedReports_WithTheirAgent()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "a1", Rank.Trainee);
        SeedUse(ctx, "a1");
        var gone = SeedUse(ctx, "a1", configure: r => { r.IsDeleted = true; r.DeletedAt = DateTime.UtcNow; });
        var (svc, _) = Build(ctx);

        var trash = await svc.GetTrashAsync();

        var item = Assert.Single(trash);
        Assert.Equal(gone.Id, item.Id);
        Assert.Equal("Codename-a1", item.Agent?.Codename);
    }

    [Fact]
    public async Task RestoreAsync_LeadershipBringsTheReportBack_NobodyElseDoes()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "a1", Rank.Trainee);
        var gone = SeedUse(ctx, "a1", configure: r =>
        {
            r.IsDeleted = true;
            r.DeletedAt = DateTime.UtcNow;
            r.DeletedById = "lead";
        });
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RestoreAsync(gone.Id, Trainee("a1").Build()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RestoreAsync(gone.Id, OnlyReader()));
        Assert.Single(await svc.GetTrashAsync());

        await svc.RestoreAsync(gone.Id, Leader().Build());

        Assert.Empty(await svc.GetTrashAsync());
        using var db = ctx.NewContext();
        var restored = Assert.Single(db.FirearmUses);
        Assert.False(restored.IsDeleted);
        Assert.Null(restored.DeletedAt);
        Assert.Null(restored.DeletedById);
    }

    [Fact]
    public async Task RestoreAsync_AnUnknownReportIsNotFound()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RestoreAsync("gibtsnicht", Leader().Build()));
    }
}
