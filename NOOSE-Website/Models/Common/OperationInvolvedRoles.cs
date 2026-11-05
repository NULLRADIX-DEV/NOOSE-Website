namespace NOOSE_Website.Models.Common;

/// <summary>Suggested roles for a person involved in an operation, and how the report groups them; free text stays possible.</summary>
public static class OperationInvolvedRoles
{
    public static readonly string[] Suggestions =
    {
        "Beschuldigter", "Tatverdächtiger", "Mittäter", "Geisel", "Opfer", "Zeuge", "Informant", "Kontaktperson",
    };

    private static readonly string[] AccusedStems = { "beschuldig", "tatverdächt", "tatverdaecht", "täter", "taeter" };
    private static readonly string[] VictimStems = { "geisel", "opfer", "geschädig", "geschaedig" };

    /// <summary>Report group of a role label.</summary>
    public static OperationRoleGroup Group(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return OperationRoleGroup.Other;
        }
        var l = label.Trim().ToLowerInvariant();
        if (AccusedStems.Any(l.Contains))
        {
            return OperationRoleGroup.Accused;
        }
        if (VictimStems.Any(l.Contains))
        {
            return OperationRoleGroup.Victim;
        }
        return OperationRoleGroup.Other;
    }

    /// <summary>Role suggestions for a person linked from the given record type; null where the label is a free note.</summary>
    public static IReadOnlyList<string>? For(string? sourceType) => sourceType switch
    {
        "Case" => CasePersonRoles.Suggestions,
        "Operation" => Suggestions,
        _ => null,
    };
}

/// <summary>How the operation report groups involved persons.</summary>
public enum OperationRoleGroup
{
    Accused,
    Victim,
    Other,
}
