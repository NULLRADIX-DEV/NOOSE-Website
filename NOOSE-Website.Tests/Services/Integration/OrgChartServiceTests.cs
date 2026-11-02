using System.Security.Claims;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="OrgChartService"/> against in-memory SQLite.</summary>
public sealed class OrgChartServiceTests
{
    // Rank >= SpecialAgentInCharge(4) => IsLeadership() => MayAllTaskforcesSee() == true.
    private static ClaimsPrincipal Leader(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).Build();

    // Junior agent: not leadership, not admin => sees only assigned taskforces.
    private static ClaimsPrincipal Plain(string id)
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).Build();

    private static OrgChartService NewService(SqliteTestContext ctx) => new(ctx.Factory);

    private static Agent Ag(string id, Rank? rank = Rank.SpecialAgent, AgentStatus status = AgentStatus.Active,
        string? codename = null, bool tru = false, bool hrb = false, bool teamLead = false)
        => Seed.Agent(id, rank ?? Rank.SpecialAgent, status, a =>
        {
            a.Rank = rank;
            if (codename != null) a.Codename = codename;
            a.IsTRU = tru;
            a.IsHRB = hrb;
            a.IsTeamLead = teamLead;
        });

    private static Taskforce Tf(string id, string name = "Alpha TF", TaskforceStatus status = TaskforceStatus.Approved)
        => new()
        {
            Id = id,
            CaseNumber = $"FIB-TF-2026-{id}",
            Name = name,
            Status = status,
            Scope = TaskforceScope.InternalAgency,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

    private static TaskforceAgent Alloc(string taskforceId, string agentId, TaskforceRole role = TaskforceRole.Member)
        => new()
        {
            Id = Guid.NewGuid().ToString(),
            TaskforceId = taskforceId,
            AgentId = agentId,
            Role = role,
        };

    // ---- roster / ranks ----

    [Fact]
    public async Task GetAsync_GroupsActiveAgentsByRank_DescendingOrder()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Ag("a1", Rank.Trainee, codename: "Alpha"));
            db.Users.Add(Ag("a2", Rank.Director, codename: "Bravo"));
            db.Users.Add(Ag("a3", Rank.SpecialAgent, codename: "Charlie"));
            db.SaveChanges();
        }

        var data = await NewService(ctx).GetAsync(Leader());

        Assert.Equal(
            new[] { Rank.Director, Rank.SpecialAgent, Rank.Trainee },
            data.Ranks.Select(g => g.Rank).ToArray());
        Assert.Equal("Bravo", data.Ranks[0].Agents.Single().Codename);
    }

    [Fact]
    public async Task GetAsync_OrdersAgentsByCodenameWithinRank()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Ag("a1", Rank.SpecialAgent, codename: "Zulu"));
            db.Users.Add(Ag("a2", Rank.SpecialAgent, codename: "Alpha"));
            db.SaveChanges();
        }

        var data = await NewService(ctx).GetAsync(Leader());

        var group = data.Ranks.Single(g => g.Rank == Rank.SpecialAgent);
        Assert.Equal(new[] { "Alpha", "Zulu" }, group.Agents.Select(a => a.Codename).ToArray());
    }

    [Fact]
    public async Task GetAsync_ExcludesNonActiveAgents()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Ag("active", Rank.SpecialAgent, AgentStatus.Active));
            db.Users.Add(Ag("pending", Rank.SpecialAgent, AgentStatus.Pending));
            db.Users.Add(Ag("blocked", Rank.SpecialAgent, AgentStatus.Blocked));
            db.Users.Add(Ag("applicant", Rank.SpecialAgent, AgentStatus.Applicant));
            db.SaveChanges();
        }

        var data = await NewService(ctx).GetAsync(Leader());

        var ids = data.Ranks.SelectMany(g => g.Agents).Select(a => a.Id).ToList();
        Assert.Equal(new[] { "active" }, ids.ToArray());
    }

    [Fact]
    public async Task GetAsync_ExcludesTeamLeads()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Ag("normal", Rank.SpecialAgent));
            db.Users.Add(Ag("teamlead", Rank.SpecialAgent, teamLead: true));
            db.SaveChanges();
        }

        var data = await NewService(ctx).GetAsync(Leader());

        var ids = data.Ranks.SelectMany(g => g.Agents).Select(a => a.Id).ToList();
        Assert.Contains("normal", ids);
        Assert.DoesNotContain("teamlead", ids);
    }

    [Fact]
    public async Task GetAsync_ExcludesPartnersFromRoster()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Ag("internal", Rank.SpecialAgent));
            // a partner carrying a NOOSE rank is still not in-house personnel
            db.Users.Add(Seed.Agent("partner", Rank.SpecialAgent,
                configure: a => a.PartnerAgency = PartnerAgency.LSPD));
            db.SaveChanges();
        }

        var data = await NewService(ctx).GetAsync(Leader());

        var ids = data.Ranks.SelectMany(g => g.Agents).Select(a => a.Id).ToList();
        Assert.Equal(new[] { "internal" }, ids.ToArray());
    }

    [Fact]
    public async Task GetAsync_ExcludesRanklessAgents()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Ag("ranked", Rank.SpecialAgent));
            db.Users.Add(Ag("rankless", rank: null));
            db.SaveChanges();
        }

        var data = await NewService(ctx).GetAsync(Leader());

        var ids = data.Ranks.SelectMany(g => g.Agents).Select(a => a.Id).ToList();
        Assert.Equal(new[] { "ranked" }, ids.ToArray());
    }

    [Fact]
    public async Task GetAsync_ProjectsTruAndHrbCrossSections()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Ag("tru", Rank.SpecialAgent, tru: true));
            db.Users.Add(Ag("hrb", Rank.SpecialAgent, hrb: true));
            db.Users.Add(Ag("both", Rank.SpecialAgent, tru: true, hrb: true));
            db.Users.Add(Ag("plain", Rank.SpecialAgent));
            db.SaveChanges();
        }

        var data = await NewService(ctx).GetAsync(Leader());

        Assert.Equal(new[] { "both", "tru" }, data.Tru.Select(a => a.Id).OrderBy(x => x).ToArray());
        Assert.Equal(new[] { "both", "hrb" }, data.Hrb.Select(a => a.Id).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task GetAsync_EmptyDatabase_ReturnsEmptyData()
    {
        using var ctx = new SqliteTestContext();

        var data = await NewService(ctx).GetAsync(Leader());

        Assert.Empty(data.Ranks);
        Assert.Empty(data.Tru);
        Assert.Empty(data.Hrb);
        Assert.Empty(data.Taskforces);
    }

    // ---- taskforces / visibility ----

    [Fact]
    public async Task GetAsync_Leader_SeesAllApprovedTaskforces()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1", "Beta"));
            db.Taskforces.Add(Tf("t2", "Alpha"));
            db.SaveChanges();
        }

        var data = await NewService(ctx).GetAsync(Leader());

        // ordered by Name
        Assert.Equal(new[] { "Alpha", "Beta" }, data.Taskforces.Select(s => s.Taskforce.Name).ToArray());
    }

    [Fact]
    public async Task GetAsync_ExcludesNonApprovedTaskforces()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1", "Approved", TaskforceStatus.Approved));
            db.Taskforces.Add(Tf("t2", "Requested", TaskforceStatus.Requested));
            db.Taskforces.Add(Tf("t3", "Rejected", TaskforceStatus.Rejected));
            db.Taskforces.Add(Tf("t4", "Resolved", TaskforceStatus.Resolved));
            db.SaveChanges();
        }

        var data = await NewService(ctx).GetAsync(Leader());

        Assert.Equal(new[] { "t1" }, data.Taskforces.Select(s => s.Taskforce.Id).ToArray());
    }

    [Fact]
    public async Task GetAsync_NonMember_SeesNoTaskforces()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Ag("viewer", Rank.Trainee));
            db.Taskforces.Add(Tf("t1"));
            db.SaveChanges();
        }

        var data = await NewService(ctx).GetAsync(Plain("viewer"));

        Assert.Empty(data.Taskforces);
    }

    [Fact]
    public async Task GetAsync_Member_SeesOnlyAssignedTaskforces()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Ag("viewer", Rank.Trainee));
            db.Taskforces.Add(Tf("t1", "Assigned"));
            db.Taskforces.Add(Tf("t2", "Unassigned"));
            db.TaskforceAgents.Add(Alloc("t1", "viewer"));
            db.SaveChanges();
        }

        var data = await NewService(ctx).GetAsync(Plain("viewer"));

        Assert.Equal(new[] { "t1" }, data.Taskforces.Select(s => s.Taskforce.Id).ToArray());
    }

    [Fact]
    public async Task GetAsync_TaskforceMembers_LeadsFirstThenByRoleThenCodename()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Ag("m1", Rank.SpecialAgent, codename: "Zulu"));
            db.Users.Add(Ag("m2", Rank.SpecialAgent, codename: "Alpha"));
            db.Users.Add(Ag("l1", Rank.SpecialAgent, codename: "Yankee"));
            db.Users.Add(Ag("l2", Rank.SpecialAgent, codename: "Bravo"));
            db.Taskforces.Add(Tf("t1"));
            db.TaskforceAgents.Add(Alloc("t1", "m1", TaskforceRole.Member));
            db.TaskforceAgents.Add(Alloc("t1", "m2", TaskforceRole.Member));
            db.TaskforceAgents.Add(Alloc("t1", "l1", TaskforceRole.LeadInvestigator));
            db.TaskforceAgents.Add(Alloc("t1", "l2", TaskforceRole.CidLead));
            db.SaveChanges();
        }

        var data = await NewService(ctx).GetAsync(Leader());

        var members = data.Taskforces.Single().Members;
        // leads (non-Member) first, ordered by role enum, then members by codename
        Assert.Equal(
            new[] { "Yankee", "Bravo", "Alpha", "Zulu" },
            members.Select(m => m.Agent!.Codename).ToArray());
    }

    [Fact]
    public async Task GetAsync_ExcludesTeamLeadMembersFromStaffing()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Ag("normal", Rank.SpecialAgent, codename: "Normal"));
            db.Users.Add(Ag("teamlead", Rank.SpecialAgent, codename: "Lead", teamLead: true));
            db.Taskforces.Add(Tf("t1"));
            db.TaskforceAgents.Add(Alloc("t1", "normal"));
            db.TaskforceAgents.Add(Alloc("t1", "teamlead"));
            db.SaveChanges();
        }

        var data = await NewService(ctx).GetAsync(Leader());

        var members = data.Taskforces.Single().Members;
        Assert.Equal(new[] { "normal" }, members.Select(m => m.AgentId).ToArray());
    }

    [Fact]
    public async Task GetAsync_ExcludesTerminatedAndPartnerMembersFromStaffing()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Ag("normal", Rank.SpecialAgent, codename: "Normal"));
            db.Users.Add(Ag("gone", Rank.SpecialAgent, AgentStatus.Terminated, codename: "Weg"));
            db.Users.Add(Seed.Agent("partner", Rank.SpecialAgent, configure: a =>
            {
                a.Codename = "Extern";
                a.PartnerAgency = PartnerAgency.LSPD;
            }));
            db.Taskforces.Add(Tf("t1"));
            db.TaskforceAgents.Add(Alloc("t1", "normal"));
            db.TaskforceAgents.Add(Alloc("t1", "gone"));
            db.TaskforceAgents.Add(Alloc("t1", "partner"));
            db.SaveChanges();
        }

        var data = await NewService(ctx).GetAsync(Leader());

        var members = data.Taskforces.Single().Members;
        Assert.Equal(new[] { "normal" }, members.Select(m => m.AgentId).ToArray());
    }
}
