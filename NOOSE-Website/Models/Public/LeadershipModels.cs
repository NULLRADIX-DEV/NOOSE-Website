using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Public;

/// <summary>The public roster: leadership cards on top, everyone else by name only.</summary>
public record PublicRoster(IReadOnlyList<PublicLeadershipCard> Leadership, IReadOnlyList<PublicRosterMember> Members)
{
    public static readonly PublicRoster Empty = new([], []);
}

/// <summary>One leadership card as an anonymous visitor sees it.</summary>
/// <remarks>Outward. Name, rank wording and phone, nothing else: no codename, no id, no unit, no badge number.</remarks>
public record PublicLeadershipCard(string Name, string Title, string? Phone);

/// <summary>One agent below the leadership band; the name is all that leaves.</summary>
public record PublicRosterMember(string Name);

/// <summary>One leadership row as the settings panel works on it.</summary>
/// <remarks>Inward: it names the account behind the card, which the outward card deliberately does not.</remarks>
public record PublicLeadershipEdit(string AgentId, string Codename, string Name, Rank Rank, string? Phone);
