using NOOSE_Website.Models.Calendar;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Tests.Models;

public class ModelMapsTests
{
    // ---------------------------------------------------------------
    // SearchNavigation.For(recordsType, targetId) — routes from the catalog
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("Person", "/personen/")]
    [InlineData("Faction", "/fraktionen/")]
    [InlineData("PersonGroup", "/personengruppen/")]
    [InlineData("Party", "/parteien/")]
    [InlineData("Operation", "/operationen/")]
    [InlineData("AgentActivity", "/aktivitaeten/")]
    [InlineData("Taskforce", "/taskforces/")]
    [InlineData("Case", "/vorgaenge/")]
    [InlineData("Job", "/aufgaben/")]
    [InlineData("Appointment", "/kalender/")]
    [InlineData("Meeting", "/besprechungen/")]
    [InlineData("Document", "/dokumente/")]
    [InlineData("Law", "/gesetze/")]
    [InlineData("Agent", "/personal/")]
    [InlineData("AgentAbduction", "/entfuehrungen/")]
    [InlineData("KassenBuchung", "/kasse/buchung/")]
    [InlineData("FinancingRequest", "/finanzierungen/")]
    [InlineData("Bewerbung", "/bewerbungen/")]
    public void For_knownRecordType_buildsFeatureRoute(string recordsType, string expectedPrefix)
    {
        var result = SearchNavigation.For(recordsType, "abc-123");

        Assert.Equal($"{expectedPrefix}abc-123", result);
    }

    // Inverted deliberately: these used to answer "/personen/{id}", which opened a person file with another
    // record's id. A type without a page now yields null, and the caller renders an unclickable row.
    [Theory]
    [InlineData("Unknown")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("faction")] // case-sensitive: lowercase is not the Faction row
    [InlineData("Comment")] // content: routed through its parent, never on its own
    [InlineData("Source")]
    [InlineData("PersonDoc")]
    public void For_typeWithoutAPage_isNull_ratherThanTheWrongRecord(string? recordsType)
    {
        Assert.Null(SearchNavigation.For(recordsType, "id-9"));
    }

    [Fact]
    public void For_embedsTargetIdVerbatim()
    {
        var result = SearchNavigation.For(nameof(NOOSE_Website.Data.Entities.Cases.Case), "GUID-WITH-Weird_Chars.1");

        Assert.Equal("/vorgaenge/GUID-WITH-Weird_Chars.1", result);
    }

    // ---------------------------------------------------------------
    // SearchNavigation.For(SearchHit) — TargetType overrides Category
    // ---------------------------------------------------------------

    [Fact]
    public void For_hit_usesCategory_whenTargetTypeNull()
    {
        var hit = new SearchHit("Faction", "f-1", "Title", "Snippet", "FIB-F-1", TargetType: null);

        Assert.Equal("/fraktionen/f-1", SearchNavigation.For(hit));
    }

    [Fact]
    public void For_hit_prefersTargetType_overCategory_andAppendsTheSection()
    {
        // A comment (Category) that belongs to a Party (TargetType) resolves to the party's Kommentare tab.
        var hit = new SearchHit("Comment", "p-7", "Title", "Snippet", "FIB-PA-7", TargetType: "Party");

        Assert.Equal("/parteien/p-7?tab=kommentare", SearchNavigation.For(hit));
    }

    [Fact]
    public void For_hit_emptyTargetType_fallsBackToTheCategory()
    {
        // "" used to be non-null and skipped the ?? fallthrough, routing the hit to "/personen/".
        var hit = new SearchHit("Faction", "z-3", "Title", "Snippet", "FIB-F-3", TargetType: "");

        Assert.Equal("/fraktionen/z-3", SearchNavigation.For(hit));
    }

    [Fact]
    public void For_hit_unroutableTargetType_isNull()
    {
        var hit = new SearchHit("Comment", "x-2", "Title", "Snippet", "", TargetType: "Notification");

        Assert.Null(SearchNavigation.For(hit));
    }

    [Fact]
    public void For_hit_prefersAnExplicitHref()
    {
        var hit = new SearchHit("Comment", "x-2", "Title", "Snippet", "", TargetType: "Party")
        {
            Href = "/einstellungen?tab=tags",
        };

        Assert.Equal("/einstellungen?tab=tags", SearchNavigation.For(hit));
    }

    // ---------------------------------------------------------------
    // CustomFieldRecordTypes.Display — label map + fallback
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("Person", "Person")]
    [InlineData("Faction", "Fraktion")]
    [InlineData("PersonGroup", "Personengruppe")]
    [InlineData("Party", "Partei")]
    [InlineData("Operation", "Operation")]
    [InlineData("Case", "Vorgang")]
    [InlineData("Taskforce", "Taskforce")]
    public void Display_knownTypeName_returnsGermanLabel(string typeName, string expected)
    {
        Assert.Equal(expected, CustomFieldRecordTypes.Display(typeName));
    }

    [Theory]
    [InlineData("Document")]
    [InlineData("Law")]
    [InlineData("Unknown")]
    [InlineData("person")] // case-sensitive: not a match
    public void Display_unknownTypeName_fallsBackToTypeName(string typeName)
    {
        Assert.Equal(typeName, CustomFieldRecordTypes.Display(typeName));
    }

    [Fact]
    public void Display_emptyString_returnsEmptyString()
    {
        Assert.Equal(string.Empty, CustomFieldRecordTypes.Display(string.Empty));
    }

    [Fact]
    public void All_containsExactlySevenSupportedRecordTypes()
    {
        Assert.Equal(7, CustomFieldRecordTypes.All.Count);
    }

    [Fact]
    public void All_typeNames_matchKnownRecordTypes()
    {
        var typeNames = CustomFieldRecordTypes.All.Select(e => e.TypeName).ToArray();

        Assert.Equal(
            new[] { "Person", "Faction", "PersonGroup", "Party", "Operation", "Case", "Taskforce" },
            typeNames);
    }

    [Fact]
    public void All_everyEntryDisplay_matchesDisplayMethod()
    {
        foreach (var entry in CustomFieldRecordTypes.All)
        {
            Assert.Equal(entry.Display, CustomFieldRecordTypes.Display(entry.TypeName));
        }
    }

    // ---------------------------------------------------------------
    // PartnerVisibilityConfig.RankKey — "{(int)agency}:{(int)rank}"
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(PartnerAgency.DoJ, PartnerRank.Member, "1:1")]
    [InlineData(PartnerAgency.DoJ, PartnerRank.Special, "1:2")]
    [InlineData(PartnerAgency.DoJ, PartnerRank.Chief, "1:3")]
    [InlineData(PartnerAgency.LSPD, PartnerRank.Member, "2:1")]
    [InlineData(PartnerAgency.LSPD, PartnerRank.Special, "2:2")]
    [InlineData(PartnerAgency.LSPD, PartnerRank.Chief, "2:3")]
    [InlineData(PartnerAgency.LSMD, PartnerRank.Member, "3:1")]
    [InlineData(PartnerAgency.LSMD, PartnerRank.Special, "3:2")]
    [InlineData(PartnerAgency.LSMD, PartnerRank.Chief, "3:3")]
    public void RankKey_buildsIntColonIntKey(PartnerAgency agency, PartnerRank rank, string expected)
    {
        Assert.Equal(expected, PartnerVisibilityConfig.RankKey(agency, rank));
    }

    [Fact]
    public void RankKey_isStableAndDistinctPerPair()
    {
        var keys = (from a in PartnerAgencyDisplay.All
                    from r in PartnerRankDisplay.All
                    select PartnerVisibilityConfig.RankKey(a, r)).ToArray();

        Assert.Equal(keys.Length, keys.Distinct().Count());
    }

    // ---------------------------------------------------------------
    // CalendarDisplay.Colour — colour per source + fallback
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(CalendarSource.Appointment, "#3FB950")]
    [InlineData(CalendarSource.Operation, "#F0883E")]
    [InlineData(CalendarSource.Observation, "#B27923")]
    [InlineData(CalendarSource.Job, "#9A9488")]
    [InlineData(CalendarSource.Followup, "#D29922")]
    [InlineData(CalendarSource.FactionActivity, "#83A412")]
    [InlineData(CalendarSource.PersonDoc, "#A371F7")]
    [InlineData(CalendarSource.Meeting, "#8A7650")]
    [InlineData(CalendarSource.Absence, "#DB61A2")]
    [InlineData(CalendarSource.TeamAbsence, "#B07C9E")]
    public void Colour_knownSource_returnsMappedHex(CalendarSource source, string expected)
    {
        Assert.Equal(expected, CalendarDisplay.Colour(source));
    }

    [Theory]
    [InlineData(99)]
    [InlineData(-1)]
    [InlineData(1000)]
    public void Colour_undefinedSource_fallsBackToGrey(int rawValue)
    {
        Assert.Equal("#9A9488", CalendarDisplay.Colour((CalendarSource)rawValue));
    }

    // ---------------------------------------------------------------
    // CalendarDisplay.Name — German legend label per source + fallback
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(CalendarSource.Appointment, "Termine")]
    [InlineData(CalendarSource.Operation, "Operationen")]
    [InlineData(CalendarSource.Observation, "Observationen")]
    [InlineData(CalendarSource.Job, "Aufgaben (fällig)")]
    [InlineData(CalendarSource.Followup, "Wiedervorlagen")]
    [InlineData(CalendarSource.FactionActivity, "Fraktions-Aktivitäten")]
    [InlineData(CalendarSource.PersonDoc, "Personen-Doks")]
    [InlineData(CalendarSource.Meeting, "Besprechungen")]
    [InlineData(CalendarSource.Absence, "Meine Abmeldungen")]
    [InlineData(CalendarSource.TeamAbsence, "Team-Abmeldungen")]
    public void Name_knownSource_returnsGermanLabel(CalendarSource source, string expected)
    {
        Assert.Equal(expected, CalendarDisplay.Name(source));
    }

    [Theory]
    [InlineData(99)]
    [InlineData(-5)]
    public void Name_undefinedSource_fallsBackToDash(int rawValue)
    {
        Assert.Equal("—", CalendarDisplay.Name((CalendarSource)rawValue));
    }

    // ---------------------------------------------------------------
    // CalendarDisplay.All — full ordered source list
    // ---------------------------------------------------------------

    [Fact]
    public void CalendarAll_containsAllTenSourcesInDeclarationOrder()
    {
        Assert.Equal(
            new[]
            {
                CalendarSource.Appointment,
                CalendarSource.Operation,
                CalendarSource.Observation,
                CalendarSource.Job,
                CalendarSource.Followup,
                CalendarSource.FactionActivity,
                CalendarSource.PersonDoc,
                CalendarSource.Meeting,
                CalendarSource.Absence,
                CalendarSource.TeamAbsence,
            },
            CalendarDisplay.All);
    }

    [Fact]
    public void CalendarAll_everySource_hasNonDashName()
    {
        // Each listed source has a real legend label (never the "—" fallback).
        foreach (var source in CalendarDisplay.All)
        {
            Assert.NotEqual("—", CalendarDisplay.Name(source));
        }
    }
}
