using MudBlazor;

namespace NOOSE_Website.Models.Enums;

/// <summary>Whether a catalog position is eaten or drunk; each kind has its own weekly limit.</summary>
public enum FoodKind
{
    Food = 0,
    Drink = 1,
}

/// <summary>Display labels and icons per food kind.</summary>
public static class FoodKindDisplay
{
    public static string Name(FoodKind kind) => kind switch
    {
        FoodKind.Food => "Essen",
        FoodKind.Drink => "Trinken",
        _ => "—",
    };

    public static string Icon(FoodKind kind) => kind switch
    {
        FoodKind.Food => Icons.Material.Filled.Restaurant,
        FoodKind.Drink => Icons.Material.Filled.LocalDrink,
        _ => Icons.Material.Filled.Fastfood,
    };

    public static readonly IReadOnlyList<FoodKind> All = new[] { FoodKind.Food, FoodKind.Drink };
}
