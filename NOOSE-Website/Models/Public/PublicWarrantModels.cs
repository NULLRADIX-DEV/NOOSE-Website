using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Public;

/// <summary>What the public warrant check says: only what the printed paper shows anyway, never person or subject.</summary>
/// <param name="State">Valid, expired or cancelled; an unsigned request never answers.</param>
/// <param name="SignerName">Codename of the signer as printed under the signature.</param>
/// <param name="SignerTitle">Rank wording at the moment of signing, or null when none was stored.</param>
public sealed record PublicWarrantCheck(
    WarrantType Type,
    string CaseNumber,
    WarrantState State,
    DateTime ValidFrom,
    DateTime? ValidUntil,
    DateTime? CancelledAt,
    string? SignerName,
    string? SignerTitle,
    DateTime? SignedAt);
