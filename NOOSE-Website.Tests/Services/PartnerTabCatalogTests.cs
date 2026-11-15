using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

public class PartnerTabCatalogTests
{
    // Canonical prefix -> type-key pairs, mirroring PartnerTabCatalog.All.
    public static IEnumerable<object[]> PrefixTypeKeyPairs() => new[]
    {
        new object[] { "personen", "Person" },
        new object[] { "fraktionen", "Faction" },
        new object[] { "personengruppen", "PersonGroup" },
        new object[] { "parteien", "Party" },
        new object[] { "drogenrouten", "DrugRoute" },
        new object[] { "operationen", "Operation" },
        new object[] { "vorgaenge", "Case" },
        new object[] { "taskforces", "Taskforce" },
        new object[] { "dokumente", "Document" },
        new object[] { "gesetze", "Law" },
    };

    // --- All catalog integrity ---

    [Fact]
    public void All_contains_all_ten_releasable_types()
    {
        Assert.Equal(10, PartnerTabCatalog.All.Count);
    }

    [Fact]
    public void All_routePrefixes_are_unique()
    {
        var prefixes = PartnerTabCatalog.All.Select(t => t.RoutePrefix).ToList();
        Assert.Equal(prefixes.Count, prefixes.Distinct().Count());
    }

    [Fact]
    public void All_typeKeys_are_unique()
    {
        var keys = PartnerTabCatalog.All.Select(t => t.TypeKey).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(PrefixTypeKeyPairs))]
    public void All_pairs_prefix_with_expected_typeKey(string prefix, string typeKey)
    {
        var entry = PartnerTabCatalog.All.Single(t => t.RoutePrefix == prefix);
        Assert.Equal(typeKey, entry.TypeKey);
    }

    // --- TypeKeyForPrefix ---

    [Theory]
    [MemberData(nameof(PrefixTypeKeyPairs))]
    public void TypeKeyForPrefix_knownPrefix_returnsTypeKey(string prefix, string typeKey)
    {
        Assert.Equal(typeKey, PartnerTabCatalog.TypeKeyForPrefix(prefix));
    }

    [Theory]
    [InlineData("FRAKTIONEN", "Faction")]
    [InlineData("Fraktionen", "Faction")]
    [InlineData("PeRsOnEn", "Person")]
    public void TypeKeyForPrefix_isCaseInsensitive(string prefix, string typeKey)
    {
        Assert.Equal(typeKey, PartnerTabCatalog.TypeKeyForPrefix(prefix));
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("person")]        // singular, not the plural route prefix
    [InlineData("fraktionen/123")] // whole string is the key here; not a path
    [InlineData(" fraktionen")]
    public void TypeKeyForPrefix_unknownPrefix_returnsNull(string prefix)
    {
        Assert.Null(PartnerTabCatalog.TypeKeyForPrefix(prefix));
    }

    // --- PrefixForTypeKey ---

    [Theory]
    [MemberData(nameof(PrefixTypeKeyPairs))]
    public void PrefixForTypeKey_knownTypeKey_returnsPrefix(string prefix, string typeKey)
    {
        Assert.Equal(prefix, PartnerTabCatalog.PrefixForTypeKey(typeKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData("unknown")]
    [InlineData("faction")]   // lowercase; ByTypeKey uses case-sensitive ordinal
    [InlineData("FACTION")]
    [InlineData("persongroup")]
    public void PrefixForTypeKey_unknownTypeKey_returnsNull(string typeKey)
    {
        Assert.Null(PartnerTabCatalog.PrefixForTypeKey(typeKey));
    }

    // --- Round-trip ---

    [Theory]
    [MemberData(nameof(PrefixTypeKeyPairs))]
    public void RoundTrip_prefix_to_typeKey_to_prefix(string prefix, string typeKey)
    {
        var key = PartnerTabCatalog.TypeKeyForPrefix(prefix);
        Assert.Equal(typeKey, key);
        Assert.Equal(prefix, PartnerTabCatalog.PrefixForTypeKey(key!));
    }

    [Theory]
    [MemberData(nameof(PrefixTypeKeyPairs))]
    public void RoundTrip_typeKey_to_prefix_to_typeKey(string prefix, string typeKey)
    {
        var resolvedPrefix = PartnerTabCatalog.PrefixForTypeKey(typeKey);
        Assert.Equal(prefix, resolvedPrefix);
        Assert.Equal(typeKey, PartnerTabCatalog.TypeKeyForPrefix(resolvedPrefix!));
    }

    // --- TypeKeyForPath ---

    [Fact]
    public void TypeKeyForPath_null_returnsNull()
    {
        Assert.Null(PartnerTabCatalog.TypeKeyForPath(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("///")]
    public void TypeKeyForPath_emptyOrSlashOnly_returnsNull(string path)
    {
        Assert.Null(PartnerTabCatalog.TypeKeyForPath(path));
    }

    [Theory]
    [MemberData(nameof(PrefixTypeKeyPairs))]
    public void TypeKeyForPath_barePrefix_returnsTypeKey(string prefix, string typeKey)
    {
        Assert.Equal(typeKey, PartnerTabCatalog.TypeKeyForPath(prefix));
    }

    [Theory]
    [InlineData("fraktionen/123", "Faction")]
    [InlineData("/fraktionen", "Faction")]
    [InlineData("/fraktionen/123/bearbeiten", "Faction")]
    [InlineData("personen/abc/druck", "Person")]
    public void TypeKeyForPath_usesFirstSegmentAndTrimsSlashes(string path, string typeKey)
    {
        Assert.Equal(typeKey, PartnerTabCatalog.TypeKeyForPath(path));
    }

    [Theory]
    [InlineData("FRAKTIONEN", "Faction")]
    [InlineData("Personen/123", "Person")]
    [InlineData("VorGaenge", "Case")]
    public void TypeKeyForPath_isCaseInsensitive(string path, string typeKey)
    {
        Assert.Equal(typeKey, PartnerTabCatalog.TypeKeyForPath(path));
    }

    [Theory]
    [InlineData("personen/123?tab=doks", "Person")]
    [InlineData("fraktionen?x=1", "Faction")]
    [InlineData("fraktionen/123#anchor", "Faction")]
    [InlineData("personen/123?tab=doks#frag", "Person")]
    public void TypeKeyForPath_stripsQueryAndFragment(string path, string typeKey)
    {
        Assert.Equal(typeKey, PartnerTabCatalog.TypeKeyForPath(path));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("unknown/123")]
    [InlineData("person")]        // singular is not a route prefix
    [InlineData(" personen")]     // leading space not trimmed -> no match
    [InlineData("?tab=doks")]     // query only -> empty path
    [InlineData("#frag")]         // fragment only -> empty path
    public void TypeKeyForPath_unknownOrEmptyFirstSegment_returnsNull(string path)
    {
        Assert.Null(PartnerTabCatalog.TypeKeyForPath(path));
    }

    // --- IsTypeGateExempt ---

    [Theory]
    [InlineData("dokumente")]
    [InlineData("dokumente/123")]
    [InlineData("/dokumente/123/bearbeiten")]
    [InlineData("DOKUMENTE")]
    [InlineData("dokumente?x=1")]
    public void IsTypeGateExempt_documentPaths_returnsTrue(string path)
    {
        Assert.True(PartnerTabCatalog.IsTypeGateExempt(path));
    }

    [Theory]
    [InlineData("personen")]
    [InlineData("fraktionen/123")]
    [InlineData("gesetze")]       // Law is releasable but NOT gate-exempt
    [InlineData("unknown")]
    [InlineData("")]
    public void IsTypeGateExempt_nonDocumentPaths_returnsFalse(string path)
    {
        Assert.False(PartnerTabCatalog.IsTypeGateExempt(path));
    }

    [Fact]
    public void IsTypeGateExempt_null_returnsFalse()
    {
        Assert.False(PartnerTabCatalog.IsTypeGateExempt(null));
    }
}
