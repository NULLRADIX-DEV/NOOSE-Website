using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Radio;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>The search box of the radio plan. It has no page test, so it is tested here.</summary>
public class RadioFilterTests
{
    private static RadioChannelRow Channel(
        string frequency = "411.7", string label = "TRU Einsatzkanal", string? note = null, string? taskforce = null)
        => new("id", frequency, label, RadioScope.Noose, null, taskforce is null ? null : "tf1", taskforce, note, false);

    private static RadioFactionRow Faction(string frequency, string name = "Ballas")
        => new("f1", name, frequency, false, true);

    // ==================== nothing typed ====================

    [Fact]
    public void An_empty_search_keeps_every_row()
    {
        Assert.True(RadioFilter.Matches(Channel(), null));
        Assert.True(RadioFilter.Matches(Channel(), "   "));
        Assert.True(RadioFilter.Matches(Faction("98.4"), null));
    }

    // ==================== channels ====================

    [Fact]
    public void A_channel_is_found_by_a_comma_spelled_frequency()
        => Assert.True(RadioFilter.Matches(Channel("411.7"), "411,7"));

    [Fact]
    public void A_channel_is_found_by_label_note_and_taskforce()
    {
        Assert.True(RadioFilter.Matches(Channel(label: "TRU Einsatzkanal"), "einsatz"));
        Assert.True(RadioFilter.Matches(Channel(note: "nur im Einsatz"), "nur im"));
        Assert.True(RadioFilter.Matches(Channel(taskforce: "Nachtfalke"), "nachtfalke"));
    }

    [Fact]
    public void A_channel_that_matches_nothing_is_dropped()
        => Assert.False(RadioFilter.Matches(Channel("411.7", "TRU"), "500"));

    // ==================== factions ====================

    [Fact]
    public void A_faction_frequency_is_found_in_both_spellings()
    {
        // stored with a dot, typed with a comma
        Assert.True(RadioFilter.Matches(Faction("411.7"), "411,7"));
        // stored with a comma, because nothing normalises the faction record - typed either way
        Assert.True(RadioFilter.Matches(Faction("411,7"), "411,7"));
        Assert.True(RadioFilter.Matches(Faction("411,7"), "411.7"));
    }

    [Fact]
    public void A_faction_is_found_by_name()
        => Assert.True(RadioFilter.Matches(Faction("98.4", "Ballas"), "balla"));

    [Fact]
    public void A_faction_that_matches_nothing_is_dropped()
        => Assert.False(RadioFilter.Matches(Faction("98.4", "Ballas"), "411.7"));
}
