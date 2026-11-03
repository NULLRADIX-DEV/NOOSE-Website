using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Search;

namespace NOOSE_Website.Services;

/// <summary>A share channel as stored: the webhook URL is a secret and never leaves the server.</summary>
public sealed record StoredShareChannel(string Id, string Name, string Url);

/// <summary>Which records may be shared into a Discord channel, what the post says, and how channels are kept.</summary>
public static class ChannelShareRules
{
    public const int MaxChannels = 20;
    public const int MaxNameLength = 50;
    public const int MaxNoteLength = 300;

    // escaping adds characters
    private const int MaxPostedNameChars = 120;
    private const int MaxPostedNoteChars = 650;

    /// <summary>The one refusal for a record that is missing, out of sight or classified, so none tells them apart.</summary>
    public const string Refused =
        "Diese Akte lässt sich nicht teilen: Verschlusssachen und Akten außerhalb deiner Sicht bleiben im Haus.";

    /// <summary>Refusal of a save made from an out-of-date list, so it cannot drop a newer channel or webhook.</summary>
    public const string StaleList = "Die Liste wurde inzwischen geändert, lade die Seite neu.";

    /// <summary>Access-log type of a share; the log has no column for the kind of access.</summary>
    public const string LogType = "Teilen";

    /// <summary>Audit type of the channel list, a config row without a table of its own.</summary>
    public const string AuditType = "DiscordShareChannels";

    /// <summary>The six classifiable record types; each has a detail page and the same secrecy flags.</summary>
    public static readonly IReadOnlySet<string> ShareableTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(Person), nameof(Faction), nameof(PersonGroup), nameof(Party), nameof(Case), nameof(Operation),
    };

    private static readonly HashSet<string> WebhookHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "discord.com", "discordapp.com", "ptb.discord.com", "canary.discord.com",
    };

    /// <summary>Access-log id of one share, in the shape the export uses.</summary>
    public static string LogEntry(string recordType, string recordId) => $"{recordType}:{recordId}";

    /// <summary>Whether the record carries any secrecy level; such a record is never shared.</summary>
    public static bool IsRestricted(IClassifiableRecord record)
        => RecordVisibility.LevelOf(record.IsClassified, record.IsTRUClassified, record.IsHRBClassified)
            != DocumentClassification.None;

    /// <summary>Version of the stored list; a save must hand back the one it was loaded with. A hash, never the content.</summary>
    public static string Version(string? storedJson)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(storedJson ?? string.Empty)));

    /// <summary>The note as one line, or null when empty; throws when it is too long.</summary>
    public static string? CleanNote(string? raw)
    {
        var flat = Flat(raw);
        if (flat.Length == 0)
        {
            return null;
        }
        if (flat.Length > MaxNoteLength)
        {
            throw new InvalidOperationException($"Der Hinweis darf höchstens {MaxNoteLength} Zeichen lang sein.");
        }
        return flat;
    }

    /// <summary>True for an https Discord webhook URL, optionally aimed at one thread.</summary>
    public static bool IsWebhookUrl(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !uri.IsDefaultPort
            || uri.UserInfo.Length > 0
            || uri.Fragment.Length > 0
            || !WebhookHosts.Contains(uri.Host))
        {
            return false;
        }
        // thread_id only
        var query = uri.Query.TrimStart('?');
        if (query.Length > 0 && !(query.StartsWith("thread_id=", StringComparison.Ordinal)
                && query["thread_id=".Length..] is { Length: > 0 } thread && thread.All(char.IsAsciiDigit)))
        {
            return false;
        }
        return WebhookParts(uri) is not null;
    }

    /// <summary>Host and webhook id only; the token is what posts, so it is never shown again.</summary>
    public static string Mask(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || WebhookParts(uri) is not { } parts)
        {
            return "hinterlegt";
        }
        var thread = uri.Query.Contains("thread_id=", StringComparison.Ordinal) ? " (Thread)" : string.Empty;
        return $"{uri.Host}/api/webhooks/{parts.Id}/•••{thread}";
    }

    /// <summary>Login-gated link to the record's page.</summary>
    public static string Link(string baseUrl, string recordType, string recordId)
        => baseUrl.TrimEnd('/') + SearchCatalog.Route(recordType, Uri.EscapeDataString(recordId));

    /// <summary>The post: record type, name, case number, the note as a quote and the link. Every free text escaped.</summary>
    public static string Compose(string recordType, string displayName, string? caseNumber, string? note, string link)
    {
        var name = Flat(displayName);
        var sb = new StringBuilder("📎 Geteilte Akte: ")
            .Append(RecordTypeDisplay.Name(recordType))
            .Append(" „")
            .Append(name.Length == 0 ? "—" : DiscordMarkdown.Escape(name, MaxPostedNameChars))
            .Append('“');
        var number = Flat(caseNumber);
        if (number.Length > 0)
        {
            sb.Append(" · ").Append(DiscordMarkdown.Escape(number, 60));
        }
        if (note is not null)
        {
            sb.Append("\n> ").Append(DiscordMarkdown.Escape(note, MaxPostedNoteChars));
        }
        return sb.Append('\n').Append(link).ToString();
    }

    /// <summary>Stored channels; a broken row or blob reads as no channel rather than as an error.</summary>
    public static List<StoredShareChannel> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            return (JsonSerializer.Deserialize<List<StoredShareChannel>>(json) ?? [])
                .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Id)
                    && !string.IsNullOrWhiteSpace(c.Name) && IsWebhookUrl(c.Url))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string Serialize(IReadOnlyList<StoredShareChannel> channels) => JsonSerializer.Serialize(channels);

    /// <summary>List to store plus names whose webhook was replaced; an empty URL keeps the stored webhook, a missing row is removed.</summary>
    public static (List<StoredShareChannel> Channels, List<string> Replaced) Merge(
        IReadOnlyList<StoredShareChannel> stored, IReadOnlyList<ShareChannelInput> rows, Func<string> newId)
    {
        if (rows.Count > MaxChannels)
        {
            throw new InvalidOperationException($"Mehr als {MaxChannels} Kanäle lassen sich nicht anlegen.");
        }
        var result = new List<StoredShareChannel>();
        var replaced = new List<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var name = Flat(row.Name);
            if (name.Length == 0)
            {
                throw new InvalidOperationException("Jeder Kanal braucht einen Namen.");
            }
            if (name.Length > MaxNameLength)
            {
                throw new InvalidOperationException($"Ein Kanalname darf höchstens {MaxNameLength} Zeichen lang sein.");
            }
            if (!names.Add(name))
            {
                throw new InvalidOperationException($"Der Kanalname „{name}“ kommt doppelt vor.");
            }
            var url = string.IsNullOrWhiteSpace(row.WebhookUrl) ? null : row.WebhookUrl.Trim();
            if (url is not null && !IsWebhookUrl(url))
            {
                throw new InvalidOperationException(
                    $"Die Webhook-URL für „{name}“ ist keine Discord-Webhook-Adresse (https://discord.com/api/webhooks/…).");
            }
            var existing = row.Id is null ? null : stored.FirstOrDefault(c => c.Id == row.Id);
            if (existing is not null && !ids.Add(existing.Id))
            {
                throw new InvalidOperationException("Ein Kanal steht doppelt in der Liste.");
            }
            if (existing is null)
            {
                // never trust client ids
                result.Add(new StoredShareChannel(newId(), name,
                    url ?? throw new InvalidOperationException($"Für den Kanal „{name}“ fehlt die Webhook-URL.")));
                continue;
            }
            if (url is not null && url != existing.Url)
            {
                replaced.Add(name);
            }
            result.Add(existing with { Name = name, Url = url ?? existing.Url });
        }
        return (result, replaced);
    }

    // single line, printable
    private static string Flat(string? raw)
        => raw is null
            ? string.Empty
            : string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => new string(part.Where(c => !char.IsControl(c)).ToArray()))
                .Where(part => part.Length > 0));

    // "api[/vN]/webhooks/{id}/{token}"
    private static (string Id, string Token)? WebhookParts(Uri uri)
    {
        var segments = uri.AbsolutePath.Trim('/').Split('/');
        var offset = segments.Length == 5 && segments[1].Length > 1 && segments[1][0] == 'v'
            && segments[1][1..].All(char.IsAsciiDigit) ? 1 : 0;
        if (segments.Length != 4 + offset || segments[0] != "api" || segments[1 + offset] != "webhooks")
        {
            return null;
        }
        var id = segments[2 + offset];
        var token = segments[3 + offset];
        return id.Length > 0 && id.All(char.IsAsciiDigit)
            && token.Length > 0 && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? (id, token)
            : null;
    }
}
