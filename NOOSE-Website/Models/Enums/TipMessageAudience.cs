namespace NOOSE_Website.Models.Enums;

/// <summary>Who a tip message is visible to.</summary>
public enum TipMessageAudience
{
    /// <summary>Internal handling notes; never visible to the citizen.</summary>
    Intern = 0,
    /// <summary>Conversation shared with the citizen; the agency answers as "FIB".</summary>
    Buerger = 1,
}
