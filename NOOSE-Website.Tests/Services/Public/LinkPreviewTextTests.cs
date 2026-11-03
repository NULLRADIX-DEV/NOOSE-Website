using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Tests.Services.Public;

/// <summary>The sentence and the address a shared link carries into a Discord channel.</summary>
public sealed class LinkPreviewTextTests
{
    // --- what reaches the card --------------------------------------------

    [Fact]
    public void Markup_never_reaches_the_card()
    {
        var text = LinkPreviewText.Summary("<p>Gesucht wegen <b>schweren Raubes</b>.</p>");

        Assert.Equal("Gesucht wegen schweren Raubes.", text);
    }

    [Fact]
    public void Two_paragraphs_become_one_line()
    {
        var text = LinkPreviewText.Summary("<p>Erste Zeile.</p>\r\n<p>Zweite Zeile.</p>");

        Assert.Equal("Erste Zeile. Zweite Zeile.", text);
    }

    [Fact]
    public void An_empty_body_yields_no_description()
    {
        Assert.Equal(string.Empty, LinkPreviewText.Summary(null));
        Assert.Equal(string.Empty, LinkPreviewText.Summary("   "));
        Assert.Equal(string.Empty, LinkPreviewText.Summary("<p></p>"));
    }

    // --- length -----------------------------------------------------------

    [Fact]
    public void A_text_within_the_budget_is_left_alone()
    {
        const string plain = "Amtliche Verlautbarungen des Federal Investigation Bureau.";

        Assert.Equal(plain, LinkPreviewText.Clip(plain));
    }

    [Fact]
    public void A_long_text_is_cut_on_a_word_boundary()
    {
        var text = LinkPreviewText.Clip(string.Join(' ', Enumerable.Repeat("Wortwort", 60)), 50);

        Assert.True(text.Length <= 51, text);
        // the cut sits between two words, so the last one is whole
        Assert.EndsWith("Wortwort\u2026", text, StringComparison.Ordinal);
    }

    /// <summary>A word longer than the whole budget offers no boundary, so it is cut hard rather than dropped.</summary>
    [Fact]
    public void A_single_endless_word_is_still_cut()
    {
        var text = LinkPreviewText.Clip(new string('A', 400), 40);

        Assert.Equal(new string('A', 40) + "\u2026", text);
    }

    [Fact]
    public void The_ellipsis_does_not_follow_a_comma()
    {
        var text = LinkPreviewText.Clip("Fahndungen, Organisationen, Presse und Warnungen", 27);

        Assert.Equal("Fahndungen, Organisationen\u2026", text);
    }

    // --- the scan window --------------------------------------------------

    /// <summary>The defect this pins: a body that opens with a base64 picture handing that data to the card.</summary>
    /// <remarks>
    /// A release carries its pictures inside the body, so the window can end in the middle of one. Cut there, the
    /// <c>img</c> tag loses its closing bracket and the tag stripper no longer recognises it as a tag.
    /// </remarks>
    [Fact]
    public void A_picture_at_the_top_of_a_body_does_not_spill_its_data()
    {
        var html = "<p><img src=\"data:image/png;base64," + new string('Q', 10_000) + "\" /></p><p>Der Text.</p>";

        var text = LinkPreviewText.Summary(html);

        Assert.DoesNotContain("QQQ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("base64", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_body_inside_the_window_keeps_its_opening_sentence()
    {
        var html = "<p>Das Amt teilt mit.</p><p>" + new string('x', 6_000) + "</p>";

        Assert.StartsWith("Das Amt teilt mit.", LinkPreviewText.Summary(html), StringComparison.Ordinal);
    }

    // --- the address ------------------------------------------------------

    [Theory]
    [InlineData("https://fibhq.com/gesucht", "https://fibhq.com/gesucht")]
    [InlineData("https://fibhq.com/gesucht?art=Person", "https://fibhq.com/gesucht")]
    [InlineData("https://fibhq.com/faq#anker", "https://fibhq.com/faq")]
    [InlineData("https://fibhq.com/info/faq?frage=a#a", "https://fibhq.com/info/faq")]
    public void The_canonical_address_drops_query_and_fragment(string uri, string expected)
        => Assert.Equal(expected, LinkPreviewText.Canonical(uri));

    [Fact]
    public void An_address_that_is_not_there_stays_empty()
        => Assert.Equal(string.Empty, LinkPreviewText.Canonical(null));
}
