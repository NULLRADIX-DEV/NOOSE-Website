namespace NOOSE_Website.Models.Public;

/// <summary>What the public ID check says; nothing else leaves the house.</summary>
/// <param name="Valid">True for a current card. An invalid answer carries no name and no rank.</param>
/// <param name="Alias">Public display name of the holder; null unless the card is valid.</param>
/// <param name="Title">Rank title as used outside the house; null unless the card is valid.</param>
public sealed record PublicServiceIdCheck(bool Valid, string? Alias, string? Title);
