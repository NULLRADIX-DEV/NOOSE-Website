using NOOSE_Website.Models.Common;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

public class MentionParserTests
{
    // Canonical 36-char GUID (8-4-4-4-12 hex), lowercase.
    private const string Guid1 = "0f8fad5b-d9cb-469f-a165-70867728950e";
    private const string Guid2 = "1b9d6bcd-bbfd-4b2d-9b5d-ab8dfbbd4bed";

    // ---------- Parse: null / empty / whitespace ----------

    [Fact]
    public void Parse_NullText_ReturnsEmpty()
    {
        IReadOnlyList<MentionToken> result = MentionParser.Parse(null);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_EmptyText_ReturnsEmpty()
    {
        IReadOnlyList<MentionToken> result = MentionParser.Parse("");
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_WhitespaceText_ReturnsEmpty()
    {
        // Whitespace is not null/empty, so it runs the regex; no tokens present.
        IReadOnlyList<MentionToken> result = MentionParser.Parse("   \t\n  ");
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_PlainTextWithoutMention_ReturnsEmpty()
    {
        IReadOnlyList<MentionToken> result = MentionParser.Parse("Just some ordinary text with no mentions.");
        Assert.Empty(result);
    }

    // ---------- Parse: single mention ----------

    [Fact]
    public void Parse_SingleMention_ReturnsOneToken()
    {
        string token = MentionParser.Token("Person", Guid1);
        IReadOnlyList<MentionToken> result = MentionParser.Parse(token);
        Assert.Single(result);
    }

    [Fact]
    public void Parse_SingleMention_ExtractsTypeAndId()
    {
        string token = MentionParser.Token("Person", Guid1);
        MentionToken m = Assert.Single(MentionParser.Parse(token));
        Assert.Equal("Person", m.Type);
        Assert.Equal(Guid1, m.Id);
    }

    [Fact]
    public void Parse_SingleMention_StartAndLengthAtOrigin()
    {
        string token = MentionParser.Token("Person", Guid1);
        MentionToken m = Assert.Single(MentionParser.Parse(token));
        Assert.Equal(0, m.Start);
        Assert.Equal(token.Length, m.Length);
    }

    [Fact]
    public void Parse_MentionWithLeadingText_ReportsCorrectStart()
    {
        string prefix = "Hallo ";
        string token = MentionParser.Token("Person", Guid1);
        MentionToken m = Assert.Single(MentionParser.Parse(prefix + token));
        Assert.Equal(prefix.Length, m.Start);
        Assert.Equal(token.Length, m.Length);
    }

    // ---------- Parse: multiple mentions ----------

    [Fact]
    public void Parse_TwoDistinctMentions_ReturnsTwoInOrder()
    {
        string t1 = MentionParser.Token("Person", Guid1);
        string t2 = MentionParser.Token("Fraktion", Guid2);
        IReadOnlyList<MentionToken> result = MentionParser.Parse($"{t1} und {t2}");

        Assert.Equal(2, result.Count);
        Assert.Equal("Person", result[0].Type);
        Assert.Equal(Guid1, result[0].Id);
        Assert.Equal("Fraktion", result[1].Type);
        Assert.Equal(Guid2, result[1].Id);
    }

    [Fact]
    public void Parse_MultipleMentions_StartsAreAscending()
    {
        string t1 = MentionParser.Token("Person", Guid1);
        string t2 = MentionParser.Token("Fraktion", Guid2);
        IReadOnlyList<MentionToken> result = MentionParser.Parse($"{t1} xxx {t2}");

        Assert.Equal(0, result[0].Start);
        Assert.Equal(t1.Length + " xxx ".Length, result[1].Start);
    }

    // ---------- Parse: duplicates (no de-duplication) ----------

    [Fact]
    public void Parse_DuplicateMention_ReturnsBothOccurrences()
    {
        string token = MentionParser.Token("Person", Guid1);
        IReadOnlyList<MentionToken> result = MentionParser.Parse($"{token} und nochmal {token}");

        Assert.Equal(2, result.Count);
        Assert.Equal(result[0].Type, result[1].Type);
        Assert.Equal(result[0].Id, result[1].Id);
        Assert.NotEqual(result[0].Start, result[1].Start);
    }

    // ---------- Parse: punctuation boundaries ----------

    [Theory]
    [InlineData("(", ")")]
    [InlineData("[", "]")]
    [InlineData("", ".")]
    [InlineData("", ",")]
    [InlineData("Text", "!")]
    [InlineData("wort", "wort")] // no whitespace boundary required
    public void Parse_MentionSurroundedByPunctuation_StillMatches(string before, string after)
    {
        string token = MentionParser.Token("Person", Guid1);
        string text = before + token + after;
        MentionToken m = Assert.Single(MentionParser.Parse(text));

        Assert.Equal("Person", m.Type);
        Assert.Equal(Guid1, m.Id);
        Assert.Equal(before.Length, m.Start);
        Assert.Equal(token.Length, m.Length);
    }

    // ---------- Parse: type token variations (\w+) ----------

    [Theory]
    [InlineData("Person")]
    [InlineData("Fraktion")]
    [InlineData("Partei")]
    [InlineData("Person_Group")]
    [InlineData("Type123")]
    [InlineData("a")]
    public void Parse_ValidWordType_IsAccepted(string type)
    {
        string token = MentionParser.Token(type, Guid1);
        MentionToken m = Assert.Single(MentionParser.Parse(token));
        Assert.Equal(type, m.Type);
        Assert.Equal(Guid1, m.Id);
    }

    [Fact]
    public void Parse_EmptyType_DoesNotMatch()
    {
        // \w+ requires at least one type char.
        IReadOnlyList<MentionToken> result = MentionParser.Parse("@{:" + Guid1 + "}");
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_TypeWithHyphen_DoesNotMatch()
    {
        // '-' is not a \w char, so the type group stops before it and the ':' is missing.
        IReadOnlyList<MentionToken> result = MentionParser.Parse("@{Per-son:" + Guid1 + "}");
        Assert.Empty(result);
    }

    // ---------- Parse: GUID validity boundaries ----------

    [Fact]
    public void Parse_UppercaseHexGuid_IsAccepted()
    {
        string upper = Guid1.ToUpperInvariant();
        string token = MentionParser.Token("Person", upper);
        MentionToken m = Assert.Single(MentionParser.Parse(token));
        Assert.Equal(upper, m.Id);
    }

    [Fact]
    public void Parse_MixedCaseHexGuid_IsAccepted()
    {
        string mixed = "0F8fAd5b-D9cb-469F-a165-70867728950E";
        string token = MentionParser.Token("Person", mixed);
        MentionToken m = Assert.Single(MentionParser.Parse(token));
        Assert.Equal(mixed, m.Id);
    }

    [Theory]
    [InlineData("0f8fad5b-d9cb-469f-a165-70867728950")]      // final block too short (11)
    [InlineData("0f8fad5b-d9cb-469f-a165-70867728950ee")]    // final block too long (13)
    [InlineData("0f8fad5-d9cb-469f-a165-70867728950e")]      // first block too short (7)
    [InlineData("0f8fad5bd9cb469fa16570867728950e")]         // no hyphens
    [InlineData("0f8fad5b_d9cb_469f_a165_70867728950e")]     // wrong separators
    [InlineData("gf8fad5b-d9cb-469f-a165-70867728950e")]     // non-hex char 'g'
    [InlineData("not-a-guid")]
    public void Parse_MalformedGuid_DoesNotMatch(string badId)
    {
        IReadOnlyList<MentionToken> result = MentionParser.Parse("@{Person:" + badId + "}");
        Assert.Empty(result);
    }

    // ---------- Parse: structural malformations ----------

    [Theory]
    [InlineData("@Person:0f8fad5b-d9cb-469f-a165-70867728950e}")]  // missing opening brace
    [InlineData("@{Person:0f8fad5b-d9cb-469f-a165-70867728950e")]  // missing closing brace
    [InlineData("{Person:0f8fad5b-d9cb-469f-a165-70867728950e}")]  // missing @
    [InlineData("@{Person 0f8fad5b-d9cb-469f-a165-70867728950e}")] // missing colon
    public void Parse_StructurallyBroken_DoesNotMatch(string text)
    {
        Assert.Empty(MentionParser.Parse(text));
    }

    [Fact]
    public void Parse_ExtraLongIdWithValidGuidPrefix_DoesNotMatchDueToTrailingBrace()
    {
        // Regex requires '}' immediately after the 12-char final block.
        IReadOnlyList<MentionToken> result = MentionParser.Parse("@{Person:0f8fad5b-d9cb-469f-a165-70867728950eXY}");
        Assert.Empty(result);
    }

    // ---------- Token helper ----------

    [Fact]
    public void Token_FormatsBracesColon()
    {
        Assert.Equal("@{Person:" + Guid1 + "}", MentionParser.Token("Person", Guid1));
    }

    [Theory]
    [InlineData("Person", "abc", "@{Person:abc}")]
    [InlineData("Fraktion", "id-1", "@{Fraktion:id-1}")]
    [InlineData("", "", "@{:}")]
    [InlineData("X", "", "@{X:}")]
    [InlineData("", "y", "@{:y}")]
    public void Token_BuildsExpectedString(string type, string id, string expected)
    {
        Assert.Equal(expected, MentionParser.Token(type, id));
    }

    [Fact]
    public void Token_DoesNotValidateId()
    {
        // Token is a pure formatter; it accepts non-GUID ids verbatim.
        Assert.Equal("@{X:not-a-guid}", MentionParser.Token("X", "not-a-guid"));
    }

    // ---------- Round-trip Token -> Parse ----------

    [Fact]
    public void TokenThenParse_ValidGuid_RoundTrips()
    {
        MentionToken m = Assert.Single(MentionParser.Parse(MentionParser.Token("Fraktion", Guid2)));
        Assert.Equal("Fraktion", m.Type);
        Assert.Equal(Guid2, m.Id);
    }

    [Fact]
    public void TokenThenParse_InvalidGuid_ProducesNoMatch()
    {
        // Token happily formats an invalid id, but Parse's regex rejects it.
        string token = MentionParser.Token("Person", "not-a-guid");
        Assert.Empty(MentionParser.Parse(token));
    }

    // ---------- Strip ----------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Strip_NullOrEmpty_ReturnsEmpty(string? text)
    {
        Assert.Equal(string.Empty, MentionParser.Strip(text));
    }

    [Fact]
    public void Strip_TextWithoutToken_IsUnchanged()
    {
        const string text = "Ganz normaler Text ohne Erwähnung.";
        Assert.Equal(text, MentionParser.Strip(text));
    }

    [Fact]
    public void Strip_TextWithBareAtSign_IsUnchanged()
    {
        // A lone '@' takes the regex path but matches nothing.
        Assert.Equal("Mail an a@b.de", MentionParser.Strip("Mail an a@b.de"));
    }

    [Fact]
    public void Strip_SingleToken_RemovesItAndCollapsesGap()
    {
        string text = $"Treffen mit {MentionParser.Token("Person", Guid1)} im Hafen";
        Assert.Equal("Treffen mit im Hafen", MentionParser.Strip(text));
    }

    [Fact]
    public void Strip_MultipleTokens_RemovesAll()
    {
        string text = $"{MentionParser.Token("Agent", Guid1)} und {MentionParser.Token("Faction", Guid2)}";
        Assert.Equal("und", MentionParser.Strip(text));
    }

    [Fact]
    public void Strip_TokenOnly_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, MentionParser.Strip(MentionParser.Token("Person", Guid1)));
    }

    [Fact]
    public void Strip_LeadingAndTrailingToken_TrimsEdges()
    {
        string text = $"{MentionParser.Token("Person", Guid1)} Mitte {MentionParser.Token("Person", Guid2)}";
        Assert.Equal("Mitte", MentionParser.Strip(text));
    }

    [Fact]
    public void Strip_InvalidToken_IsKept()
    {
        // Strip uses the same regex as Parse, so a non-GUID id is not a token.
        const string text = "Text @{Person:not-a-guid} Ende";
        Assert.Equal(text, MentionParser.Strip(text));
    }

    [Fact]
    public void Strip_PreservesNewlines()
    {
        string text = $"Zeile1\nZeile2 {MentionParser.Token("Person", Guid1)}";
        Assert.Equal("Zeile1\nZeile2", MentionParser.Strip(text));
    }

    // ---------- Neutralize / ClipSafe ----------

    [Fact]
    public void Neutralize_ReplacesOnlyMatchingTypes()
    {
        var text = $"a {MentionParser.Token("InvestigationNote", Guid1)} b {MentionParser.Token("Person", Guid2)}";

        var result = MentionParser.Neutralize(text, t => t.StartsWith("Investigation", StringComparison.Ordinal));

        Assert.Equal($"a [Verweis] b {MentionParser.Token("Person", Guid2)}", result);
        Assert.Equal(string.Empty, MentionParser.Neutralize(null, _ => true));
    }

    [Fact]
    public void ClipSafe_NeverLeavesAHalfToken()
    {
        var token = MentionParser.Token("InvestigationEvidence", Guid1);
        var text = $"Siehe {token} und mehr Text";

        for (var max = 0; max <= text.Length + 2; max++)
        {
            var clipped = MentionParser.ClipSafe(text, max);
            Assert.True(clipped.Length <= Math.Max(0, max));
            var opens = clipped.Split("@{").Length - 1;
            Assert.Equal(opens, MentionParser.Parse(clipped).Count);
        }
        Assert.Equal("Siehe", MentionParser.ClipSafe(text, 10));
        Assert.Equal($"Siehe {token}", MentionParser.ClipSafe(text, 6 + token.Length));
        Assert.Equal(text, MentionParser.ClipSafe(text, text.Length));
    }
}
