using MudBlazor;

namespace NOOSE_Website.Services.Decorations;

/// <summary>The fixed set of symbols a decoration may carry; the stored key resolves here.</summary>
public static class DecorationSymbols
{
    /// <summary>One selectable symbol.</summary>
    public sealed record Symbol(string Key, string Label, string Icon);

    public const string Default = "medaille";

    public static readonly IReadOnlyList<Symbol> All = new[]
    {
        new Symbol("medaille", "Medaille", Icons.Material.Filled.MilitaryTech),
        new Symbol("orden", "Orden", Icons.Material.Filled.WorkspacePremium),
        new Symbol("stern", "Stern", Icons.Material.Filled.Star),
        new Symbol("schild", "Schild", Icons.Material.Filled.Shield),
        new Symbol("wappen", "Wappen", Icons.Material.Filled.LocalPolice),
        new Symbol("pokal", "Pokal", Icons.Material.Filled.EmojiEvents),
        new Symbol("flamme", "Flamme", Icons.Material.Filled.LocalFireDepartment),
        new Symbol("herz", "Herz", Icons.Material.Filled.Favorite),
        new Symbol("jubilaeum", "Jubiläum", Icons.Material.Filled.Celebration),
        new Symbol("siegel", "Siegel", Icons.Material.Filled.Verified),
    };

    private static readonly Dictionary<string, Symbol> ByKey = All.ToDictionary(s => s.Key, StringComparer.Ordinal);

    /// <summary>True for a key the catalog editor may store.</summary>
    public static bool IsKnown(string? key) => key is not null && ByKey.ContainsKey(key);

    /// <summary>The symbol for a stored key; an unknown key falls back to the medal.</summary>
    public static Symbol Find(string? key)
        => key is not null && ByKey.TryGetValue(key, out var symbol) ? symbol : ByKey[Default];
}
