using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>Calendar-week arithmetic of the food limit.</summary>
public class FoodWeekTests
{
    [Theory]
    // Monday 16.11.2026 through Sunday 22.11.2026
    [InlineData(2026, 11, 16, 0)]
    [InlineData(2026, 11, 18, 13)]
    [InlineData(2026, 11, 22, 23)]
    public void StartOf_ReturnsTheMondayOfTheWeek(int year, int month, int day, int hour)
    {
        var start = FoodWeek.StartOf(new DateTime(year, month, day, hour, 59, 0));

        Assert.Equal(new DateTime(2026, 11, 16), start);
        Assert.Equal(DayOfWeek.Monday, start.DayOfWeek);
        Assert.Equal(TimeSpan.Zero, start.TimeOfDay);
        Assert.Equal(DateTimeKind.Unspecified, start.Kind);
    }

    [Fact]
    public void StartOf_SundayBelongsToTheWeekBefore_NotTheNextOne()
    {
        Assert.Equal(new DateTime(2026, 11, 16), FoodWeek.StartOf(new DateTime(2026, 11, 22)));
        Assert.Equal(new DateTime(2026, 11, 23), FoodWeek.StartOf(new DateTime(2026, 11, 23)));
    }

    [Fact]
    public void StartOf_CrossesTheYearBoundary()
    {
        // Friday 01.01.2027 belongs to the week starting Monday 28.12.2026
        Assert.Equal(new DateTime(2026, 12, 28), FoodWeek.StartOf(new DateTime(2027, 1, 1, 8, 0, 0)));
    }

    [Fact]
    public void Current_IsAMonday()
    {
        var current = FoodWeek.Current();

        Assert.Equal(DayOfWeek.Monday, current.DayOfWeek);
        Assert.Equal(FoodWeek.StartOf(DateTime.Now), current);
    }

    [Fact]
    public void Label_ShowsIsoWeekAndDateRange()
    {
        Assert.Equal("KW 47 · 16.11.–22.11.2026", FoodWeek.Label(new DateTime(2026, 11, 16)));
        Assert.Equal("KW 53 · 28.12.–03.01.2027", FoodWeek.Label(new DateTime(2026, 12, 28)));
    }
}
