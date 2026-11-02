using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Tests.Infrastructure;

/// <summary>Everything a wanted card shows, joined into one string for "does not contain" checks.</summary>
public static class WantedCardText
{
    public static string Of(WantedDiscordEmbed card)
        => string.Join('\n', new[] { card.Title, card.ProfilePath, card.ImagePath, card.ThumbnailPath, card.Footer }
            .Concat(card.Fields.SelectMany(f => new[] { f.Name, f.Value }))
            .Where(part => part is not null));

    /// <summary>The value of one field, or null when the card has none of that name.</summary>
    public static string? Field(WantedDiscordEmbed card, string name)
        => card.Fields.FirstOrDefault(f => f.Name == name)?.Value;
}
