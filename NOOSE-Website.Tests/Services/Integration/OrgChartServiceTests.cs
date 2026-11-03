using System.Security.Claims;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.OrgChart;
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
        string? codename = null, bool tru = false, bool hrb = false, bool teamLead = false, Division? division = null)
        => Seed.Agent(id, rank ?? Rank.SpecialAgent, status, a =>
        {
            a.Rank = rank;
            if (codename != null) a.Codename = codename;
            a.Division = division;
            a.IsTRU = tru;
            a.IsHRB = hrb;
            a.IsTeamLead = teamLead;
        });

    /// <summary>Everyone charted, Board first, in column order.</summary>
    private static string[] ChartedIds(OrgChartData data) => data.Agents.Select(a => a.Id).ToArray();

    private static OrgChartColumn Div(OrgChartData data, Division division)
        => data.Divisions.Single(c => c.Division == division);

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

    // ---- roster / units ----

    [Fact]
    public async Task GetAsync_SeatsRanksInBoardAndHomeDivision_TiersHighestFirst()
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

        Assert.Equal(new[] { Rank.Director }, data.Board.Ranks.Select(g => g.Rank).ToArray());
        Assert.Equal("Bravo", data.Board.Ranks[0].Agents.Single().Codename);
        Assert.Equal(
            new[] { Rank.SpecialAgent, Rank.Trainee },
            Div(data, Division.Cid).Ranks.Select(g => g.Rank).ToArray());
    }

    [Fact]
    public async Task GetAsync_ShowsEveryRankOfTheEightLevelLadder_BoardFromAssistantDirectorUp()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            foreach (var rank in RankDisplay.All)
            {
                db.Users.Add(Ag($"r{(int)rank}", rank, codename: $"Agent {(int)rank}"));
            }
            db.SaveChanges();
        }

        var data = await NewService(ctx).GetAsync(Leader());

        Assert.Equal(
            new[] { Rank.Director, Rank.DeputyDirector, Rank.AssistantDirector },
            data.Board.Ranks.Select(g => g.Rank).ToArray());
        Assert.Equal(
            new[] { Rank.SectionChief, Rank.SpecialAgentInCharge, Rank.SeniorSpecialAgent, Rank.SpecialAgent, Rank.Trainee },
            Div(data, Division.Cid).Ranks.Select(g => g.Rank).ToArray());
        Assert.All(data.Board.Ranks.Concat(Div(data, Division.Cid).Ranks), g => Assert.Single(g.Agents));
        Assert.Equal(8, ChartedIds(data).Length);
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

        var group = Div(data, Division.Cid).Ranks.Single(g => g.Rank == Rank.SpecialAgent);
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

        Assert.Equal(new[] { "active" }, ChartedIds(data));
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

        Assert.Contains("normal", ChartedIds(data));
        Assert.DoesNotContain("teamlead", ChartedIds(data));
    }

    [Fact]
    public async Task GetAsync_ExcludesPartnersFromRoster()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Ag("internal", Rank.SpecialAgent));
            // a partner carrying a FIB rank is still not in-house personnel
            db.Users.Add(Seed.Agent("partner", Rank.SpecialAgent,
                configure: a => a.PartnerAgency = PartnerAgency.LSPD));
            db.SaveChanges();
        }

        var data = await NewService(ctx).GetAsync(Leader());

        Assert.Equal(new[] { "internal" }, ChartedIds(data));
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

        Assert.Equal(new[] { "ranked" }, ChartedIds(data));
    }

    [Fact]
    public async Task GetAsync_SortsAgentsIntoTheirHomeDivision_ColumnsInOrderWithSeatLimits()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Ag("cid", Rank.SpecialAgent, division: Division.Cid));
            db.Users.Add(Ag("ctd", Rank.SpecialAgent, division: Division.Ctd));
            db.Users.Add(Ag("hrt1", Rank.SpecialAgent, division: Division.Hrt));
            db.Users.Add(Ag("hrt2", Rank.SeniorSpecialAgent, division: Division.Hrt));
            db.SaveChanges();
        }

        var data = await NewService(ctx).GetAsync(Leader());

        Assert.Equal(new Division?[] { Division.Cid, Division.Ctd, Division.Hrt }, data.Divisions.Select(c => c.Division).ToArray());
        Assert.Equal(new[] { 1, 1, 2 }, data.Divisions.Select(c => c.Count).ToArray());
        Assert.Equal(new[] { 21, 6, 9 }, data.Divisions.Select(c => c.Limit).ToArray());
        Assert.Equal(new[] { "hrt2", "hrt1" }, Div(data, Division.Hrt).Agents.Select(a => a.Id).ToArray());
        Assert.Equal("ctd", Div(data, Division.Ctd).Agents.Single().Id);
    }

    [Fact]
    public async Task GetAsync_ReadsALegacyTruRowWithoutHomeAsHrt()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            // row from before divisions
            db.Users.Add(Ag("old-tru", Rank.SpecialAgent, tru: true));
            db.Users.Add(Ag("old-plain", Rank.SpecialAgent));
            db.SaveChanges();
        }

        var data = await NewService(ctx).GetAsync(Leader());

        Assert.Equal(new[] { "old-tru" }, Div(data, Division.Hrt).Agents.Select(a => a.Id).ToArray());
        Assert.Equal(new[] { "old-plain" }, Div(data, Division.Cid).Agents.Select(a => a.Id).ToArray());
    }

    [Fact]
    public async Task GetAsync_BoardMemberCountsOnlyInTheBoard_NotInTheHomeDivision()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            // home stays on the row
            db.Users.Add(Ag("director", Rank.Director, division: Division.Hrt));
            db.Users.Add(Ag("assistant", Rank.AssistantDirector, division: Division.Ctd));
            db.Users.Add(Ag("operator", Rank.SpecialAgent, division: Division.Hrt));
            db.SaveChanges();
        }

        var data = await NewService(ctx).GetAsync(Leader());

        Assert.True(data.Board.IsBoard);
        Assert.Equal(2, data.Board.Count);
        Assert.Equal(4, data.Board.Limit);
        Assert.Equal(1, Div(data, Division.Hrt).Count);
        Assert.Equal(0, Div(data, Division.Ctd).Count);
        Assert.Equal(new[] { "operator" }, Div(data, Division.Hrt).Agents.Select(a => a.Id).ToArray());
    }

    [Fact]
    public async Task GetAsync_KeepsEmptyUnitsAsColumns()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Ag("cid", Rank.SpecialAgent));
            db.SaveChanges();
        }

        var data = await NewService(ctx).GetAsync(Leader());

        Assert.True(data.HasAgents);
        Assert.Equal(3, data.Divisions.Count);
        var ctd = Div(data, Division.Ctd);
        Assert.Equal(0, ctd.Count);
        Assert.Empty(ctd.Ranks);
        Assert.Equal(0, data.Board.Count);
        Assert.Empty(data.Board.Ranks);
    }

    [Fact]
    public async Task GetAsync_FlagsAColumnOverItsSoftLimit()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            for (var i = 0; i < 7; i++)
            {
                db.Users.Add(Ag($"ctd{i}", Rank.SpecialAgent, division: Division.Ctd));
            }
            db.Users.Add(Ag("cid", Rank.SpecialAgent, division: Division.Cid));
            db.SaveChanges();
        }

        var data = await NewService(ctx).GetAsync(Leader());

        var ctd = Div(data, Division.Ctd);
        Assert.Equal(7, ctd.Count);
        Assert.Equal(6, ctd.Limit);
        Assert.True(ctd.OverLimit);
        Assert.False(Div(data, Division.Cid).OverLimit);
    }

    [Fact]
    public async Task GetAsync_ProjectsAdministrationAsCrossSection_AcrossUnitsAndBoard_HighestRankFirst()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Ag("in-cid", Rank.SpecialAgent, codename: "Zulu", hrb: true));
            db.Users.Add(Ag("in-hrt", Rank.SpecialAgent, codename: "Alpha", hrb: true, division: Division.Hrt));
            db.Users.Add(Ag("in-board", Rank.Director, codename: "Chief", hrb: true));
            db.Users.Add(Ag("plain", Rank.SpecialAgent));
            db.SaveChanges();
        }

        var data = await NewService(ctx).GetAsync(Leader());

        Assert.Equal(new[] { "in-board", "in-hrt", "in-cid" }, data.Administration.Select(a => a.Id).ToArray());
        // still once in their own column
        Assert.Equal(4, ChartedIds(data).Length);
        Assert.Contains("in-hrt", Div(data, Division.Hrt).Agents.Select(a => a.Id));
        Assert.Contains("in-board", data.Board.Agents.Select(a => a.Id));
    }

    [Fact]
    public async Task GetAsync_EmptyDatabase_ReturnsEmptyData()
    {
        using var ctx = new SqliteTestContext();

        var data = await NewService(ctx).GetAsync(Leader());

        Assert.False(data.HasAgents);
        Assert.Empty(data.Agents);
        Assert.Equal(0, data.Board.Count);
        Assert.Equal(3, data.Divisions.Count);
        Assert.All(data.Divisions, c => Assert.Equal(0, c.Count));
        Assert.Empty(data.Administration);
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
    public async Task GetAsync_ExcludesArchivedTaskforces()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1", "Active"));
            var archived = Tf("t2", "Archived");
            archived.IsArchived = true;
            db.Taskforces.Add(archived);
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
