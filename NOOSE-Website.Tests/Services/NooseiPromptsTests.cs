using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Llm;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>The prompt is a control, not copy. What is asserted here is what the code checks afterwards, or what a
/// weak model reliably gets wrong without being shown.</summary>
public class NooseiPromptsTests
{
    [Fact]
    public void Chat_NamesTheCitationForm_ExactlyAsTheCheckLooksForIt()
    {
        // the machine check in NooseiCitations only works if the prompt demands a form that carries a case number
        Assert.Contains("FIB-P-2026-0001", NooseiPrompts.Chat);
        Assert.Contains("[Person Max Mustermann", NooseiPrompts.Chat);
    }

    [Fact]
    public void Chat_CarriesTheStopRuleAndTheAmbiguityRule()
    {
        Assert.Contains("zweimal mit denselben Parametern", NooseiPrompts.Chat);
        Assert.Contains("frage nach", NooseiPrompts.Chat);
        Assert.Contains("hole_kurzbrief", NooseiPrompts.Chat);
    }

    [Fact]
    public void Chat_BoundsTheOutputFormatToWhatThePanelKeeps()
    {
        // MarkdownRenderer drops raw HTML, so asking for it produces an answer with holes in it
        Assert.Contains("kein HTML", NooseiPrompts.Chat);
        Assert.Contains("Keine Tabellen", NooseiPrompts.Chat);
    }

    [Fact]
    public void Chat_ShowsWorkedExamples_NotOnlyRules()
    {
        Assert.Contains("nur_anzahl", NooseiPrompts.Chat);
        Assert.Contains("finde_verbindungsweg", NooseiPrompts.Chat);
        // the one answer a tool result never justifies
        Assert.Contains("existiert nicht", NooseiPrompts.Chat);
    }

    [Fact]
    public void EveryPrompt_KeepsTheRoleplayCarveOutOrTheAntiFabricationRule()
    {
        foreach (var feature in Enum.GetValues<LlmFeature>())
        {
            var prompt = NooseiPrompts.Get(feature);
            Assert.False(string.IsNullOrWhiteSpace(prompt));
            Assert.Contains("FIBI", prompt);
        }
    }

    [Fact]
    public void Combine_AppendsTheAddendum_AndCanNeverDeleteAControl()
    {
        var combined = NooseiPrompts.Combine(NooseiPrompts.Chat, "Immer siezen.");

        Assert.StartsWith(NooseiPrompts.Chat, combined);
        Assert.Contains("Immer siezen.", combined);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Combine_WithoutAnAddendum_ChangesNothing(string? addendum)
        => Assert.Equal(NooseiPrompts.Chat, NooseiPrompts.Combine(NooseiPrompts.Chat, addendum));

    [Fact]
    public void MaxAnswerTokens_DefaultsToACeilingForTheChat_AndToNoneElsewhere()
    {
        var options = new LlmOptions();

        Assert.NotNull(options.MaxAnswerTokensFor(LlmFeature.Chat));
        Assert.Null(options.MaxAnswerTokensFor(LlmFeature.Brief));
    }

    [Fact]
    public void MaxAnswerTokens_OfZero_MeansNoCeilingAtAll()
    {
        var options = new LlmOptions();
        options.MaxAnswerTokensByFeature[LlmFeature.Chat] = 0;

        Assert.Null(options.MaxAnswerTokensFor(LlmFeature.Chat));
    }
}
