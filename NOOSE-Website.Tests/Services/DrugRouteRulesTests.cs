using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>When two route entries are the same route.</summary>
public sealed class DrugRouteRulesTests
{
    [Theory]
    [InlineData("Kokain Nord", "kokain nord")]
    [InlineData("  Kokain   Nord ", "KOKAIN NORD")]
    [InlineData("Kokain\tNord", "Kokain Nord")]
    [InlineData("Meth Süd", "METH SÜD")]
    public void Case_and_spacing_do_not_count(string a, string b)
        => Assert.Equal(DrugRouteRules.Key(a), DrugRouteRules.Key(b));

    [Fact]
    public void A_typo_stays_a_different_route()
        => Assert.NotEqual(DrugRouteRules.Key("Kokain-Nord"), DrugRouteRules.Key("Kokain Nord"));

    [Fact]
    public void Empty_is_no_route()
    {
        Assert.Equal(string.Empty, DrugRouteRules.Key(null));
        Assert.Equal(string.Empty, DrugRouteRules.Key("   "));
    }
}
