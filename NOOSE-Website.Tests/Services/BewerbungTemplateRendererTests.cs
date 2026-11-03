using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

public class BewerbungTemplateRendererTests
{
    private const string Block = BewerbungTemplateRenderer.Redaction;

    // ---------------------------------------------------------------
    // Redaction constant
    // ---------------------------------------------------------------

    [Fact]
    public void Redaction_constant_is_seven_full_block_characters()
    {
        Assert.Equal("███████", BewerbungTemplateRenderer.Redaction);
        Assert.Equal(7, BewerbungTemplateRenderer.Redaction.Length);
    }

    // ---------------------------------------------------------------
    // RenderForApplicant
    // ---------------------------------------------------------------

    [Fact]
    public void RenderForApplicant_replaces_NAME_with_redaction_block()
    {
        var result = BewerbungTemplateRenderer.RenderForApplicant("Hallo NAME,", "Max", "Agent");
        Assert.Equal($"Hallo {Block},", result);
    }

    [Fact]
    public void RenderForApplicant_replaces_all_NAME_occurrences()
    {
        var result = BewerbungTemplateRenderer.RenderForApplicant("NAME und NAME", null, null);
        Assert.Equal($"{Block} und {Block}", result);
    }

    [Fact]
    public void RenderForApplicant_replaces_BEWERBER_with_applicant_name()
    {
        var result = BewerbungTemplateRenderer.RenderForApplicant("Sehr geehrte/r BEWERBER", "Max Mustermann", null);
        Assert.Equal("Sehr geehrte/r Max Mustermann", result);
    }

    [Fact]
    public void RenderForApplicant_uses_fallback_salutation_when_applicant_name_null()
    {
        var result = BewerbungTemplateRenderer.RenderForApplicant("Hallo BEWERBER", null, null);
        Assert.Equal("Hallo Bewerber/in", result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void RenderForApplicant_uses_fallback_salutation_when_applicant_name_blank(string name)
    {
        var result = BewerbungTemplateRenderer.RenderForApplicant("BEWERBER", name, null);
        Assert.Equal("Bewerber/in", result);
    }

    [Fact]
    public void RenderForApplicant_trims_applicant_name()
    {
        var result = BewerbungTemplateRenderer.RenderForApplicant("BEWERBER", "  Max  ", null);
        Assert.Equal("Max", result);
    }

    [Fact]
    public void RenderForApplicant_html_encodes_applicant_name()
    {
        var result = BewerbungTemplateRenderer.RenderForApplicant("BEWERBER", "Tom & <Jerry>", null);
        Assert.Equal("Tom &amp; &lt;Jerry&gt;", result);
    }

    [Fact]
    public void RenderForApplicant_replaces_DIENSTGRAD_when_rank_provided()
    {
        var result = BewerbungTemplateRenderer.RenderForApplicant("Ihr DIENSTGRAD", null, "Special Agent");
        Assert.Equal("Ihr Special Agent", result);
    }

    [Fact]
    public void RenderForApplicant_html_encodes_dienstgrad()
    {
        var result = BewerbungTemplateRenderer.RenderForApplicant("DIENSTGRAD", null, "<Rank>&");
        Assert.Equal("&lt;Rank&gt;&amp;", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RenderForApplicant_keeps_DIENSTGRAD_token_when_rank_blank(string? rank)
    {
        var result = BewerbungTemplateRenderer.RenderForApplicant("DIENSTGRAD", null, rank);
        Assert.Equal("DIENSTGRAD", result);
    }

    [Fact]
    public void RenderForApplicant_leaves_DATUM_and_UHRZEIT_tokens_untouched()
    {
        var result = BewerbungTemplateRenderer.RenderForApplicant("DATUM UHRZEIT", "Max", "Agent");
        Assert.Equal("DATUM UHRZEIT", result);
    }

    [Fact]
    public void RenderForApplicant_does_not_touch_partial_word_matches()
    {
        // VORNAME / NAMENS embed NAME/BEWERBER-like substrings but are not whole-word tokens
        var result = BewerbungTemplateRenderer.RenderForApplicant("VORNAME NAME", null, null);
        Assert.Equal($"VORNAME {Block}", result);
    }

    [Fact]
    public void RenderForApplicant_does_not_replace_NAMES_plural()
    {
        var result = BewerbungTemplateRenderer.RenderForApplicant("NAMES", null, null);
        Assert.Equal("NAMES", result);
    }

    [Fact]
    public void RenderForApplicant_empty_html_returns_empty()
    {
        var result = BewerbungTemplateRenderer.RenderForApplicant("", "Max", "Agent");
        Assert.Equal("", result);
    }

    [Fact]
    public void RenderForApplicant_full_template_replaces_all_expected_tokens()
    {
        var html = "Hallo BEWERBER, ich bin NAME (DIENSTGRAD). Termin: DATUM UHRZEIT.";
        var result = BewerbungTemplateRenderer.RenderForApplicant(html, "Anna", "Trainee");
        Assert.Equal($"Hallo Anna, ich bin {Block} (Trainee). Termin: DATUM UHRZEIT.", result);
    }

    // ---------------------------------------------------------------
    // The sender's rank: neutral, whatever the unit
    // ---------------------------------------------------------------

    [Fact]
    public void SenderRank_is_the_neutral_title_of_every_rank_and_never_an_HRT_title()
    {
        var hrtTitles = RankDisplay.All.Select(r => RankDisplay.HrtTitle(r)).OfType<string>().ToList();

        foreach (var rank in RankDisplay.All)
        {
            var title = BewerbungTemplateRenderer.SenderRank(rank);

            Assert.Equal(RankDisplay.Name(rank), title);
            Assert.DoesNotContain(title!, hrtTitles);
            var letter = BewerbungTemplateRenderer.RenderForApplicant("Mit freundlichen Grüßen, DIENSTGRAD", "Anna", title);
            Assert.All(hrtTitles, hrt => Assert.DoesNotContain(hrt, letter));
        }
    }

    [Fact]
    public void SenderRank_without_a_rank_leaves_the_token_standing()
    {
        Assert.Null(BewerbungTemplateRenderer.SenderRank(null));
        Assert.Equal("DIENSTGRAD", BewerbungTemplateRenderer.RenderForApplicant("DIENSTGRAD", null, BewerbungTemplateRenderer.SenderRank(null)));
    }

    // ---------------------------------------------------------------
    // Redact
    // ---------------------------------------------------------------

    [Fact]
    public void Redact_null_returns_empty_string()
    {
        var result = BewerbungTemplateRenderer.Redact(null!, "Max");
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Redact_empty_returns_empty_string()
    {
        var result = BewerbungTemplateRenderer.Redact("", "Max");
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void Redact_replaces_NAME_in_text_segment()
    {
        var result = BewerbungTemplateRenderer.Redact("<p>NAME</p>", "Max");
        Assert.Equal($"<p>{Block}</p>", result);
    }

    [Fact]
    public void Redact_replaces_BEWERBER_in_text_segment()
    {
        var result = BewerbungTemplateRenderer.Redact("<p>BEWERBER</p>", "Max");
        Assert.Equal("<p>Max</p>", result);
    }

    [Fact]
    public void Redact_uses_fallback_for_BEWERBER_when_name_null()
    {
        var result = BewerbungTemplateRenderer.Redact("<p>BEWERBER</p>", null);
        Assert.Equal("<p>Bewerber/in</p>", result);
    }

    [Fact]
    public void Redact_does_not_touch_NAME_inside_tag_attribute()
    {
        // NAME lives inside a tag/attribute, so it must remain untouched
        var result = BewerbungTemplateRenderer.Redact("<a title=\"NAME\">link</a>", "Max");
        Assert.Equal("<a title=\"NAME\">link</a>", result);
    }

    [Fact]
    public void Redact_html_encodes_applicant_name()
    {
        var result = BewerbungTemplateRenderer.Redact("BEWERBER", "A & B");
        Assert.Equal("A &amp; B", result);
    }

    [Fact]
    public void Redact_preserves_markup_while_rewriting_text()
    {
        var result = BewerbungTemplateRenderer.Redact("<div><b>NAME</b> an BEWERBER</div>", "Lea");
        Assert.Equal($"<div><b>{Block}</b> an Lea</div>", result);
    }

    [Fact]
    public void Redact_leaves_DATUM_and_DIENSTGRAD_tokens_untouched()
    {
        var result = BewerbungTemplateRenderer.Redact("DATUM DIENSTGRAD UHRZEIT", "Max");
        Assert.Equal("DATUM DIENSTGRAD UHRZEIT", result);
    }

    [Fact]
    public void Redact_plain_text_without_tags_still_redacts()
    {
        var result = BewerbungTemplateRenderer.Redact("NAME schreibt an BEWERBER", "Max");
        Assert.Equal($"{Block} schreibt an Max", result);
    }

    // ---------------------------------------------------------------
    // FillDateTime
    // ---------------------------------------------------------------

    [Fact]
    public void FillDateTime_null_returns_empty_string()
    {
        var result = BewerbungTemplateRenderer.FillDateTime(null!, "21.07.2026", "10:00");
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void FillDateTime_empty_returns_empty_string()
    {
        var result = BewerbungTemplateRenderer.FillDateTime("", "21.07.2026", "10:00");
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void FillDateTime_replaces_DATUM_token()
    {
        var result = BewerbungTemplateRenderer.FillDateTime("Am DATUM.", "21.07.2026", null);
        Assert.Equal("Am 21.07.2026.", result);
    }

    [Fact]
    public void FillDateTime_replaces_UHRZEIT_token()
    {
        var result = BewerbungTemplateRenderer.FillDateTime("Um UHRZEIT Uhr.", null, "10:00");
        Assert.Equal("Um 10:00 Uhr.", result);
    }

    [Fact]
    public void FillDateTime_replaces_both_tokens()
    {
        var result = BewerbungTemplateRenderer.FillDateTime("DATUM UHRZEIT", "21.07.2026", "10:00");
        Assert.Equal("21.07.2026 10:00", result);
    }

    [Fact]
    public void FillDateTime_replaces_all_DATUM_occurrences()
    {
        var result = BewerbungTemplateRenderer.FillDateTime("DATUM und DATUM", "X", null);
        Assert.Equal("X und X", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FillDateTime_keeps_DATUM_token_when_date_blank(string? date)
    {
        var result = BewerbungTemplateRenderer.FillDateTime("DATUM", date, null);
        Assert.Equal("DATUM", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FillDateTime_keeps_UHRZEIT_token_when_time_blank(string? time)
    {
        var result = BewerbungTemplateRenderer.FillDateTime("UHRZEIT", null, time);
        Assert.Equal("UHRZEIT", result);
    }

    [Fact]
    public void FillDateTime_html_encodes_date_value()
    {
        var result = BewerbungTemplateRenderer.FillDateTime("DATUM", "<b>&", null);
        Assert.Equal("&lt;b&gt;&amp;", result);
    }

    [Fact]
    public void FillDateTime_html_encodes_time_value()
    {
        var result = BewerbungTemplateRenderer.FillDateTime("UHRZEIT", null, "<t>");
        Assert.Equal("&lt;t&gt;", result);
    }

    [Fact]
    public void FillDateTime_does_not_replace_partial_word()
    {
        var result = BewerbungTemplateRenderer.FillDateTime("DATUMS", "X", null);
        Assert.Equal("DATUMS", result);
    }

    [Fact]
    public void FillDateTime_leaves_html_untouched_when_no_token_present()
    {
        var result = BewerbungTemplateRenderer.FillDateTime("no tokens here", "X", "Y");
        Assert.Equal("no tokens here", result);
    }

    // ---------------------------------------------------------------
    // HasDateToken
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("DATUM", true)]
    [InlineData("Am DATUM um", true)]
    [InlineData("<p>DATUM</p>", true)]
    [InlineData("no token", false)]
    [InlineData("DATUMS", false)]
    [InlineData("VORDATUM", false)]
    [InlineData("UHRZEIT", false)]
    [InlineData("", false)]
    public void HasDateToken_detects_unfilled_date_token(string html, bool expected)
    {
        Assert.Equal(expected, BewerbungTemplateRenderer.HasDateToken(html));
    }

    [Fact]
    public void HasDateToken_null_returns_false()
    {
        Assert.False(BewerbungTemplateRenderer.HasDateToken(null!));
    }

    // ---------------------------------------------------------------
    // HasTimeToken
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("UHRZEIT", true)]
    [InlineData("Um UHRZEIT Uhr", true)]
    [InlineData("<p>UHRZEIT</p>", true)]
    [InlineData("no token", false)]
    [InlineData("UHRZEITEN", false)]
    [InlineData("DATUM", false)]
    [InlineData("", false)]
    public void HasTimeToken_detects_unfilled_time_token(string html, bool expected)
    {
        Assert.Equal(expected, BewerbungTemplateRenderer.HasTimeToken(html));
    }

    [Fact]
    public void HasTimeToken_null_returns_false()
    {
        Assert.False(BewerbungTemplateRenderer.HasTimeToken(null!));
    }

    // ---------------------------------------------------------------
    // HtmlToText
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n ")]
    public void HtmlToText_blank_input_returns_empty(string? html)
    {
        Assert.Equal(string.Empty, BewerbungTemplateRenderer.HtmlToText(html!));
    }

    [Fact]
    public void HtmlToText_strips_inline_tags()
    {
        var result = BewerbungTemplateRenderer.HtmlToText("<div><span>Hi</span></div>");
        Assert.Equal("Hi", result);
    }

    [Fact]
    public void HtmlToText_converts_closing_paragraph_to_newline()
    {
        var result = BewerbungTemplateRenderer.HtmlToText("<p>A</p><p>B</p>");
        Assert.Equal("A\nB", result);
    }

    [Theory]
    [InlineData("Line1<br>Line2")]
    [InlineData("Line1<br/>Line2")]
    [InlineData("Line1<br />Line2")]
    [InlineData("Line1<BR>Line2")]
    public void HtmlToText_converts_break_variants_to_newline(string html)
    {
        Assert.Equal("Line1\nLine2", BewerbungTemplateRenderer.HtmlToText(html));
    }

    [Fact]
    public void HtmlToText_converts_list_item_close_to_newline()
    {
        var result = BewerbungTemplateRenderer.HtmlToText("<ul><li>One</li><li>Two</li></ul>");
        Assert.Equal("One\nTwo", result);
    }

    [Fact]
    public void HtmlToText_converts_heading_close_to_newline()
    {
        var result = BewerbungTemplateRenderer.HtmlToText("<h1>Title</h1>Body");
        Assert.Equal("Title\nBody", result);
    }

    [Fact]
    public void HtmlToText_decodes_html_entities()
    {
        var result = BewerbungTemplateRenderer.HtmlToText("<p>Tom &amp; Jerry</p>");
        Assert.Equal("Tom & Jerry", result);
    }

    [Fact]
    public void HtmlToText_decodes_entities_after_tag_stripping_so_encoded_angle_brackets_survive()
    {
        // &lt;tag&gt; are entities, not real tags, so they must survive stripping and decode to <tag>
        var result = BewerbungTemplateRenderer.HtmlToText("&lt;tag&gt;");
        Assert.Equal("<tag>", result);
    }

    [Fact]
    public void HtmlToText_collapses_excess_blank_lines()
    {
        var result = BewerbungTemplateRenderer.HtmlToText("A</p></p></p>B");
        Assert.Equal("A\n\nB", result);
    }

    [Fact]
    public void HtmlToText_trims_leading_and_trailing_whitespace()
    {
        var result = BewerbungTemplateRenderer.HtmlToText("<p>Only</p>");
        Assert.Equal("Only", result);
    }

    [Fact]
    public void HtmlToText_collapses_repeated_breaks()
    {
        var result = BewerbungTemplateRenderer.HtmlToText("A<br><br><br>B");
        Assert.Equal("A\n\nB", result);
    }

    [Fact]
    public void HtmlToText_full_template_flattens_to_plain_text()
    {
        var html = "<h1>Hallo</h1><p>Erste Zeile</p><p>Zweite &amp; Zeile</p>";
        var result = BewerbungTemplateRenderer.HtmlToText(html);
        Assert.Equal("Hallo\nErste Zeile\nZweite & Zeile", result);
    }
}
