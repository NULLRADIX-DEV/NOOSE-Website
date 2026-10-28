using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Public;

/// <summary>The public roster: the leadership as an org chart by rank, everyone else by name only.</summary>
public record PublicRoster(IReadOnlyList<PublicLeadershipTier> Leadership, IReadOnlyList<PublicRosterMember> Members)
{
    public static readonly PublicRoster Empty = new([], []);
}

/// <summary>One rank level of the org chart, highest first; the title is the rank wording, never its value.</summary>
public record PublicLeadershipTier(string Title, IReadOnlyList<PublicLeadershipCard> Cards);

/// <summary>One leadership card as an anonymous visitor sees it.</summary>
/// <remarks>Outward. Name, phone and photo, nothing else: no codename, no id, no unit, no badge number.</remarks>
public record PublicLeadershipCard(string Name, string? Phone, string? PhotoUrl);

/// <summary>One agent below the leadership band; the name is all that leaves.</summary>
public record PublicRosterMember(string Name);

/// <summary>One leadership row as the settings panel works on it.</summary>
/// <remarks>Inward: it names the account behind the card, which the outward card deliberately does not.</remarks>
public record PublicLeadershipEdit(string AgentId, string Codename, string Name, Rank Rank, string? Phone, string? PhotoUrl);

/// <summary>A card photo, as the anonymous endpoint streams it.</summary>
public record PublicLeadershipPhoto(string FileNameSaved, string ContentType);
