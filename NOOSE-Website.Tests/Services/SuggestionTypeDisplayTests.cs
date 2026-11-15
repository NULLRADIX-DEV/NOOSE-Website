using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Tests.Services;

/// <summary>German labels and the panel ordering for the suggestion categories.</summary>
public class SuggestionTypeDisplayTests
{
    [Fact]
    public void Every_type_has_a_label()
    {
        foreach (var type in Enum.GetValues<SuggestionType>())
        {
            Assert.False(string.IsNullOrWhiteSpace(SuggestionTypeDisplay.Name(type)));
            Assert.NotEqual("—", SuggestionTypeDisplay.Name(type));
        }
    }

    [Fact]
    public void All_covers_every_live_type_exactly_once()
    {
        var types = Enum.GetValues<SuggestionType>().Where(t => !SuggestionTypeDisplay.Retired.Contains(t)).ToArray();
        Assert.DoesNotContain(SuggestionTypeDisplay.All, SuggestionTypeDisplay.Retired.Contains);
        Assert.Equal(types.Length, SuggestionTypeDisplay.All.Count);
        Assert.Equal(types.OrderBy(t => t), SuggestionTypeDisplay.All.OrderBy(t => t));
    }
}
