using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using NOOSE_Website.Authorization;

namespace NOOSE_Website.Services;

/// <summary>Limits, numbering, validity and permissions of a service directive; pure, so service and pages read one answer.</summary>
public static partial class DirectiveRules
{
    public const int MaxTitle = 200;
    public const int MaxSubject = 100;
    public const int MaxChangeNote = 300;

    /// <summary>The number as it is shown and cited: DA-07, DA-123.</summary>
    public static string Label(int number) => "DA-" + number.ToString("00", CultureInfo.InvariantCulture);

    /// <summary>Reads a typed number such as DA-07, da 7 or DA07; a bare number or DA-0 is no directive.</summary>
    public static bool TryParseNumber(string? text, out int number)
    {
        number = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var match = NumberPattern().Match(text.Trim());
        return match.Success
            && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number)
            && number > 0;
    }

    /// <summary>The newest version that has started by <paramref name="today"/>, or null when none has.</summary>
    public static int? ValidVersion(IReadOnlyList<(int Version, DateOnly ValidFrom)> versions, DateOnly today)
    {
        int? best = null;
        foreach (var (version, validFrom) in versions)
        {
            if (validFrom <= today && (best is null || version > best))
            {
                best = version;
            }
        }
        return best;
    }

    /// <summary>A new version may not start before the one it replaces.</summary>
    public static bool IsValidFromAllowed(DateOnly validFrom, DateOnly? previousValidFrom)
        => previousValidFrom is null || validFrom >= previousValidFrom;

    /// <summary>The first version an acknowledgment must reach: only a version that asks moves it.</summary>
    public static int NextAcknowledgeFrom(int currentAcknowledgeFrom, int newVersion, bool requiresAcknowledgment)
        => requiresAcknowledgment ? newVersion : currentAcknowledgeFrom;

    /// <summary>May issue, replace, repeal, trash or restore a directive: leadership with write access.</summary>
    public static bool MayManage(ClaimsPrincipal actor)
        // team leads stay invisible
        => actor.MayWrite() && actor.IsInternalAgent() && !actor.IsTeamLead() && actor.IsLeadership();

    /// <summary>May see the quota and the list of who is missing: leadership and supervision, never a partner.</summary>
    public static bool MaySeeQuota(ClaimsPrincipal actor)
        => actor.MayClassifiedRead() && actor.IsInternalAgent();

    /// <summary>May acknowledge a directive: every internal agent that may write.</summary>
    public static bool MayAcknowledge(ClaimsPrincipal actor)
        => actor.MayWrite() && actor.IsInternalAgent();

    [GeneratedRegex(@"^DA\s*-?\s*([0-9]{1,9})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumberPattern();
}
