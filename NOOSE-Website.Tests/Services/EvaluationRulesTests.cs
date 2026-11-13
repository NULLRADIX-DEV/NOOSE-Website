using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>Who may read and write service evaluations, the preset title and the grade scale.</summary>
public class EvaluationRulesTests
{
    private static ClaimsPrincipalBuilder Agent(string id, Rank rank) => ClaimsPrincipalBuilder.Agent(id).WithRank(rank);

    [Fact]
    public void MayRead_OwnFileForAnyInHouseAgent()
        => Assert.True(EvaluationRules.MayRead(Agent("a1", Rank.Trainee).Build(), "a1"));

    [Fact]
    public void MayRead_AnotherFileOnlyForThePersonnelSide()
    {
        Assert.False(EvaluationRules.MayRead(Agent("a2", Rank.SpecialAgentInCharge).Build(), "a1"));
        Assert.True(EvaluationRules.MayRead(Agent("a2", Rank.SectionChief).Build(), "a1"));
        Assert.True(EvaluationRules.MayRead(Agent("a2", Rank.SpecialAgent).AsHrb().Build(), "a1"));
        Assert.True(EvaluationRules.MayRead(Agent("a2", Rank.Trainee).AsAdmin().Build(), "a1"));
        Assert.True(EvaluationRules.MayRead(ClaimsPrincipalBuilder.Agent("tl").AsTeamLead().Build(), "a1"));
    }

    [Fact]
    public void MayRead_NeverForAPartner()
        => Assert.False(EvaluationRules.MayRead(
            Agent("p1", Rank.Director).AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build(), "a1"));

    [Fact]
    public void MayWrite_LeadershipAndAdministrationWithWriteAccess()
    {
        Assert.True(EvaluationRules.MayWrite(Agent("a", Rank.SectionChief).Build()));
        Assert.True(EvaluationRules.MayWrite(Agent("a", Rank.SpecialAgent).AsHrb().Build()));
        Assert.False(EvaluationRules.MayWrite(Agent("a", Rank.SpecialAgentInCharge).Build()));
        Assert.False(EvaluationRules.MayWrite(ClaimsPrincipalBuilder.Agent("tl").AsTeamLead().WithRank(Rank.Director).Build()));
        Assert.False(EvaluationRules.MayWrite(Agent("d", Rank.Director).AsHrb().AsDemo().Build()));
    }

    [Fact]
    public void CleanTitle_EmptyGetsThePresetWithTheDate()
    {
        var day = new DateTime(2026, 11, 13, 18, 30, 0);
        Assert.Equal("Beurteilung vom 13.11.2026", EvaluationRules.CleanTitle("  ", day));
        Assert.Equal("Beurteilung vom 13.11.2026", EvaluationRules.CleanTitle(null, day));
        Assert.Equal("Zwischenbeurteilung", EvaluationRules.CleanTitle(" Zwischenbeurteilung ", day));
    }

    [Fact]
    public void CleanTitle_RefusesAnOversizedTitle()
        => Assert.Throws<InvalidOperationException>(
            () => EvaluationRules.CleanTitle(new string('x', EvaluationRules.MaxTitleLength + 1), DateTime.Now));

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void RequireValidGrade_RefusesAGradeOutsideTheScale(int grade)
        => Assert.Throws<InvalidOperationException>(() => EvaluationRules.RequireValidGrade((EvaluationGrade)grade));

    [Fact]
    public void Grades_AreTheFiveStepsOfTheRegulation()
    {
        Assert.Equal([1, 2, 3, 4, 5], EvaluationGradeDisplay.All.Select(g => (int)g));
        Assert.Equal(["sehr gut", "gut", "befriedigend", "mangelhaft", "ungenügend"],
            EvaluationGradeDisplay.All.Select(EvaluationGradeDisplay.Word));
        Assert.Equal("2 – gut", EvaluationGradeDisplay.Name(EvaluationGrade.Good));
    }
}
