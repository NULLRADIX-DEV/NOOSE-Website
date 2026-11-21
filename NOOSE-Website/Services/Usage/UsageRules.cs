using System.Globalization;
using System.Security.Claims;
using NOOSE_Website.Authorization;
using NOOSE_Website.Models.Navigation;
using NOOSE_Website.Models.Statistics;

namespace NOOSE_Website.Services.Usage;

/// <summary>Who is counted, how long counters live and when a number is too small to show.</summary>
public static class UsageRules
{
    /// <summary>Days a counter row is kept.</summary>
    public const int RetentionDays = 365;

    /// <summary>A row's numbers show only once this many different agents used it on one day.</summary>
    public const int MinShownAgents = 3;

    /// <summary>Shown placeholder for a hidden number.</summary>
    public const string Hidden = "–";

    /// <summary>Only an internal agent with a rank who may write: no demo, partner, read-only supervision, citizen or visitor.</summary>
    public static bool IsCounted(ClaimsPrincipal user)
        => user.Identity?.IsAuthenticated == true
           && user.IsInternalAgent()
           && user.MayWrite()
           && user.GetRank() is not null;

    /// <summary>The agent's own choice; counted unless they switched it off.</summary>
    public static bool IsOptedOut(NavPreferences prefs) => prefs.UsageStatsOptOut;

    /// <summary>True when the row's numbers may show.</summary>
    /// <remarks>Agent-days can't tell people apart (one agent on three days is three), so the bar is the busiest single day.</remarks>
    public static bool IsShown(UsageRow row) => row.Never || row.PeakAgents >= MinShownAgents;

    /// <summary>Events as the statistics show them; none at all is no secret.</summary>
    public static string Events(UsageRow row)
        => IsShown(row) ? row.Events.ToString("N0", CultureInfo.CurrentCulture) : Hidden;

    /// <summary>Agent-days as the statistics show them.</summary>
    public static string AgentDays(UsageRow row)
        => IsShown(row) ? row.AgentDays.ToString(CultureInfo.CurrentCulture) : Hidden;
}
