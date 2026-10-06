using NOOSE_Website.Authorization;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Tests.Authorization;

public class PartnerRoutesTests
{
    // --- Empty / null / slash-only normalize to "" -> dashboard fallthrough returns true ---

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("///")]
    [InlineData("//")]
    public void IsAllowed_NullEmptyOrSlashOnly_ReturnsTrue(string? path)
    {
        Assert.True(PartnerRoutes.IsAllowed(path));
    }

    // --- Dashboard and own profile are always allowed ---

    [Theory]
    [InlineData("dashboard")]
    [InlineData("/dashboard")]
    [InlineData("/dashboard/")]
    [InlineData("DASHBOARD")]
    [InlineData("dashboard?tab=1")]
    [InlineData("profil")]
    [InlineData("/profil")]
    [InlineData("PROFIL")]
    [InlineData("profil/")]
    [InlineData("profil/settings")]
    [InlineData("profil/abc-123/edit")]
    public void IsAllowed_DashboardAndProfile_ReturnsTrue(string path)
    {
        Assert.True(PartnerRoutes.IsAllowed(path));
    }

    // --- Every allowed prefix: bare list route ---

    [Theory]
    [InlineData("personen")]
    [InlineData("fraktionen")]
    [InlineData("personengruppen")]
    [InlineData("parteien")]
    [InlineData("operationen")]
    [InlineData("vorgaenge")]
    [InlineData("taskforces")]
    [InlineData("dokumente")]
    [InlineData("gesetze")]
    [InlineData("suche")]
    public void IsAllowed_AllowedPrefixBareRoute_ReturnsTrue(string path)
    {
        Assert.True(PartnerRoutes.IsAllowed(path));
    }

    // --- Every allowed prefix: detail route ("prefix/{id}") ---

    [Theory]
    [InlineData("personen/abc-123")]
    [InlineData("fraktionen/abc-123")]
    [InlineData("personengruppen/abc-123")]
    [InlineData("parteien/abc-123")]
    [InlineData("operationen/abc-123")]
    [InlineData("vorgaenge/abc-123")]
    [InlineData("taskforces/abc-123")]
    [InlineData("dokumente/abc-123")]
    [InlineData("gesetze/abc-123")]
    [InlineData("suche/foo")]
    public void IsAllowed_AllowedPrefixDetailRoute_ReturnsTrue(string path)
    {
        Assert.True(PartnerRoutes.IsAllowed(path));
    }

    // --- Blocked create/edit/trash suffixes on otherwise-allowed prefixes ---

    [Theory]
    [InlineData("personen/neu")]
    [InlineData("personen/abc-123/bearbeiten")]
    [InlineData("personen/papierkorb")]
    [InlineData("fraktionen/neu")]
    [InlineData("fraktionen/abc-123/bearbeiten")]
    [InlineData("fraktionen/papierkorb")]
    [InlineData("vorgaenge/neu")]
    [InlineData("operationen/abc/bearbeiten")]
    [InlineData("taskforces/papierkorb")]
    [InlineData("gesetze/neu")]
    [InlineData("parteien/neu")]
    [InlineData("personengruppen/abc/bearbeiten")]
    public void IsAllowed_BlockedSuffixOnAllowedPrefix_ReturnsFalse(string path)
    {
        Assert.False(PartnerRoutes.IsAllowed(path));
    }

    // --- Document authoring exception overrides the blanket create/edit block ---

    [Theory]
    [InlineData("dokumente/neu")]
    [InlineData("/dokumente/neu")]
    [InlineData("Dokumente/Neu")]
    [InlineData("dokumente/neu?draft=1")]
    [InlineData("dokumente/abc-123/bearbeiten")]
    [InlineData("dokumente/bearbeiten")]
    [InlineData("dokumente/some-guid-value/bearbeiten")]
    public void IsAllowed_DocumentAuthoringRoute_ReturnsTrue(string path)
    {
        Assert.True(PartnerRoutes.IsAllowed(path));
    }

    // --- Document trash is NOT part of the authoring exception -> blocked ---

    [Fact]
    public void IsAllowed_DocumentTrash_ReturnsFalse()
    {
        Assert.False(PartnerRoutes.IsAllowed("dokumente/papierkorb"));
    }

    // --- The authoring exception is dokumente-specific: same edit shape elsewhere stays blocked ---

    [Theory]
    [InlineData("personen/abc/bearbeiten")]
    [InlineData("fraktionen/abc/bearbeiten")]
    public void IsAllowed_EditRouteOnNonDokumentePrefix_ReturnsFalse(string path)
    {
        Assert.False(PartnerRoutes.IsAllowed(path));
    }

    // --- Unknown / explicitly-not-listed prefixes are blocked ---

    [Theory]
    [InlineData("admin")]
    [InlineData("admin/system")]
    [InlineData("statistik")]
    [InlineData("kalender")]
    [InlineData("organigramm")]
    [InlineData("brett")]
    [InlineData("graph")]
    [InlineData("personal")]
    [InlineData("aufgaben")]
    [InlineData("neu")]
    [InlineData("bearbeiten")]
    [InlineData("papierkorb")]
    public void IsAllowed_UnknownPrefix_ReturnsFalse(string path)
    {
        Assert.False(PartnerRoutes.IsAllowed(path));
    }

    // --- Prefix-bleed guard: "prefix/" boundary prevents a longer word from matching ---

    [Theory]
    [InlineData("personenakte")]
    [InlineData("suchergebnis")]
    [InlineData("gesetzestext")]
    [InlineData("profils")]
    [InlineData("dashboards")]
    public void IsAllowed_PrefixIsNotABoundaryMatch_ReturnsFalse(string path)
    {
        Assert.False(PartnerRoutes.IsAllowed(path));
    }

    // --- Leading/trailing slashes are stripped before matching ---

    [Theory]
    [InlineData("/personen", true)]
    [InlineData("/personen/", true)]
    [InlineData("///personen///", true)]
    [InlineData("/personen/abc-123", true)]
    [InlineData("/personen/neu", false)]
    [InlineData("/admin", false)]
    public void IsAllowed_LeadingSlashNormalization(string path, bool expected)
    {
        Assert.Equal(expected, PartnerRoutes.IsAllowed(path));
    }

    // --- Query string and fragment are stripped before matching ---

    [Theory]
    [InlineData("personen?foo=bar", true)]
    [InlineData("personen/abc-123#section", true)]
    [InlineData("suche?q=hello", true)]
    [InlineData("personen/neu?x=1", false)]
    [InlineData("fraktionen/abc/bearbeiten#tab", false)]
    [InlineData("admin?x=1", false)]
    public void IsAllowed_QueryAndFragmentStripped(string path, bool expected)
    {
        Assert.Equal(expected, PartnerRoutes.IsAllowed(path));
    }

    // --- Matching is case-insensitive (ToLowerInvariant) ---

    [Theory]
    [InlineData("PERSONEN", true)]
    [InlineData("Personen/ABC-123", true)]
    [InlineData("PERSONEN/NEU", false)]
    [InlineData("ADMIN", false)]
    public void IsAllowed_IsCaseInsensitive(string path, bool expected)
    {
        Assert.Equal(expected, PartnerRoutes.IsAllowed(path));
    }

    // --- Trim only strips slashes, not spaces: whitespace-only stays non-empty and is blocked ---

    [Theory]
    [InlineData("   ")]
    [InlineData(" personen")]
    [InlineData("personen ")]
    public void IsAllowed_WhitespaceIsNotTrimmed_ReturnsFalse(string path)
    {
        Assert.False(PartnerRoutes.IsAllowed(path));
    }

    // --- The blocked-suffix check requires a slash boundary (no substring bleed) ---

    [Theory]
    [InlineData("personenneu")]      // ends in "neu" but not "/neu" -> not a blocked suffix, not an allowed prefix
    [InlineData("dokumenteneu")]
    public void IsAllowed_BlockedSuffixNeedsSlashBoundaryAndPrefixStillFails_ReturnsFalse(string path)
    {
        Assert.False(PartnerRoutes.IsAllowed(path));
    }

    // --- A deeper dokumente/neu/... path is not the exact authoring route, but dokumente/ is an allowed prefix ---

    [Fact]
    public void IsAllowed_DokumenteDeepNonBlockedPath_ReturnsTrue()
    {
        Assert.True(PartnerRoutes.IsAllowed("dokumente/neu/foo"));
    }

    // --- The citizen portal: every signed-in account may open it, so this list must not refuse it either ---

    [Theory]
    [InlineData("buerger")]
    [InlineData("buerger/hinweise")]
    [InlineData("buerger/belohnung/NOOSE-BEL-2026-0001/druck")]
    public void IsAllowed_CitizenPortal_ReturnsTrue(string path)
    {
        // BuergerLayout does not consult this list; blocking it here would refuse a partner the printable page only
        Assert.True(PartnerRoutes.IsAllowed(path));
    }

    // --- Function pages open only with the agency function ---

    [Theory]
    [InlineData("graph", PartnerFeature.Graph)]
    [InlineData("funk", PartnerFeature.Radio)]
    [InlineData("lageberichte/archiv", PartnerFeature.SituationReports)]
    [InlineData("lageberichte/abc", PartnerFeature.SituationReports)]
    [InlineData("anfragen", PartnerFeature.ShareRequests)]
    public void IsAllowed_FunctionPage_OnlyWithItsFunction(string path, PartnerFeature feature)
    {
        Assert.False(PartnerRoutes.IsAllowed(path));
        Assert.False(PartnerRoutes.IsAllowed(path, ~feature & AllFeatures));
        Assert.True(PartnerRoutes.IsAllowed(path, feature));
    }

    [Theory]
    [InlineData("fahndung")]
    [InlineData("doks")]
    [InlineData("kalender")]
    [InlineData("statistik")]
    [InlineData("funk/neu")]
    public void IsAllowed_InternalPages_StayClosedWithEveryFunction(string path)
        => Assert.False(PartnerRoutes.IsAllowed(path, AllFeatures));

    private const PartnerFeature AllFeatures = PartnerFeature.Noosei | PartnerFeature.Graph | PartnerFeature.Radio
        | PartnerFeature.SituationReports | PartnerFeature.ShareRequests;
}
