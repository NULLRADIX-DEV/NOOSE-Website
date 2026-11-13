using System.Text.RegularExpressions;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.People;

namespace NOOSE_Website.Services.Discord.Bot;

/// <summary>Finds the first person or faction link of the agency's own site in a chat message.</summary>
public static partial class DiscordLinkParser
{
    [GeneratedRegex(@"https?://(?<host>[A-Za-z0-9.\-]+)(:\d+)?/(?<kind>personen|fraktionen)/(?<id>[0-9a-fA-F\-]{36})(?![0-9a-fA-F\-])", RegexOptions.IgnoreCase)]
    private static partial Regex RecordLink();

    /// <summary>Type and id of the first own-site record link, or null. Only the configured host counts.</summary>
    public static (string Type, string Id)? Find(string? text, string baseUrl)
    {
        if (string.IsNullOrEmpty(text) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var site))
        {
            return null;
        }
        foreach (Match match in RecordLink().Matches(text))
        {
            var host = match.Groups["host"].Value;
            if (!string.Equals(host, site.Host, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(host, "www." + site.Host, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var type = match.Groups["kind"].Value.Equals("personen", StringComparison.OrdinalIgnoreCase) ? nameof(Person) : nameof(Faction);
            return (type, match.Groups["id"].Value.ToLowerInvariant());
        }
        return null;
    }
}
