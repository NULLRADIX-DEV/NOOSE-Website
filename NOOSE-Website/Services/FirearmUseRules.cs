using System.Security.Claims;
using NOOSE_Website.Authorization;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Who may report, read and delete a firearm-use entry, and who is told. Sole source for the guards and the pages that mirror them.</summary>
public static class FirearmUseRules
{
    /// <summary>Longest reason the column holds.</summary>
    public const int MaxReasonLength = 1000;

    /// <summary>Lowest rank the bell reaches.</summary>
    public const Rank NotifyFloor = Rank.SpecialAgentInCharge;

    /// <summary>The report is trimmed; an empty or oversized one is refused with a message for the dialog.</summary>
    public static string CleanReason(string? reason)
    {
        var text = reason?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            throw new InvalidOperationException("Bitte eine kurze Begründung angeben.");
        }
        if (text.Length > MaxReasonLength)
        {
            throw new InvalidOperationException($"Die Begründung darf höchstens {MaxReasonLength} Zeichen lang sein.");
        }
        return text;
    }

    /// <summary>One's own file always, anybody else's only with the leadership read; an in-house account in both cases.</summary>
    public static bool MayRead(ClaimsPrincipal viewer, string agentId)
        => viewer.IsInternalAgent()
           && (string.Equals(viewer.GetAgentId(), agentId, StringComparison.Ordinal) || viewer.MayClassifiedRead());

    /// <summary>Deleting needs write access and leadership, and never reaches one's own report.</summary>
    public static bool MayDelete(ClaimsPrincipal actor, string ownerId)
        => actor.IsInternalAgent() && actor.MayWrite() && actor.IsLeadership()
           && !string.Equals(actor.GetAgentId(), ownerId, StringComparison.Ordinal);

    /// <summary>Whether a recipient's unit hears about a report from an agent of <paramref name="reporterHome"/>: the Board always, a division only for its own members.</summary>
    public static bool Notifies(OrgUnit recipient, Division? reporterHome)
        => recipient.IsBoard || (reporterHome is not null && recipient.Home == reporterHome);
}
