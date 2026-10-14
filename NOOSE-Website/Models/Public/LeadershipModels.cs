using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Public;

/// <summary>One released leadership entry as an anonymous visitor sees it.</summary>
/// <remarks>
/// Outward. It carries a real name on purpose — this is the one surface where the house names its own leadership —
/// and it carries nothing else about the account: no codename, no id, no flags, no rank value. The rank WORDING is
/// an editorial copy, so the chart says what the editor released rather than what the roster happens to hold.
/// </remarks>
public record PublicLeadershipCard(
    string Key,
    string DisplayName,
    string Title,
    string? RoleText,
    bool HasPhoto,
    IReadOnlyList<PublicLeadershipDecoration> Decorations);

/// <summary>One released decoration on a leadership card: what it is called and how it looks, nothing else.</summary>
public record PublicLeadershipDecoration(string Name, string Symbol, DecorationTier Tier);

/// <summary>One entry as the editor works on it.</summary>
/// <remarks>Inward: it names the agent behind the entry, which the outward card deliberately does not.</remarks>
public record PublicLeadershipEdit(
    string Id,
    string Key,
    string AgentId,
    string? AgentCodename,
    string DisplayName,
    string Title,
    string? RoleText,
    int SortOrder,
    bool HasPhoto,
    DateTime? PublishedAt,
    string? PublishedByCodename,
    IReadOnlyList<string> DecorationAwardIds);

/// <summary>What the editor submits.</summary>
public class PublicLeadershipInput
{
    public string? Id { get; set; }
    public string AgentId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? RoleText { get; set; }
    public int SortOrder { get; set; }

    /// <summary>Awards to release with the entry; null leaves the released copies as they are.</summary>
    public List<string>? DecorationAwardIds { get; set; }
}

/// <summary>A released photo, as the anonymous endpoint streams it.</summary>
public record PublicLeadershipPhoto(string FileNameSaved, string ContentType);
