namespace NOOSE_Website.Models.Enums;

/// <summary>Autocomplete suggestion category.</summary>
public enum SuggestionType
{
    Weapon = 0,
    Vehicle = 1,
    Location = 2,
    Inventory = 3,
    Kind = 4,
    PartyRole = 5,
    OperationType = 6,
    CaseType = 7,
    /// <summary>Retired: routes are own records now; kept so stored rows keep their number.</summary>
    DrugRoute = 8,
    FinancingCategory = 9,
    EvidenceCategory = 10,
}
