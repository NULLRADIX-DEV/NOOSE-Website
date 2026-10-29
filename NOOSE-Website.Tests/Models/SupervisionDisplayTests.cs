using NOOSE_Website.Models.Personnel;

namespace NOOSE_Website.Tests.Models;

public class SupervisionDisplayTests
{
    [Fact]
    public void Median_OfNothing_IsNull()
        => Assert.Null(SupervisionMath.Median([]));

    [Theory]
    [InlineData(new[] { 3, 1, 2 }, 2.0)]
    [InlineData(new[] { 4, 1, 3, 2 }, 2.5)]
    [InlineData(new[] { 0, 0, 9 }, 0.0)]
    public void Median_IsTheMiddleValue(int[] values, double expected)
        => Assert.Equal(expected, SupervisionMath.Median(values));

    [Theory]
    [InlineData(30, 30)]
    [InlineData(90, 90)]
    [InlineData(365, 365)]
    [InlineData(null, 90)]
    [InlineData(14, 90)]
    [InlineData(-1, 90)]
    public void Window_FallsBackToTheDefault(int? asked, int expected)
        => Assert.Equal(expected, SupervisionWindow.Normalize(asked));

    [Theory]
    [InlineData(14.0, 14, "7")]
    [InlineData(4.0, 30, "0,9")]
    [InlineData(0.0, 90, "0")]
    public void PerWeek_ScalesTheWindowToSevenDays(double total, int days, string expected)
        => Assert.Equal(expected, SupervisionDisplay.PerWeek(total, days));

    [Fact]
    public void Team_NamesTheMedian()
    {
        Assert.Equal("Team 2,5", SupervisionDisplay.Team(2.5));
        Assert.Equal("Team 3", SupervisionDisplay.Team(3));
        Assert.Equal("", SupervisionDisplay.Team(null));
    }

    [Fact]
    public void TeamPerWeek_ScalesTheMedianLikeTheValue()
    {
        Assert.Equal("Team 0,5", SupervisionDisplay.TeamPerWeek(2, 30));
        Assert.Equal("", SupervisionDisplay.TeamPerWeek(null, 30));
    }

    [Theory]
    [InlineData(30, "30 Tage")]
    [InlineData(90, "90 Tage")]
    [InlineData(365, "1 Jahr")]
    public void Window_ReadsAsWords(int days, string expected)
        => Assert.Equal(expected, SupervisionDisplay.Window(days));
}
