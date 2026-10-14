using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NOOSE_Website.Models.Common;

namespace NOOSE_Website.Services;

/// <summary>One faction line on the Discord threat board.</summary>
public sealed record ThreatBoardEntry(string Id, string Name, int Score);

/// <summary>The board embed as the app words it; the webhook service turns it into Discord's shape.</summary>
public sealed record ThreatBoardEmbed(string Title, string Description, int Color, string Footer, DateTime AsOfUtc);

/// <summary>The posted board message and the webhook URL it was posted through.</summary>
public sealed record ThreatBoardMessage(string MessageId, string WebhookUrl);

/// <summary>How the Discord threat board picks, orders, words and recognises its factions.</summary>
public static partial class FactionThreatBoard
{
    /// <summary>Lines shown before the rest collapses into a count.</summary>
    public const int MaxLines = 25;

    /// <summary>Longest faction name shown on a line.</summary>
    public const int MaxNameChars = 60;

    // Discord takes 4096 characters per embed description; the margin holds the overflow line
    private const int MaxDescriptionChars = 3900;

    // bump when the wording changes, so the next pass rewrites the message
    private const int Layout = 1;

    private const int Red = 0xE53935;
    private const int Green = 0x43A047;
    private const string Office = "National Office of Security Enforcement";

    /// <summary>Stored threshold, or the default when missing or out of range.</summary>
    public static int ParseThreshold(string? raw)
        => int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && IsValidThreshold(value)
            ? value
            : DiscordWebhookConfig.DefaultThreatBoardThreshold;

    public static bool IsValidThreshold(int value)
        => value is >= DiscordWebhookConfig.MinThreatBoardThreshold and <= DiscordWebhookConfig.MaxThreatBoardThreshold;

    /// <summary>Highest score first; name and id break ties so the order never flips between passes.</summary>
    public static IReadOnlyList<ThreatBoardEntry> Order(IEnumerable<ThreatBoardEntry> entries)
        => entries
            .OrderByDescending(e => e.Score)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ToList();

    /// <summary>The embed for an ordered list; an empty list says so in green.</summary>
    public static ThreatBoardEmbed Render(IReadOnlyList<ThreatBoardEntry> ordered, int threshold, string baseUrl,
        DateTime asOfUtc)
    {
        var title = $"🚨 GEFAHRENLAGE · FRAKTIONEN ÜBER {threshold}";
        if (ordered.Count == 0)
        {
            return new ThreatBoardEmbed(title, $"✅ Derzeit liegt keine Fraktion über {threshold}.", Green, Office,
                asOfUtc);
        }

        var description = new StringBuilder($"> Bedrohungs-Score über {threshold}, höchster zuerst.\n");
        var shown = 0;
        foreach (var entry in ordered)
        {
            var line = $"\n🔴 `{entry.Score}` **[{Escape(Shorten(entry.Name))}]({FileLink(baseUrl, entry.Id)})**";
            if (shown >= MaxLines || description.Length + line.Length > MaxDescriptionChars)
            {
                break;
            }
            description.Append(line);
            shown++;
        }
        if (shown < ordered.Count)
        {
            description.Append($"\n\n… und {ordered.Count - shown} weitere");
        }
        var count = ordered.Count == 1 ? "1 Fraktion" : $"{ordered.Count} Fraktionen";
        return new ThreatBoardEmbed(title, description.ToString(), Red, $"{count} · {Office}", asOfUtc);
    }

    /// <summary>Hash of everything the message shows except its time stamp; equal hash, nothing to edit.</summary>
    public static string Fingerprint(IReadOnlyList<ThreatBoardEntry> ordered, int threshold, string baseUrl)
    {
        var canonical = new StringBuilder()
            .Append(Layout).Append('\n')
            .Append(threshold.ToString(CultureInfo.InvariantCulture)).Append('\n')
            .Append(baseUrl).Append('\n');
        foreach (var entry in ordered)
        {
            canonical.Append(entry.Id).Append('\t').Append(entry.Name).Append('\t')
                .Append(entry.Score.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
        // a hash, not the list: the row is a system setting, and its first write lands in the change log
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }

    /// <summary>The webhook's own id from its URL; a URL of another shape is told apart by a hash.</summary>
    public static string WebhookId(string webhookUrl)
    {
        var match = WebhookIdPattern().Match(webhookUrl);
        return match.Success
            ? match.Groups[1].Value
            : "h" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(webhookUrl.Trim())))[..16];
    }

    /// <summary>Stored form of the posted board: its id and the URL it went out through, so a later pass can still remove it.</summary>
    public static string MessageRef(string messageId, string webhookUrl) => $"{messageId}|{webhookUrl}";

    public static ThreatBoardMessage? ParseRef(string? raw)
    {
        var bar = raw?.IndexOf('|') ?? -1;
        if (bar <= 0 || bar == raw!.Length - 1)
        {
            return null;
        }
        var messageId = raw[..bar];
        return messageId.All(char.IsAsciiDigit) ? new ThreatBoardMessage(messageId, raw[(bar + 1)..]) : null;
    }

    /// <summary>Post URL that makes Discord answer with the message, so its id can be kept.</summary>
    public static string PostUrl(string webhookUrl) => webhookUrl + (webhookUrl.Contains('?') ? "&" : "?") + "wait=true";

    /// <summary>Discord's error code for an edit inside an archived thread; only a new post reopens the thread.</summary>
    public const int ArchivedThreadCode = 50083;

    /// <summary>Discord's error code for a webhook that no longer exists; its messages stay in the channel.</summary>
    public const int UnknownWebhookCode = 10015;

    /// <summary>How a refused edit is read: gone or frozen means a fresh post, anything else waits for the next pass.</summary>
    public static ThreatBoardEdit RefusedEdit(int status, int? discordCode)
        => discordCode == ArchivedThreadCode ? ThreatBoardEdit.Archived
            : status == 404 ? ThreatBoardEdit.Missing
            : ThreatBoardEdit.Failed;

    /// <summary>How a refused delete is read: gone already, out of reach for good, or worth another try.</summary>
    public static ThreatBoardRemoval RefusedDelete(int status, int? discordCode) => status switch
    {
        404 when discordCode == UnknownWebhookCode => ThreatBoardRemoval.Unreachable,
        404 => ThreatBoardRemoval.Removed,
        401 or 403 => ThreatBoardRemoval.Unreachable,
        _ when discordCode == ArchivedThreadCode => ThreatBoardRemoval.Unreachable,
        _ => ThreatBoardRemoval.Failed,
    };

    /// <summary>The path of one URL with the query of another; a rotated token keeps the old thread.</summary>
    public static string WithQueryOf(string url, string queryFrom)
    {
        var q = url.IndexOf('?');
        var path = q < 0 ? url : url[..q];
        var other = queryFrom.IndexOf('?');
        return other < 0 ? path : path + queryFrom[other..];
    }

    /// <summary>Edit URL of one message; a thread_id query stays, Discord reads it on edits too.</summary>
    public static string EditUrl(string webhookUrl, string messageId)
    {
        var q = webhookUrl.IndexOf('?');
        var path = q < 0 ? webhookUrl : webhookUrl[..q];
        var query = q < 0 ? string.Empty : webhookUrl[q..];
        return $"{path.TrimEnd('/')}/messages/{messageId}{query}";
    }

    private static string FileLink(string baseUrl, string factionId)
        => $"{baseUrl.TrimEnd('/')}/fraktionen/{Uri.EscapeDataString(factionId)}";

    private static string Shorten(string name)
    {
        var flat = string.Join(' ', name.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (flat.Length == 0)
        {
            return "—";
        }
        if (flat.Length <= MaxNameChars)
        {
            return flat;
        }
        var cut = MaxNameChars - 1;
        // never split an emoji in half
        if (char.IsHighSurrogate(flat[cut - 1]))
        {
            cut--;
        }
        return flat[..cut] + "…";
    }

    // a name must not break out of the link text or switch on formatting
    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            // Discord pairs link brackets before it reads escapes, so a lone bracket would still break the link
            if (c is '[' or ']')
            {
                sb.Append(c == '[' ? '［' : '］');
                continue;
            }
            if (c is '\\' or '*' or '_' or '~' or '`' or '|' or '>' or '(' or ')' or '<' or '#')
            {
                sb.Append('\\');
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"/webhooks/(\d+)/", RegexOptions.CultureInvariant)]
    private static partial Regex WebhookIdPattern();
}
