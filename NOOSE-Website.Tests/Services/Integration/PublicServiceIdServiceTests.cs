using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Public;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The anonymous ID check: one answer for every miss, and nothing but codename, rank and validity.</summary>
public sealed class PublicServiceIdServiceTests
{
    private static Agent Holder(string id, string number, Action<Agent>? configure = null)
        => Seed.Agent(id, Rank.SpecialAgent, configure: a =>
        {
            a.BadgeNumber = number;
            a.Codename = "Falke";
            a.RealName = "Klara Klarname";
            // opted in, so the other rules are what decides
            a.HiddenFromPublicRoster = false;
            configure?.Invoke(a);
        });

    private static async Task<SqliteTestContext> SeededAsync(bool moduleOn = true, params Agent[] agents)
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        await PublicModuleSeeder.SeedAsync(db);
        (await db.OeffentlicheModule.SingleAsync(m => m.Key == PublicModules.ServiceId)).IsEnabled = moduleOn;
        db.Users.AddRange(agents);
        await db.SaveChangesAsync();
        return ctx;
    }

    private static PublicServiceIdService NewService(SqliteTestContext ctx)
        => new(ctx.Factory, new PublicModuleService(ctx.Factory, new MemoryCache(new MemoryCacheOptions())));

    [Fact]
    public void The_module_is_off_by_default_and_has_no_tab()
    {
        var module = PublicModules.All.Single(m => m.Key == PublicModules.ServiceId);

        Assert.True(module.Available);
        Assert.False(module.DefaultEnabled);
        Assert.Null(module.NavRoute);
        Assert.True(PublicRoutes.IsPublic(ServiceIdAddress.Path("IV")));
    }

    [Fact]
    public async Task An_active_holder_is_valid_with_codename_and_rank_title()
    {
        using var ctx = await SeededAsync(true, Holder("a", "IV", a => a.Rank = Rank.SeniorSpecialAgent));

        var check = await NewService(ctx).CheckAsync("IV");

        Assert.NotNull(check);
        Assert.True(check.Valid);
        Assert.Equal("Falke", check.Alias);
        Assert.Equal(RankDisplay.Name(Rank.SeniorSpecialAgent), check.Title);
    }

    [Theory]
    [InlineData("iv")]
    [InlineData(" IV ")]
    public async Task The_number_is_read_case_and_space_tolerant(string typed)
    {
        using var ctx = await SeededAsync(true, Holder("a", "IV"));

        Assert.True((await NewService(ctx).CheckAsync(typed))!.Valid);
    }

    [Fact]
    public async Task A_stored_lower_case_number_still_matches()
    {
        using var ctx = await SeededAsync(true, Holder("a", "iv"));

        Assert.True((await NewService(ctx).CheckAsync("IV"))!.Valid);
    }

    [Fact]
    public async Task The_module_off_answers_like_an_unknown_number()
    {
        using var ctx = await SeededAsync(false, Holder("a", "IV"));

        Assert.Null(await NewService(ctx).CheckAsync("IV"));
    }

    [Fact]
    public async Task The_kill_switch_beats_the_module()
    {
        using var ctx = await SeededAsync(true, Holder("a", "IV"));
        await using (var db = ctx.NewContext())
        {
            db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.PublicAreaKillSwitch, Value = "true" });
            await db.SaveChangesAsync();
        }

        Assert.Null(await NewService(ctx).CheckAsync("IV"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("XLI")]
    [InlineData("0")]
    [InlineData("A-12")]
    [InlineData("IV' OR '1'='1")]
    [InlineData("IVIVIVIVIVIVIVIVIV")]
    public async Task A_malformed_number_is_a_miss(string? typed)
    {
        using var ctx = await SeededAsync(true, Holder("a", "IV"));

        Assert.Null(await NewService(ctx).CheckAsync(typed));
    }

    [Fact]
    public async Task An_unassigned_number_is_a_miss_like_every_other_miss()
    {
        using var ctx = await SeededAsync(true, Holder("a", "IV"));

        Assert.Null(await NewService(ctx).CheckAsync("V"));
    }

    [Fact]
    public async Task A_terminated_agent_reads_as_invalid_without_name_or_rank()
    {
        using var ctx = await SeededAsync(true, Holder("gone", "IV", a => a.Status = AgentStatus.Terminated));

        var check = await NewService(ctx).CheckAsync("IV");

        Assert.NotNull(check);
        Assert.False(check.Valid);
        Assert.Null(check.Alias);
        Assert.Null(check.Title);
    }

    [Fact]
    public async Task A_freed_number_belongs_to_its_new_active_holder()
    {
        using var ctx = await SeededAsync(true,
            Holder("gone", "IV", a => { a.Status = AgentStatus.Terminated; a.Codename = "Alt"; }),
            Holder("new", "IV", a => a.Codename = "Neu"));

        var check = await NewService(ctx).CheckAsync("IV");

        Assert.True(check!.Valid);
        Assert.Equal("Neu", check.Alias);
    }

    [Fact]
    public async Task Two_active_holders_of_one_legacy_number_are_a_miss()
    {
        using var ctx = await SeededAsync(true, Holder("a", "IV"), Holder("b", "IV"));

        Assert.Null(await NewService(ctx).CheckAsync("IV"));
    }

    [Fact]
    public async Task Only_agents_on_the_public_roster_answer()
    {
        using var ctx = await SeededAsync(true,
            Holder("hidden", "IV", a => a.HiddenFromPublicRoster = true),
            Holder("hiddenGone", "V", a => { a.HiddenFromPublicRoster = true; a.Status = AgentStatus.Terminated; }));
        var service = NewService(ctx);

        Assert.Null(await service.CheckAsync("IV"));
        Assert.Null(await service.CheckAsync("V"));
    }

    [Theory]
    [InlineData(AgentStatus.Blocked)]
    [InlineData(AgentStatus.Pending)]
    [InlineData(AgentStatus.Applicant)]
    [InlineData(AgentStatus.Civilian)]
    public async Task A_suspended_or_not_yet_released_account_is_a_miss(AgentStatus status)
    {
        using var ctx = await SeededAsync(true, Holder("a", "IV", a => a.Status = status));

        Assert.Null(await NewService(ctx).CheckAsync("IV"));
    }

    [Fact]
    public async Task Supervision_and_partners_are_a_miss_even_when_terminated()
    {
        using var ctx = await SeededAsync(true,
            Holder("tl", "IV", a => a.IsTeamLead = true),
            Holder("tlGone", "V", a => { a.IsTeamLead = true; a.Status = AgentStatus.Terminated; }),
            Holder("partner", "VI", a => a.PartnerAgency = PartnerAgency.LSPD));
        var service = NewService(ctx);

        Assert.Null(await service.CheckAsync("IV"));
        Assert.Null(await service.CheckAsync("V"));
        Assert.Null(await service.CheckAsync("VI"));
    }

    [Fact]
    public async Task An_active_account_without_rank_or_codename_is_a_miss()
    {
        using var ctx = await SeededAsync(true,
            Holder("norank", "IV", a => a.Rank = null),
            Holder("noname", "V", a => a.Codename = " "));
        var service = NewService(ctx);

        Assert.Null(await service.CheckAsync("IV"));
        Assert.Null(await service.CheckAsync("V"));
    }

    [Fact]
    public async Task No_real_name_and_no_internal_field_leaves_the_house()
    {
        // the shape carries three things; whatever else the account holds has no field to travel in
        Assert.Equal(["Valid", "Alias", "Title"],
            typeof(PublicServiceIdCheck).GetProperties().Select(p => p.Name).ToArray());

        using var ctx = await SeededAsync(true, Holder("a", "IV", a => a.PendingRealName = "Noch Nicht"));
        var check = await NewService(ctx).CheckAsync("IV");

        var everything = string.Join('|', typeof(PublicServiceIdCheck).GetProperties().Select(p => p.GetValue(check)));
        Assert.DoesNotContain("Klarname", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("Klara", everything, StringComparison.Ordinal);
        Assert.DoesNotContain("Noch Nicht", everything, StringComparison.Ordinal);
    }
}
