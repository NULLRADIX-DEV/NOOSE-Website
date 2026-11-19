using System.Reflection;
using System.Text.RegularExpressions;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Navigation;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Usage;

namespace NOOSE_Website.Tests.Services.Usage;

/// <summary>The catalog is the only place a counter key can come from; these keep it complete and free of ids.</summary>
public sealed class UsageCatalogTests
{
    [Fact]
    public void Keys_are_unique_short_and_plain()
    {
        var keys = UsageCatalog.All.Select(f => f.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        // plain ascii tokens: nothing that could carry an id, a path or a search text
        Assert.All(keys, k =>
        {
            Assert.InRange(k.Length, 1, 100);
            Assert.Matches(new Regex("^[a-z0-9.:_-]+$"), k);
        });
        Assert.All(UsageCatalog.All, f =>
        {
            Assert.False(string.IsNullOrWhiteSpace(f.Label));
            Assert.False(string.IsNullOrWhiteSpace(f.Group));
        });
    }

    [Fact]
    public void Every_public_key_constant_is_listed()
    {
        var constants = typeof(UsageCatalog).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name != nameof(UsageCatalog.GroupHotkeys)
                && !f.Name.StartsWith("Group", StringComparison.Ordinal))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
        Assert.NotEmpty(constants);
        Assert.All(constants, key => Assert.NotNull(UsageCatalog.Find(key)));
    }

    [Fact]
    public void Every_page_the_tracker_can_name_is_listed()
    {
        // what the layout counts: ByRoute of an entry's own route, so a twin sharing a route is never reached
        var reachable = NavCatalog.Internal
            .Where(e => e.Area is not NavArea.Partner && NavCatalog.ByRoute(e.Route)?.Key == e.Key)
            .Select(e => UsageCatalog.Page(e.Key));
        Assert.All(reachable, key => Assert.NotNull(UsageCatalog.Find(key)));
    }

    [Fact]
    public void Every_page_feature_names_a_menu_entry()
    {
        foreach (var feature in UsageCatalog.All.Where(f => f.Key.StartsWith("page:", StringComparison.Ordinal)))
        {
            Assert.NotNull(NavCatalog.ByKey(feature.Key["page:".Length..]));
        }
    }

    [Fact]
    public void Every_shortcut_area_and_llm_feature_is_listed()
    {
        Assert.All(ShortcutRoutes.Areas.Values, key => Assert.NotNull(UsageCatalog.Find(UsageCatalog.HotkeyGoto(key))));
        Assert.All(Enum.GetValues<LlmFeature>(), f => Assert.NotNull(UsageCatalog.Find(UsageCatalog.Fibi(f))));
        Assert.All(UsageCatalog.PaletteKinds, k => Assert.NotNull(UsageCatalog.Find(UsageCatalog.PalettePick(k.Kind))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("page:does-not-exist")]
    [InlineData("search:q=Michael+Townley")]
    [InlineData("page:personen/3f2a")]
    public void Unknown_keys_are_not_found(string? key) => Assert.Null(UsageCatalog.Find(key));
}
