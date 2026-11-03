using NOOSE_Website.Models.Llm;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>The one mechanical check on a chat answer. It warns and never rejects — and it never says a record does
/// not exist, because NOOSEI only ever saw the part of the stock the asker may see.</summary>
public class NooseiCitationsTests
{
    [Fact]
    public void Unsupported_FindsACaseNumberNoToolEverReturned()
    {
        var evidence = new[] { "Treffer: Person | Max Mustermann | Aktenzeichen: FIB-P-2026-0001" };

        var unsupported = NooseiCitations.Unsupported(
            "Max Mustermann [Person Max Mustermann · FIB-P-2026-0001] führt die Ballas "
            + "[Fraktion Ballas · FIB-F-2026-0099].", evidence);

        Assert.Equal("FIB-F-2026-0099", Assert.Single(unsupported));
    }

    [Fact]
    public void Unsupported_StillReadsTheLegacyNoosePrefix()
    {
        var unsupported = NooseiCitations.Unsupported(
            "Siehe [Person Max Mustermann · NOOSE-P-2026-0001] und NOOSE-F-2026-0099.",
            ["Treffer: Aktenzeichen: NOOSE-P-2026-0001"]);

        Assert.Equal("NOOSE-F-2026-0099", Assert.Single(unsupported));
    }

    [Fact]
    public void Unsupported_AcceptsANumberTheQuestionItselfNamed()
    {
        // a case number quoted back at the asker is not a fabrication
        var unsupported = NooseiCitations.Unsupported(
            "Zu FIB-P-2026-0042 finde ich nichts.", ["Was steht in FIB-P-2026-0042?"]);

        Assert.Empty(unsupported);
    }

    [Fact]
    public void Unsupported_IsEmpty_WhenTheAnswerCitesNothing()
    {
        Assert.Empty(NooseiCitations.Unsupported("Dazu finde ich keine Akte.", ["irgendwas"]));
        Assert.Empty(NooseiCitations.Unsupported(null, ["irgendwas"]));
        Assert.Empty(NooseiCitations.Unsupported("   ", ["irgendwas"]));
    }

    [Fact]
    public void Unsupported_ReportsEachNumberOnce_AndCapsTheList()
    {
        var answer = string.Join(" ", Enumerable.Range(1, 12).Select(i => $"FIB-P-2026-{i:0000}"))
            + " FIB-P-2026-0001";

        var unsupported = NooseiCitations.Unsupported(answer, []);

        Assert.Equal(NooseiCitations.MaxReported, unsupported.Count);
        Assert.Equal(unsupported.Count, unsupported.Distinct().Count());
    }

    [Fact]
    public void Evidence_TakesToolOutputAndTheQuestions_ButNotAnEarlierAnswer()
    {
        IReadOnlyList<LlmMessage> transcript =
        [
            LlmMessage.User("Alte Frage zu FIB-P-2026-0004"),
            LlmMessage.Assistant("Frühere Antwort mit FIB-P-2026-0002"),
            LlmMessage.Tool("c1", "suche_akten", "Treffer FIB-P-2026-0001"),
        ];

        var evidence = NooseiCitations.Evidence(transcript, "Neue Frage").ToList();

        Assert.Contains(evidence, e => e!.Contains("FIB-P-2026-0001"));
        Assert.Contains(evidence, e => e!.Contains("FIB-P-2026-0004"));
        Assert.Contains(evidence, e => e == "Neue Frage");
        // a number the model invented once would otherwise vouch for itself in every follow-up
        Assert.DoesNotContain(evidence, e => e!.Contains("FIB-P-2026-0002"));
    }

    [Fact]
    public void Evidence_IgnoresTheSystemPrompt_WhichShowsTheCitationFormByExample()
    {
        // NooseiPrompts.Chat carries a sample case number; counting it would license the one fake number the
        // model is most likely to reach for
        IReadOnlyList<LlmMessage> transcript = [LlmMessage.System(NooseiPrompts.Chat)];

        var unsupported = NooseiCitations.Unsupported(
            "Siehe [Person Max Mustermann · FIB-P-2026-0001].",
            NooseiCitations.Evidence(transcript, "Frage"));

        Assert.Equal("FIB-P-2026-0001", Assert.Single(unsupported));
    }

    [Fact]
    public void Evidence_CountsToolOutputThatHadToTravelAsPlainContext()
    {
        // the shape a scope change and every pre-tool-call conversation produce
        IReadOnlyList<LlmMessage> transcript =
        [
            LlmMessage.Assistant(NooseiHistoryWindow.Flatten("lies_akte", "Akte FIB-P-2026-0007")),
        ];

        var unsupported = NooseiCitations.Unsupported(
            "Siehe FIB-P-2026-0007.", NooseiCitations.Evidence(transcript, "Frage"));

        Assert.Empty(unsupported);
    }

    [Fact]
    public void Evidence_WithoutATranscript_IsJustTheQuestion()
        => Assert.Equal(["Frage"], NooseiCitations.Evidence(null, "Frage").ToList());

    [Fact]
    public void Notice_SaysNotEvidenced_AndNeverThatSomethingDoesNotExist()
    {
        var one = NooseiCitations.Notice(["FIB-P-2026-0009"]);
        var two = NooseiCitations.Notice(["FIB-P-2026-0009", "FIB-F-2026-0002"]);

        Assert.NotNull(one);
        Assert.Contains("Nicht belegt", one);
        Assert.Contains("FIB-P-2026-0009", one);
        Assert.DoesNotContain("existiert", one);
        Assert.DoesNotContain("existiert", two);
        // singular and plural differ, because a warning that reads wrong reads as a bug
        Assert.Contains("dieses Aktenzeichen", one);
        Assert.Contains("diese Aktenzeichen", two);
    }

    [Fact]
    public void Notice_OfNothing_IsNull() => Assert.Null(NooseiCitations.Notice([]));
}
