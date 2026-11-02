using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NOOSE_Website.Authorization;
using NOOSE_Website.Components.Account;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>What the login claims factory writes for a row and what the principal reads back.</summary>
public class DivisionClaimsTests
{
    /// <summary>The production factory over the account row, like every sign-in and every watcher run.</summary>
    private static async Task<ClaimsPrincipal> BuildAsync(Agent agent)
    {
        using var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        db.Users.Add(agent);
        await db.SaveChangesAsync();

        var users = new UserManager<Agent>(new UserStore<Agent>(db), Options.Create(new IdentityOptions()),
            new PasswordHasher<Agent>(), [], [], new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(), null!, NullLogger<UserManager<Agent>>.Instance);
        var roles = new RoleManager<IdentityRole>(new RoleStore<IdentityRole>(db), [],
            new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(),
            NullLogger<RoleManager<IdentityRole>>.Instance);
        var factory = new AgentClaimsPrincipalFactory(users, roles, Options.Create(new IdentityOptions()),
            new ConfigurationBuilder().Build());
        return await factory.CreateAsync(agent);
    }

    private static Agent Row(Rank? rank, Division? division, bool isTru, Action<Agent>? cfg = null)
        => Seed.Agent("a", rank ?? Rank.Trainee, configure: a =>
        {
            a.Rank = rank;
            a.Division = division;
            a.IsTRU = isTru;
            a.SecurityStamp = Guid.NewGuid().ToString();
            cfg?.Invoke(a);
        });

    [Fact]
    public async Task HrtMember_RoundTripsDivisionAndTru()
    {
        var user = await BuildAsync(Row(Rank.SpecialAgent, Division.Hrt, isTru: true));

        Assert.Equal(Division.Hrt, user.GetDivision());
        Assert.True(user.IsTRU());
        Assert.False(user.IsBoard());
        Assert.Equal(Rank.SpecialAgent, user.GetRank());
    }

    [Theory]
    [InlineData(Division.Cid)]
    [InlineData(Division.Ctd)]
    public async Task OtherDivisions_RoundTripWithoutTru(Division division)
    {
        var user = await BuildAsync(Row(Rank.SpecialAgent, division, isTru: false));

        Assert.Equal(division, user.GetDivision());
        Assert.False(user.IsTRU());
    }

    /// <summary>The Board keeps its home division but is never TRU, even when the column still says so.</summary>
    [Fact]
    public async Task BoardMember_KeepsTheHomeAndIsNeverTru()
    {
        var user = await BuildAsync(Row(Rank.Director, Division.Hrt, isTru: true));

        Assert.Equal(Division.Hrt, user.GetDivision());
        Assert.False(user.IsTRU());
        Assert.True(user.IsBoard());
    }

    /// <summary>A row from before the division column: the legacy TRU flag still says where the agent belongs.</summary>
    [Fact]
    public async Task LegacyRow_DerivesTheHomeFromTheTruFlag()
    {
        var tru = await BuildAsync(Row(Rank.SeniorSpecialAgent, null, isTru: true));
        var plain = await BuildAsync(Row(Rank.SpecialAgent, null, isTru: false));

        Assert.Equal(Division.Hrt, tru.GetDivision());
        Assert.True(tru.IsTRU());
        Assert.Equal(Division.Cid, plain.GetDivision());
        Assert.False(plain.IsTRU());
    }

    [Fact]
    public async Task HrtBelowSpecialAgent_ReadsAsCid()
    {
        var user = await BuildAsync(Row(Rank.Trainee, Division.Hrt, isTru: true));

        Assert.Equal(Division.Cid, user.GetDivision());
        Assert.False(user.IsTRU());
    }

    [Fact]
    public async Task Partner_CarriesNoDivision()
    {
        var user = await BuildAsync(Row(null, Division.Hrt, isTru: true, a =>
        {
            a.PartnerAgency = PartnerAgency.LSPD;
            a.PartnerRank = PartnerRank.Member;
        }));

        Assert.Null(user.GetDivision());
        Assert.False(user.IsTRU());
        Assert.Null(user.FindFirst(AgentClaimTypes.Division));
    }

    [Fact]
    public async Task AccountWithoutRank_CarriesNoDivision()
    {
        var user = await BuildAsync(Row(null, null, isTru: false, a => a.Status = AgentStatus.Civilian));

        Assert.Null(user.FindFirst(AgentClaimTypes.Division));
        Assert.False(user.IsBoard());
    }
}
