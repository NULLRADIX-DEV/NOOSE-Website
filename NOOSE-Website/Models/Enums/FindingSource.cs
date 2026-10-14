namespace NOOSE_Website.Models.Enums;

/// <summary>Where a reported finding came from; the ledger keys on it.</summary>
public enum FindingSource
{
    /// <summary>A leadership-defined counter-intelligence rule.</summary>
    Rule = 0,

    /// <summary>The NOOSEI usage anomaly checks.</summary>
    Noosei = 1,
}
