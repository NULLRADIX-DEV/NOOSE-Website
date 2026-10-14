using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Decorations;

namespace NOOSE_Website.Models.Decorations;

/// <summary>One catalog entry as the settings panel lists it.</summary>
public record DecorationView(
    string Id,
    string Name,
    string? Description,
    string? DefaultCitation,
    string Symbol,
    DecorationTier Tier,
    bool IsActive,
    int Order,
    int AwardCount);

/// <summary>What the catalog editor submits.</summary>
public class DecorationInput
{
    public string? Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? DefaultCitation { get; set; }
    public string Symbol { get; set; } = DecorationSymbols.Default;
    public DecorationTier Tier { get; set; } = DecorationTier.Gold;
    public bool IsActive { get; set; } = true;
    public int Order { get; set; }
}

/// <summary>What the award dialog submits; the three switches only matter on a new award.</summary>
public class DecorationAwardInput
{
    public string AgentId { get; set; } = string.Empty;
    public string DecorationId { get; set; } = string.Empty;
    public DateTime? AwardedOn { get; set; }
    public string Citation { get; set; } = string.Empty;
    public bool PostToBoard { get; set; } = true;
    public bool PostToDiscord { get; set; } = true;
    public bool NotifyAgent { get; set; } = true;
}

/// <summary>One award in a personnel file or profile.</summary>
public record DecorationAwardView(
    string Id,
    string AgentId,
    string DecorationId,
    string DecorationName,
    string Symbol,
    DecorationTier Tier,
    DateTime AwardedOn,
    string Citation,
    string? AwardedByName,
    DateTime? RevokedAt,
    string? RevokedByName,
    string? RevocationReason)
{
    public bool IsRevoked => RevokedAt is not null;
}

/// <summary>The active awards of one decoration on one agent, folded into a plaque.</summary>
public record DecorationPlaqueView(
    string DecorationId,
    string Name,
    string Symbol,
    DecorationTier Tier,
    int Count,
    DateTime LatestOn);

/// <summary>A recent award for the dashboard card.</summary>
public record RecentDecorationView(
    string AwardId,
    string AgentId,
    string Codename,
    string DecorationName,
    string Symbol,
    DecorationTier Tier,
    DateTime AwardedOn);

/// <summary>Signatory and place printed on every certificate.</summary>
public record CertificateSettings(string? SignatoryId, string Place);

/// <summary>An agent offered as signatory; a stored one that no longer qualifies is flagged.</summary>
public record SignatoryOption(string Id, string Codename, Rank? Rank, bool Eligible);

/// <summary>What a certificate certifies.</summary>
public enum CertificateKind
{
    Decoration = 0,
    Appointment = 1,
    Promotion = 2,
}

/// <summary>Everything a certificate page prints; the real name only when the viewer may choose it.</summary>
/// <param name="Rank">Recipient's rank when decorated, or the rank a history row conferred.</param>
public record CertificateView(
    CertificateKind Kind,
    string AgentId,
    string Codename,
    string? RealName,
    string? BadgeNumber,
    Rank? Rank,
    DateTime Date,
    string? DecorationName,
    string? Symbol,
    DecorationTier? Tier,
    string? Citation,
    string? SignatoryName,
    Rank? SignatoryRank,
    string Place);

/// <summary>One decoration as copied onto a public leadership profile.</summary>
/// <remarks>Inward shape of the stored copy; the award id lets a revocation find and drop it.</remarks>
public record PublicDecorationCopy(string AwardId, string Name, string Symbol, DecorationTier Tier, int Year);
