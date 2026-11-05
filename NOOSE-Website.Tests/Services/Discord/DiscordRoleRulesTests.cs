using NOOSE_Website.Data.Entities;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Discord;

/// <summary>Desired and managed Discord roles per agent state, and the diff against what the member carries.</summary>
public class DiscordRoleRulesTests
{
    private static DiscordRoleMapping Map(Action<MapBuilder>? configure = null)
    {
        var b = new MapBuilder();
        configure?.Invoke(b);
        return b.Build();
    }

    private sealed class MapBuilder
    {
        public IReadOnlyList<string> Base = ["base"];
        public IReadOnlyList<string> Board = ["board"];
        public IReadOnlyList<string> Administration = ["hrb"];
        public IReadOnlyList<string> Admin = ["admin"];
        public IReadOnlyList<string> TeamLead = ["tl"];
        public IReadOnlyList<string> Resident = ["bewohner"];

        public Dictionary<Rank, IReadOnlyList<string>> ByRank = new()
        {
            [Rank.SpecialAgent] = ["r2"],
            [Rank.AssistantDirector] = ["r6"],
        };

        public Dictionary<Division, IReadOnlyList<string>> ByDivision = new()
        {
            [Division.Cid] = ["cid"],
            [Division.Ctd] = ["ctd"],
            [Division.Hrt] = ["hrt"],
        };

        public DiscordRoleMapping Build()
            => new(Base, ByRank, ByDivision, Board, Administration, Admin, TeamLead, Resident);
    }

    private static void AssertSet(IReadOnlySet<string> actual, params string[] expected)
        => Assert.True(actual.SetEquals(expected), $"expected {{{string.Join(",", expected)}}}, got {{{string.Join(",", actual)}}}");

    [Fact]
    public void Active_internal_agent_gets_base_rank_division_and_flags()
    {
        var map = Map();
        var agent = Seed.Agent("a", Rank.SpecialAgent, configure: a =>
        {
            a.Division = Division.Cid;
            a.IsHRB = true;
        });

        var target = DiscordRoleRules.Target(agent, map);

        Assert.NotNull(target);
        AssertSet(target.Desired, "base", "r2", "cid", "hrb");
        Assert.True(target.Managed.SetEquals(map.AllManaged));
    }

    [Fact]
    public void Active_admin_gets_admin_role()
    {
        var agent = Seed.Agent("a", Rank.SpecialAgent, configure: a =>
        {
            a.Division = Division.Ctd;
            a.IsAdmin = true;
        });

        var target = DiscordRoleRules.Target(agent, Map());

        Assert.NotNull(target);
        AssertSet(target.Desired, "base", "r2", "ctd", "admin");
    }

    [Fact]
    public void Board_member_gets_board_not_home_division()
    {
        var agent = Seed.Agent("a", Rank.AssistantDirector, configure: a => a.Division = Division.Hrt);

        var target = DiscordRoleRules.Target(agent, Map());

        Assert.NotNull(target);
        Assert.Contains("board", target.Desired);
        Assert.Contains("r6", target.Desired);
        Assert.DoesNotContain("hrt", target.Desired);
    }

    [Fact]
    public void Active_agent_loses_resident()
    {
        var map = Map();
        var agent = Seed.Agent("a", Rank.SpecialAgent, configure: a => a.Division = Division.Cid);
        var target = DiscordRoleRules.Target(agent, map)!;

        var diff = DiscordRoleRules.Diff(target, ["bewohner", "fremd"]);

        Assert.Equal(["bewohner"], diff.Remove);
        Assert.DoesNotContain("fremd", diff.Remove);
    }

    [Fact]
    public void Terminated_former_agent_gets_only_resident()
    {
        var map = Map();
        var agent = Seed.Agent("a", Rank.SpecialAgent, AgentStatus.Terminated, a => a.Division = Division.Cid);

        var target = DiscordRoleRules.Target(agent, map);

        Assert.NotNull(target);
        AssertSet(target.Desired, "bewohner");
        Assert.True(target.Managed.SetEquals(map.AllManaged));
    }

    [Fact]
    public void Blocked_former_agent_gets_only_resident()
    {
        var map = Map();
        var agent = Seed.Agent("a", Rank.SpecialAgent, AgentStatus.Blocked, a => a.Division = Division.Cid);

        var target = DiscordRoleRules.Target(agent, map);

        Assert.NotNull(target);
        AssertSet(target.Desired, "bewohner");
        Assert.True(target.Managed.SetEquals(map.AllManaged));
    }

    [Fact]
    public void Team_lead_gets_only_team_lead_roles_and_keeps_resident()
    {
        var agent = Seed.Agent("a", Rank.SpecialAgent, configure: a =>
        {
            a.Division = Division.Cid;
            a.IsTeamLead = true;
            a.IsAdmin = true;
        });

        var target = DiscordRoleRules.Target(agent, Map());

        Assert.NotNull(target);
        AssertSet(target.Desired, "tl");
        Assert.DoesNotContain("bewohner", target.Managed);
        Assert.Contains("base", target.Managed);
    }

    [Theory]
    [InlineData(AgentStatus.Terminated)]
    [InlineData(AgentStatus.Blocked)]
    public void Terminated_team_lead_gets_nothing_and_keeps_resident(AgentStatus status)
    {
        var agent = Seed.Agent("a", Rank.SpecialAgent, status, a =>
        {
            a.Division = Division.Cid;
            a.IsTeamLead = true;
        });

        var target = DiscordRoleRules.Target(agent, Map());

        Assert.NotNull(target);
        Assert.Empty(target.Desired);
        Assert.DoesNotContain("bewohner", target.Managed);
        Assert.Contains("tl", target.Managed);
    }

    [Theory]
    [InlineData(AgentStatus.Active)]
    [InlineData(AgentStatus.Terminated)]
    [InlineData(AgentStatus.Blocked)]
    public void Partner_loses_fib_roles_and_keeps_resident(AgentStatus status)
    {
        var agent = Seed.Agent("a", Rank.SpecialAgent, status, a => a.PartnerAgency = PartnerAgency.LSPD);

        var target = DiscordRoleRules.Target(agent, Map());

        Assert.NotNull(target);
        Assert.Empty(target.Desired);
        Assert.DoesNotContain("bewohner", target.Managed);
        Assert.Contains("base", target.Managed);
    }

    [Fact]
    public void Former_agent_turned_partner_is_a_partner()
    {
        var agent = Seed.Agent("a", Rank.SpecialAgent, AgentStatus.Active, a =>
        {
            a.PartnerAgency = PartnerAgency.LSPD;
            a.Rank = null;
        });

        var target = DiscordRoleRules.Target(agent, Map());

        Assert.NotNull(target);
        Assert.Empty(target.Desired);
        Assert.DoesNotContain("bewohner", target.Managed);
    }

    [Theory]
    [InlineData(AgentStatus.Pending)]
    [InlineData(AgentStatus.Applicant)]
    [InlineData(AgentStatus.Civilian)]
    [InlineData(AgentStatus.Blocked)]
    [InlineData(AgentStatus.Terminated)]
    public void Never_released_accounts_are_skipped(AgentStatus status)
    {
        var agent = Seed.Agent("a", Rank.SpecialAgent, status, a => a.Rank = null);

        Assert.Null(DiscordRoleRules.Target(agent, Map()));
    }

    [Theory]
    [InlineData(AgentStatus.Pending)]
    [InlineData(AgentStatus.Applicant)]
    [InlineData(AgentStatus.Civilian)]
    public void Not_yet_internal_status_is_skipped_even_with_a_rank(AgentStatus status)
    {
        var agent = Seed.Agent("a", Rank.SpecialAgent, status);

        Assert.Null(DiscordRoleRules.Target(agent, Map()));
    }

    [Fact]
    public void Demo_and_missing_discord_id_are_skipped()
    {
        var demo = Seed.Agent(DemoIdentity.AgentId, Rank.Director);
        var noDiscord = Seed.Agent("a", Rank.SpecialAgent, configure: a => a.DiscordId = "");

        Assert.Null(DiscordRoleRules.Target(demo, Map()));
        Assert.Null(DiscordRoleRules.Target(noDiscord, Map()));
    }

    [Fact]
    public void Shared_role_in_two_entries_is_desired_once()
    {
        var map = Map(b =>
        {
            b.ByRank = new() { [Rank.SpecialAgent] = ["shared"] };
            b.ByDivision = new() { [Division.Cid] = ["shared"] };
        });
        var agent = Seed.Agent("a", Rank.SpecialAgent, configure: a => a.Division = Division.Cid);

        var target = DiscordRoleRules.Target(agent, map)!;
        var diff = DiscordRoleRules.Diff(target, []);

        Assert.Contains("shared", target.Desired);
        Assert.Single(diff.Add, "shared");
    }

    [Fact]
    public void Unmapped_roles_are_never_removed()
    {
        var agent = Seed.Agent("a", Rank.SpecialAgent, configure: a => a.Division = Division.Cid);
        var target = DiscordRoleRules.Target(agent, Map())!;

        var diff = DiscordRoleRules.Diff(target, ["x"]);

        Assert.Empty(diff.Remove);
    }

    [Fact]
    public void Diff_adds_missing_and_removes_managed_leftovers_only()
    {
        var agent = Seed.Agent("a", Rank.SpecialAgent, configure: a => a.Division = Division.Cid);
        var target = DiscordRoleRules.Target(agent, Map())!;

        var diff = DiscordRoleRules.Diff(target, ["base", "hrt", "fremd"]);

        Assert.Equal(["cid", "r2"], diff.Add);
        Assert.Equal(["hrt"], diff.Remove);
        Assert.False(diff.IsEmpty);
    }

    [Fact]
    public void Diff_is_empty_when_member_already_matches()
    {
        var agent = Seed.Agent("a", Rank.SpecialAgent, configure: a => a.Division = Division.Cid);
        var target = DiscordRoleRules.Target(agent, Map())!;

        var diff = DiscordRoleRules.Diff(target, ["base", "r2", "cid", "fremd"]);

        Assert.True(diff.IsEmpty);
    }

    [Fact]
    public void Missing_entries_and_blank_ids_count_as_empty()
    {
        var map = Map(b =>
        {
            b.Base = [" ", ""];
            b.ByRank = new();
            b.ByDivision = new() { [Division.Cid] = [] };
        });
        var agent = Seed.Agent("a", Rank.SpecialAgent, configure: a => a.Division = Division.Cid);

        var target = DiscordRoleRules.Target(agent, map);

        Assert.NotNull(target);
        AssertSet(target.Desired);
        Assert.DoesNotContain(" ", map.AllManaged);
        Assert.DoesNotContain("", map.AllManaged);
    }

    [Fact]
    public void Empty_mapping_does_nothing()
    {
        var agent = Seed.Agent("a", Rank.SpecialAgent, configure: a => a.Division = Division.Cid);

        var target = DiscordRoleRules.Target(agent, DiscordRoleMapping.Empty);

        Assert.NotNull(target);
        Assert.Empty(target.Desired);
        Assert.Empty(target.Managed);
        Assert.Null(DiscordRoleRules.Validate(DiscordRoleMapping.Empty));
    }

    [Fact]
    public void Deleted_target_matches_terminated_internal()
    {
        var map = Map();

        var target = DiscordRoleRules.DeletedTarget(map);

        AssertSet(target.Desired, "bewohner");
        Assert.True(target.Managed.SetEquals(map.AllManaged));
    }

    [Theory]
    [InlineData(Rank.SpecialAgent, null, true)]
    [InlineData(null, null, false)]
    [InlineData(Rank.SpecialAgent, PartnerAgency.LSPD, false)]
    [InlineData(null, PartnerAgency.LSPD, false)]
    public void CountsAsFormerAgent_needs_a_rank_and_no_partner(Rank? rank, PartnerAgency? partner, bool expected)
    {
        var agent = Seed.Agent("a", configure: a =>
        {
            a.Rank = rank;
            a.PartnerAgency = partner;
        });

        Assert.Equal(expected, DiscordRoleRules.CountsAsFormerAgent(agent));
    }

    [Fact]
    public void Validate_rejects_resident_in_another_entry()
    {
        var map = Map(b => b.Base = ["base", "bewohner"]);

        Assert.NotNull(DiscordRoleRules.Validate(map));
    }

    [Fact]
    public void Validate_rejects_resident_in_rank_or_division_entry()
    {
        var inRank = Map(b => b.ByRank = new() { [Rank.Director] = ["bewohner"] });
        var inDivision = Map(b => b.ByDivision = new() { [Division.Hrt] = ["bewohner"] });

        Assert.NotNull(DiscordRoleRules.Validate(inRank));
        Assert.NotNull(DiscordRoleRules.Validate(inDivision));
    }

    [Fact]
    public void Validate_accepts_a_clean_mapping()
        => Assert.Null(DiscordRoleRules.Validate(Map()));
}
