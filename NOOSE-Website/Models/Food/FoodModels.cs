using NOOSE_Website.Data.Entities.Food;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Food;

/// <summary>An agent's weekly food and drink allowance, as shown on the limit tiles.</summary>
public record FoodLimitStatus(
    string AgentId,
    string Codename,
    Rank? Rank,
    DateTime WeekStart,
    int FoodLimit,
    int DrinkLimit,
    int FoodConsumed,
    int DrinkConsumed,
    bool FoodIsOverride,
    bool DrinkIsOverride)
{
    /// <summary>What is left; negative once leadership deliberately overran the limit.</summary>
    public int FoodRemaining => FoodLimit - FoodConsumed;
    public int DrinkRemaining => DrinkLimit - DrinkConsumed;

    public int Limit(FoodKind kind) => kind == FoodKind.Drink ? DrinkLimit : FoodLimit;
    public int Consumed(FoodKind kind) => kind == FoodKind.Drink ? DrinkConsumed : FoodConsumed;
    public int Remaining(FoodKind kind) => kind == FoodKind.Drink ? DrinkRemaining : FoodRemaining;

    public static FoodLimitStatus Empty { get; } =
        new(string.Empty, string.Empty, null, DateTime.MinValue, 0, 0, 0, 0, false, false);
}

/// <summary>A catalog position plus the evidence-room item it books out of and that item's stock.</summary>
public record FoodItemDisplay(FoodItem Item, string Name, bool HasImage, int OnHand);

/// <summary>A food request plus the requester's codename, so lists need no extra lookup.</summary>
public record FoodRequestDisplay(FoodRequest Request, string AgentCodename);

/// <summary>Form model for creating/editing a catalog position.</summary>
public class FoodItemInput
{
    public string EvidenceItemId { get; set; } = string.Empty;
    public FoodKind Kind { get; set; } = FoodKind.Food;
    public string? Description { get; set; }
    public int MaxQuantity { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    public int Sorting { get; set; }
}

/// <summary>Form model for filing a food request.</summary>
public class FoodRequestInput
{
    public string? Justification { get; set; }
    public List<FoodRequestLineInput> Lines { get; set; } = new();
}

/// <summary>One basket line: a catalog position and how many of it.</summary>
public class FoodRequestLineInput
{
    public string ItemId { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
}

/// <summary>Decision payload: per-line cuts plus the notes the decision needs.</summary>
public class FoodDecisionInput
{
    /// <summary>Approved quantity per line id; a line left out keeps its requested quantity, 0 strikes it.</summary>
    public Dictionary<string, int> ApprovedQuantities { get; set; } = new();

    public string? Note { get; set; }

    /// <summary>Mandatory as soon as the approval exceeds the agent's remaining weekly food or drink limit.</summary>
    public string? OverrunReason { get; set; }
}
