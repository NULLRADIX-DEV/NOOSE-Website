using System.Security.Claims;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="AuditLogQueryService"/> against in-memory SQLite.</summary>
public sealed class AuditLogQueryServiceTests
{
    private static readonly DateTime T1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T3 = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static AuditLogQueryService NewService(SqliteTestContext ctx)
        => new(ctx.Factory);

    // Director => IsLeadership => passes RequireLeadership.
    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    // Trainee: not leadership, not admin => fails RequireLeadership.
    private static ClaimsPrincipal NonLeader()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).Build();

    private static AuditLog Change(DateTime ts, string type = "Person", string entityId = "p1",
        AuditAction action = AuditAction.Modified, string? agentId = null, string? agentName = null,
        string? changesJson = null)
        => new()
        {
            Timestamp = ts,
            EntityType = type,
            EntityId = entityId,
            Action = action,
            AgentId = agentId,
            AgentName = agentName,
            ChangesJson = changesJson,
        };

    private static AccessLog Access(DateTime ts, string type = "Person", string entityId = "p1",
        string? agentId = null, string? agentName = null)
        => new()
        {
            Timestamp = ts,
            EntityType = type,
            EntityId = entityId,
            AgentId = agentId,
            AgentName = agentName,
        };

    // ---- QueryChangesAsync -------------------------------------------------

    [Fact]
    public async Task QueryChangesAsync_ReturnsRowsNewestFirst_WithTotal()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.AuditLogs.AddRange(Change(T1), Change(T2), Change(T3));
            db.SaveChanges();
        }

        var result = await NewService(ctx).QueryChangesAsync(new AuditLogFilter(), Leader());

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(3, result.Rows.Count);
        Assert.False(result.Capped);
        Assert.Equal(T3, result.Rows[0].TimestampUtc);
        Assert.Equal(T2, result.Rows[1].TimestampUtc);
        Assert.Equal(T1, result.Rows[2].TimestampUtc);
    }

    [Fact]
    public async Task QueryChangesAsync_FiltersByAgentId()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.AuditLogs.AddRange(
                Change(T1, agentId: "a1"),
                Change(T2, agentId: "a2"),
                Change(T3, agentId: "a1"));
            db.SaveChanges();
        }

        var result = await NewService(ctx).QueryChangesAsync(new AuditLogFilter { AgentId = "a1" }, Leader());

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public async Task QueryChangesAsync_FiltersByEntityType()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.AuditLogs.AddRange(
                Change(T1, type: "Person"),
                Change(T2, type: "Faction"),
                Change(T3, type: "Person"));
            db.SaveChanges();
        }

        var result = await NewService(ctx).QueryChangesAsync(new AuditLogFilter { EntityType = "Faction" }, Leader());

        var row = Assert.Single(result.Rows);
        Assert.Equal("Faction", row.EntityType);
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task QueryChangesAsync_FiltersByAction()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.AuditLogs.AddRange(
                Change(T1, action: AuditAction.Created),
                Change(T2, action: AuditAction.Deleted),
                Change(T3, action: AuditAction.Created));
            db.SaveChanges();
        }

        var result = await NewService(ctx).QueryChangesAsync(new AuditLogFilter { Action = AuditAction.Deleted }, Leader());

        var row = Assert.Single(result.Rows);
        Assert.Equal(AuditAction.Deleted, row.Action);
    }

    [Fact]
    public async Task QueryChangesAsync_FiltersByEntityId()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.AuditLogs.AddRange(
                Change(T1, entityId: "p1"),
                Change(T2, entityId: "p2"),
                Change(T3, entityId: "p1"));
            db.SaveChanges();
        }

        var result = await NewService(ctx).QueryChangesAsync(new AuditLogFilter { EntityId = "p2" }, Leader());

        var row = Assert.Single(result.Rows);
        Assert.Equal("p2", row.EntityId);
    }

    [Fact]
    public async Task QueryChangesAsync_FiltersByDateRange()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.AuditLogs.AddRange(Change(T1), Change(T2), Change(T3));
            db.SaveChanges();
        }

        // FromUtc inclusive (>= T2), ToUtc exclusive (< T3) => only T2 survives.
        var result = await NewService(ctx).QueryChangesAsync(
            new AuditLogFilter { FromUtc = T2, ToUtc = T3 }, Leader());

        var row = Assert.Single(result.Rows);
        Assert.Equal(T2, row.TimestampUtc);
    }

    [Fact]
    public async Task QueryChangesAsync_CapsRows_WhenOverMax()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            var rows = new List<AuditLog>(AuditLogFilter.MaxRows + 1);
            for (var i = 0; i < AuditLogFilter.MaxRows + 1; i++)
            {
                rows.Add(Change(T1.AddMinutes(i)));
            }
            db.AuditLogs.AddRange(rows);
            db.SaveChanges();
        }

        var result = await NewService(ctx).QueryChangesAsync(new AuditLogFilter(), Leader());

        Assert.Equal(AuditLogFilter.MaxRows + 1, result.TotalCount);
        Assert.Equal(AuditLogFilter.MaxRows, result.Rows.Count);
        Assert.True(result.Capped);
    }

    [Fact]
    public async Task QueryChangesAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => NewService(ctx).QueryChangesAsync(new AuditLogFilter(), NonLeader()));
    }

    // ---- QueryAccessAsync --------------------------------------------------

    [Fact]
    public async Task QueryAccessAsync_ReturnsRowsNewestFirst_WithTotal()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.AccessLogs.AddRange(Access(T1), Access(T2), Access(T3));
            db.SaveChanges();
        }

        var result = await NewService(ctx).QueryAccessAsync(new AuditLogFilter(), Leader());

        Assert.Equal(3, result.TotalCount);
        Assert.Equal(3, result.Rows.Count);
        Assert.False(result.Capped);
        Assert.Equal(T3, result.Rows[0].TimestampUtc);
        Assert.Equal(T1, result.Rows[2].TimestampUtc);
    }

    [Fact]
    public async Task QueryAccessAsync_FiltersByEntityTypeAndAgent()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.AccessLogs.AddRange(
                Access(T1, type: "Person", agentId: "a1"),
                Access(T2, type: "Faction", agentId: "a1"),
                Access(T3, type: "Person", agentId: "a2"));
            db.SaveChanges();
        }

        var result = await NewService(ctx).QueryAccessAsync(
            new AuditLogFilter { EntityType = "Person", AgentId = "a1" }, Leader());

        var row = Assert.Single(result.Rows);
        Assert.Equal("Person", row.EntityType);
        Assert.Equal(T1, row.TimestampUtc);
    }

    [Fact]
    public async Task QueryAccessAsync_FiltersByDateRange()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.AccessLogs.AddRange(Access(T1), Access(T2), Access(T3));
            db.SaveChanges();
        }

        var result = await NewService(ctx).QueryAccessAsync(
            new AuditLogFilter { FromUtc = T2, ToUtc = T3 }, Leader());

        var row = Assert.Single(result.Rows);
        Assert.Equal(T2, row.TimestampUtc);
    }

    [Fact]
    public async Task QueryAccessAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => NewService(ctx).QueryAccessAsync(new AuditLogFilter(), NonLeader()));
    }

    [Fact]
    public async Task BothQueries_FilterByTeamLeadAndPartnerIds()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("supervisor", configure: a => a.IsTeamLead = true));
            db.Users.Add(Seed.Agent("extern", configure: a => a.PartnerAgency = PartnerAgency.LSPD));
            db.AuditLogs.AddRange(
                Change(T1, agentId: "supervisor"),
                Change(T2, agentId: "extern"),
                Change(T3, agentId: "internal"));
            db.AccessLogs.AddRange(
                Access(T1, agentId: "supervisor"),
                Access(T2, agentId: "extern"),
                Access(T3, agentId: "internal"));
            db.SaveChanges();
        }

        // the option list now offers these accounts, so their ids must actually hit rows
        foreach (var id in new[] { "supervisor", "extern" })
        {
            var changes = await NewService(ctx).QueryChangesAsync(new AuditLogFilter { AgentId = id }, Leader());
            var accesses = await NewService(ctx).QueryAccessAsync(new AuditLogFilter { AgentId = id }, Leader());
            Assert.Single(changes.Rows);
            Assert.Single(accesses.Rows);
        }
    }

    // ---- GetFilterOptionsAsync ---------------------------------------------

    [Fact]
    public async Task GetFilterOptionsAsync_ReturnsAgentsSortedAndUnionedTypes()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("u1", configure: a => a.Codename = "Zulu"));
            db.Users.Add(Seed.Agent("u2", configure: a => a.Codename = "Alpha"));
            db.AuditLogs.AddRange(Change(T1, type: "Person"), Change(T2, type: "Faction"));
            db.AccessLogs.AddRange(Access(T1, type: "Case"), Access(T2, type: "Person"));
            db.SaveChanges();
        }

        var result = await NewService(ctx).GetFilterOptionsAsync(Leader());

        // Agents ordered by codename.
        Assert.Equal(new[] { "Alpha", "Zulu" }, result.Agents.Select(a => a.Codename).ToArray());
        // Union of change- and access-log types, ordered alphabetically.
        Assert.Equal(new[] { "Case", "Faction", "Person" }, result.EntityTypes.ToArray());
    }

    [Fact]
    public async Task GetFilterOptionsAsync_SkipsCodenamelessAccounts_ButListsTeamLeadsWithMarker()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("u1", configure: a => a.Codename = "Zulu"));
            db.Users.Add(Seed.Agent("u2", configure: a => a.Codename = "Alpha"));
            db.Users.Add(Seed.Agent("pending", status: AgentStatus.Pending,
                configure: a => a.Codename = string.Empty));
            db.Users.Add(Seed.Agent("applicant", status: AgentStatus.Applicant,
                configure: a => a.Codename = string.Empty));
            db.Users.Add(Seed.Agent("supervisor", configure: a =>
            {
                a.Codename = "Aufsicht";
                a.IsTeamLead = true;
            }));
            // supervision stays listed even with the admin flag on top
            db.Users.Add(Seed.Agent("chief", configure: a =>
            {
                a.Codename = "Chef";
                a.IsTeamLead = true;
                a.IsAdmin = true;
            }));
            db.SaveChanges();
        }

        var result = await NewService(ctx).GetFilterOptionsAsync(Leader());

        Assert.Equal(new[] { "Alpha", "Aufsicht", "Chef", "Zulu" }, result.Agents.Select(a => a.Codename).ToArray());
        Assert.Equal("Teamleitung", result.Agents.Single(a => a.Codename == "Aufsicht").Marker);
        Assert.Equal("Teamleitung", result.Agents.Single(a => a.Codename == "Chef").Marker);
        Assert.Null(result.Agents.Single(a => a.Codename == "Alpha").Marker);
    }

    [Fact]
    public async Task GetFilterOptionsAsync_ListsPartnerAccountsWithAgencyMarker()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("u1", configure: a => a.Codename = "Intern"));
            db.Users.Add(Seed.Agent("p1", configure: a =>
            {
                a.Codename = "Extern";
                a.PartnerAgency = PartnerAgency.LSPD;
            }));
            db.Users.Add(Seed.Agent("p2", configure: a =>
            {
                a.Codename = "Beides";
                a.PartnerAgency = PartnerAgency.DoJ;
                a.IsTeamLead = true;
            }));
            db.SaveChanges();
        }

        var result = await NewService(ctx).GetFilterOptionsAsync(Leader());

        Assert.Equal(new[] { "Beides", "Extern", "Intern" }, result.Agents.Select(a => a.Codename).ToArray());
        Assert.Equal("LSPD", result.Agents.Single(a => a.Codename == "Extern").Marker);
        Assert.Equal("Teamleitung · DoJ", result.Agents.Single(a => a.Codename == "Beides").Marker);
        Assert.Null(result.Agents.Single(a => a.Codename == "Intern").Marker);
    }

    [Fact]
    public async Task GetFilterOptionsAsync_KeepsTerminatedAgents()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("gone", status: AgentStatus.Terminated,
                configure: a => a.Codename = "Ehemalig"));
            db.SaveChanges();
        }

        var result = await NewService(ctx).GetFilterOptionsAsync(Leader());

        Assert.Equal("Ehemalig", Assert.Single(result.Agents).Codename);
    }

    [Fact]
    public async Task GetFilterOptionsAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => NewService(ctx).GetFilterOptionsAsync(NonLeader()));
    }
}
