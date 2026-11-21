using NOOSE_Website.Models.Enums;
using NOOSE_Website.Navigation;

namespace NOOSE_Website.Services.Usage;

/// <summary>One countable feature: the key stored in the counters, what the leadership reads, and its group.</summary>
public sealed record UsageFeature(string Key, string Label, string Group);

/// <summary>Every countable feature; the tracker drops any key not listed here, so no id or text can slip in.</summary>
public static class UsageCatalog
{
    public const string GroupHotkeys = "Tastenkürzel";
    public const string GroupPalette = "Schnellzugriff (Strg+K)";
    public const string GroupSearch = "Suche";
    public const string GroupFibi = "FIBI";
    public const string GroupOutput = "Druck und Export";

    public const string HotkeyHelp = "hotkey:help";
    public const string HotkeyNew = "hotkey:new";
    public const string HotkeyEdit = "hotkey:edit";
    public const string PaletteOpen = "palette:open";
    public const string SearchRun = "search:run";
    public const string SearchSaved = "search:saved";
    public const string PrintView = "print:view";
    public const string ExportList = "export:list";
    public const string ExportDossier = "export:dossier";

    /// <summary>What a palette row was, as chosen by the agent; the kind and never the target.</summary>
    public static readonly IReadOnlyList<(string Kind, string Label)> PaletteKinds =
    [
        ("command", "Befehl"),
        ("favorite", "Favorit"),
        ("view", "Gespeicherte Ansicht"),
        ("recent", "Zuletzt besucht"),
        ("record", "Akte aus der Schnellsuche"),
        ("search", "Alles durchsuchen"),
        ("fibi", "FIBI fragen"),
    ];

    public static string Page(string navKey) => "page:" + navKey;

    public static string HotkeyGoto(string navKey) => "hotkey:goto:" + navKey;

    public static string PalettePick(string kind) => "palette:pick:" + kind;

    public static string Fibi(LlmFeature feature) => "fibi:" + feature.ToString().ToLowerInvariant();

    /// <summary>Every countable feature, pages first, then in the order the groups read.</summary>
    public static IReadOnlyList<UsageFeature> All { get; } = Build();

    private static readonly Dictionary<string, UsageFeature> ByKey =
        All.ToDictionary(f => f.Key, StringComparer.Ordinal);

    /// <summary>The feature behind a key, or null when the catalog does not know it.</summary>
    public static UsageFeature? Find(string? key)
        => key is not null && ByKey.TryGetValue(key, out var feature) ? feature : null;

    /// <summary>Group names in reading order.</summary>
    public static IReadOnlyList<string> Groups { get; } = All.Select(f => f.Group).Distinct(StringComparer.Ordinal).ToList();

    private static List<UsageFeature> Build()
    {
        var list = new List<UsageFeature>();

        // route twins never resolve, so skip
        NavArea[] order = [NavArea.Primary, .. NavAreaCatalog.Areas, .. NavAreaCatalog.MenuAreas];
        foreach (var area in order)
        {
            var group = "Seiten · " + NavAreaCatalog.Name(area);
            foreach (var entry in NavAreaCatalog.Entries(area).Where(e => NavCatalog.ByRoute(e.Route)?.Key == e.Key))
            {
                list.Add(new UsageFeature(Page(entry.Key), entry.Label, group));
            }
        }

        list.Add(new UsageFeature(HotkeyHelp, "? zeigt die Übersicht der Kürzel", GroupHotkeys));
        list.Add(new UsageFeature(HotkeyNew, "n legt auf einer Liste einen Eintrag an", GroupHotkeys));
        list.Add(new UsageFeature(HotkeyEdit, "e bearbeitet die geöffnete Akte", GroupHotkeys));
        foreach (var (letter, key) in ShortcutRoutes.Areas)
        {
            if (NavCatalog.ByKey(key) is { } entry)
            {
                list.Add(new UsageFeature(HotkeyGoto(key), $"g dann {letter} öffnet {entry.Label}", GroupHotkeys));
            }
        }

        list.Add(new UsageFeature(PaletteOpen, "Schnellzugriff geöffnet", GroupPalette));
        foreach (var (kind, label) in PaletteKinds)
        {
            list.Add(new UsageFeature(PalettePick(kind), "Gewählt: " + label, GroupPalette));
        }

        list.Add(new UsageFeature(SearchRun, "Globale Suche ausgeführt", GroupSearch));
        list.Add(new UsageFeature(SearchSaved, "Gespeicherte Suche geöffnet", GroupSearch));

        foreach (var feature in Enum.GetValues<LlmFeature>())
        {
            list.Add(new UsageFeature(Fibi(feature), "FIBI: " + LlmFeatureDisplay.Name(feature), GroupFibi));
        }

        list.Add(new UsageFeature(PrintView, "Druckansicht geöffnet", GroupOutput));
        list.Add(new UsageFeature(ExportList, "Liste als CSV exportiert", GroupOutput));
        list.Add(new UsageFeature(ExportDossier, "Dossier exportiert", GroupOutput));
        return list;
    }
}
