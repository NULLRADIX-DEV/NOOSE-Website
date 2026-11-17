using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>Unit tests for who may carry the Administration Division flag.</summary>
public class DepartmentRulesTests
{
    [Theory]
    [InlineData(Rank.Trainee)]
    [InlineData(Rank.SpecialAgent)]
    [InlineData(Rank.SeniorSpecialAgent)]
    [InlineData(Rank.SpecialAgentInCharge)]
    [InlineData(Rank.SectionChief)]
    [InlineData(Rank.AssistantDirector)]
    [InlineData(Rank.DeputyDirector)]
    [InlineData(Rank.Director)]
    public void MayHold_EveryRank(Rank rank)
        => Assert.True(DepartmentRules.MayHold(rank));

    /// <summary>A rankless account is a partner or a pending one; neither belongs to a department.</summary>
    [Fact]
    public void MayHold_WithoutRankMayNot()
        => Assert.False(DepartmentRules.MayHold(null));

    [Fact]
    public void MinimumRank_IsTrainee()
        => Assert.Equal(Rank.Trainee, DepartmentRules.MinimumRank);

    [Fact]
    public void RequireMayHold_PassesForATrainee()
        => DepartmentRules.RequireMayHold(Rank.Trainee, "Die Administration Division");

    [Fact]
    public void RequireMayHold_NamesTheDepartment()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DepartmentRules.RequireMayHold(null, "Die Administration Division"));

        Assert.Contains("Administration Division", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Switches and guard share one sentence.</summary>
    [Fact]
    public void Hint_IsTheSentenceTheGuardThrows()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => DepartmentRules.RequireMayHold(null, DepartmentRules.Name));

        Assert.Equal(ex.Message, DepartmentRules.Hint);
        Assert.Equal("Die Administration Division gibt es nur für freigegebene Agenten mit Dienstgrad.", DepartmentRules.Hint);
    }

    [Fact]
    public void ColumnLabel_NamesTheDivisionNotTheOldBranch()
    {
        Assert.Equal("Administration", DepartmentRules.ColumnLabel);
        Assert.DoesNotContain("HRB", DepartmentRules.Hint, StringComparison.Ordinal);
    }
}
