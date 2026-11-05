using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Infrastructure.Discord;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services.Discord;

/// <summary>The role sync against a fake bot: only the diff goes out, failures are written down, and nothing happens while it is off.</summary>
public class DiscordRoleSyncServiceTests
{
    private const string Guild = "100000000000000001";
    private const string UserA = "200000000000000001";
    private const string UserB = "200000000000000002";

    private static readonly string BaseRole = Role(1);
    private static readonly string ResidentRole = Role(2);
    private static readonly string SpecialAgentRole = Role(3);
    private static readonly string AssistantDirectorRole = Role(4);
    private static readonly string CidRole = Role(5);
    private static readonly string TeamLeadRole = Role(9);
    private static readonly string Foreign = Role(50);

    private static string Role(int n) => $"3000000000000000{n:00}";

    private static DiscordRoleMapping Map() => new(
        [BaseRole],
        new Dictionary<Rank, IReadOnlyList<string>>
        {
            [Rank.SpecialAgent] = [SpecialAgentRole],
            [Rank.AssistantDirector] = [AssistantDirectorRole],
        },
        new Dictionary<Division, IReadOnlyList<string>> { [Division.Cid] = [CidRole] },
        [Role(6)], [Role(7)], [Role(8)], [TeamLeadRole], [ResidentRole]);

    private static ClaimsPrincipal Admin()
        => ClaimsPrincipalBuilder.Agent("admin").WithRank(Rank.Director).AsAdmin().Build();

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.SectionChief).Build();

    private static ClaimsPrincipal Plain()
        => ClaimsPrincipalBuilder.Agent("plain").WithRank(Rank.SpecialAgent).Build();

    private sealed class FakeBot : IDiscordBotClient
    {
        public bool IsConfigured { get; set; } = true;

        public Dictionary<string, List<string>> Members { get; } = [];

        public List<string> Calls { get; } = [];

        public List<string> Reasons { get; } = [];

        public DiscordGuildRoles GuildRoles { get; set; } = new([], 0);

        /// <summary>Called with (operation, userId); a returned exception is thrown.</summary>
        public Func<string, string, Exception?>? Fault { get; set; }

        /// <summary>Called with (operation, userId, roleId) before an add or remove; a returned exception is thrown.</summary>
        public Func<string, string, string, Exception?>? RoleFault { get; set; }

        /// <summary>Awaited at the start of each member lookup, to change things in the middle of a run.</summary>
        public Func<string, Task>? OnGet { get; set; }

        public IEnumerable<string> Writes => Calls.Where(c => c.StartsWith("add:") || c.StartsWith("remove:"));

        public async Task<IReadOnlyList<string>?> GetMemberRolesAsync(string guildId, string userId, CancellationToken ct)
        {
            Calls.Add($"get:{userId}");
            if (OnGet is not null)
            {
                await OnGet(userId);
            }
            Check("get", userId);
            return Members.TryGetValue(userId, out var roles) ? roles.ToList() : null;
        }

        public Task<DiscordGuildRoles> GetGuildRolesAsync(string guildId, CancellationToken ct)
        {
            Calls.Add("guildroles");
            Check("guildroles", "");
            return Task.FromResult(GuildRoles);
        }

        public Task AddRoleAsync(string guildId, string userId, string roleId, string reason, CancellationToken ct)
        {
            Calls.Add($"add:{userId}:{roleId}");
            Reasons.Add(reason);
            Check("add", userId);
            CheckRole("add", userId, roleId);
            Members[userId].Add(roleId);
            return Task.CompletedTask;
        }

        public Task RemoveRoleAsync(string guildId, string userId, string roleId, string reason, CancellationToken ct)
        {
            Calls.Add($"remove:{userId}:{roleId}");
            Reasons.Add(reason);
            Check("remove", userId);
            CheckRole("remove", userId, roleId);
            Members[userId].Remove(roleId);
            return Task.CompletedTask;
        }

        private void CheckRole(string operation, string userId, string roleId)
        {
            if (RoleFault?.Invoke(operation, userId, roleId) is { } fault)
            {
                throw fault;
            }
        }

        private void Check(string operation, string userId)
        {
            if (Fault?.Invoke(operation, userId) is { } fault)
            {
                throw fault;
            }
        }
    }

    private sealed class Fixture : IDisposable
    {
        public SqliteTestContext Ctx { get; } = new();

        public FakeBot Bot { get; } = new();

        public DiscordRoleSyncQueue Queue { get; } = new();

        public DiscordRoleSyncService Service { get; }

        public Fixture() => Service = new DiscordRoleSyncService(Ctx.Factory, Bot, Queue, NullLogger<DiscordRoleSyncService>.Instance);

        public static async Task<Fixture> CreateAsync(bool enabled = true, string? guild = Guild, DiscordRoleMapping? map = null)
        {
            var fixture = new Fixture();
            await fixture.Service.SaveSettingsAsync(new DiscordRoleSyncSettings(enabled, guild, map ?? Map()), Admin());
            return fixture;
        }

        public async Task<Agent> AddAgentAsync(string id, string discordId, Rank? rank = Rank.SpecialAgent,
            AgentStatus status = AgentStatus.Active, Action<Agent>? configure = null)
        {
            var agent = Seed.Agent(id, rank ?? Rank.SpecialAgent, status, a =>
            {
                a.DiscordId = discordId;
                a.Rank = rank;
                a.Division = Division.Cid;
                configure?.Invoke(a);
            });
            await using var db = Ctx.NewContext();
            db.Users.Add(agent);
            await db.SaveChangesAsync();
            return agent;
        }

        public async Task<Agent> ReadAsync(string id)
        {
            await using var db = Ctx.NewContext();
            return await db.Users.AsNoTracking().SingleAsync(u => u.Id == id);
        }

        public async Task UpdateAsync(string id, Action<Agent> change)
        {
            await using var db = Ctx.NewContext();
            var agent = await db.Users.SingleAsync(u => u.Id == id);
            change(agent);
            await db.SaveChangesAsync();
        }

        public async Task SetStampAsync(string id, DateTime? syncAt, string? error)
        {
            await using var db = Ctx.NewContext();
            await db.Users.Where(u => u.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(u => u.DiscordRolesSyncAt, syncAt)
                .SetProperty(u => u.DiscordRolesSyncError, error));
        }

        public void Dispose() => Ctx.Dispose();
    }

    private static void AssertRoles(IEnumerable<string> actual, params string[] expected)
        => Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));

    // ---------- sync of one agent ----------

    [Fact]
    public async Task Sync_adds_and_removes_only_the_diff()
    {
        using var f = await Fixture.CreateAsync();
        await f.AddAgentAsync("a", UserA);
        f.Bot.Members[UserA] = [ResidentRole, Foreign];

        await f.Service.SyncAgentAsync("a", "Beförderung");

        AssertRoles(f.Bot.Members[UserA], BaseRole, SpecialAgentRole, CidRole, Foreign);
        AssertRoles(f.Bot.Writes, $"add:{UserA}:{BaseRole}", $"add:{UserA}:{SpecialAgentRole}", $"add:{UserA}:{CidRole}",
            $"remove:{UserA}:{ResidentRole}");
    }

    [Fact]
    public async Task Removals_go_out_before_additions()
    {
        using var f = await Fixture.CreateAsync();
        await f.AddAgentAsync("a", UserA, status: AgentStatus.Terminated);
        f.Bot.Members[UserA] = [BaseRole, SpecialAgentRole];

        await f.Service.SyncAgentAsync("a", "Kündigung");

        var writes = f.Bot.Writes.ToList();
        Assert.StartsWith("remove:", writes[0]);
        Assert.StartsWith("remove:", writes[1]);
        Assert.Equal($"add:{UserA}:{ResidentRole}", writes[2]);
    }

    [Fact]
    public async Task Sync_sends_the_reason_with_the_site_prefix()
    {
        using var f = await Fixture.CreateAsync();
        await f.AddAgentAsync("a", UserA);
        f.Bot.Members[UserA] = [];

        await f.Service.SyncAgentAsync("a", "Beförderung");

        Assert.NotEmpty(f.Bot.Reasons);
        Assert.All(f.Bot.Reasons, r => Assert.Equal("FIB-Website: Beförderung", r));
    }

    [Fact]
    public async Task Sync_stamps_success_and_clears_error()
    {
        using var f = await Fixture.CreateAsync();
        await f.AddAgentAsync("a", UserA);
        await f.SetStampAsync("a", null, "Bot darf die Rolle nicht ändern (403)");
        f.Bot.Members[UserA] = [];
        var before = DateTime.UtcNow.AddSeconds(-1);

        await f.Service.SyncAgentAsync("a", "Beförderung");

        var agent = await f.ReadAsync("a");
        Assert.NotNull(agent.DiscordRolesSyncAt);
        Assert.InRange(agent.DiscordRolesSyncAt!.Value, before, DateTime.UtcNow.AddSeconds(1));
        Assert.Null(agent.DiscordRolesSyncError);
    }

    [Fact]
    public async Task A_member_already_in_line_is_stamped_without_any_write()
    {
        using var f = await Fixture.CreateAsync();
        await f.AddAgentAsync("a", UserA);
        f.Bot.Members[UserA] = [BaseRole, SpecialAgentRole, CidRole];

        await f.Service.SyncAgentAsync("a", "Tagesabgleich");

        Assert.Empty(f.Bot.Writes);
        Assert.NotNull((await f.ReadAsync("a")).DiscordRolesSyncAt);
    }

    [Theory]
    [InlineData(false, true, Guild)]
    [InlineData(true, false, Guild)]
    [InlineData(true, true, null)]
    public async Task Sync_does_nothing_when_disabled_or_no_token_or_no_server(bool enabled, bool configured, string? guild)
    {
        using var f = await Fixture.CreateAsync(enabled, guild);
        f.Bot.IsConfigured = configured;
        await f.AddAgentAsync("a", UserA);
        f.Bot.Members[UserA] = [ResidentRole];

        await f.Service.SyncAgentAsync("a", "Beförderung");
        await f.Service.SyncDeletedAsync(UserB, isTeamLead: false);
        await f.Service.SyncAllAsync("Tagesabgleich");

        Assert.Empty(f.Bot.Calls);
        var agent = await f.ReadAsync("a");
        Assert.Null(agent.DiscordRolesSyncAt);
        Assert.Null(agent.DiscordRolesSyncError);
        Assert.Null((await f.Service.GetStatusAsync(Admin())).LastRunUtc);
    }

    [Fact]
    public async Task Member_not_on_server_is_noted_not_failed()
    {
        using var f = await Fixture.CreateAsync();
        await f.AddAgentAsync("a", UserA);
        var earlier = new DateTime(2026, 11, 1, 6, 0, 0, DateTimeKind.Utc);
        await f.SetStampAsync("a", earlier, null);

        await f.Service.SyncAgentAsync("a", "Beförderung");

        var agent = await f.ReadAsync("a");
        Assert.Equal(DiscordRoleRules.NotOnServer, agent.DiscordRolesSyncError);
        Assert.Equal(earlier, agent.DiscordRolesSyncAt);
        Assert.Empty(f.Bot.Writes);
    }

    [Fact]
    public async Task Sync_reads_the_agent_fresh()
    {
        using var f = await Fixture.CreateAsync();
        await f.AddAgentAsync("a", UserA);
        f.Bot.Members[UserA] = [];

        // promoted after the job was queued
        await f.UpdateAsync("a", a => a.Rank = Rank.AssistantDirector);
        await f.Service.SyncAgentAsync("a", "Beförderung");

        Assert.Contains(AssistantDirectorRole, f.Bot.Members[UserA]);
        Assert.DoesNotContain(SpecialAgentRole, f.Bot.Members[UserA]);
    }

    [Fact]
    public async Task A_vanished_account_is_skipped_quietly()
    {
        using var f = await Fixture.CreateAsync();

        await f.Service.SyncAgentAsync("gone", "Beförderung");

        Assert.Empty(f.Bot.Calls);
    }

    [Theory]
    [InlineData(AgentStatus.Pending)]
    [InlineData(AgentStatus.Applicant)]
    public async Task Accounts_the_rules_skip_never_reach_discord(AgentStatus status)
    {
        using var f = await Fixture.CreateAsync();
        await f.AddAgentAsync("a", UserA, status: status);

        await f.Service.SyncAgentAsync("a", "Freigabe");

        Assert.Empty(f.Bot.Calls);
    }

    [Fact]
    public async Task A_skipped_account_loses_a_stale_error_so_the_status_list_stays_honest()
    {
        using var f = await Fixture.CreateAsync();
        await f.AddAgentAsync("a", UserA, status: AgentStatus.Pending);
        await f.SetStampAsync("a", null, "Bot darf die Rolle nicht ändern (403)");

        await f.Service.SyncAgentAsync("a", "Tagesabgleich");

        Assert.Null((await f.ReadAsync("a")).DiscordRolesSyncError);
        Assert.Empty(f.Bot.Calls);
    }

    // ---------- failures ----------

    [Fact]
    public async Task Forbidden_records_error_and_run_continues()
    {
        using var f = await Fixture.CreateAsync();
        await f.AddAgentAsync("a", UserA);
        await f.AddAgentAsync("b", UserB);
        f.Bot.Members[UserA] = [];
        f.Bot.Members[UserB] = [];
        f.Bot.Fault = (op, user) => op == "add" && user == UserA
            ? new DiscordBotException(403, "Discord 403: Missing Permissions") : null;

        await f.Service.SyncAllAsync("Tagesabgleich");

        var first = await f.ReadAsync("a");
        Assert.Contains("403", first.DiscordRolesSyncError);
        Assert.Null(first.DiscordRolesSyncAt);
        var second = await f.ReadAsync("b");
        Assert.Null(second.DiscordRolesSyncError);
        Assert.NotNull(second.DiscordRolesSyncAt);
        AssertRoles(f.Bot.Members[UserB], BaseRole, SpecialAgentRole, CidRole);
    }

    private static Exception Failure(string kind) => kind switch
    {
        "discord" => new DiscordBotException(500, "Discord 500: boom"),
        "timeout" => new TaskCanceledException("timeout"),
        "network" => new HttpRequestException("no route"),
        "json" => new JsonException("bad json"),
        "unconfigured" => new InvalidOperationException("not configured"),
        "badid" => new ArgumentException("Not a Discord id."),
        _ => new KeyNotFoundException("id"),
    };

    [Theory]
    [InlineData("discord")]
    [InlineData("timeout")]
    [InlineData("network")]
    [InlineData("json")]
    [InlineData("unconfigured")]
    [InlineData("badid")]
    [InlineData("shape")]
    public async Task Any_failure_is_written_down_and_the_next_agent_still_runs(string kind)
    {
        using var f = await Fixture.CreateAsync();
        await f.AddAgentAsync("a", UserA);
        await f.AddAgentAsync("b", UserB);
        f.Bot.Members[UserA] = [];
        f.Bot.Members[UserB] = [];
        f.Bot.Fault = (op, user) => op == "get" && user == UserA ? Failure(kind) : null;

        await f.Service.SyncAllAsync("Tagesabgleich");

        var first = await f.ReadAsync("a");
        Assert.False(string.IsNullOrWhiteSpace(first.DiscordRolesSyncError));
        Assert.Null(first.DiscordRolesSyncAt);
        Assert.NotNull((await f.ReadAsync("b")).DiscordRolesSyncAt);
    }

    [Fact]
    public async Task A_long_error_text_is_cut_to_the_column_width()
    {
        using var f = await Fixture.CreateAsync();
        await f.AddAgentAsync("a", UserA);
        f.Bot.Fault = (_, _) => new DiscordBotException(400, "Discord 400: " + new string('x', 600));

        await f.Service.SyncAgentAsync("a", "Beförderung");

        Assert.Equal(200, (await f.ReadAsync("a")).DiscordRolesSyncError!.Length);
    }

    [Fact]
    public async Task A_refused_removal_does_not_stop_the_other_removals_or_the_additions()
    {
        using var f = await Fixture.CreateAsync();
        await f.AddAgentAsync("a", UserA, status: AgentStatus.Terminated);
        f.Bot.Members[UserA] = [BaseRole, SpecialAgentRole, CidRole];
        // the first removal in sort order is the one the bot may not touch
        f.Bot.RoleFault = (op, _, role) => op == "remove" && role == BaseRole
            ? new DiscordBotException(403, "Discord 403: Missing Permissions") : null;

        await f.Service.SyncAgentAsync("a", "Kündigung");

        AssertRoles(f.Bot.Writes, $"remove:{UserA}:{BaseRole}", $"remove:{UserA}:{SpecialAgentRole}",
            $"remove:{UserA}:{CidRole}", $"add:{UserA}:{ResidentRole}");
        AssertRoles(f.Bot.Members[UserA], BaseRole, ResidentRole);
        var agent = await f.ReadAsync("a");
        Assert.Contains("403", agent.DiscordRolesSyncError);
        Assert.Null(agent.DiscordRolesSyncAt);
    }

    [Fact]
    public async Task A_refused_addition_does_not_stop_the_other_additions()
    {
        using var f = await Fixture.CreateAsync();
        await f.AddAgentAsync("a", UserA);
        f.Bot.Members[UserA] = [];
        f.Bot.RoleFault = (op, _, role) => op == "add" && role == BaseRole
            ? new DiscordBotException(403, "Discord 403: Missing Permissions") : null;

        await f.Service.SyncAgentAsync("a", "Beförderung");

        AssertRoles(f.Bot.Members[UserA], SpecialAgentRole, CidRole);
        Assert.Contains("403", (await f.ReadAsync("a")).DiscordRolesSyncError);
    }

    [Fact]
    public async Task The_first_role_failure_is_the_one_recorded()
    {
        using var f = await Fixture.CreateAsync();
        await f.AddAgentAsync("a", UserA, status: AgentStatus.Terminated);
        f.Bot.Members[UserA] = [BaseRole, SpecialAgentRole];
        f.Bot.RoleFault = (op, _, role) => (op, role) switch
        {
            ("remove", var r) when r == BaseRole => new DiscordBotException(403, "Discord 403: Missing Permissions"),
            ("remove", var r) when r == SpecialAgentRole => new DiscordBotException(404, "Discord 404: Unknown Role"),
            _ => null,
        };

        await f.Service.SyncAgentAsync("a", "Kündigung");

        Assert.Contains("403", (await f.ReadAsync("a")).DiscordRolesSyncError);
        Assert.Contains($"add:{UserA}:{ResidentRole}", f.Bot.Writes);
    }

    [Theory]
    [InlineData("401")]
    [InlineData("429")]
    [InlineData("500")]
    [InlineData("timeout")]
    [InlineData("network")]
    public async Task A_failure_that_hits_every_call_ends_the_agent(string kind)
    {
        using var f = await Fixture.CreateAsync();
        await f.AddAgentAsync("a", UserA, status: AgentStatus.Terminated);
        f.Bot.Members[UserA] = [BaseRole, SpecialAgentRole, CidRole];
        f.Bot.RoleFault = (_, _, _) => kind switch
        {
            "timeout" => new TaskCanceledException("timeout"),
            "network" => new HttpRequestException("no route"),
            _ => new DiscordBotException(int.Parse(kind), $"Discord {kind}: x"),
        };

        await f.Service.SyncAgentAsync("a", "Kündigung");

        Assert.Single(f.Bot.Writes);
        var agent = await f.ReadAsync("a");
        Assert.False(string.IsNullOrWhiteSpace(agent.DiscordRolesSyncError));
        Assert.Null(agent.DiscordRolesSyncAt);
    }

    [Fact]
    public async Task A_failure_after_a_refused_role_ends_the_agent_but_keeps_the_first_error()
    {
        using var f = await Fixture.CreateAsync();
        await f.AddAgentAsync("a", UserA, status: AgentStatus.Terminated);
        f.Bot.Members[UserA] = [BaseRole, SpecialAgentRole, CidRole];
        f.Bot.RoleFault = (op, _, role) => (op, role) switch
        {
            ("remove", var r) when r == BaseRole => new DiscordBotException(403, "Discord 403: Missing Permissions"),
            ("remove", var r) when r == SpecialAgentRole => new TaskCanceledException("timeout"),
            _ => null,
        };

        await f.Service.SyncAgentAsync("a", "Kündigung");

        Assert.Equal(2, f.Bot.Writes.Count());
        Assert.Contains("403", (await f.ReadAsync("a")).DiscordRolesSyncError);
    }

    [Fact]
    public async Task Cancelling_the_run_escapes_and_is_not_recorded_as_a_failure()
    {
        using var f = await Fixture.CreateAsync();
        await f.AddAgentAsync("a", UserA);
        using var cts = new CancellationTokenSource();
        f.Bot.Fault = (_, _) =>
        {
            cts.Cancel();
            return new OperationCanceledException(cts.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => f.Service.SyncAgentAsync("a", "Beförderung", cts.Token));

        Assert.Null((await f.ReadAsync("a")).DiscordRolesSyncError);
    }

    // ---------- deleted accounts and the full run ----------

    [Fact]
    public async Task Deleted_account_gets_resident()
    {
        using var f = await Fixture.CreateAsync();
        f.Bot.Members[UserA] = [BaseRole, SpecialAgentRole, Foreign];

        await f.Service.SyncDeletedAsync(UserA, isTeamLead: false);

        AssertRoles(f.Bot.Members[UserA], ResidentRole, Foreign);
        Assert.All(f.Bot.Reasons, r => Assert.Equal("FIB-Website: Konto gelöscht", r));
    }

    [Fact]
    public async Task A_deleted_account_that_is_not_on_the_server_is_left_alone()
    {
        using var f = await Fixture.CreateAsync();

        await f.Service.SyncDeletedAsync(UserA, isTeamLead: false);

        Assert.Empty(f.Bot.Writes);
    }

    [Fact]
    public async Task A_failure_with_a_deleted_account_does_not_throw()
    {
        using var f = await Fixture.CreateAsync();
        f.Bot.Fault = (_, _) => new DiscordBotException(403, "Discord 403: Missing Permissions");

        await f.Service.SyncDeletedAsync(UserA, isTeamLead: false);
    }

    [Fact]
    public async Task Deleted_team_lead_loses_fib_roles_and_is_not_made_a_resident()
    {
        using var f = await Fixture.CreateAsync();
        f.Bot.Members[UserA] = [TeamLeadRole, BaseRole, Foreign];

        await f.Service.SyncDeletedAsync(UserA, isTeamLead: true);

        AssertRoles(f.Bot.Members[UserA], Foreign);
        Assert.DoesNotContain($"add:{UserA}:{ResidentRole}", f.Bot.Calls);
    }

    [Fact]
    public async Task A_released_account_behind_a_deleted_one_is_synced_from_its_row_not_made_a_resident()
    {
        using var f = await Fixture.CreateAsync();
        await f.AddAgentAsync("a", UserA);
        f.Bot.Members[UserA] = [BaseRole, SpecialAgentRole, CidRole];

        // same person signed up again and was released before the job ran
        await f.Service.SyncDeletedAsync(UserA, isTeamLead: false);

        AssertRoles(f.Bot.Members[UserA], BaseRole, SpecialAgentRole, CidRole);
    }

    [Fact]
    public async Task A_pending_account_behind_a_deleted_former_agent_gets_the_deleted_target()
    {
        using var f = await Fixture.CreateAsync();
        await f.AddAgentAsync("again", UserA, rank: null, status: AgentStatus.Pending);
        f.Bot.Members[UserA] = [BaseRole, SpecialAgentRole, CidRole, Foreign];

        await f.Service.SyncDeletedAsync(UserA, isTeamLead: false);

        AssertRoles(f.Bot.Members[UserA], ResidentRole, Foreign);
    }

    [Fact]
    public async Task SyncAll_stamps_last_run()
    {
        using var f = await Fixture.CreateAsync();
        await f.AddAgentAsync("a", UserA);
        f.Bot.Members[UserA] = [];
        var before = DateTime.UtcNow.AddSeconds(-1);

        await f.Service.SyncAllAsync("Tagesabgleich");

        var status = await f.Service.GetStatusAsync(Admin());
        Assert.NotNull(status.LastRunUtc);
        Assert.Equal(DateTimeKind.Utc, status.LastRunUtc!.Value.Kind);
        Assert.InRange(status.LastRunUtc.Value, before, DateTime.UtcNow.AddSeconds(1));
    }

    [Fact]
    public async Task A_second_run_moves_the_last_run_stamp()
    {
        using var f = await Fixture.CreateAsync();
        await f.Service.SyncAllAsync("Tagesabgleich");
        var longAgo = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await using (var db = f.Ctx.NewContext())
        {
            var stamp = longAgo.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            await db.SystemSettings.Where(s => s.Key == SystemSettingKeys.DiscordRoleSyncLastRun)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Value, stamp));
        }

        await f.Service.SyncAllAsync("Tagesabgleich");

        Assert.True((await f.Service.GetStatusAsync(Admin())).LastRunUtc > longAgo);
    }

    [Fact]
    public async Task Turning_the_switch_off_during_a_run_stops_it()
    {
        using var f = await Fixture.CreateAsync();
        await f.AddAgentAsync("a", UserA);
        await f.AddAgentAsync("b", UserB);
        f.Bot.Members[UserA] = [];
        f.Bot.Members[UserB] = [];
        f.Bot.OnGet = async user =>
        {
            if (user == UserA)
            {
                await f.Service.SaveSettingsAsync(new DiscordRoleSyncSettings(false, Guild, Map()), Admin());
            }
        };

        await f.Service.SyncAllAsync("Tagesabgleich");

        Assert.DoesNotContain(f.Bot.Calls, c => c.Contains(UserB));
        Assert.Empty(f.Bot.Members[UserB]);
        Assert.Null((await f.ReadAsync("b")).DiscordRolesSyncAt);
        Assert.Null((await f.Service.GetStatusAsync(Admin())).LastRunUtc);
    }

    [Fact]
    public async Task A_mapping_saved_during_a_run_is_used_for_the_next_agent()
    {
        using var f = await Fixture.CreateAsync();
        await f.AddAgentAsync("a", UserA);
        await f.AddAgentAsync("b", UserB);
        f.Bot.Members[UserA] = [];
        f.Bot.Members[UserB] = [];
        var newRole = Role(60);
        var changed = Map() with
        {
            ByRank = new Dictionary<Rank, IReadOnlyList<string>> { [Rank.SpecialAgent] = [newRole] },
        };
        f.Bot.OnGet = async user =>
        {
            if (user == UserA)
            {
                await f.Service.SaveSettingsAsync(new DiscordRoleSyncSettings(true, Guild, changed), Admin());
            }
        };

        await f.Service.SyncAllAsync("Tagesabgleich");

        Assert.Contains(newRole, f.Bot.Members[UserB]);
        Assert.DoesNotContain(SpecialAgentRole, f.Bot.Members[UserB]);
    }

    [Fact]
    public async Task An_agent_changed_during_a_run_is_synced_from_its_fresh_row()
    {
        using var f = await Fixture.CreateAsync();
        await f.AddAgentAsync("a", UserA);
        await f.AddAgentAsync("b", UserB);
        f.Bot.Members[UserA] = [];
        f.Bot.Members[UserB] = [];
        f.Bot.OnGet = async user =>
        {
            if (user == UserA)
            {
                await f.UpdateAsync("b", a => a.Rank = Rank.AssistantDirector);
            }
        };

        await f.Service.SyncAllAsync("Tagesabgleich");

        Assert.Contains(AssistantDirectorRole, f.Bot.Members[UserB]);
        Assert.DoesNotContain(SpecialAgentRole, f.Bot.Members[UserB]);
    }

    [Fact]
    public async Task SyncAll_leaves_out_accounts_without_discord_id_and_those_the_rules_skip()
    {
        using var f = await Fixture.CreateAsync();
        await f.AddAgentAsync("a", UserA);
        await f.AddAgentAsync("none", "");
        await f.AddAgentAsync("pending", UserB, status: AgentStatus.Pending);
        f.Bot.Members[UserA] = [];

        await f.Service.SyncAllAsync("Tagesabgleich");

        Assert.DoesNotContain(f.Bot.Calls, c => c.Contains(UserB));
        Assert.Equal(1, f.Bot.Calls.Count(c => c.StartsWith("get:")));
    }

    [Fact]
    public async Task Cancelling_a_full_run_leaves_the_last_run_unstamped()
    {
        using var f = await Fixture.CreateAsync();
        await f.AddAgentAsync("a", UserA);
        using var cts = new CancellationTokenSource();
        f.Bot.Fault = (_, _) =>
        {
            cts.Cancel();
            return new OperationCanceledException(cts.Token);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.SyncAllAsync("Tagesabgleich", cts.Token));

        Assert.Null((await f.Service.GetStatusAsync(Admin())).LastRunUtc);
    }

    // ---------- dry run ----------

    [Fact]
    public async Task Preview_writes_nothing()
    {
        using var f = await Fixture.CreateAsync(enabled: false);
        await f.AddAgentAsync("a", UserA);
        f.Bot.Members[UserA] = [ResidentRole, Foreign];
        f.Bot.GuildRoles = new DiscordGuildRoles(
        [
            new DiscordGuildRole(BaseRole, "FIB", 2, false),
            new DiscordGuildRole(SpecialAgentRole, "Special Agent", 3, false),
            new DiscordGuildRole(CidRole, "CID", 4, false),
            new DiscordGuildRole(ResidentRole, "Bewohner", 1, false),
        ], 10);

        var rows = await f.Service.PreviewAsync(Admin());

        var row = Assert.Single(rows);
        Assert.Equal("a", row.AgentId);
        Assert.Equal("Codename-a", row.Codename);
        AssertRoles(row.Add, "FIB", "Special Agent", "CID");
        AssertRoles(row.Remove, "Bewohner");
        Assert.Null(row.Note);
        Assert.Empty(f.Bot.Writes);
        var agent = await f.ReadAsync("a");
        Assert.Null(agent.DiscordRolesSyncAt);
        Assert.Null(agent.DiscordRolesSyncError);
        Assert.Null((await f.Service.GetStatusAsync(Admin())).LastRunUtc);
    }

    [Fact]
    public async Task Preview_leaves_out_agents_already_in_line()
    {
        using var f = await Fixture.CreateAsync();
        await f.AddAgentAsync("a", UserA);
        f.Bot.Members[UserA] = [BaseRole, SpecialAgentRole, CidRole];
        f.Bot.GuildRoles = new DiscordGuildRoles([], 10);

        Assert.Empty(await f.Service.PreviewAsync(Admin()));
    }

    [Fact]
    public async Task Preview_falls_back_to_ids_when_the_server_roles_cannot_be_read()
    {
        using var f = await Fixture.CreateAsync();
        await f.AddAgentAsync("a", UserA);
        f.Bot.Members[UserA] = [BaseRole, SpecialAgentRole, ResidentRole];
        f.Bot.Fault = (op, _) => op == "guildroles" ? new HttpRequestException("down") : null;

        var row = Assert.Single(await f.Service.PreviewAsync(Admin()));

        AssertRoles(row.Add, CidRole);
        AssertRoles(row.Remove, ResidentRole);
        Assert.Null(row.Note);
    }

    [Fact]
    public async Task Preview_flags_roles_the_server_does_not_know_or_the_bot_may_not_give()
    {
        using var f = await Fixture.CreateAsync();
        await f.AddAgentAsync("a", UserA);
        f.Bot.Members[UserA] = [];
        // the base role sits above the bot, CID is gone from the server
        f.Bot.GuildRoles = new DiscordGuildRoles(
        [
            new DiscordGuildRole(BaseRole, "FIB", 9, false),
            new DiscordGuildRole(SpecialAgentRole, "Special Agent", 3, false),
        ], 5);

        var row = Assert.Single(await f.Service.PreviewAsync(Admin()));

        Assert.Contains("FIB", row.Note);
        Assert.Contains(CidRole, row.Note);
        Assert.DoesNotContain("Special Agent", row.Note);
    }

    [Fact]
    public async Task Preview_notes_a_member_who_is_not_on_the_server()
    {
        using var f = await Fixture.CreateAsync();
        await f.AddAgentAsync("a", UserA);

        var row = Assert.Single(await f.Service.PreviewAsync(Admin()));

        Assert.Equal(DiscordRoleRules.NotOnServer, row.Note);
        Assert.Empty(row.Add);
        Assert.Empty(row.Remove);
    }

    [Fact]
    public async Task Preview_notes_a_failed_lookup_and_goes_on()
    {
        using var f = await Fixture.CreateAsync();
        await f.AddAgentAsync("a", UserA);
        await f.AddAgentAsync("b", UserB);
        f.Bot.Members[UserB] = [];
        f.Bot.Fault = (op, user) => op == "get" && user == UserA ? new DiscordBotException(403, "Discord 403: x") : null;

        var rows = await f.Service.PreviewAsync(Admin());

        Assert.Equal(2, rows.Count);
        Assert.Contains("403", rows.Single(r => r.AgentId == "a").Note);
        Assert.NotEmpty(rows.Single(r => r.AgentId == "b").Add);
    }

    [Fact]
    public async Task Preview_without_token_or_server_id_says_so()
    {
        using var noToken = await Fixture.CreateAsync();
        noToken.Bot.IsConfigured = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => noToken.Service.PreviewAsync(Admin()));

        using var noServer = await Fixture.CreateAsync(guild: null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => noServer.Service.PreviewAsync(Admin()));
    }

    [Fact]
    public async Task Preview_is_for_admins_only()
    {
        using var f = await Fixture.CreateAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.PreviewAsync(Leader()));
        Assert.Empty(f.Bot.Calls);
    }

    // ---------- run now ----------

    [Fact]
    public async Task RunNow_queues_a_full_run_and_calls_nothing_itself()
    {
        using var f = await Fixture.CreateAsync();
        await f.AddAgentAsync("a", UserA);

        await f.Service.RunNowAsync(Admin());

        Assert.True(f.Queue.Reader.TryRead(out var job));
        Assert.Equal(new AllJob("von Hand angestoßen"), job);
        Assert.False(f.Queue.Reader.TryRead(out _));
        Assert.Empty(f.Bot.Calls);
    }

    [Fact]
    public async Task RunNow_requires_admin_and_write()
    {
        using var f = await Fixture.CreateAsync();

        // leadership without admin
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.RunNowAsync(Leader()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.RunNowAsync(Plain()));
        // read-only supervision: team lead without admin
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.RunNowAsync(
            ClaimsPrincipalBuilder.Agent("ro").WithRank(Rank.Director).AsTeamLead().Build()));
        // admins who may not write (demo session, partner)
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.RunNowAsync(
            ClaimsPrincipalBuilder.Agent("demo").WithRank(Rank.Director).AsAdmin().AsDemo().Build()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.RunNowAsync(
            ClaimsPrincipalBuilder.Agent("partner").AsAdmin().AsPartner(PartnerAgency.LSPD, PartnerRank.Member).Build()));

        Assert.False(f.Queue.Reader.TryRead(out _));
    }

    // ---------- settings ----------

    [Fact]
    public async Task Settings_default_to_off_with_an_empty_mapping()
    {
        using var f = new Fixture();

        var settings = await f.Service.GetSettingsAsync();

        Assert.False(settings.Enabled);
        Assert.Null(settings.GuildId);
        Assert.Empty(settings.Mapping.AllManaged);
    }

    [Fact]
    public async Task Settings_survive_a_round_trip()
    {
        using var f = await Fixture.CreateAsync();

        var settings = await f.Service.GetSettingsAsync();

        Assert.True(settings.Enabled);
        Assert.Equal(Guild, settings.GuildId);
        Assert.True(settings.Mapping.AllManaged.SetEquals(Map().AllManaged));
        AssertRoles(settings.Mapping.ByRank[Rank.SpecialAgent], SpecialAgentRole);
        AssertRoles(settings.Mapping.ByDivision[Division.Cid], CidRole);
        AssertRoles(settings.Mapping.Resident, ResidentRole);
        AssertRoles(settings.Mapping.Base, BaseRole);
    }

    [Fact]
    public async Task The_mapping_is_stored_as_one_json_value_keyed_by_names()
    {
        using var f = await Fixture.CreateAsync();

        await using var db = f.Ctx.NewContext();
        var stored = await db.SystemSettings.Where(s => s.Key == SystemSettingKeys.DiscordRoleMapping)
            .Select(s => s.Value).SingleAsync();

        Assert.Contains("\"SpecialAgent\"", stored);
        Assert.Contains("\"Cid\"", stored);
        Assert.DoesNotContain("AllManaged", stored);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{\"ByRank\":{\"Wizard\":[\"1\"]}}")]
    [InlineData("[1,2]")]
    public async Task A_broken_mapping_reads_as_empty_and_the_rest_still_reads(string json)
    {
        using var f = await Fixture.CreateAsync();
        await using (var db = f.Ctx.NewContext())
        {
            await db.SystemSettings.Where(s => s.Key == SystemSettingKeys.DiscordRoleMapping)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Value, json));
        }

        var settings = await f.Service.GetSettingsAsync();

        Assert.Empty(settings.Mapping.AllManaged);
        Assert.True(settings.Enabled);
        Assert.Equal(Guild, settings.GuildId);
    }

    [Fact]
    public async Task Save_requires_admin_and_write()
    {
        using var f = new Fixture();
        var settings = new DiscordRoleSyncSettings(true, Guild, Map());

        // leadership without admin
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SaveSettingsAsync(settings, Leader()));
        // read-only supervision: team lead without admin
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SaveSettingsAsync(settings,
            ClaimsPrincipalBuilder.Agent("ro").WithRank(Rank.Director).AsTeamLead().Build()));
        // an admin who may not write (demo session)
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.SaveSettingsAsync(settings,
            ClaimsPrincipalBuilder.Agent("demo").WithRank(Rank.Director).AsAdmin().AsDemo().Build()));

        Assert.False((await f.Service.GetSettingsAsync()).Enabled);

        await f.Service.SaveSettingsAsync(settings, Admin());
        Assert.True((await f.Service.GetSettingsAsync()).Enabled);
    }

    [Fact]
    public async Task Save_rejects_resident_in_other_entry()
    {
        using var f = new Fixture();
        var map = Map() with { Base = [BaseRole, ResidentRole] };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Service.SaveSettingsAsync(new DiscordRoleSyncSettings(true, Guild, map), Admin()));

        Assert.False((await f.Service.GetSettingsAsync()).Enabled);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1234567890123456")]
    [InlineData("123456789012345678901")]
    [InlineData("12345678901234567x")]
    public async Task Save_rejects_bad_role_ids(string bad)
    {
        using var f = new Fixture();
        var map = Map() with { Admin = [bad] };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Service.SaveSettingsAsync(new DiscordRoleSyncSettings(true, Guild, map), Admin()));
    }

    [Fact]
    public async Task Save_rejects_a_bad_role_id_inside_a_rank_entry()
    {
        using var f = new Fixture();
        var map = Map() with { ByRank = new Dictionary<Rank, IReadOnlyList<string>> { [Rank.Trainee] = ["nope"] } };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Service.SaveSettingsAsync(new DiscordRoleSyncSettings(true, Guild, map), Admin()));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("12345")]
    [InlineData("123456789012345678901")]
    public async Task Save_rejects_a_bad_server_id(string bad)
    {
        using var f = new Fixture();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => f.Service.SaveSettingsAsync(new DiscordRoleSyncSettings(true, bad, Map()), Admin()));
    }

    [Fact]
    public async Task Save_trims_ids_drops_blanks_and_duplicates_and_allows_clearing_the_server()
    {
        using var f = new Fixture();
        var map = Map() with { Base = [$" {BaseRole} ", "", BaseRole] };

        await f.Service.SaveSettingsAsync(new DiscordRoleSyncSettings(false, "  ", map), Admin());

        var settings = await f.Service.GetSettingsAsync();
        Assert.Null(settings.GuildId);
        AssertRoles(settings.Mapping.Base, BaseRole);
    }

    // ---------- server roles and status ----------

    [Fact]
    public async Task Guild_roles_are_for_leadership_and_null_without_token_server_or_answer()
    {
        using var f = await Fixture.CreateAsync();
        f.Bot.GuildRoles = new DiscordGuildRoles([new DiscordGuildRole(BaseRole, "FIB", 2, false)], 5);

        Assert.Single((await f.Service.GetGuildRolesAsync(Leader()))!.Roles);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.GetGuildRolesAsync(Plain()));

        f.Bot.Fault = (op, _) => op == "guildroles" ? new DiscordBotException(403, "Discord 403: x") : null;
        Assert.Null(await f.Service.GetGuildRolesAsync(Leader()));

        f.Bot.Fault = null;
        f.Bot.IsConfigured = false;
        Assert.Null(await f.Service.GetGuildRolesAsync(Leader()));

        using var noServer = await Fixture.CreateAsync(guild: null);
        Assert.Null(await noServer.Service.GetGuildRolesAsync(Leader()));
    }

    [Fact]
    public async Task Status_lists_failures_by_codename()
    {
        using var f = await Fixture.CreateAsync();
        var stamp = new DateTime(2026, 11, 4, 5, 0, 0, DateTimeKind.Utc);
        await f.AddAgentAsync("a", UserA, configure: a =>
        {
            a.Codename = "Falke";
            a.RealName = "Max Mustermann";
        });
        await f.AddAgentAsync("b", UserB, configure: a => a.Codename = "Adler");
        await f.AddAgentAsync("ok", "200000000000000003");
        await f.SetStampAsync("a", stamp, "Bot darf die Rolle nicht ändern (403)");
        await f.SetStampAsync("b", null, DiscordRoleRules.NotOnServer);
        await f.SetStampAsync("ok", stamp, null);

        var status = await f.Service.GetStatusAsync(Leader());

        Assert.Equal(new[] { "Adler", "Falke" }, status.Failures.Select(x => x.Codename));
        var falke = status.Failures.Single(x => x.AgentId == "a");
        Assert.Equal("Bot darf die Rolle nicht ändern (403)", falke.Error);
        Assert.Equal(stamp, falke.LastSuccessUtc);
        Assert.Null(status.Failures.Single(x => x.AgentId == "b").LastSuccessUtc);
        Assert.DoesNotContain("Mustermann", JsonSerializer.Serialize(status));
    }

    [Fact]
    public async Task Status_reports_token_and_switch()
    {
        using var f = await Fixture.CreateAsync(enabled: false);

        var off = await f.Service.GetStatusAsync(Leader());
        Assert.True(off.TokenConfigured);
        Assert.False(off.Enabled);
        Assert.Empty(off.Failures);

        f.Bot.IsConfigured = false;
        Assert.False((await f.Service.GetStatusAsync(Leader())).TokenConfigured);
    }

    [Fact]
    public async Task Status_is_for_leadership()
    {
        using var f = await Fixture.CreateAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.GetStatusAsync(Plain()));
    }

    [Fact]
    public async Task Read_only_supervision_reads_status_and_server_roles_whatever_its_rank()
    {
        using var f = await Fixture.CreateAsync();
        f.Bot.GuildRoles = new DiscordGuildRoles([new DiscordGuildRole(BaseRole, "FIB", 2, false)], 5);
        // team lead without admin, below leadership: the page lets it in, so the reads must too
        var supervisor = ClaimsPrincipalBuilder.Agent("ro").WithRank(Rank.SpecialAgent).AsTeamLead().Build();

        Assert.True((await f.Service.GetStatusAsync(supervisor)).TokenConfigured);
        Assert.Single((await f.Service.GetGuildRolesAsync(supervisor))!.Roles);

        // but nothing beyond reading
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.PreviewAsync(supervisor));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.RunNowAsync(supervisor));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.GetStatusAsync(Plain()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service.GetGuildRolesAsync(Plain()));
    }
}
