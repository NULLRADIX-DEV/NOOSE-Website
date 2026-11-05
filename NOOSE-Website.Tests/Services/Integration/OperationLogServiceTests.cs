using System.Security.Claims;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Infrastructure.Chat;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="OperationLogService"/> against in-memory SQLite.</summary>
public sealed class OperationLogServiceTests
{
    private static readonly DateTime T0 = new(2026, 11, 5, 18, 0, 0, DateTimeKind.Utc);

    private static OperationLogService Build(SqliteTestContext ctx)
        => new(ctx.Factory, new OperationLogBroadcaster(), Substitute.For<INotificationService>());

    private static ClaimsPrincipal Leader(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).WithCodename("Falcon").Build();

    private static ClaimsPrincipal Junior(string id = "junior")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).WithCodename("Rookie").Build();

    private static ClaimsPrincipal Hrt(string id = "hrt")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.SpecialAgent).WithCodename("Breach").AsTru().Build();

    private static ClaimsPrincipal Partner(string id = "partner1")
        => ClaimsPrincipalBuilder.Agent(id).AsPartner(PartnerAgency.LSPD, PartnerRank.Member).Build();

    private static ClaimsPrincipal Supervision(string id = "tl")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).AsTeamLead().Build();

    private static void Seed(SqliteTestContext ctx, string id, OperationStatus status, string? assigned = null,
        bool classified = false, string? expiry = null)
    {
        using var db = ctx.NewContext();
        db.Operations.Add(new Operation
        {
            Id = id,
            CaseNumber = "FIB-OP-2026-" + id,
            Title = "Operation " + id,
            Status = status,
            IsClassified = classified,
            Expiry = expiry,
            CreatedAt = T0,
        });
        if (assigned is not null)
        {
            db.OperationAgents.Add(new OperationAgent { OperationId = id, AgentId = assigned, CreatedAt = T0 });
        }
        db.SaveChanges();
    }

    private static OperationLogEntry Line(string operationId, string text, DateTime at, OperationLogKind kind = OperationLogKind.Situation,
        string? location = null, bool addendum = false, string? by = "someone")
        => new()
        {
            OperationId = operationId,
            Kind = kind,
            Text = text,
            Location = location,
            IsAddendum = addendum,
            AuthorName = "Falcon",
            CreatedAt = at,
            CreatedById = by,
        };

    // ---------- rules ----------

    [Fact]
    public void MayWrite_LeadershipHrtAndAssigned_OnlyInternalWriters()
    {
        Assert.True(OperationLogRules.MayWrite(Leader(), isAssigned: false));
        Assert.True(OperationLogRules.MayWrite(Hrt(), isAssigned: false));
        Assert.True(OperationLogRules.MayWrite(Junior(), isAssigned: true));
        Assert.False(OperationLogRules.MayWrite(Junior(), isAssigned: false));
        Assert.False(OperationLogRules.MayWrite(Partner(), isAssigned: true));
        Assert.False(OperationLogRules.MayWrite(Supervision(), isAssigned: true));
    }

    [Fact]
    public void ToExpiryText_FormatsLinesOldestFirst()
    {
        var text = OperationLogRules.ToExpiryText(new[]
        {
            Line("o", "Zugriff erfolgt", T0.AddMinutes(10), OperationLogKind.Result, addendum: true),
            Line("o", "Ziel betritt Lagerhalle", T0, OperationLogKind.Situation, location: "Hafen"),
        }, TimeZoneInfo.Utc);

        Assert.Equal(
            "05.11. 18:00 · Lagemeldung · Hafen · Falcon: Ziel betritt Lagerhalle\n"
            + "05.11. 18:10 · Ergebnis (Nachtrag) · Falcon: Zugriff erfolgt",
            text);
    }

    [Fact]
    public void Merge_AppendsBelowBlankLine_OrReplaces()
    {
        Assert.Equal("alt\n\nneu", OperationLogRules.Merge("alt\n", "neu", append: true));
        Assert.Equal("neu", OperationLogRules.Merge("alt", "neu", append: false));
        Assert.Equal("neu", OperationLogRules.Merge(null, "neu", append: true));
    }

    // ---------- reading ----------

    [Fact]
    public async Task GetEntriesAsync_Chronological_AndNeverForPartners()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx, "op1", OperationStatus.Running);
        using (var db = ctx.NewContext())
        {
            db.OperationLogEntries.AddRange(Line("op1", "zwei", T0.AddMinutes(5)), Line("op1", "eins", T0),
                Line("other", "fremd", T0));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var internalView = await svc.GetEntriesAsync("op1", ViewerScope.From(Junior()));
        var partnerView = await svc.GetEntriesAsync("op1", ViewerScope.From(Partner()));

        Assert.Equal(new[] { "eins", "zwei" }, internalView.Select(e => e.Text).ToArray());
        Assert.Empty(partnerView);
    }

    [Fact]
    public async Task GetEntriesAsync_Empty_WhenOperationClassifiedForViewer()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx, "vs", OperationStatus.Running, classified: true);
        using (var db = ctx.NewContext())
        {
            db.OperationLogEntries.Add(Line("vs", "geheim", T0));
            db.SaveChanges();
        }

        var result = await Build(ctx).GetEntriesAsync("vs", ViewerScope.From(Junior()));

        Assert.Empty(result);
    }

    // ---------- writing ----------

    [Fact]
    public async Task AddAsync_AssignedAgent_WritesWhileRunning()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx, "op1", OperationStatus.Running, assigned: "junior");
        var svc = Build(ctx);

        var entry = await svc.AddAsync("op1", OperationLogKind.Request, "  Brauche Air-1  ", "Vinewood", Junior());

        Assert.Equal("Brauche Air-1", entry.Text);
        Assert.Equal(OperationLogKind.Request, entry.Kind);
        Assert.Equal("Vinewood", entry.Location);
        Assert.False(entry.IsAddendum);
        Assert.Equal("Rookie", entry.AuthorName);
        Assert.True((await svc.GetAccessAsync("op1", Junior())).MayWrite);
    }

    [Fact]
    public async Task AddAsync_UnassignedJunior_IsRefused()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx, "op1", OperationStatus.Running);
        var svc = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.AddAsync("op1", OperationLogKind.Situation, "Hallo", null, Junior()));
        Assert.False((await svc.GetAccessAsync("op1", Junior())).MayWrite);
    }

    [Fact]
    public async Task AddAsync_Hrt_WritesWithoutAssignment()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx, "op1", OperationStatus.Running);

        var entry = await Build(ctx).AddAsync("op1", OperationLogKind.Situation, "Team in Position", null, Hrt());

        Assert.Equal("Breach", entry.AuthorName);
    }

    [Fact]
    public async Task AddAsync_PlannedOperation_IsClosed()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx, "op1", OperationStatus.Planned);
        var svc = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AddAsync("op1", OperationLogKind.Situation, "zu früh", null, Leader()));
        Assert.False((await svc.GetAccessAsync("op1", Leader())).MayWrite);
    }

    [Theory]
    [InlineData(OperationStatus.Completed)]
    [InlineData(OperationStatus.Aborted)]
    public async Task AddAsync_EndedOperation_WritesAddendum(OperationStatus status)
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx, "op1", status);
        var svc = Build(ctx);

        var entry = await svc.AddAsync("op1", OperationLogKind.Result, "Nachgereicht", null, Leader());
        var access = await svc.GetAccessAsync("op1", Leader());

        Assert.True(entry.IsAddendum);
        Assert.True(access.MayWrite);
        Assert.True(access.IsAddendum);
    }

    [Fact]
    public async Task AddAsync_Partner_IsRefused()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx, "op1", OperationStatus.Running, assigned: "partner1");

        await Assert.ThrowsAnyAsync<Exception>(
            () => Build(ctx).AddAsync("op1", OperationLogKind.Situation, "Hallo", null, Partner()));
    }

    [Fact]
    public async Task AddAsync_EmptyText_IsRefused()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx, "op1", OperationStatus.Running);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(ctx).AddAsync("op1", OperationLogKind.Situation, "   ", null, Leader()));
    }

    // ---------- deleting ----------

    [Fact]
    public async Task DeleteAsync_OtherAgentsLine_IsRefused_LeadershipMay()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx, "op1", OperationStatus.Running, assigned: "junior");
        var line = Line("op1", "fremd", T0, by: "someone-else");
        using (var db = ctx.NewContext())
        {
            db.OperationLogEntries.Add(line);
            db.SaveChanges();
        }
        var svc = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.DeleteAsync(line.Id, Junior()));
        await svc.DeleteAsync(line.Id, Leader());

        Assert.Empty(await svc.GetEntriesAsync("op1", ViewerScope.From(Leader())));
    }

    // ---------- taking over into Ablauf ----------

    [Fact]
    public async Task ApplyToExpiryAsync_AppendsOrReplaces()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx, "op1", OperationStatus.Completed, expiry: "Vorher");
        using (var db = ctx.NewContext())
        {
            db.OperationLogEntries.Add(Line("op1", "Zugriff", T0, OperationLogKind.Result));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        await svc.ApplyToExpiryAsync("op1", append: true, Leader());
        string? appended;
        using (var db = ctx.NewContext())
        {
            appended = db.Operations.Single(o => o.Id == "op1").Expiry;
        }
        await svc.ApplyToExpiryAsync("op1", append: false, Leader());
        string? replaced;
        using (var db = ctx.NewContext())
        {
            replaced = db.Operations.Single(o => o.Id == "op1").Expiry;
        }

        Assert.StartsWith("Vorher\n\n", appended);
        Assert.EndsWith("Falcon: Zugriff", appended);
        Assert.DoesNotContain("Vorher", replaced);
        Assert.EndsWith("Falcon: Zugriff", replaced);
    }

    [Fact]
    public async Task ApplyToExpiryAsync_EmptyLog_IsRefused()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx, "op1", OperationStatus.Completed);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(ctx).ApplyToExpiryAsync("op1", append: false, Leader()));
    }

    [Fact]
    public async Task ApplyToExpiryAsync_TooLong_IsRefused_AndLeavesAblauf()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx, "op1", OperationStatus.Completed, expiry: new string('x', 3990));
        using (var db = ctx.NewContext())
        {
            db.OperationLogEntries.Add(Line("op1", "Zugriff erfolgt", T0));
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(ctx).ApplyToExpiryAsync("op1", append: true, Leader()));
        using var check = ctx.NewContext();
        Assert.Equal(3990, check.Operations.Single(o => o.Id == "op1").Expiry!.Length);
    }
}
