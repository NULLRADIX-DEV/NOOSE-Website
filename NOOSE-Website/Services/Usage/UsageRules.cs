using System.Security.Claims;
using NOOSE_Website.Authorization;
using NOOSE_Website.Models.Navigation;

namespace NOOSE_Website.Services.Usage;

/// <summary>Who is counted, how long counters live and when a number is too small to show.</summary>
public static class UsageRules
{
    /// <summary>Days a counter row is kept.</summary>
    public const int RetentionDays = 365;

    /// <summary>Below this many different agents the statistics show "&lt; 3" instead of the number.</summary>
    public const int MinShownAgents = 3;

    /// <summary>Only an internal agent with a rank who may write: no demo, partner, read-only supervision, citizen or visitor.</summary>
    public static bool IsCounted(ClaimsPrincipal user)
        => user.Identity?.IsAuthenticated == true
           && user.IsInternalAgent()
           && user.MayWrite()
           && user.GetRank() is not null;

    /// <summary>The agent's own choice; counted unless they switched it off.</summary>
    public static bool IsOptedOut(NavPreferences prefs) => prefs.UsageStatsOptOut;

    /// <summary>Agent-days as the statistics show them, hiding small numbers; none at all is no secret.</summary>
    public static string AgentDays(int agentDays)
        => agentDays == 0 ? "0" : agentDays < MinShownAgents ? "< " + MinShownAgents : agentDays.ToString(System.Globalization.CultureInfo.CurrentCulture);
}
