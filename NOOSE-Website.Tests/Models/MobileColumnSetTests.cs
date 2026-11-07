using NOOSE_Website.Models.Navigation;

namespace NOOSE_Website.Tests.Models;

/// <summary>Which list columns a phone shows: the agent's choice, cleaned up, or the list's defaults.</summary>
public sealed class MobileColumnSetTests
{
    private static readonly string[] People = ["aktenzeichen", "name", "aliase", "gefaehrdung", "zuletzt"];
    private static readonly string[] PeopleDefaults = ["name", "gefaehrdung"];

    [Fact]
    public void Resolve_uses_defaults_when_nothing_stored()
    {
        var visible = MobileColumnSet.Resolve(People, PeopleDefaults, null);

        Assert.Equal(["gefaehrdung", "name"], visible.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Resolve_uses_defaults_when_stored_is_empty()
    {
        Assert.Equal(["gefaehrdung", "name"], MobileColumnSet.Resolve(People, PeopleDefaults, []).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Resolve_keeps_the_stored_choice()
    {
        var visible = MobileColumnSet.Resolve(People, PeopleDefaults, ["aktenzeichen", "zuletzt"]);

        Assert.Equal(["aktenzeichen", "zuletzt"], visible.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Resolve_drops_unknown_keys()
    {
        var visible = MobileColumnSet.Resolve(["name", "gefaehrdung"], PeopleDefaults, ["name", "alt"]);

        Assert.Equal(["name"], visible);
    }

    [Fact]
    public void Resolve_falls_back_when_stored_is_empty_after_cleanup()
    {
        // a column renamed or removed since the agent chose: the list must not end up without any column
        var visible = MobileColumnSet.Resolve(People, PeopleDefaults, ["alt", "weg"]);

        Assert.Equal(["gefaehrdung", "name"], visible.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ClassFor_hides_only_the_columns_not_chosen()
    {
        var visible = MobileColumnSet.Resolve(People, PeopleDefaults, null);

        Assert.Equal(string.Empty, MobileColumnSet.ClassFor(visible, "name"));
        Assert.Equal("noose-mobil-aus", MobileColumnSet.ClassFor(visible, "aktenzeichen"));
    }
}
