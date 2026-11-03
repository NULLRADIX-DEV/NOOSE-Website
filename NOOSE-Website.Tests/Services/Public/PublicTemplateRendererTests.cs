using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Tests.Services.Public;

/// <summary>The fourth token system: what it fills, what it blacks out and what it refuses.</summary>
public class PublicTemplateRendererTests
{
    private static readonly DateTime Stamp = new(2026, 8, 20, 11, 48, 0, DateTimeKind.Unspecified);

    private static PublicTemplateContext Ctx(string? name = "Max Mustermann", string? caseNumber = "FIB-T-2026-0001")
        => new(name, caseNumber, Stamp);

    [Fact]
    public void EveryToken_IsFilled()
    {
        var text = PublicTemplateRenderer.Render(
            "Sehr geehrte/r BUERGER, Ihr Anliegen AKTENZEICHEN ging am DATUM um UHRZEIT ein.", Ctx());

        Assert.Equal("Sehr geehrte/r Max Mustermann, Ihr Anliegen FIB-T-2026-0001 ging am 20.08.2026 um 11:48 ein.",
            text);
    }

    [Fact]
    public void SenderName_IsRedacted()
    {
        var text = PublicTemplateRenderer.Render("Mit freundlichen Grüßen\nNAME", Ctx());

        Assert.EndsWith(PublicTemplateRenderer.Redaction, text, StringComparison.Ordinal);
        Assert.DoesNotContain("NAME", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ASubstitutedNameContainingTheToken_IsNotRedactedAfterwards()
    {
        // redaction runs first for exactly this reason: nothing substituted in may be hit by it
        var text = PublicTemplateRenderer.Render("Hallo BUERGER", Ctx("NAME Nachname"));

        Assert.Equal("Hallo NAME Nachname", text);
    }

    [Fact]
    public void TokensMatchWholeWordsOnly()
    {
        var text = PublicTemplateRenderer.Render("AKTENZEICHENX bleibt, VORNAME bleibt", Ctx());

        Assert.Equal("AKTENZEICHENX bleibt, VORNAME bleibt", text);
    }

    [Fact]
    public void NothingIsHtmlEncoded()
    {
        // the substantive difference to BewerbungTemplateRenderer: the target column is plain text, and an
        // encoded ampersand would be delivered to the citizen as "&amp;"
        var text = PublicTemplateRenderer.Render("Firma BUERGER & Söhne <intern>", Ctx("Müller & Sohn"));

        Assert.Equal("Firma Müller & Sohn & Söhne <intern>", text);
    }

    [Fact]
    public void LineBreaksSurvive()
    {
        var text = PublicTemplateRenderer.Render("Zeile eins\n\nZeile zwei", Ctx());

        Assert.Equal("Zeile eins\n\nZeile zwei", text);
    }

    [Fact]
    public void WithoutAName_TheSalutationFallsBackInsteadOfGoingBlank()
    {
        // the anonymous tip is why the fallback exists at all
        var text = PublicTemplateRenderer.Render("Sehr geehrte/r BUERGER,", Ctx(name: null));

        Assert.Equal($"Sehr geehrte/r {PublicTemplateRenderer.CitizenFallback},", text);
        Assert.DoesNotContain("Sehr geehrte/r ,", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("")]
    public void ABlankName_CountsAsNoName(string name)
        => Assert.Equal(PublicTemplateRenderer.CitizenFallback,
            PublicTemplateRenderer.Render("BUERGER", Ctx(name)));

    [Fact]
    public void WithoutACaseNumber_ADashStandsIn()
        => Assert.Equal("Vorgang —", PublicTemplateRenderer.Render("Vorgang AKTENZEICHEN", Ctx(caseNumber: null)));

    [Theory]
    [InlineData("Hallo {{Name}}")]
    // the bare opener too: it would be delivered verbatim, and WarnhinweisService rejects it the same way
    [InlineData("Hallo {{Name ohne Ende")]
    [InlineData("Siehe @{Person:11111111-1111-1111-1111-111111111111}")]
    [InlineData("Lieber BEWERBER")]
    [InlineData("Ihr DIENSTGRAD")]
    public void TokensOfTheOtherSystems_AreForeign(string text)
        => Assert.True(PublicTemplateRenderer.HasForeignToken(text));

    [Theory]
    [InlineData("Am DATUM um UHRZEIT, BUERGER, AKTENZEICHEN, NAME")]
    [InlineData("Ganz ohne Platzhalter")]
    [InlineData("")]
    public void TheOwnSet_IsNotForeign(string text)
        => Assert.False(PublicTemplateRenderer.HasForeignToken(text));

    [Fact]
    public void TheSample_RendersWithoutAnyTokenLeftOver()
    {
        var text = PublicTemplateRenderer.Render("BUERGER AKTENZEICHEN DATUM UHRZEIT NAME",
            PublicTemplateRenderer.SampleContext(Stamp));

        Assert.Equal($"Max Mustermann FIB-T-2026-0001 20.08.2026 11:48 {PublicTemplateRenderer.Redaction}", text);
    }

    [Fact]
    public void TheStoredLength_IsDerivedFromTheMessageCap()
    {
        // one number, not two: what is stored has to still fit once the substitutions grew it
        Assert.True(PublicTemplateRules.MaxLength < TicketRules.MaxMessageLength);
        Assert.True(PublicTemplateRules.MaxLength > PublicTemplateRules.MinLength);
        Assert.Equal(TipRules.MaxMessageLength, TicketRules.MaxMessageLength);
    }
}
