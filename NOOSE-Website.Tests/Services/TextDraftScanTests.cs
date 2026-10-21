using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace NOOSE_Website.Tests.Services;

/// <summary>Structural guard for the browser drafts of long texts: who keeps one, and who drops it after saving.</summary>
/// <remarks>
/// The drafts live in the browser and the fields are Razor, so no test here can type into one. What decides whether
/// a lost circuit costs the text is visible in the source, though: a key on the field, and a drop after the save.
/// A missing key keeps nothing without saying so; a missing drop offers already saved text as unsaved next time.
/// </remarks>
public sealed class TextDraftScanTests
{
    /// <summary>Long fields that keep no draft on purpose, as "file#bound value" with the reason.</summary>
    private static readonly Dictionary<string, string> Exempt = new()
    {
        ["Common/Shared/BountyDialog.razor#_justification"] = "decision reason; restored on another decision it would be wrong",
        ["Common/Shared/FollowupDialog.razor#_note"] = "one-line note on a reminder",
        ["Common/Shared/NooseiDialog.razor#_instruction"] = "the answer is what counts, not the question",
        ["Common/Shared/PartnerShareDialog.razor#_reqJustification"] = "decision reason",
        ["Pages/Absences/Shared/AbsenceDialog.razor#_reason"] = "one-line remark",
        ["Pages/Account/Shared/TextSnippetDialog.razor#_text"] = "a snippet is short and written once",
        ["Pages/Admin/Shared/CustomFieldDefinitionDialog.razor#_input.Options"] = "configuration, not prose",
        ["Pages/Admin/Shared/DocTemplateDialog.razor#_input.DefaultReceivedInformation"] = "template default, not prose",
        ["Pages/Admin/Shared/LlmQuotaRulesPanel.razor#_addendum"] = "configuration, not prose",
        ["Pages/Admin/Shared/ModuleDialog.razor#_description"] = "one-line description",
        ["Pages/Admin/Shared/ObjectionListPanel.razor#_note"] = "decision reason",
        ["Pages/Factions/Shared/BulkMemberDialog.razor#_pasteText"] = "pasted list, pasted again in a second",
        ["Pages/Meetings/Shared/MeetingMeasureDialog.razor#_note"] = "short note on a measure, like a reminder note",
        ["Pages/Ki/Shared/NooseiChatPanel.razor#_input"] = "the answer is what counts, not the question",
        ["Pages/Personnel/Shared/TerminationDialog.razor#_reason"] = "decision reason",
        ["Pages/Recruiting/Shared/BewerbungMessagePanel.razor#_editText"] = "correction of a sent message that stays in the thread",
        ["Pages/Tickets/Shared/InternalTicketOpenDialog.razor#_text"] = "also opened from the citizen portal, which keeps nothing in the browser",
        ["Pages/Tickets/Shared/TicketCloseDialog.razor#_note"] = "decision note",
        ["Pages/Tickets/Shared/TicketMessagePanel.razor#_editText"] = "correction of a sent message that stays in the thread",
        ["Pages/Tips/Shared/TipMessagePanel.razor#_editText"] = "correction of a sent message that stays in the thread",
    };

    private static string ComponentsRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "NOOSE-Website", "Components"));

    private static string WebRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "NOOSE-Website", "wwwroot"));

    private static List<(string Name, string Text)> RazorFiles()
    {
        var root = ComponentsRoot();
        Assert.True(Directory.Exists(root), $"Quellordner nicht gefunden: {root}");
        return Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories)
            .Select(f => (Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText(f)))
            .ToList();
    }

    /// <summary>Every opening tag of a component, read past a '>' inside quotes or a lambda's parentheses.</summary>
    private static IEnumerable<(int Start, string Tag)> Tags(string text, string component)
    {
        foreach (Match m in Regex.Matches(text, "<" + component + @"\b"))
        {
            var i = m.Index + m.Length;
            var depth = 0;
            char? quote = null;
            for (; i < text.Length; i++)
            {
                var c = text[i];
                if (quote is { } q)
                {
                    if (c == q)
                    {
                        quote = null;
                    }
                }
                else if (c is '"' or '\'')
                {
                    quote = c;
                }
                else if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    depth--;
                }
                else if (c == '>' && depth == 0)
                {
                    break;
                }
            }
            yield return (m.Index, text[m.Index..Math.Min(i + 1, text.Length)]);
        }
    }

    private static int LinesOf(string tag, int fallback)
        => Regex.Match(tag, "Lines=\"(\\d+)\"") is { Success: true } m ? int.Parse(m.Groups[1].Value) : fallback;

    private static string BindingOf(string tag)
    {
        foreach (var pattern in new[] { "@bind-Text=\"([^\"]+)\"", "@bind-Value=\"([^\"]+)\"", @"\bText=""@\(([^?)]+?)\s*\?\?" })
        {
            if (Regex.Match(tag, pattern) is { Success: true } m)
            {
                return m.Groups[1].Value.Trim();
            }
        }
        return "?";
    }

    // the citizen portal and the public pages keep nothing in the browser: other people, and the tip form carries
    // an anonymity promise a stored draft on a shared computer would break
    private static bool IsOutside(string name)
        => name.StartsWith("Pages/Portal/", StringComparison.Ordinal) || name.StartsWith("Pages/Public/", StringComparison.Ordinal);

    [Fact]
    public void Every_rich_text_editor_keeps_a_draft_or_is_the_compact_chat()
    {
        var offenders = RazorFiles()
            .Where(f => !f.Name.EndsWith("/RichTextEditor.razor", StringComparison.Ordinal))
            .SelectMany(f => Tags(f.Text, "RichTextEditor")
                .Where(t => !t.Tag.Contains("DraftKey=", StringComparison.Ordinal)
                    && !t.Tag.Contains("Compact=\"true\"", StringComparison.Ordinal))
                .Select(t => $"{f.Name}:{f.Text[..t.Start].Count(c => c == '\n') + 1}"))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_long_plain_field_keeps_a_draft_or_says_why_not()
    {
        var offenders = new List<string>();
        foreach (var (name, text) in RazorFiles().Where(f => !IsOutside(f.Name)))
        {
            if (name.EndsWith("/MentionInput.razor", StringComparison.Ordinal))
            {
                continue;
            }
            foreach (var (start, tag) in Tags(text, "MentionInput"))
            {
                if (LinesOf(tag, 2) >= 3 && !tag.Contains("DraftKey=", StringComparison.Ordinal)
                    && !Exempt.ContainsKey($"{name}#{BindingOf(tag)}"))
                {
                    offenders.Add($"{name}#{BindingOf(tag)}");
                }
            }
            foreach (var (start, tag) in Tags(text, "MudTextField"))
            {
                if (LinesOf(tag, 1) >= 3 && !InsideTextDraft(text, start) && !Exempt.ContainsKey($"{name}#{BindingOf(tag)}"))
                {
                    offenders.Add($"{name}#{BindingOf(tag)}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    /// <summary>A plain field keeps its draft through a TextDraft around it: one opened before it and not closed yet.</summary>
    private static bool InsideTextDraft(string text, int start)
    {
        var open = text.LastIndexOf("<TextDraft", start, StringComparison.Ordinal);
        return open >= 0 && text.IndexOf("</TextDraft>", open, start - open, StringComparison.Ordinal) < 0;
    }

    [Fact]
    public void The_citizen_portal_and_the_public_pages_keep_nothing_in_the_browser()
    {
        // other people on possibly shared computers, and a tip carries an anonymity promise a stored text breaks
        var offenders = RazorFiles()
            .Where(f => IsOutside(f.Name))
            .Where(f => f.Text.Contains("DraftKey", StringComparison.Ordinal)
                || f.Text.Contains("<TextDraft", StringComparison.Ordinal))
            .Select(f => f.Name)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_field_tag_runs_past_its_end()
    {
        // An ASCII quote inside German quotation marks ends a Razor attribute early: the helper text is cut on
        // screen, and every check above reads the rest of the page as part of this one tag.
        var offenders = RazorFiles()
            .SelectMany(f => new[] { "MentionInput", "MudTextField", "RichTextEditor", "TextDraft" }
                .SelectMany(c => Tags(f.Text, c))
                .Where(t => t.Tag.Length > 1500)
                .Select(t => $"{f.Name}:{f.Text[..t.Start].Count(ch => ch == '\n') + 1}"))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_exemptions_still_name_a_field_that_exists()
    {
        // a renamed binding would otherwise leave a dead entry behind, and the next field of that name slips through
        var fields = RazorFiles()
            .SelectMany(f => Tags(f.Text, "MentionInput").Concat(Tags(f.Text, "MudTextField"))
                .Select(t => $"{f.Name}#{BindingOf(t.Tag)}"))
            .ToHashSet();

        Assert.All(Exempt.Keys, key => Assert.Contains(key, fields));
    }

    [Fact]
    public void Every_field_draft_is_dropped_after_its_save()
    {
        var offenders = new List<string>();
        foreach (var (name, text) in RazorFiles())
        {
            if (name.EndsWith("/TextDraft.razor", StringComparison.Ordinal)
                || name.EndsWith("/MentionInput.razor", StringComparison.Ordinal))
            {
                continue;
            }
            var keys = Tags(text, "MentionInput").Concat(Tags(text, "TextDraft"))
                .Where(t => t.Tag.Contains("DraftKey=", StringComparison.Ordinal))
                .ToList();
            // a key built from a form's scope is dropped by whoever owns the scope, checked below
            var own = keys.Count(t => !t.Tag.Contains("DraftKeys.In(DraftScope", StringComparison.Ordinal)
                && !t.Tag.Contains("DraftKeys.In(DraftScopeFor(", StringComparison.Ordinal));
            // one drop per keyed field at least: two composers and one drop means one of them never lets go
            var drops = Regex.Matches(text, @"\.MarkSavedAsync\(\)|TextDraft\.Discard(?:Scope)?Async\(").Count;
            if (own > drops)
            {
                offenders.Add($"{name} ({own} fields, {drops} drops)");
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_page_that_owns_a_scope_drops_it_on_each_save_path()
    {
        // one drop per @page: a create route and an edit route are two save paths
        var offenders = RazorFiles()
            .Where(f => f.Text.Contains("private string DraftScope =>", StringComparison.Ordinal))
            .Where(f =>
            {
                var pages = Math.Max(1, Regex.Matches(f.Text, "^@page ", RegexOptions.Multiline).Count);
                var drops = Regex.Matches(f.Text, @"TextDraft\.DiscardScopeAsync\(JS, [^;]*DraftScope\);").Count;
                return drops < pages;
            })
            .Select(f => f.Name)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_dialog_scope_has_a_caller_that_drops_it()
    {
        // A dialog closes before its caller saves, so the caller drops the scope; a scope nobody drops lingers.
        // Caller means: another file that names the scope and drops a scope - how it passes it along is its business.
        var files = RazorFiles();
        var scopes = files
            .SelectMany(f => Regex.Matches(f.Text, @"public (?:static string|const string) (\w*DraftScope\w*)\b")
                .Select(m => (File: f.Name, Owner: Path.GetFileNameWithoutExtension(f.Name), Member: m.Groups[1].Value)))
            .ToList();

        Assert.NotEmpty(scopes);
        var offenders = scopes
            .Where(s => !files.Any(f => f.Name != s.File
                && Regex.IsMatch(f.Text, @"\b" + s.Owner + @"\." + s.Member + @"\b")
                && f.Text.Contains("TextDraft.DiscardScopeAsync(", StringComparison.Ordinal)))
            .Select(s => $"{s.Owner}.{s.Member}")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void Each_draft_module_is_loaded_under_one_version()
    {
        // Two different ?v= load two copies of a module, and two copies of entwurf.js keep two field registries.
        // An import without any ?v= is a third address, so it counts as a version of its own.
        var sources = RazorFiles().Select(f => f.Text)
            .Concat(Directory.EnumerateFiles(Path.Combine(WebRoot(), "js"), "*.js").Select(File.ReadAllText))
            .ToList();

        foreach (var module in new[] { "entwurf.js", "richtext.js" })
        {
            var versions = sources
                .SelectMany(s => Regex.Matches(s, Regex.Escape(module) + @"(?:\?v=(\d+))?[""']")
                    .Select(m => m.Groups[1].Success ? m.Groups[1].Value : "(ohne)"))
                .Distinct()
                .ToList();
            Assert.True(versions.Count == 1, $"{module}: {string.Join(", ", versions)}");
        }
    }
}
