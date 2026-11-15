using NOOSE_Website.Navigation;

namespace NOOSE_Website.Services;

/// <summary>What a keyboard shortcut means, decided here rather than in the browser.</summary>
/// <remarks>
/// The JavaScript side recognises the key sequence and nothing else. Which letter leads where, and which page
/// offers "new" or "edit" at all, is a table - and a table in a .razor code block would have no test, because
/// there is no bUnit in this repo. Every method here works on the address alone, so all of it is testable.
/// </remarks>
public static class ShortcutRoutes
{
    /// <summary>Second letter of the "g" chord, mapped to a <see cref="NavCatalog"/> key.</summary>
    /// <remarks>
    /// Twelve, and no more: a shortcut nobody can recall is a shortcut nobody uses. The letters are the German
    /// initials of what they open, which is why "f" is Fraktionen and not the "folgen" the backlog suggested.
    /// </remarks>
    public static readonly IReadOnlyDictionary<char, string> Areas = new Dictionary<char, string>
    {
        ['d'] = "dashboard",
        ['p'] = "personen",
        ['f'] = "fraktionen",
        ['v'] = "vorgaenge",
        ['o'] = "operationen",
        ['a'] = "aufgaben",
        ['k'] = "kalender",
        ['t'] = "taskforces",
        ['s'] = "suche",
        ['b'] = "brett",
        ['h'] = "handbuch",
        ['w'] = "watchlist",
    };

    /// <summary>List pages that can create, and what they create. Mirrors the palette's own table.</summary>
    private static readonly IReadOnlyDictionary<string, string> Creatable = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["personen"] = "Personen-Akte",
        ["fraktionen"] = "Fraktion",
        ["personengruppen"] = "Personengruppe",
        ["parteien"] = "Partei",
        ["drogenrouten"] = "Drogenroute",
        ["operationen"] = "Operation",
        ["taskforces"] = "Taskforce",
        ["vorgaenge"] = "Vorgang",
        ["aufgaben"] = "Aufgabe",
        ["dokumente"] = "Dokument",
    };

    /// <summary>Record types whose editor is gated on write access and nothing narrower.</summary>
    /// <remarks>
    /// Fifteen types have a <c>/{typ}/{id}/bearbeiten</c> route; six of them are deliberately missing here,
    /// because their editor asks more than <c>MayWrite()</c> and the key cannot know the answer: a meeting wants
    /// <c>HighestClassificationPage</c>, a task, an appointment and an activity want creator-or-leadership, a
    /// board notice its own MayManage, a document its authorship and secrecy level. Sending somebody there on a
    /// keystroke would trade the page they were reading for a refusal - worse than the key doing nothing.
    /// </remarks>
    private static readonly IReadOnlySet<string> Editable = new HashSet<string>(StringComparer.Ordinal)
    {
        "personen", "fraktionen", "personengruppen", "parteien", "drogenrouten", "vorgaenge", "operationen", "taskforces",
        "entfuehrungen", "informanten",
    };

    /// <summary>Create route for the list the viewer is on, or null where creating makes no sense.</summary>
    /// <remarks>Only the bare list counts. On a detail page "new" would open a form the reader did not ask
    /// for, and the segment after the type is an id, not a command.</remarks>
    public static string? NewRouteFor(string? path)
    {
        var parts = Segments(path);
        return parts.Length == 1 && Creatable.ContainsKey(parts[0]) ? $"/{parts[0]}/neu" : null;
    }

    /// <summary>German label of what "new" would create here, for the overview and the snackbar.</summary>
    public static string? NewLabelFor(string? path)
    {
        var parts = Segments(path);
        return parts.Length == 1 ? Creatable.GetValueOrDefault(parts[0]) : null;
    }

    /// <summary>Editor route for the record the viewer is on, or null.</summary>
    /// <remarks>
    /// Exactly two segments: a type and an id. Anything longer is already a sub-page - and on
    /// <c>/personen/{id}/bearbeiten</c> the answer must be null rather than a second "bearbeiten".
    /// The literal "neu" is not an id, so a create form does not answer either.
    /// </remarks>
    public static string? EditRouteFor(string? path)
    {
        var parts = Segments(path);
        if (parts.Length != 2 || !Editable.Contains(parts[0]) || string.Equals(parts[1], "neu", StringComparison.Ordinal))
        {
            return null;
        }
        return $"/{parts[0]}/{parts[1]}/bearbeiten";
    }

    /// <summary>Nav key the "g" chord points at, or null for an unbound letter.</summary>
    public static string? AreaKeyFor(char letter)
        => Areas.GetValueOrDefault(char.ToLowerInvariant(letter));

    /// <summary>Path split into segments, with the query string and any trailing slash dropped.</summary>
    private static string[] Segments(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }
        var clean = path.Split('?', 2)[0].Split('#', 2)[0].Trim('/');
        return clean.Length == 0 ? [] : clean.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }
}
