using System.Security.Claims;
using System.Text.RegularExpressions;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Llm;

namespace NOOSE_Website.Services;

/// <summary>NOOSEI inside a rich-text editor: proofreading and composing.</summary>
public interface ITextAssistService
{
    bool IsAvailable { get; }

    /// <summary>Corrects spelling and clear grammar errors without touching formatting, meaning or facts.</summary>
    Task<TextAssistResult> CorrectAsync(string? html, TextAssistContext context, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    /// <summary>Writes text from an instruction; the result is Markdown rendered for the editor.</summary>
    Task<TextAssistResult> ComposeAsync(string instruction, string? surroundingText, TextAssistContext context,
        string? subject, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ITextAssistService" />
public sealed partial class TextAssistService(INooseiGateway noosei, INooseiSettingsService settings) : ITextAssistService
{
    /// <summary>Plain block text, not HTML: a document editor legitimately holds more prose than a dossier context.</summary>
    public const int MaxCorrectChars = 12_000;

    public const int MaxBlocks = 400;

    public const int MaxInstructionChars = 1_000;

    /// <summary>Above this share of touched words a correction looks like a rewrite, and the dialog says so.</summary>
    public const double RewriteRatio = 0.35;

    [GeneratedRegex(@"\{\{[^}]{1,64}\}\}", RegexOptions.Compiled)]
    private static partial Regex PlaceholderRegex();

    [GeneratedRegex(@"\b(NAME|BEWERBER|DATUM|UHRZEIT|DIENSTGRAD)\b", RegexOptions.Compiled)]
    private static partial Regex RecruitingTokenRegex();

    [GeneratedRegex(NooseiCitations.CaseNumberPattern + @"|\d+", RegexOptions.Compiled)]
    private static partial Regex FactRegex();

    [GeneratedRegex(@"@\{\w+:[0-9a-fA-F-]{36}\}", RegexOptions.Compiled)]
    private static partial Regex MentionRegex();

    public bool IsAvailable => noosei.IsConfigured;

    public async Task<TextAssistResult> CorrectAsync(
        string? html, TextAssistContext context, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLlmUse(actor);
        Permission.RequireWriteAccess(actor);
        if (!noosei.IsConfigured)
        {
            throw new InvalidOperationException("FIBI ist nicht konfiguriert.");
        }

        // keeps the editor's image placeholder alive; the plain Clean() drops it and the picture with it
        var clean = HtmlCleanup.CleanAiPayload(html);
        var document = TextBlocks.Parse(clean);
        // the very same list the prompt is built from — a second, filtered one would shift the numbering
        var blocks = document.Blocks;
        if (blocks.Count == 0)
        {
            throw new InvalidOperationException("Es gibt nichts zu korrigieren.");
        }
        if (blocks.Count > MaxBlocks || document.TotalChars > MaxCorrectChars)
        {
            throw new InvalidOperationException(
                $"Der Text ist zu lang für FIBI (max. rund {MaxCorrectChars:N0} Zeichen Fließtext). "
                + "Markiere den Abschnitt, den FIBI korrigieren soll.");
        }

        var answer = await noosei.AskAsync(
            new NooseiCall(
                LlmFeature.Proofread,
                [
                    LlmMessage.System(NooseiPrompts.Combine(
                        NooseiPrompts.Proofread + "\n\n" + ContextLine(context),
                        await AddendumAsync(cancellationToken))),
                    LlmMessage.User(document.ToPrompt()),
                ],
                LoggedPrompt: document.ToPrompt(),
                Temperature: 0.1,
                // the model has to echo the whole text back, and a reasoning model spends part of this budget
                // before writing a single character — too tight a cap truncates the answer mid-block
                MaxTokens: Math.Min(8_000, document.TotalChars + 1_000)),
            actor,
            cancellationToken);

        if (answer.Truncated)
        {
            throw new InvalidOperationException(
                "FIBI hat die Antwort abgeschnitten. Bitte einen kürzeren Abschnitt markieren "
                + "und erneut korrigieren lassen.");
        }

        var corrections = TextBlocks.ParseAnswer(answer.Text, blocks.Count)
            ?? throw new InvalidOperationException("FIBI hat unbrauchbar geantwortet. Bitte erneut versuchen.");

        for (var i = 0; i < blocks.Count; i++)
        {
            TextBlocks.Apply(blocks[i], corrections[blocks[i].Number]);
        }
        var corrected = document.ToHtml();

        Guard(context, clean, corrected);

        var diff = HtmlDiff.Compare(clean, corrected);
        var warnings = new List<string>();
        if (diff.StructureChanged)
        {
            warnings.Add("FIBI hat die Gliederung verändert. Bitte genau prüfen.");
        }
        if (diff.ChangedRatio > RewriteRatio)
        {
            warnings.Add("FIBI hat den Text stark verändert. Das sieht nach einer Umformulierung aus, "
                + "nicht nach einer Korrektur. Bitte Zeile für Zeile prüfen.");
        }
        if (diff.Degraded)
        {
            warnings.Add("Die Unterschiede lassen sich nicht Wort für Wort darstellen, weil zu viel geändert wurde.");
        }

        return new TextAssistResult(
            corrected, diff.Html, answer.Charge.QuotaTokens, answer.Charge.Status,
            diff.StructureChanged, diff.ChangedRatio, diff.Unchanged, diff.Degraded, warnings);
    }

    public async Task<TextAssistResult> ComposeAsync(
        string instruction, string? surroundingText, TextAssistContext context, string? subject,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLlmUse(actor);
        Permission.RequireWriteAccess(actor);
        if (!noosei.IsConfigured)
        {
            throw new InvalidOperationException("FIBI ist nicht konfiguriert.");
        }
        if (string.IsNullOrWhiteSpace(instruction))
        {
            throw new InvalidOperationException("Bitte beschreiben, was FIBI schreiben soll.");
        }

        var task = instruction.Trim();
        if (task.Length > MaxInstructionChars)
        {
            task = task[..MaxInstructionChars];
        }

        var prompt = new System.Text.StringBuilder();
        prompt.Append("Auftrag: ").AppendLine(task);
        if (!string.IsNullOrWhiteSpace(subject))
        {
            prompt.Append("Bezug: ").AppendLine(subject.Trim());
        }
        if (!string.IsNullOrWhiteSpace(surroundingText))
        {
            prompt.AppendLine("Bereits vorhandener Text (nur als Kontext, nicht wiederholen):");
            prompt.AppendLine(PromptRedactor.Clip(surroundingText, 2_000));
        }

        var answer = await noosei.AskAsync(
            new NooseiCall(
                LlmFeature.Compose,
                [
                    LlmMessage.System(NooseiPrompts.Combine(
                        NooseiPrompts.Compose + "\n\n" + ContextLine(context),
                        await AddendumAsync(cancellationToken))),
                    LlmMessage.User(prompt.ToString()),
                ],
                LoggedPrompt: task,
                Temperature: 0.5,
                MaxTokens: 1_500),
            actor,
            cancellationToken);

        var html = MarkdownRenderer.ToEditorHtml(answer.Text);
        if (string.IsNullOrWhiteSpace(html))
        {
            throw new InvalidOperationException("FIBI hat keinen Text geliefert. Bitte erneut versuchen.");
        }
        GuardTemplateTokens(context, string.Empty, html, composing: true);

        return new TextAssistResult(
            html, null, answer.Charge.QuotaTokens, answer.Charge.Status,
            false, 0, false, false, []);
    }

    // ---- guards ----

    /// <summary>Everything a correction is forbidden to change. A violation rejects the answer outright.</summary>
    private static void Guard(TextAssistContext context, string before, string after)
    {
        GuardTemplateTokens(context, before, after, composing: false);

        if (!SameMultiset(FactRegex(), before, after))
        {
            throw new InvalidOperationException(
                "FIBI hat Zahlen oder Aktenzeichen verändert. Die Korrektur wurde verworfen.");
        }
        if (!SameMultiset(MentionRegex(), before, after))
        {
            throw new InvalidOperationException(
                "FIBI hat Erwähnungen verändert. Die Korrektur wurde verworfen.");
        }
    }

    private static void GuardTemplateTokens(TextAssistContext context, string before, string after, bool composing)
    {
        if (context is TextAssistContext.DocumentTemplate or TextAssistContext.ActivityTemplate
            or TextAssistContext.PersonnelTemplate or TextAssistContext.RecruitingTemplate)
        {
            if (!composing && !SameMultiset(PlaceholderRegex(), before, after))
            {
                throw new InvalidOperationException(
                    "FIBI hat Platzhalter verändert. Die Korrektur wurde verworfen.");
            }
        }

        if (context != TextAssistContext.RecruitingTemplate)
        {
            return;
        }

        // The blackout in BewerbungTemplateRenderer matches \bNAME\b case-sensitively. A "correction" to
        // "Name" would silently switch it off for every message built from this template, so the count AND the
        // exact casing of these bare tokens must survive untouched.
        if (composing)
        {
            return;
        }
        if (!SameMultiset(RecruitingTokenRegex(), before, after))
        {
            throw new InvalidOperationException(
                "FIBI hat die Anonymisierungs-Platzhalter (NAME, BEWERBER, …) verändert. "
                + "Die Korrektur wurde verworfen, weil sonst die Schwärzung ausfällt.");
        }
    }

    /// <summary>Same tokens, same number of times, same casing — order may differ, nothing else may.</summary>
    private static bool SameMultiset(Regex regex, string before, string after)
    {
        var left = Counts(regex, before);
        var right = Counts(regex, after);
        return left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var n) && n == pair.Value);
    }

    private static Dictionary<string, int> Counts(Regex regex, string text)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match match in regex.Matches(text))
        {
            counts[match.Value] = counts.GetValueOrDefault(match.Value) + 1;
        }
        return counts;
    }

    // ---- prompt context ----

    private const string TemplateLine =
        "Der Text ist eine wiederverwendbare Vorlage. Ausdrücke in doppelten geschweiften Klammern "
        + "({{Name}}, {{Aktenzeichen}}, {{Datum}}, {{Uhrzeit}}, {{Agent}}, {{Dienstgrad}}) sind Steuerzeichen "
        + "und werden Zeichen für Zeichen unverändert übernommen.";

    private const string RecruitingLine =
        "Der Text ist eine Vorlage für ein Bewerber-Anschreiben. Die Wörter NAME, BEWERBER, DATUM, UHRZEIT "
        + "und DIENSTGRAD sind in GROSSBUCHSTABEN Steuerzeichen. Sie werden exakt so, in Großbuchstaben, "
        + "unverändert übernommen und niemals korrigiert, kleingeschrieben oder ersetzt.";

    private static string ContextLine(TextAssistContext context) => context switch
    {
        TextAssistContext.Document => "Der Text ist ein Dokument der FIB-Aktenbibliothek.",
        TextAssistContext.Announcement => "Der Text ist eine interne Bekanntmachung am schwarzen Brett der Behörde.",
        TextAssistContext.Activity => "Der Text ist ein Dienstbericht (Aktivitätsnachweis) eines Agenten.",
        TextAssistContext.MeetingMinutes => "Der Text ist das Protokoll einer Besprechung.",
        TextAssistContext.AgendaNote => "Der Text ist eine Notiz zu einem Tagesordnungspunkt.",
        TextAssistContext.PersonnelNote => "Der Text ist eine Notiz in der Personalakte eines Agenten.",
        TextAssistContext.Promotion => "Der Text ist die Begründung eines Beförderungsantrags.",
        TextAssistContext.RecruitingTemplate => RecruitingLine,
        _ => TemplateLine,
    };

    private async Task<string?> AddendumAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await settings.GetAddendumAsync(cancellationToken);
        }
        catch (Exception)
        {
            return null; // a missing house rule must never block an editor action
        }
    }
}
