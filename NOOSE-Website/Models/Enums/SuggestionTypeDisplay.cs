namespace NOOSE_Website.Models.Enums;

/// <summary>Display labels for the autocomplete suggestion categories.</summary>
public static class SuggestionTypeDisplay
{
    public static string Name(SuggestionType type) => type switch
    {
        SuggestionType.Weapon => "Waffen",
        SuggestionType.Vehicle => "Fahrzeuge",
        SuggestionType.Location => "Orte",
        SuggestionType.Inventory => "Inventar (Fraktionen)",
        SuggestionType.Kind => "Fraktions-Arten",
        SuggestionType.PartyRole => "Rollen (Parteien)",
        SuggestionType.OperationType => "Einsatzarten",
        SuggestionType.CaseType => "Vorgangsarten",
        SuggestionType.DrugRoute => "Drogenrouten",
        SuggestionType.FinancingCategory => "Finanzierungs-Kategorien",
        SuggestionType.EvidenceCategory => "Asservat-Kategorien",
        _ => "—",
    };

    /// <summary>Types no panel offers any more; the number stays taken.</summary>
    public static readonly IReadOnlySet<SuggestionType> Retired = new HashSet<SuggestionType> { SuggestionType.DrugRoute };

    /// <summary>All live types in panel display order.</summary>
    public static readonly IReadOnlyList<SuggestionType> All = new[]
    {
        SuggestionType.Weapon,
        SuggestionType.Vehicle,
        SuggestionType.Location,
        SuggestionType.Inventory,
        SuggestionType.Kind,
        SuggestionType.PartyRole,
        SuggestionType.OperationType,
        SuggestionType.CaseType,
        SuggestionType.FinancingCategory,
        SuggestionType.EvidenceCategory,
    };
}
