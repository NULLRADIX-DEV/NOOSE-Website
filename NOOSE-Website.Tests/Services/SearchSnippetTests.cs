using NOOSE_Website.Services.Search;

namespace NOOSE_Website.Tests.Services;

/// <summary>The text a result row shows: a window around the match, not the head of the document.</summary>
public class SearchSnippetTests
{
    [Fact]
    public void Centres_the_window_on_the_match_rather_than_the_head()
    {
        var text = new string('a', 500) + " NADEL " + new string('b', 500);

        var snippet = SearchSnippet.Around(text, "NADEL");

        Assert.Contains("NADEL", snippet);
        Assert.StartsWith("…", snippet);
        Assert.EndsWith("…", snippet);
    }

    [Fact]
    public void Matches_case_insensitively()
    {
        Assert.Contains("Nadel", SearchSnippet.Around("Heuhaufen mit Nadel darin", "nadel"));
    }

    [Fact]
    public void No_leading_ellipsis_when_the_match_is_at_the_start()
    {
        var snippet = SearchSnippet.Around("Nadel " + new string('b', 500), "Nadel");

        Assert.StartsWith("Nadel", snippet);
    }

    [Fact]
    public void Falls_back_to_the_head_when_the_term_is_not_in_the_plain_text()
    {
        // the term may have matched inside an attribute, which the plain-text projection drops
        var snippet = SearchSnippet.Around("Ein kurzer Text ohne den Begriff", "farbe");

        Assert.Equal("Ein kurzer Text ohne den Begriff", snippet);
    }

    [Fact]
    public void Head_fallback_is_capped_and_marked()
    {
        var snippet = SearchSnippet.Around(new string('a', 5000), "nichts");

        Assert.True(snippet.Length <= SearchSnippet.HeadMax + 1);
        Assert.EndsWith("…", snippet);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Empty_input_yields_empty_output(string? plain)
    {
        Assert.Equal(string.Empty, SearchSnippet.Around(plain, "egal"));
    }

    [Fact]
    public void An_empty_query_yields_the_head_rather_than_throwing()
    {
        Assert.Equal("Kurzer Text", SearchSnippet.Around("Kurzer Text", ""));
    }

    private static string Marked(string text, string query)
        => string.Concat(SearchSnippet.Segments(text, query).Select(s => s.Hit ? $"[{s.Part}]" : s.Part));

    [Fact]
    public void Segments_mark_each_query_word_separately()
    {
        Assert.Equal("[Nadel] im [Heu]haufen", Marked("Nadel im Heuhaufen", "heu nadel"));
    }

    [Theory]
    [InlineData("Herr Müller kommt", "muller", "Herr [Müller] kommt")]
    [InlineData("Herr Muller kommt", "müller", "Herr [Muller] kommt")]
    [InlineData("Die Straße ist lang", "strasse", "Die [Straße] ist lang")]
    [InlineData("Die Strasse ist lang", "straße", "Die [Strasse] ist lang")]
    [InlineData("GROSSE Straße", "große", "[GROSSE] Straße")]
    public void Segments_ignore_case_accents_and_sharp_s(string text, string query, string expected)
    {
        Assert.Equal(expected, Marked(text, query));
    }

    [Fact]
    public void Segments_keep_a_combining_accent_inside_the_hit()
    {
        // "u" + U+0308 is an ü written as two chars
        Assert.Equal("[Mu\u0308ller]", Marked("Mu\u0308ller", "muller"));
    }

    [Fact]
    public void Segments_cover_part_of_a_sharp_s_word_without_slicing_it()
    {
        Assert.Equal("F[uß]ball", Marked("Fußball", "uss"));
        // the query ends in the middle of the ß: the whole letter is marked
        Assert.Equal("F[uß]ball", Marked("Fußball", "us"));
    }

    [Fact]
    public void Segments_merge_overlapping_and_adjacent_hits()
    {
        Assert.Equal("[Nadelstich] hier", Marked("Nadelstich hier", "nadel stich"));
        Assert.Equal("[Heuhaufen]", Marked("Heuhaufen", "heuha haufen"));
    }

    [Fact]
    public void Segments_without_a_hit_return_the_text_whole()
    {
        var parts = SearchSnippet.Segments("Nichts zu sehen", "zebra");

        Assert.Single(parts);
        Assert.False(parts[0].Hit);
        Assert.Equal("Nichts zu sehen", parts[0].Part);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("x")]
    public void Segments_with_an_empty_or_too_short_query_mark_nothing(string? query)
    {
        Assert.Equal("Ein Text", Marked("Ein Text", query!));
    }

    [Fact]
    public void Segments_of_empty_text_are_empty()
    {
        Assert.Empty(SearchSnippet.Segments(null, "x"));
        Assert.Empty(SearchSnippet.Segments("", "text"));
    }

    [Fact]
    public void Segments_survive_emoji_and_lone_surrogates()
    {
        Assert.Equal("🚓 [Streife] 🚓", Marked("🚓 Streife 🚓", "streife"));
        Assert.Equal("\ud83d [Streife]", Marked("\ud83d Streife", "streife"));
        // a hit right behind a pair keeps the pair whole
        Assert.Equal("🚓[Streife]", Marked("🚓Streife", "streife"));
    }

    [Fact]
    public void Segments_join_back_to_the_original_text()
    {
        const string text = "Müller und Straße, 🚓 Nadel";

        Assert.Equal(text, string.Concat(SearchSnippet.Segments(text, "muller strasse nadel").Select(s => s.Part)));
    }

    [Fact]
    public void Around_centres_on_the_first_word_when_the_phrase_is_absent()
    {
        var text = new string('a', 500) + " Nadel " + new string('b', 200) + " Heu " + new string('c', 500);

        var snippet = SearchSnippet.Around(text, "heu nadel");

        Assert.Contains("Nadel", snippet);
        Assert.StartsWith("…", snippet);
    }

    [Fact]
    public void Around_finds_an_accented_word_from_an_unaccented_query()
    {
        var text = new string('a', 500) + " Müller " + new string('b', 500);

        Assert.Contains("Müller", SearchSnippet.Around(text, "muller"));
    }

    [Fact]
    public void Around_still_prefers_the_whole_phrase()
    {
        var text = "Nadel " + new string('a', 500) + " Heu Nadel " + new string('b', 500);

        Assert.Contains("Heu Nadel", SearchSnippet.Around(text, "heu nadel"));
    }
}
