using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Public;

namespace NOOSE_Website.Services.Public;

/// <summary>One name and value pair of the card.</summary>
public sealed record WantedDiscordField(string Name, string Value, bool Inline);

/// <summary>The card as the app words it; the webhook service makes the paths absolute and turns it into Discord's shape.</summary>
/// <remarks>
/// Built from outward types only, so it cannot hold the charge, a PersonId, the internal case number, a codename or a
/// score. At most one of <see cref="ImagePath"/> and <see cref="ThumbnailPath"/> is set.
/// </remarks>
public sealed record WantedDiscordEmbed(
    string Title,
    string ProfilePath,
    int Color,
    IReadOnlyList<WantedDiscordField> Fields,
    string? ImagePath,
    string? ThumbnailPath,
    string Footer,
    DateTime? TimestampUtc);

/// <summary>How the Discord card of a public wanted notice is worded, coloured and cut.</summary>
/// <remarks>
/// Takes <see cref="PublicWantedCard"/> and <see cref="PublicBounty"/> and nothing else on purpose. No accusation text:
/// after a retraction the post is then a dead link rather than a standing allegation.
/// </remarks>
public static class WantedDiscordCard
{
    /// <summary>Discord takes 256 characters in an embed title.</summary>
    public const int MaxTitleChars = 256;

    /// <summary>Discord takes 1024 characters in a field value.</summary>
    public const int MaxFieldChars = 1024;

    /// <summary>Room for one hint name; the editor caps them far below.</summary>
    private const int MaxHintChars = 200;

    private const int Cyan = 0x00B8D4;
    private const int Gold = 0xC9A227;
    private const string Office = "National Office of Security Enforcement";

    /// <summary>The card of a fresh notice; the bounty shows only when there is money on the head.</summary>
    public static WantedDiscordEmbed Published(PublicWantedCard card, PublicBounty? bounty)
    {
        var fields = new List<WantedDiscordField> { CaseField(card), HazardField(card) };
        if (bounty is { Total: > 0m })
        {
            fields.Add(new WantedDiscordField("Kopfgeld", Amount(bounty), true));
        }
        AddDetails(fields, card);
        return new WantedDiscordEmbed(Title(Emoji(card.Kind), Heading(card.Kind), card.DisplayName), ProfilePath(card),
            HazardColour(card.HazardLevel), fields, PhotoPath(card), null, Office, Utc(card.PublishedAt));
    }

    /// <summary>The card of a raised bounty; the photo is a thumbnail, the new total comes first.</summary>
    public static WantedDiscordEmbed BountyRaised(PublicWantedCard card, PublicBounty total, DateTime raisedAt)
    {
        var fields = new List<WantedDiscordField>
        {
            new("Neues Kopfgeld", Amount(total), true),
            CaseField(card),
            HazardField(card),
        };
        AddDetails(fields, card);
        return new WantedDiscordEmbed(Title("💰", "Kopfgeld erhöht", card.DisplayName), ProfilePath(card), Gold,
            fields, null, PhotoPath(card), Office, Utc(raisedAt));
    }

    private static WantedDiscordField CaseField(PublicWantedCard card)
        => new("Aktenzeichen", DiscordMarkdown.Escape(card.CaseNumber, MaxFieldChars), true);

    // same as the profile
    private static WantedDiscordField HazardField(PublicWantedCard card)
        => new("Gefahrenstufe", HazardLevelLogic.Name(card.HazardLevel), true);

    private static void AddDetails(List<WantedDiscordField> fields, PublicWantedCard card)
    {
        if (!string.IsNullOrWhiteSpace(card.AliasText))
        {
            fields.Add(new WantedDiscordField("Auch bekannt als",
                DiscordMarkdown.Escape(card.AliasText.Trim(), MaxFieldChars), false));
        }
        var hints = Hints(card.Hints);
        if (hints is not null)
        {
            fields.Add(new WantedDiscordField("Warnhinweise", hints, false));
        }
    }

    // whole lines drop off
    private static string? Hints(IReadOnlyList<PublicWantedHint> hints)
    {
        var lines = hints
            .Where(h => !string.IsNullOrWhiteSpace(h.Label))
            .Select(h => $"{WarnhinweisColours.Dot(h.Colour)} {DiscordMarkdown.Escape(h.Label.Trim(), MaxHintChars)}")
            .ToList();
        if (lines.Count == 0)
        {
            return null;
        }

        var value = string.Join('\n', lines);
        for (var shown = lines.Count - 1; value.Length > MaxFieldChars; shown--)
        {
            value = string.Join('\n', lines.Take(shown)) + $"\n… und {lines.Count - shown} weitere";
        }
        return value;
    }

    private static string Title(string emoji, string heading, string displayName)
    {
        var head = $"{emoji} {heading}: ";
        var flat = string.Join(' ', displayName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return head + DiscordMarkdown.Escape(flat.Length == 0 ? "—" : flat, MaxTitleChars - head.Length);
    }

    // items say Sachfahndung
    private static string Heading(PublicWantedKind kind)
        => WantedKinds.IsItem(kind)
            ? $"Sachfahndung {PublicWantedKindDisplay.Name(kind)}"
            : PublicWantedKindDisplay.Name(kind);

    private static string Emoji(PublicWantedKind kind) => kind switch
    {
        PublicWantedKind.Vermisst => "❓",
        PublicWantedKind.Zeugenaufruf => "📣",
        PublicWantedKind.Fahrzeug => "🚗",
        PublicWantedKind.Waffe => "🔫",
        _ => "🔎",
    };

    private static int HazardColour(HazardLevel level) => level switch
    {
        HazardLevel.Critical => 0xE53935,
        HazardLevel.High => 0xFB8C00,
        HazardLevel.Medium => 0x1E88E5,
        _ => Cyan,
    };

    private static string Amount(PublicBounty bounty) => (bounty.IsCap ? "bis " : string.Empty) + Money.Format(bounty.Total);

    private static string ProfilePath(PublicWantedCard card) => $"/gesucht/{Uri.EscapeDataString(card.CaseNumber)}";

    // items never have one
    private static string? PhotoPath(PublicWantedCard card)
        => card.HasPhoto && !WantedKinds.IsItem(card.Kind) ? ProfilePath(card) + "/foto" : null;

    // stored as UTC
    private static DateTime? Utc(DateTime? value)
        => value is { } at
            ? at.Kind switch
            {
                DateTimeKind.Local => at.ToUniversalTime(),
                DateTimeKind.Utc => at,
                _ => DateTime.SpecifyKind(at, DateTimeKind.Utc),
            }
            : null;
}
