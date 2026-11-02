using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>The one escaper every Discord text goes through.</summary>
public class DiscordMarkdownTests
{
    [Theory]
    [InlineData("plain text", "plain text")]
    [InlineData("", "")]
    [InlineData("**fett**", @"\*\*fett\*\*")]
    [InlineData("_a_ ~b~ `c` |d| >e #f", @"\_a\_ \~b\~ \`c\` \|d\| \>e \#f")]
    [InlineData("<@123> <@&456>", @"\<@123\> \<@&456\>")]
    [InlineData("back\\slash", @"back\\slash")]
    [InlineData("(link)", @"\(link\)")]
    public void EveryMarkdownCharacter_IsEscaped(string text, string expected)
        => Assert.Equal(expected, DiscordMarkdown.Escape(text));

    [Fact]
    public void Brackets_AreSwappedForFullwidthOnesBecauseDiscordPairsThemBeforeReadingEscapes()
        => Assert.Equal(@"［a］\(b\)", DiscordMarkdown.Escape("[a](b)"));

    [Fact]
    public void ATextWithinTheLimit_IsLeftWhole()
        => Assert.Equal(@"\*\*\*", DiscordMarkdown.Escape("***", 6));

    [Fact]
    public void ATextPastTheLimit_IsCutAtAWholeEscapeWithAnEllipsis()
    {
        // 4 characters fit beside the ellipsis' one; an odd cut would leave a lone backslash
        var cut = DiscordMarkdown.Escape("*****", 5);

        Assert.Equal(@"\*\*…", cut);
        Assert.True(cut.Length <= 5);
    }

    [Fact]
    public void ACut_NeverSplitsAnEmoji()
    {
        var cut = DiscordMarkdown.Escape("ab🔥🔥🔥", 5);

        Assert.Equal("ab🔥…", cut);
        _ = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetBytes(cut);
    }

    [Fact]
    public void ALoneHighSurrogate_DoesNotThrow()
        => Assert.Equal("a\ud83d", DiscordMarkdown.Escape("a\ud83d"));
}
