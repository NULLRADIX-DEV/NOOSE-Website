using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Real names beside codenames: who is listed and who may read the list.</summary>
public sealed class AgentRealNamesTests
{
    private sealed class FixedAuth(ClaimsPrincipal user) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(user));
    }

    private static async Task<SqliteTestContext> SeededAsync()
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        db.Users.AddRange(
            Seed.Agent("agent", Rank.SpecialAgent, configure: a => a.RealName = "Klara Klarname"),
            Seed.Agent("fired", Rank.SpecialAgent, AgentStatus.Blocked, a => a.RealName = "Ex Agent"),
            Seed.Agent("reader", Rank.Director, configure: a => { a.RealName = "Aufsicht Echt"; a.IsTeamLead = true; }),
            Seed.Agent("partner", Rank.SpecialAgent, configure: a => { a.RealName = "Partner Echt"; a.PartnerAgency = PartnerAgency.LSPD; }),
            Seed.Agent("citizen", Rank.Trainee, AgentStatus.Civilian, a => { a.Rank = null; a.RealName = "Buerger Echt"; }),
            Seed.Agent("blank", Rank.SpecialAgent, configure: a => a.RealName = "  "));
        await db.SaveChangesAsync();
        return ctx;
    }

    private static AgentRealNames Lookup(SqliteTestContext ctx, ClaimsPrincipal viewer)
        => new(ctx.Factory, new MemoryCache(new MemoryCacheOptions()), new FixedAuth(viewer));

    [Fact]
    public async Task Lists_released_in_house_accounts_only()
    {
        using var ctx = await SeededAsync();
        await using var db = ctx.NewContext();

        var map = await AgentRealNames.LoadAsync(db);

        Assert.Equal(new[] { "agent", "fired" }, map.Keys.Order().ToArray());
        Assert.Equal("Klara Klarname", map["agent"]);
    }

    [Fact]
    public async Task Special_agent_reads_the_names()
    {
        using var ctx = await SeededAsync();
        var viewer = ClaimsPrincipalBuilder.Agent("viewer").WithRank(Rank.SpecialAgent).Build();

        Assert.Contains("agent", (await Lookup(ctx, viewer).GetAsync()).Keys);
    }

    [Theory]
    [InlineData("trainee")]
    [InlineData("reader")]
    [InlineData("partner")]
    public async Task Others_get_nothing(string kind)
    {
        using var ctx = await SeededAsync();
        var viewer = kind switch
        {
            "trainee" => ClaimsPrincipalBuilder.Agent("viewer").WithRank(Rank.Trainee).Build(),
            "reader" => ClaimsPrincipalBuilder.Agent("viewer").WithRank(Rank.Director).AsTeamLead().Build(),
            _ => ClaimsPrincipalBuilder.Agent("viewer").AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build(),
        };

        Assert.Empty(await Lookup(ctx, viewer).GetAsync());
    }
}
