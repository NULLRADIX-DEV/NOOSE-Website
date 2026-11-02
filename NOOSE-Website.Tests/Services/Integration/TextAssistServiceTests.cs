using System.Security.Claims;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Llm;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>NOOSEI in the editor. Almost every test here is about an answer that must be REJECTED.</summary>
public sealed class TextAssistServiceTests
{
    private static ClaimsPrincipal Agent()
        => ClaimsPrincipalBuilder.Agent("a").WithRank(Rank.SpecialAgent).Build();

    private static NooseiAnswer Answer(string text, bool truncated = false)
        => new(text, LlmUsage.Empty, new LlmQuotaCharge(90, 0.0009m, LlmQuotaStatus.Empty, null, true), 1, [], false,
            truncated);

    private static TextAssistService Build(string answer, Action<NooseiCall>? inspect = null, bool truncated = false)
    {
        var gateway = Substitute.For<INooseiGateway>();
        gateway.IsConfigured.Returns(true);
        gateway.AskAsync(Arg.Any<NooseiCall>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                inspect?.Invoke(call.Arg<NooseiCall>());
                return Answer(answer, truncated);
            });

        var settings = Substitute.For<INooseiSettingsService>();
        settings.GetAddendumAsync(Arg.Any<CancellationToken>()).Returns((string?)null);

        return new TextAssistService(gateway, settings);
    }

    // ---- the happy path ----

    [Fact]
    public async Task Correct_FixesTheTextAndKeepsTheMarkup()
    {
        var svc = Build("[1] Der Verdächtige wurde festgenommen.");

        var result = await svc.CorrectAsync(
            "<p>Der Verdächtige wurde <b>festgenomen</b>.</p>", TextAssistContext.Document, Agent());

        Assert.Contains("festgenommen", result.Html);
        Assert.Contains("<b>", result.Html);
        Assert.False(result.StructureChanged);
        Assert.NotNull(result.DiffHtml);
        Assert.Contains("<ins", result.DiffHtml);
        Assert.Equal(90L, result.QuotaTokens);
    }

    [Fact]
    public async Task Correct_NeverSendsMarkupToTheModel()
    {
        NooseiCall? seen = null;
        var svc = Build("[1] Der Verdächtige wurde festgenommen.", call => seen = call);

        await svc.CorrectAsync(
            "<p>Der Verdächtige wurde <b>festgenomen</b>.</p><p><img src=\"data:image/png;base64,AAAA\"></p>",
            TextAssistContext.Document, Agent());

        var payload = string.Join("\n", seen!.Messages.Select(m => m.Content));
        Assert.DoesNotContain("<b>", payload);
        Assert.DoesNotContain("base64", payload);
        Assert.Contains("[1]", payload);
    }

    [Fact]
    public async Task Correct_KeepsTheEditorsImagePlaceholder()
    {
        // The editor swaps every base64 image for this marker before marshalling and swaps it back on apply.
        // Sanitizing it away deleted the picture from the corrected document without anyone noticing:
        // both guards compare the stripped text against itself, and the diff only looks at words.
        var svc = Build("[1] Der Verdächtige wurde festgenommen.");

        var result = await svc.CorrectAsync(
            "<p>Der Verdächtige wurde <b>festgenomen</b>.</p><p><img data-noosei-bild=\"0\"></p>",
            TextAssistContext.Document, Agent());

        Assert.Contains("data-noosei-bild=\"0\"", result.Html);
        Assert.Contains("festgenommen", result.Html);
    }

    [Fact]
    public async Task Correct_NumbersOnlyTheBlocksItSends()
    {
        // Quill leaves an empty <p> behind on every extra Enter; a numbered-but-empty line in the prompt
        // made the model answer with more blocks than the answer check expected
        NooseiCall? seen = null;
        var svc = Build("[1] Erster Satz.\n[2] Zweiter Satz.", call => seen = call);

        var result = await svc.CorrectAsync(
            "<p>erster satz</p><p><br></p><p>zweiter satz</p>", TextAssistContext.Document, Agent());

        var payload = string.Join("\n", seen!.Messages.Select(m => m.Content));
        Assert.Contains("[2]", payload);
        Assert.DoesNotContain("[3]", payload);
        Assert.Contains("Erster Satz.", result.Html);
        Assert.Contains("Zweiter Satz.", result.Html);
    }

    [Fact]
    public async Task Correct_KeepsTheEmptyParagraphInPlace()
    {
        var svc = Build("[1] Eins.\n[2] Zwei.");

        var result = await svc.CorrectAsync(
            "<p>eins</p><p><br></p><p>zwei</p>", TextAssistContext.Document, Agent());

        Assert.Contains("<p><br></p>", result.Html);
    }

    [Fact]
    public async Task Correct_ToleratesAMarkdownDecoratedMarker()
    {
        var svc = Build("**[1]** Der Verdächtige wurde festgenommen.");

        var result = await svc.CorrectAsync(
            "<p>Der Verdächtige wurde festgenomen.</p>", TextAssistContext.Document, Agent());

        Assert.Contains("festgenommen", result.Html);
    }

    [Fact]
    public async Task Correct_ReportsATruncatedAnswerAsSuch()
    {
        var svc = Build("[1] Der Verdächtige wurde festge", truncated: true);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CorrectAsync("<p>Der Verdächtige wurde festgenomen.</p><p>Zweiter Satz.</p>",
                TextAssistContext.Document, Agent()));

        Assert.Contains("abgeschnitten", ex.Message);
    }

    [Fact]
    public async Task Correct_ReportsWhenNothingChanged()
    {
        var svc = Build("[1] Alles korrekt.");

        var result = await svc.CorrectAsync("<p>Alles korrekt.</p>", TextAssistContext.Document, Agent());

        Assert.True(result.Unchanged);
        Assert.Empty(result.Warnings);
    }

    // ---- structural and volume warnings ----

    [Fact]
    public async Task Correct_WarnsAboutARewrite()
    {
        var svc = Build("[1] Vollständig anders formulierter Text ohne jede Ähnlichkeit.");

        var result = await svc.CorrectAsync(
            "<p>Der Verdaechtige wurde gestern festgenomen.</p>", TextAssistContext.Document, Agent());

        Assert.True(result.ChangedRatio > TextAssistService.RewriteRatio);
        Assert.Contains(result.Warnings, w => w.Contains("Umformulierung"));
    }

    // ---- hard rejections ----

    [Fact]
    public async Task Correct_RejectsAChangedBlockCount()
    {
        var svc = Build("[1] Eins korrigiert.\n[2] Ein Absatz zu viel.");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CorrectAsync("<p>Eins.</p>", TextAssistContext.Document, Agent()));
    }

    [Fact]
    public async Task Correct_RejectsAChangedNumber()
    {
        var svc = Build("[1] Die Festnahme war am 4.4. um 19 Uhr.");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CorrectAsync("<p>Die Festnahme war am 3.4. um 19 Uhr.</p>", TextAssistContext.Document, Agent()));

        Assert.Contains("Zahlen", ex.Message);
    }

    [Fact]
    public async Task Correct_RejectsAChangedCaseNumber()
    {
        var svc = Build("[1] Siehe Akte FIB-P-2026-0002.");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CorrectAsync("<p>Siehe Akte FIB-P-2026-0001.</p>", TextAssistContext.Document, Agent()));
    }

    [Fact]
    public async Task Correct_RejectsAChangedPlaceholder()
    {
        var svc = Build("[1] Sehr geehrte(r) {{name}}, hiermit bestätigen wir.");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CorrectAsync("<p>Sehr geehrte(r) {{Name}}, hiermit bestaetigen wir.</p>",
                TextAssistContext.DocumentTemplate, Agent()));

        Assert.Contains("Platzhalter", ex.Message);
    }

    [Fact]
    public async Task Correct_RejectsADowncasedRecruitingToken()
    {
        // BewerbungTemplateRenderer matches \bNAME\b case-sensitively: "Name" would silently disable the blackout
        var svc = Build("[1] Hallo Name, dein Termin ist am DATUM.");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CorrectAsync("<p>Hallo NAME, dein Termin ist am DATUM.</p>",
                TextAssistContext.RecruitingTemplate, Agent()));

        Assert.Contains("Schwärzung", ex.Message);
    }

    [Fact]
    public async Task Correct_RejectsARemovedRecruitingToken()
    {
        var svc = Build("[1] Hallo Bewerber, dein Termin ist am DATUM.");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CorrectAsync("<p>Hallo BEWERBER, dein Termin ist am DATUM.</p>",
                TextAssistContext.RecruitingTemplate, Agent()));
    }

    [Fact]
    public async Task Correct_AcceptsARecruitingTemplateWithUntouchedTokens()
    {
        var svc = Build("[1] Hallo NAME, dein Termin ist am DATUM.");

        var result = await svc.CorrectAsync("<p>Hallo NAME, dein Termin ist am DATUM.</p>",
            TextAssistContext.RecruitingTemplate, Agent());

        Assert.Contains("NAME", result.Html);
    }

    [Fact]
    public async Task Correct_RejectsAChangedMentionToken()
    {
        var id = Guid.NewGuid().ToString();
        var svc = Build("[1] Siehe dazu die Akte.");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CorrectAsync($"<p>Siehe dazu @{{Person:{id}}} die Akte.</p>", TextAssistContext.Document, Agent()));
    }

    [Fact]
    public async Task Correct_RejectsAnUnusableAnswer()
    {
        var svc = Build("Tut mir leid, das kann ich nicht.");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CorrectAsync("<p>Ein Satz.</p>", TextAssistContext.Document, Agent()));
    }

    // ---- limits and guards ----

    [Fact]
    public async Task Correct_RejectsAnEmptyDocument()
    {
        var svc = Build("[1] egal");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CorrectAsync("<p><br></p>", TextAssistContext.Document, Agent()));
    }

    [Fact]
    public async Task Correct_RejectsAnOversizeDocument()
    {
        var svc = Build("[1] egal");
        var huge = "<p>" + new string('x', TextAssistService.MaxCorrectChars + 100) + "</p>";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CorrectAsync(huge, TextAssistContext.Document, Agent()));

        Assert.Contains("zu lang", ex.Message);
    }

    [Theory]
    [InlineData("partner")]
    [InlineData("demo")]
    [InlineData("supervision")]
    public async Task Correct_DeniesTheRolesWithoutEditorAccess(string role)
    {
        var svc = Build("[1] egal");
        var builder = ClaimsPrincipalBuilder.Agent("a").WithRank(Rank.SpecialAgent);
        var actor = role switch
        {
            "partner" => builder.AsPartner(PartnerAgency.LSPD, PartnerRank.Member).Build(),
            "demo" => builder.AsDemo().Build(),
            _ => builder.AsTeamLead().Build(),
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CorrectAsync("<p>Text.</p>", TextAssistContext.Document, actor));
    }

    // ---- composing ----

    [Fact]
    public async Task Compose_RendersMarkdownForTheEditor()
    {
        var svc = Build("**Fett** und eine Liste:\n\n- eins\n- zwei");

        var result = await svc.ComposeAsync("Schreib eine Bekanntmachung.", null,
            TextAssistContext.Announcement, "Dienstbesprechung", Agent());

        Assert.Contains("<strong>Fett</strong>", result.Html);
        Assert.Contains("<li>", result.Html);
        Assert.Null(result.DiffHtml);
    }

    [Fact]
    public async Task Compose_DropsTablesThatQuillCannotRender()
    {
        var svc = Build("| a | b |\n| - | - |\n| 1 | 2 |");

        var result = await svc.ComposeAsync("Tabelle bitte", null, TextAssistContext.Document, null, Agent());

        Assert.DoesNotContain("<table", result.Html);
    }

    [Fact]
    public async Task Compose_PassesTheInstructionAndSubject()
    {
        NooseiCall? seen = null;
        var svc = Build("Text", call => seen = call);

        await svc.ComposeAsync("Schreib eine Begründung.", "Vorhandener Text",
            TextAssistContext.Promotion, "Beförderung Falke", Agent());

        var payload = string.Join("\n", seen!.Messages.Select(m => m.Content));
        Assert.Contains("Schreib eine Begründung.", payload);
        Assert.Contains("Beförderung Falke", payload);
        Assert.Contains("Vorhandener Text", payload);
        Assert.Contains("Beförderungsantrags", payload); // the per-site context hint
    }

    [Fact]
    public async Task Compose_RejectsAnEmptyInstruction()
    {
        var svc = Build("Text");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ComposeAsync("   ", null, TextAssistContext.Document, null, Agent()));
    }

    [Fact]
    public async Task Compose_RejectsAnEmptyAnswer()
    {
        var svc = Build("   ");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ComposeAsync("Schreib was.", null, TextAssistContext.Document, null, Agent()));
    }
}
