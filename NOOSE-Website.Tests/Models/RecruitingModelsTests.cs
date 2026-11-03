using NOOSE_Website.Models.Recruiting;

namespace NOOSE_Website.Tests.Models;

public class RecruitingModelsTests
{
    private static TestEvaluation Eval(int totalPoints, int maxPoints, int? passPercent) =>
        new(
            Title: "Test",
            CompletedAt: null,
            TotalPoints: totalPoints,
            MaxPoints: maxPoints,
            PassPercent: passPercent,
            GradedAt: null,
            GradedByName: null,
            Items: Array.Empty<TestEvaluationItem>());

    // ----- Percent: div-by-zero guard -----

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(100)]
    public void Percent_MaxPointsZero_ReturnsZero(int totalPoints)
    {
        var eval = Eval(totalPoints, 0, null);
        Assert.Equal(0, eval.Percent);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-10)]
    public void Percent_MaxPointsNegative_ReturnsZero(int maxPoints)
    {
        // MaxPoints > 0 is false for negative -> guard returns 0
        var eval = Eval(5, maxPoints, null);
        Assert.Equal(0, eval.Percent);
    }

    // ----- Percent: normal computation -----

    [Theory]
    [InlineData(0, 10, 0)]
    [InlineData(5, 10, 50)]
    [InlineData(10, 10, 100)]
    [InlineData(1, 1, 100)]
    [InlineData(1, 3, 33)]   // 33.33 -> 33
    [InlineData(2, 3, 67)]   // 66.66 -> 67
    [InlineData(15, 10, 150)] // over 100% is not clamped
    public void Percent_NormalValues_RoundsCorrectly(int totalPoints, int maxPoints, int expected)
    {
        var eval = Eval(totalPoints, maxPoints, null);
        Assert.Equal(expected, eval.Percent);
    }

    // ----- Percent: banker's rounding (Math.Round default = ToEven) at .5 midpoints -----

    [Theory]
    [InlineData(1, 8, 12)]  // 12.5 -> 12 (even)
    [InlineData(3, 8, 38)]  // 37.5 -> 38 (even)
    [InlineData(5, 8, 62)]  // 62.5 -> 62 (even)
    [InlineData(7, 8, 88)]  // 87.5 -> 88 (even)
    public void Percent_MidpointValues_UsesBankersRounding(int totalPoints, int maxPoints, int expected)
    {
        var eval = Eval(totalPoints, maxPoints, null);
        Assert.Equal(expected, eval.Percent);
    }

    // ----- Passed: null tri-state -----

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5, 10)]
    [InlineData(10, 10)]
    public void Passed_PassPercentNull_ReturnsNull(int totalPoints, int maxPoints)
    {
        var eval = Eval(totalPoints, maxPoints, null);
        Assert.Null(eval.Passed);
    }

    // ----- Passed: boundary at equal, below, above -----

    [Theory]
    [InlineData(5, 10, 50, true)]   // Percent 50 == PassPercent 50 -> pass
    [InlineData(49, 100, 50, false)] // Percent 49 < 50 -> fail
    [InlineData(51, 100, 50, true)]  // Percent 51 > 50 -> pass
    [InlineData(10, 10, 100, true)]  // 100 == 100 -> pass
    [InlineData(99, 100, 100, false)] // 99 < 100 -> fail
    public void Passed_WithThreshold_ComparesPercentAgainstPassPercent(
        int totalPoints, int maxPoints, int passPercent, bool expected)
    {
        var eval = Eval(totalPoints, maxPoints, passPercent);
        Assert.Equal(expected, eval.Passed);
    }

    [Fact]
    public void Passed_PassPercentZero_AlwaysPasses()
    {
        // Percent 0 >= PassPercent 0 -> true
        var eval = Eval(0, 10, 0);
        Assert.Equal(0, eval.Percent);
        Assert.True(eval.Passed);
    }

    [Fact]
    public void Passed_MaxPointsZeroWithThreshold_UsesGuardedPercentZero()
    {
        // Percent guarded to 0; 0 >= 50 -> false
        var failEval = Eval(100, 0, 50);
        Assert.Equal(0, failEval.Percent);
        Assert.False(failEval.Passed);

        // 0 >= 0 -> true
        var passEval = Eval(100, 0, 0);
        Assert.True(passEval.Passed);

        // still null tri-state when no threshold
        var nullEval = Eval(100, 0, null);
        Assert.Null(nullEval.Passed);
    }

    // ----- record positional deconstruction / equality -----

    [Fact]
    public void TestEvaluation_ValueEquality_SameItemsReferenceEqual()
    {
        var items = Array.Empty<TestEvaluationItem>();
        var a = new TestEvaluation("T", null, 5, 10, 50, null, null, items);
        var b = new TestEvaluation("T", null, 5, 10, 50, null, null, items);
        Assert.Equal(a, b);
    }

    [Fact]
    public void TestEvaluation_ValueEquality_DiffersOnTotalPoints()
    {
        var items = Array.Empty<TestEvaluationItem>();
        var a = new TestEvaluation("T", null, 5, 10, 50, null, null, items);
        var b = new TestEvaluation("T", null, 6, 10, 50, null, null, items);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TestOptionView_ExposesPositionalProperties()
    {
        var opt = new TestOptionView("opt-1", "Label A");
        Assert.Equal("opt-1", opt.OptionId);
        Assert.Equal("Label A", opt.Label);
    }

    [Fact]
    public void LinkedPersonInfo_ExposesPositionalProperties()
    {
        var info = new LinkedPersonInfo("p1", "Doe", "FIB-P-2026-0001", 42, 80, null, true);
        Assert.Equal("p1", info.PersonId);
        Assert.Equal("Doe", info.Name);
        Assert.Equal("FIB-P-2026-0001", info.CaseNumber);
        Assert.Equal(42, info.ThreatScore);
        Assert.Equal(80, info.ThreatConfidence);
        Assert.Null(info.ScoreCalculatedAt);
        Assert.True(info.IsClassified);
    }

    // ----- mutable model defaults -----

    [Fact]
    public void BewerbungSubmitModel_Defaults_NameEmptyOthersNull()
    {
        var m = new BewerbungSubmitModel();
        Assert.Equal(string.Empty, m.Name);
        Assert.Null(m.AcademicDegree);
        Assert.Null(m.BirthDate);
        Assert.Null(m.Employer);
        Assert.Null(m.PriorExperience);
        Assert.Null(m.CoverLetter);
    }

    [Fact]
    public void TestAnswerInput_Defaults_QuestionIdEmptyOthersNull()
    {
        var a = new TestAnswerInput();
        Assert.Equal(string.Empty, a.QuestionId);
        Assert.Empty(a.SelectedOptionIds);
        Assert.Null(a.FreeText);
    }
}
