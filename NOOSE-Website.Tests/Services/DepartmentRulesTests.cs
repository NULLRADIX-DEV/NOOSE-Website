using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>Unit tests for the TRU/HRB rank threshold from the service regulation.</summary>
public class DepartmentRulesTests
{
    [Theory]
    [InlineData(Rank.SpecialAgent)]
    [InlineData(Rank.SeniorSpecialAgent)]
    [InlineData(Rank.SpecialAgentInCharge)]
    [InlineData(Rank.DeputyDirector)]
    [InlineData(Rank.Director)]
    public void MayHold_FromSpecialAgentUpwards(Rank rank)
        => Assert.True(DepartmentRules.MayHold(rank));

    [Fact]
    public void MayHold_TraineeMayNot()
        => Assert.False(DepartmentRules.MayHold(Rank.Trainee));

    /// <summary>A rankless account is a partner or a pending one; neither belongs to a department.</summary>
    [Fact]
    public void MayHold_WithoutRankMayNot()
        => Assert.False(DepartmentRules.MayHold(null));

    [Fact]
    public void MinimumRank_IsSpecialAgent()
        => Assert.Equal(Rank.SpecialAgent, DepartmentRules.MinimumRank);

    [Fact]
    public void RequireMayHold_PassesAtTheThreshold()
        => DepartmentRules.RequireMayHold(Rank.SpecialAgent, "Die Administration Division");

    [Fact]
    public void RequireMayHold_NamesTheDepartmentAndTheRank()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DepartmentRules.RequireMayHold(Rank.Trainee, "Die Administration Division"));

        Assert.Contains("Administration Division", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Special Agent", ex.Message, StringComparison.Ordinal);
    }
}
