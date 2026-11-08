using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Regulation;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>The in-page search of the service regulation; pure filtering over a loaded document.</summary>
public sealed class RegulationTextSearchTests
{
    private static RegulationParagraphRow Row(string id, string sectionId, string heading, string html, bool repealed = false)
        => new(id, sectionId, heading, RegulationAudience.Bureau, 1, 1, 1, html, new DateOnly(2026, 10, 25),
            new DateTime(2026, 10, 25), 1, false, repealed, repealed ? new DateTime(2026, 11, 1) : null, null, false);

    private static RegulationDocument Document()
    {
        var radio = Row("p1", "s1", "Funkdisziplin", "<p>Im <b>Funk</b> gilt Ruhe. Codes stehen in der Tabelle.</p>");
        var dress = Row("p2", "s1", "Kleiderordnung", "<p>Dienstkleidung trägt jeder Agent im Dienst.</p>");
        var cars = Row("p3", "s2", "Fahrzeuge", "<p>Dienstwagen parken an der Straße vor dem Büro.</p>");
        var old = Row("p4", "s2", "Alte Funkregel", "<p>Früher galt Kanal 3.</p>", repealed: true);
        return new RegulationDocument(
            [
                new RegulationSectionView("s1", 1, "Handlungsvorschriften", false, [radio, dress]),
                new RegulationSectionView("s2", 2, "Dienstausrüstung", false, [cars]),
            ],
            [old], null, false, false);
    }

    private static List<RegulationSearchGroup> Search(string? query)
        => RegulationTextSearch.Filter(Document(), RegulationTextSearch.FoldedText, query);

    private static List<string> Ids(List<RegulationSearchGroup> groups)
        => groups.SelectMany(g => g.Paragraphs).Select(p => p.Id).ToList();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("f")]
    [InlineData(" f ")]
    public void A_query_below_two_characters_does_not_search(string? query)
    {
        Assert.Empty(RegulationTextSearch.Terms(query));
        Assert.Empty(Search(query));
    }

    [Fact]
    public void Terms_are_folded_distinct_and_capped()
    {
        Assert.Equal(["funk", "ausrustung", "strasse"], RegulationTextSearch.Terms("Funk  AUSRÜSTUNG funk Straße"));
        Assert.Equal(RegulationTextSearch.MaxTerms, RegulationTextSearch.Terms(string.Join(' ', Enumerable.Range(0, 20).Select(i => $"w{i}"))).Count);
    }

    [Theory]
    [InlineData("Ärger", "arger")]
    [InlineData("STRASSE", "strasse")]
    [InlineData("Straße", "strasse")]
    [InlineData("Café", "cafe")]
    public void Fold_drops_case_accents_and_sharp_s(string input, string expected)
        => Assert.Equal(expected, RegulationTextSearch.Fold(input));

    [Fact]
    public void Matches_heading_section_title_and_text()
    {
        Assert.Equal(["p2"], Ids(Search("kleiderordnung")));
        Assert.Equal(["p3", "p4"], Ids(Search("Dienstausrüstung")));
        Assert.Equal(["p1"], Ids(Search("tabelle")));
    }

    [Fact]
    public void Accents_and_sharp_s_in_the_query_or_text_do_not_matter()
    {
        Assert.Equal(["p3"], Ids(Search("strasse")));
        Assert.Equal(["p3"], Ids(Search("buro")));
        Assert.Equal(["p3", "p4"], Ids(Search("dienstausrustung")));
    }

    [Fact]
    public void Every_word_must_appear_but_may_sit_in_different_fields()
    {
        // "funk" in the text, "handlungsvorschriften" only in the section title
        Assert.Equal(["p1"], Ids(Search("funk handlungsvorschriften")));
        Assert.Empty(Search("funk fahrzeuge"));
    }

    [Fact]
    public void One_letter_words_are_dropped_so_they_cannot_match_everything()
    {
        Assert.Empty(RegulationTextSearch.Terms("a b c"));
        Assert.Empty(Search("a b c"));
        Assert.Equal(["funk"], RegulationTextSearch.Terms("funk a"));
    }

    [Fact]
    public void A_word_split_by_inline_markup_is_still_one_word()
    {
        var row = Row("x", "s1", "Titel", "<p><b>Funk</b>disziplin und <em>Dienst</em><span class=\"c\">wagen</span></p><p>Ende</p><p>Anfang</p>");
        var folded = RegulationTextSearch.FoldedText(row);
        Assert.Contains("funkdisziplin", folded);
        Assert.Contains("dienstwagen", folded);
        // block tags keep their space, two paragraphs never glue together
        Assert.DoesNotContain("endeanfang", folded);
    }

    [Fact]
    public void Markup_is_not_searched()
    {
        // tag names and attributes are never text
        Assert.Empty(Search("<b>"));
        Assert.Empty(Search("p>"));
    }

    [Fact]
    public void Hits_keep_document_order_grouped_by_section_with_repealed_last()
    {
        var groups = Search("funk");
        Assert.Equal(["s1", null], groups.Select(g => g.SectionId).ToList());
        Assert.Equal("Abschnitt 1: Handlungsvorschriften", groups[0].Caption);
        Assert.Equal("Aufgehoben", groups[1].Caption);
        Assert.Equal(["p1", "p4"], Ids(groups));
    }

    [Fact]
    public void A_repealed_paragraph_matches_its_former_section_title()
        => Assert.Equal(["p3", "p4"], Ids(Search("dienstausrüstung")));

    [Fact]
    public void Sections_without_hits_are_left_out()
    {
        var groups = Search("dienstkleidung");
        Assert.Single(groups);
        Assert.Equal("s1", groups[0].SectionId);
    }
}
