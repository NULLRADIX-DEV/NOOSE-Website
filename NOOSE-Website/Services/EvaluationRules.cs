using System.Security.Claims;
using NOOSE_Website.Authorization;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Who may read and write service evaluations. Sole source for the guards and the pages that mirror them.</summary>
public static class EvaluationRules
{
    /// <summary>Longest title the column holds.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>Usual interval between two evaluations per the service regulation.</summary>
    public static readonly TimeSpan UsualInterval = TimeSpan.FromDays(28);

    /// <summary>One's own file always, anybody else's with leadership, supervision or the Administration Division.</summary>
    public static bool MayRead(ClaimsPrincipal viewer, string agentId)
        => viewer.IsInternalAgent()
           && (string.Equals(viewer.GetAgentId(), agentId, StringComparison.Ordinal)
               || viewer.IsHrbOrLeadership()
               || viewer.IsOnlyReader());

    /// <summary>Writing and deleting: leadership and the Administration Division with write access.</summary>
    public static bool MayWrite(ClaimsPrincipal actor) => actor.MayWrite() && actor.IsHrbOrLeadership();

    /// <summary>The preset title for an evaluation on the given day.</summary>
    public static string DefaultTitle(DateTime entryDate) => $"Beurteilung vom {entryDate:dd.MM.yyyy}";

    /// <summary>Trimmed title, the preset when empty; an oversized one is refused.</summary>
    public static string CleanTitle(string? title, DateTime entryDate)
    {
        var text = title?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return DefaultTitle(entryDate);
        }
        if (text.Length > MaxTitleLength)
        {
            throw new InvalidOperationException($"Der Titel darf höchstens {MaxTitleLength} Zeichen lang sein.");
        }
        return text;
    }

    /// <summary>Refuses a grade outside the five steps.</summary>
    public static void RequireValidGrade(EvaluationGrade grade)
    {
        if (!Enum.IsDefined(grade))
        {
            throw new InvalidOperationException("Bitte eine Note von 1 bis 5 wählen.");
        }
    }
}
