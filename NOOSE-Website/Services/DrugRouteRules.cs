namespace NOOSE_Website.Services;

/// <summary>What makes two drug routes the same route; one active record per route.</summary>
public static class DrugRouteRules
{
    /// <summary>Comparison key: case, outer and doubled whitespace do not make a different route.</summary>
    public static string Key(string? designation)
        => string.Join(' ', (designation ?? string.Empty)
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();
}
