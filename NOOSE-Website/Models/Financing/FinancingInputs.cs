using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Financing;

/// <summary>Form model for creating/editing a catalog position.</summary>
public class FinancingItemInput
{
    public string Name { get; set; } = string.Empty;
    public string? Category { get; set; }
    public string? Description { get; set; }
    public decimal UnitPrice { get; set; }

    /// <summary>Share NOOSE covers (1-100).</summary>
    public int SubsidyPercent { get; set; } = 100;

    /// <summary>CID threshold, also the fallback for a division left empty.</summary>
    public Rank MinimumRank { get; set; } = Rank.Trainee;
    public Rank? MinimumRankCtd { get; set; }
    public Rank? MinimumRankHrt { get; set; }
    public int MaxQuantity { get; set; } = 1;
    public bool IsActive { get; set; } = true;
    public int Sorting { get; set; }
}

/// <summary>Form model for filing a funding request.</summary>
public class FinancingRequestInput
{
    public string Justification { get; set; } = string.Empty;
    public List<FinancingRequestLineInput> Lines { get; set; } = new();
}

/// <summary>One basket line: a catalog position and how many of it.</summary>
public class FinancingRequestLineInput
{
    public string ItemId { get; set; } = string.Empty;
    public int Quantity { get; set; } = 1;
}

/// <summary>Decision payload: per-line cuts plus the notes the decision needs.</summary>
public class FinancingDecisionInput
{
    /// <summary>Approved quantity per line id; a line left out keeps its requested quantity, 0 strikes it.</summary>
    public Dictionary<string, int> ApprovedQuantities { get; set; } = new();

    public string? Note { get; set; }

    /// <summary>Mandatory as soon as the approval exceeds the agent's remaining budget.</summary>
    public string? OverrunReason { get; set; }
}
