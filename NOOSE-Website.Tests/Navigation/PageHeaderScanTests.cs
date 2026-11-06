using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace NOOSE_Website.Tests.Navigation;

/// <summary>Page headers keep their secondary actions apart, and the ⋮ menu below 960px loses neither an action nor a check.</summary>
/// <remarks>
/// <c>PageHeader</c> shows <c>Actions</c> at every width, <c>MoreActions</c> only from 960px up and
/// <c>MoreActionsMenu</c> only below. A secondary button left in <c>Actions</c> pushes a phone page wider than the
/// screen; an entry in the menu without the <c>AuthorizeView</c> its button sits in shows a partner or a reading agent
/// an action they do not have today.
/// </remarks>
public sealed class PageHeaderScanTests
{
    /// <summary>Buttons that are never the main action of a page and belong in MoreActions.</summary>
    private static readonly string[] SecondaryButtons =
    [
        "SavedViewButton", "ListExportButton", "FollowButton", "PartnerShareButton", "ChannelShareButton",
        "DossierExportButton", "JobCreateButton",
    ];

    /// <summary>Header buttons with an <c>AsMenuItem</c> form; in the menu each needs that form.</summary>
    private static readonly string[] MenuCapableButtons =
    [
        .. SecondaryButtons, "HandbookHelpButton", "FirearmUseButton",
    ];

    /// <summary>Headers still to be split, by Task 6 of the phone plan; the list may only shrink.</summary>
    private static readonly HashSet<string> NotYetSplit = new(StringComparer.Ordinal)
    {
        "Components/Pages/Cases/CasesList.razor",
        "Components/Pages/Factions/FactionsList.razor",
        "Components/Pages/Jobs/JobsList.razor",
        "Components/Pages/Notifications/NotificationInbox.razor",
        "Components/Pages/Operations/OperationsList.razor",
    };

    /// <summary>Files that may set a title with Typo.h4 themselves, each with the reason.</summary>
    private static readonly Dictionary<string, string> OwnTitles = new(StringComparer.Ordinal)
    {
        ["Components/Common/Shared/PageHeader.razor"] = "der Seitenkopf selbst",
        ["Components/Common/Shared/StatTile.razor"] = "Kennzahl einer Kachel, keine Überschrift",
        ["Components/Pages/Public/"] = "statisch gerenderte öffentliche Seiten mit eigenem Hero (Bauplan Task 17)",
        ["Components/Pages/Portal/"] = "Bürger- und Bewerberportal mit eigenem Layout (Bauplan Task 17)",
        ["Components/Pages/Legal/"] = "Rechtstexte im LegalLayout, Überschrift des Dokuments",
    };

    private static readonly Regex Policy = new(@"<AuthorizeView\b[^>]*?\b(Policy|Roles)=""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex OwnH4 = new(@"Typo=""(@?Typo\.)?h4""", RegexOptions.Compiled);

    private static string SourceRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "NOOSE-Website"));

    private static IEnumerable<(string Path, string Text)> RazorFiles()
    {
        var root = SourceRoot();
        Assert.True(Directory.Exists(root), $"Quellordner nicht gefunden: {root}");
        var sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}bin{sep}", StringComparison.Ordinal)
                        && !f.Contains($"{sep}obj{sep}", StringComparison.Ordinal))
            .Select(f => (Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText(f)))
            .OrderBy(f => f.Item1, StringComparer.Ordinal);
    }

    /// <summary>The child content of each <c>&lt;PageHeader&gt;…&lt;/PageHeader&gt;</c>; a self-closing header has none.</summary>
    private static IEnumerable<string> Headers(string razor)
    {
        var at = 0;
        while ((at = razor.IndexOf("<PageHeader", at, StringComparison.Ordinal)) >= 0)
        {
            var next = razor.IndexOf("<PageHeader", at + 1, StringComparison.Ordinal);
            var close = razor.IndexOf("</PageHeader>", at, StringComparison.Ordinal);
            if (close >= 0 && (next < 0 || close < next))
            {
                yield return razor[at..close];
            }
            at = next < 0 ? razor.Length : next;
        }
    }

    /// <summary>The content of a header's parameter block, e.g. <c>&lt;Actions&gt;…&lt;/Actions&gt;</c>; null when absent.</summary>
    private static string? Section(string header, string name)
    {
        var open = $"<{name}>";
        var start = header.IndexOf(open, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }
        var end = header.IndexOf($"</{name}>", start, StringComparison.Ordinal);
        return end < 0 ? header[(start + open.Length)..] : header[(start + open.Length)..end];
    }

    private static int Count(string text, string pattern) => Regex.Matches(text, pattern).Count;

    private static string Policies(string text)
        => string.Join(", ", Policy.Matches(text).Select(m => m.Groups[2].Value).Order(StringComparer.Ordinal));

    [Fact]
    public void Header_actions_are_split()
    {
        var offenders = new List<string>();
        var stillListed = new List<string>();
        foreach (var (path, text) in RazorFiles())
        {
            var hits = Headers(text)
                .Select(h => Section(h, "Actions"))
                .OfType<string>()
                .SelectMany(actions => SecondaryButtons.Where(b => Regex.IsMatch(actions, $@"<{b}\b"))
                    .Concat(actions.Contains("papierkorb", StringComparison.OrdinalIgnoreCase) ? ["Papierkorb"] : []))
                .Distinct()
                .ToList();

            if (hits.Count > 0 && !NotYetSplit.Contains(path))
            {
                offenders.Add($"{path}: {string.Join(", ", hits)} gehört in MoreActions");
            }
            else if (hits.Count == 0 && NotYetSplit.Contains(path))
            {
                stillListed.Add($"{path} ist umgestellt – aus {nameof(NotYetSplit)} streichen");
            }
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
        Assert.True(stillListed.Count == 0, string.Join(Environment.NewLine, stillListed));
    }

    [Fact]
    public void Menu_items_keep_their_AuthorizeView()
    {
        var offenders = new List<string>();
        foreach (var (path, text) in RazorFiles())
        {
            foreach (var header in Headers(text))
            {
                var buttons = Section(header, "MoreActions");
                var menu = Section(header, "MoreActionsMenu");
                if (buttons is null && menu is null)
                {
                    continue;
                }
                if (buttons is null || menu is null)
                {
                    offenders.Add($"{path}: MoreActions und MoreActionsMenu gibt es nur zusammen");
                    continue;
                }

                var inButtons = Policies(buttons);
                var inMenu = Policies(menu);
                if (inButtons != inMenu)
                {
                    offenders.Add($"{path}: Knöpfe hinter [{inButtons}], Menüeinträge hinter [{inMenu}]");
                }
            }
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void Every_secondary_action_is_in_the_menu()
    {
        var offenders = new List<string>();
        foreach (var (path, text) in RazorFiles())
        {
            foreach (var header in Headers(text))
            {
                var buttons = Section(header, "MoreActions");
                var menu = Section(header, "MoreActionsMenu");
                if (buttons is null || menu is null)
                {
                    continue; // a missing half is Menu_items_keep_their_AuthorizeView's finding
                }

                foreach (var component in MenuCapableButtons)
                {
                    var wide = Count(buttons, $@"<{component}\b");
                    var narrow = Count(menu, $@"<{component}\b[^>]*\bAsMenuItem=""true""");
                    if (wide != narrow)
                    {
                        offenders.Add($"{path}: {component} {wide}× als Knopf, {narrow}× mit AsMenuItem im Menü");
                    }
                }

                // plain buttons (and the entries of a menu among them) each come back as one menu entry
                var plain = Count(buttons, @"<(MudButton|MudIconButton|MudMenuItem)\b");
                var entries = Count(menu, @"<MudMenuItem\b");
                if (plain != entries)
                {
                    offenders.Add($"{path}: {plain} Knöpfe in MoreActions, {entries} MudMenuItem in MoreActionsMenu");
                }
            }
        }

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }

    [Fact(Skip = "wird in Phase Parallel scharf")]
    public void No_hand_built_page_titles()
    {
        var offenders = RazorFiles()
            .Where(f => !OwnTitles.Keys.Any(k => k.EndsWith('/') ? f.Path.StartsWith(k, StringComparison.Ordinal) : f.Path == k))
            .Where(f => OwnH4.IsMatch(f.Text))
            .Select(f => $"{f.Path}: Titel mit Typo.h4 von Hand gebaut – PageHeader nutzen oder mit Grund in {nameof(OwnTitles)} eintragen")
            .ToList();

        Assert.True(offenders.Count == 0, string.Join(Environment.NewLine, offenders));
    }
}
