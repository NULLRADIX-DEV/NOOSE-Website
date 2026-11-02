using NOOSE_Website.Data.Entities;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services;

/// <summary>Division membership, the HRT rank threshold, the Board by rank and the soft seat limits.</summary>
public class DivisionRulesTests
{
    // ---- Board ----

    [Theory]
    [MemberData(nameof(AllRanks))]
    public void IsBoard_FollowsTheBoardFloor(Rank rank)
        => Assert.Equal(rank >= DivisionRules.BoardFloor, DivisionRules.IsBoard(rank));

    [Fact]
    public void IsBoard_DirectorIsOnTheBoardAndSpecialAgentInChargeIsNot()
    {
        Assert.True(DivisionRules.IsBoard(Rank.Director));
        Assert.False(DivisionRules.IsBoard(Rank.SpecialAgentInCharge));
    }

    [Fact]
    public void IsBoard_WithoutRankIsFalse()
        => Assert.False(DivisionRules.IsBoard(null));

    public static TheoryData<Rank> AllRanks()
    {
        var data = new TheoryData<Rank>();
        foreach (var rank in Enum.GetValues<Rank>())
        {
            data.Add(rank);
        }
        return data;
    }

    // ---- Of ----

    [Fact]
    public void Of_StoredDivisionWins()
    {
        var unit = DivisionRules.Of(Rank.SpecialAgent, Division.Ctd, isTru: true);

        Assert.Equal(new OrgUnit(Division.Ctd, false), unit);
    }

    [Fact]
    public void Of_EmptyDivisionOnARankedAccountDerivesFromTheLegacyTruFlag()
    {
        Assert.Equal(Division.Hrt, DivisionRules.Of(Rank.SpecialAgent, null, isTru: true).Home);
        Assert.Equal(Division.Cid, DivisionRules.Of(Rank.SpecialAgent, null, isTru: false).Home);
    }

    [Fact]
    public void Of_BoardMemberKeepsTheHomeDivision()
    {
        var unit = DivisionRules.Of(Rank.Director, Division.Hrt, isTru: false);

        Assert.True(unit.IsBoard);
        Assert.Equal(Division.Hrt, unit.Home);
    }

    /// <summary>A row demoted by the old release keeps its HRT value but can no longer hold it.</summary>
    [Fact]
    public void Of_HrtBelowSpecialAgentReadsAsCid()
        => Assert.Equal(Division.Cid, DivisionRules.Of(Rank.Trainee, Division.Hrt, isTru: false).Home);

    [Fact]
    public void Of_AccountWithoutRankHasNoUnit()
        => Assert.True(DivisionRules.Of(null, Division.Cid, isTru: false).IsNone);

    [Fact]
    public void Of_PartnerHasNoUnit()
    {
        var partner = Seed.Agent("p", Rank.SpecialAgent, configure: a =>
        {
            a.Rank = null;
            a.PartnerAgency = PartnerAgency.LSPD;
        });

        Assert.True(DivisionRules.Of(partner).IsNone);
    }

    // ---- Normalize / Apply ----

    [Fact]
    public void Normalize_WithoutRankIsNone()
        => Assert.Null(DivisionRules.Normalize(null, Division.Cid));

    [Fact]
    public void Normalize_NothingRequestedMeansCid()
        => Assert.Equal(Division.Cid, DivisionRules.Normalize(Rank.Trainee, null));

    [Theory]
    [InlineData(Division.Cid)]
    [InlineData(Division.Ctd)]
    public void Normalize_CidAndCtdTakeTrainees(Division division)
        => Assert.Equal(division, DivisionRules.Normalize(Rank.Trainee, division));

    [Fact]
    public void Normalize_HrtBelowSpecialAgentFallsBackToCid()
        => Assert.Equal(Division.Cid, DivisionRules.Normalize(Rank.Trainee, Division.Hrt));

    [Fact]
    public void Normalize_HrtFromSpecialAgentStays()
        => Assert.Equal(Division.Hrt, DivisionRules.Normalize(Rank.SpecialAgent, Division.Hrt));

    [Theory]
    [InlineData(Rank.SpecialAgent, Division.Hrt, true)]
    [InlineData(Rank.Director, Division.Hrt, false)]
    [InlineData(Rank.SpecialAgent, Division.Ctd, false)]
    [InlineData(Rank.SpecialAgent, Division.Cid, false)]
    public void IsTru_IsTheHrtBelowTheBoard(Rank rank, Division home, bool expected)
    {
        Assert.Equal(expected, DivisionRules.IsTru(rank, home));
        Assert.Equal(expected, DivisionRules.IsTru(DivisionRules.Of(rank, home, isTru: false)));
    }

    [Fact]
    public void Apply_WritesDivisionAndTruFromTheRank()
    {
        var agent = Seed.Agent("a", Rank.SpecialAgent);

        DivisionRules.Apply(agent, Division.Hrt);
        Assert.Equal(Division.Hrt, agent.Division);
        Assert.True(agent.IsTRU);

        agent.Rank = Rank.Director;
        DivisionRules.Apply(agent, Division.Hrt);
        Assert.Equal(Division.Hrt, agent.Division);
        Assert.False(agent.IsTRU);
    }

    [Fact]
    public void Clear_EmptiesDivisionAndTru()
    {
        var agent = Seed.Agent("a", Rank.SpecialAgent, configure: a => { a.Division = Division.Hrt; a.IsTRU = true; });

        DivisionRules.Clear(agent);

        Assert.Null(agent.Division);
        Assert.False(agent.IsTRU);
    }

    // ---- hold ----

    [Theory]
    [InlineData(Division.Cid, Rank.Trainee, true)]
    [InlineData(Division.Ctd, Rank.Trainee, true)]
    [InlineData(Division.Hrt, Rank.Trainee, false)]
    [InlineData(Division.Hrt, Rank.SpecialAgent, true)]
    [InlineData(Division.Hrt, Rank.Director, true)]
    public void MayHold_OnlyTheHrtAsksForARank(Division division, Rank rank, bool expected)
        => Assert.Equal(expected, DivisionRules.MayHold(division, rank));

    [Theory]
    [InlineData(Division.Cid)]
    [InlineData(Division.Ctd)]
    [InlineData(Division.Hrt)]
    public void MayHold_RanklessNeverMay(Division division)
        => Assert.False(DivisionRules.MayHold(division, null));

    [Fact]
    public void RequireMayHold_NamesTheDivisionAndTheRank()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DivisionRules.RequireMayHold(Division.Hrt, Rank.Trainee));

        Assert.StartsWith("Das Hostage Rescue Team (HRT)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Special Agent", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireMayHold_RanklessAccountBelongsToNoDivision()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => DivisionRules.RequireMayHold(Division.Cid, null));

        Assert.Contains("Dienstgrad", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireMayHold_PassesAtTheThreshold()
        => DivisionRules.RequireMayHold(Division.Hrt, Rank.SpecialAgent);

    // ---- display and limits ----

    [Fact]
    public void DivisionDisplay_KnowsEveryDivision()
    {
        Assert.True(Enum.GetValues<Division>().SequenceEqual(DivisionDisplay.All));
        Assert.Equal("Criminal Investigation Division (CID)", DivisionDisplay.Name(Division.Cid));
        Assert.Equal("Counterterrorism Division (CTD)", DivisionDisplay.Name(Division.Ctd));
        Assert.Equal("Hostage Rescue Team (HRT)", DivisionDisplay.Name(Division.Hrt));
        foreach (var division in DivisionDisplay.All)
        {
            Assert.NotEqual("—", DivisionDisplay.Short(division));
            Assert.NotEmpty(DivisionDisplay.Icon(division));
            Assert.NotEqual(MudBlazor.Color.Default, DivisionDisplay.Colour(division));
        }
    }

    [Fact]
    public void Limits_AreTheConceptNumbers()
    {
        Assert.Equal(21, DivisionDisplay.Limit(Division.Cid));
        Assert.Equal(6, DivisionDisplay.Limit(Division.Ctd));
        Assert.Equal(9, DivisionDisplay.Limit(Division.Hrt));
        Assert.Equal(4, DivisionDisplay.BoardLimit);
        Assert.Equal(4, DivisionRules.Limit(new OrgUnit(Division.Cid, IsBoard: true)));
        Assert.Equal(9, DivisionRules.Limit(new OrgUnit(Division.Hrt, IsBoard: false)));
    }

    [Fact]
    public void Label_SaysBoardWithTheHome()
    {
        Assert.Equal("CTD", DivisionRules.Label(new OrgUnit(Division.Ctd, false)));
        Assert.Equal("Board (HRT)", DivisionRules.Label(new OrgUnit(Division.Hrt, true)));
        Assert.Equal("—", DivisionRules.Label(default));
    }

    [Fact]
    public void SameSeat_ABoardMemberHoldsOneWhateverTheHome()
    {
        Assert.True(DivisionRules.SameSeat(new OrgUnit(Division.Cid, true), new OrgUnit(Division.Hrt, true)));
        Assert.False(DivisionRules.SameSeat(new OrgUnit(Division.Cid, true), new OrgUnit(Division.Cid, false)));
        Assert.True(DivisionRules.SameSeat(new OrgUnit(Division.Ctd, false), new OrgUnit(Division.Ctd, false)));
        Assert.False(DivisionRules.SameSeat(new OrgUnit(Division.Ctd, false), new OrgUnit(Division.Cid, false)));
    }

    [Fact]
    public void RequireSeat_BelowTheLimitPasses()
        => DivisionRules.RequireSeat(new OrgUnit(Division.Ctd, false), takenByOthers: 5, confirmOverLimit: false);

    [Fact]
    public void RequireSeat_FullWithoutConfirmationThrowsTheGermanMessage()
    {
        var ex = Assert.Throws<DivisionLimitExceededException>(
            () => DivisionRules.RequireSeat(new OrgUnit(Division.Ctd, false), takenByOthers: 6, confirmOverLimit: false));

        Assert.Equal(6, ex.Taken);
        Assert.Equal(6, ex.Limit);
        Assert.StartsWith("Die Counterterrorism Division (CTD) ist mit 6 von 6 Plätzen", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Bestätigung", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireSeat_FullWithConfirmationPasses()
        => DivisionRules.RequireSeat(new OrgUnit(Division.Hrt, false), takenByOthers: 12, confirmOverLimit: true);

    [Fact]
    public void RequireSeat_TheBoardHasItsOwnLimit()
    {
        var ex = Assert.Throws<DivisionLimitExceededException>(
            () => DivisionRules.RequireSeat(new OrgUnit(Division.Cid, true), takenByOthers: 4, confirmOverLimit: false));

        Assert.StartsWith("Das Board of Directors ist mit 4 von 4 Plätzen", ex.Message, StringComparison.Ordinal);
    }

    // ---- counting ----

    private static async Task<int> CountAsync(SqliteTestContext ctx, OrgUnit unit, string? except = null)
    {
        await using var db = ctx.NewContext();
        return await DivisionRules.CountAsync(db.Users, unit, except);
    }

    [Fact]
    public async Task CountAsync_CountsSelectableAgentsOfTheUnitOnly()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.AddRange(
                Seed.Agent("cid-1", Rank.Trainee, configure: a => a.Division = Division.Cid),
                Seed.Agent("cid-2", Rank.SpecialAgent, configure: a => a.Division = Division.Cid),
                Seed.Agent("ctd-1", Rank.SpecialAgent, configure: a => a.Division = Division.Ctd),
                Seed.Agent("blocked", Rank.SpecialAgent, AgentStatus.Blocked, a => a.Division = Division.Cid),
                Seed.Agent("lead", Rank.SpecialAgent, configure: a => { a.Division = Division.Cid; a.IsTeamLead = true; }),
                Seed.Agent("partner", Rank.SpecialAgent, configure: a =>
                {
                    a.Rank = null;
                    a.PartnerAgency = PartnerAgency.DoJ;
                }));
            db.SaveChanges();
        }

        Assert.Equal(2, await CountAsync(ctx, new OrgUnit(Division.Cid, false)));
        Assert.Equal(1, await CountAsync(ctx, new OrgUnit(Division.Ctd, false)));
        Assert.Equal(0, await CountAsync(ctx, new OrgUnit(Division.Hrt, false)));
    }

    [Fact]
    public async Task CountAsync_LeavesOutTheAgentBeingMoved()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.AddRange(
                Seed.Agent("a", Rank.SpecialAgent, configure: x => x.Division = Division.Ctd),
                Seed.Agent("b", Rank.SpecialAgent, configure: x => x.Division = Division.Ctd));
            db.SaveChanges();
        }

        Assert.Equal(1, await CountAsync(ctx, new OrgUnit(Division.Ctd, false), except: "a"));
    }

    /// <summary>The Board counts as one unit by rank; its members hold no seat in their home division.</summary>
    [Fact]
    public async Task CountAsync_BoardMembersCountAsTheBoardNotTheirHome()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.AddRange(
                Seed.Agent("director", Rank.Director, configure: a => a.Division = Division.Hrt),
                Seed.Agent("deputy", Rank.DeputyDirector, configure: a => a.Division = Division.Cid),
                Seed.Agent("agent", Rank.SpecialAgent, configure: a => a.Division = Division.Hrt));
            db.SaveChanges();
        }

        Assert.Equal(2, await CountAsync(ctx, new OrgUnit(null, IsBoard: true)));
        Assert.Equal(1, await CountAsync(ctx, new OrgUnit(Division.Hrt, false)));
        Assert.Equal(0, await CountAsync(ctx, new OrgUnit(Division.Cid, false)));
    }

    [Fact]
    public async Task CountAsync_DerivesTheHomeOfARowWithoutDivision()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.AddRange(
                Seed.Agent("tru", Rank.SpecialAgent, configure: a => a.IsTRU = true),
                Seed.Agent("plain", Rank.SpecialAgent));
            db.SaveChanges();
        }

        Assert.Equal(1, await CountAsync(ctx, new OrgUnit(Division.Hrt, false)));
        Assert.Equal(1, await CountAsync(ctx, new OrgUnit(Division.Cid, false)));
    }
}
