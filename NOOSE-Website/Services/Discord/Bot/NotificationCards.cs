using System.Globalization;
using System.Text;
using NetCord;
using NetCord.Rest;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Services.Discord.Bot;

/// <summary>The ping line of a card and plain ping messages for the rest; a tuple cannot name an element Rest.</summary>
public readonly record struct PingLineSet(string First, IReadOnlyList<string> Rest);

/// <summary>Renders every Discord notification as one bot card; pure, no I/O and no clock.</summary>
public static class NotificationCards
{
    private const int MaxHeadlineChars = 300;
    private const int MaxReasonChars = 1024;
    private const int MaxRecordIdChars = 64;
    private const int MaxSnowflakeChars = 20;

    /// <summary>Discord takes 2000 characters in a plain message.</summary>
    private const int MaxPlainMessageChars = 2000;

    /// <summary>Discord takes 512 characters in a link button.</summary>
    private const int MaxLinkChars = 512;

    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    // ---------------------------------------------------------------- cards

    /// <summary>The card of a category that carries no data beyond its notice; the headline only where the caller allows it.</summary>
    public static IReadOnlyList<IMessageComponentProperties> Generic(NotificationType type, string baseUrl, string? href,
        string? headline, string ping, bool acknowledgeable = false)
    {
        var (accent, emoji, title) = Style(type);
        var body = DiscordWebhookService.Notice(type);
        var line = DiscordCards.Inline(headline, MaxHeadlineChars);
        if (line.Length > 0)
        {
            body += $"\n**{line}**";
        }
        return Assemble(accent, ping, DiscordCards.HeaderWithThumbnail(emoji, title, null, LogoUrl(baseUrl)), body,
            Buttons(type, href, baseUrl, acknowledgeable));
    }

    /// <summary>The personnel-file entry; pings the subject agent.</summary>
    public static IReadOnlyList<IMessageComponentProperties> PersonnelEntry(string ping, string subject, string art,
        DateTime entryDate, string reason, string executors, string? link)
    {
        var (accent, emoji, title) = Style(NotificationType.PersonnelEntry);
        var body = DiscordCards.Fields(
            ("👤 Name", Dash(DiscordCards.Inline(subject))),
            ("🏷️ Art", Dash(DiscordCards.Inline(art))),
            ("📅 Datum", Stamp(entryDate, "dddd, d. MMMM yyyy 'um' HH:mm")),
            ("📋 Begründung", Dash(Reason(reason))),
            ("👮 Auszuführender", Dash(DiscordCards.Inline(executors, 400))));
        return Assemble(accent, ping, DiscordCards.HeaderWithThumbnail(emoji, title, null, LogoOf(link)), body, OpenRow(link));
    }

    /// <summary>The decoration ceremony; pings the honoured agent and names the codename only.</summary>
    public static IReadOnlyList<IMessageComponentProperties> Decoration(string ping, string codename, string decoration,
        DateTime awardedOn, string citation, string? awardedBy, string? link)
    {
        var (accent, emoji, title) = Style(NotificationType.DecorationAwarded);
        var body = DiscordCards.Fields(
            ("👤 Agent", Dash(DiscordCards.Inline(codename, 100))),
            ("🎖️ Ehrenzeichen", Dash(DiscordCards.Inline(decoration, 150))),
            ("📅 Verliehen am", Stamp(awardedOn, "dddd, d. MMMM yyyy")),
            ("📜 Verleihungstext", Reason(citation)),
            ("✍️ Verliehen durch", Dash(DiscordCards.Inline(awardedBy, 100))));
        return Assemble(accent, ping, DiscordCards.HeaderWithThumbnail(emoji, title, null, LogoOf(link)), body, OpenRow(link));
    }

    /// <summary>The official termination notice; the chips are mention markup that pings nobody inside a card.</summary>
    public static IReadOnlyList<IMessageComponentProperties> Termination(string subjectDisplay, string? subjectChip,
        string? executorChip, DateTime at, string reason, string? link)
    {
        var (accent, emoji, title) = Style(NotificationType.AgentTerminated);
        var body = "> Hiermit wird die **Kündigung** des folgenden Mitglieds offiziell bekannt gegeben.\n"
            + DiscordCards.Fields(
                ("👤 Name", Subject(subjectDisplay, subjectChip)),
                ("📅 Datum", Stamp(at, "dddd, d. MMMM yyyy HH:mm")),
                ("👮 Ausgeführt von", Chip(executorChip)),
                ("📋 Begründung", Dash(Reason(reason))));
        return Assemble(accent, string.Empty, DiscordCards.HeaderWithThumbnail(emoji, title, null, LogoOf(link)), body, OpenRow(link));
    }

    /// <summary>The public wanted or bounty card; the photo replaces the logo, the paths become absolute.</summary>
    public static IReadOnlyList<IMessageComponentProperties> Wanted(WantedDiscordEmbed card, string baseUrl, string ping)
    {
        var photo = DiscordWebhookService.Link(baseUrl, card.ImagePath ?? card.ThumbnailPath) ?? LogoUrl(baseUrl);
        // title pre-escaped
        var header = new ComponentSectionProperties(new ComponentSectionThumbnailProperties(new ComponentMediaProperties(photo)))
        {
            new TextDisplayProperties(DiscordCards.Clip("### " + card.Title, 3800)),
        };
        return Assemble(DiscordCards.Orange, ping, header, WantedBody(card.Fields),
            OpenRow(DiscordWebhookService.Link(baseUrl, card.ProfilePath)));
    }

    /// <summary>An app-authored broadcast; the content is the caller's markdown and the card never offers a record button.</summary>
    public static IReadOnlyList<IMessageComponentProperties> Custom(NotificationType type, string content, string? link, string ping)
    {
        var (accent, emoji, title) = Style(type);
        return Assemble(accent, ping, DiscordCards.HeaderWithThumbnail(emoji, title, null, LogoOf(link)), content, OpenRow(link));
    }

    /// <summary>The daily overview; its lines hold the mentions, so no ping line of its own.</summary>
    public static IReadOnlyList<IMessageComponentProperties> Digest(string content, string? link)
    {
        var (accent, emoji, title) = Style(NotificationType.PingDigest);
        return Assemble(accent, string.Empty, DiscordCards.HeaderWithThumbnail(emoji, title, null, LogoOf(link)), content, OpenRow(link));
    }

    /// <summary>A shared record; the caller's text, escaped already, carries its own link.</summary>
    public static IReadOnlyList<IMessageComponentProperties> Share(string content)
        => Assemble(DiscordCards.Steel, string.Empty, null, content, null);

    // ---------------------------------------------------------------- buttons

    /// <summary>The button row of a category: its actions where the href names a record, then "Öffnen"; an announcement offers "Quittieren" only when it is acknowledgeable.</summary>
    public static ActionRowProperties? Buttons(NotificationType type, string? href, string? baseUrl = null,
        bool acknowledgeable = false)
    {
        var root = string.IsNullOrWhiteSpace(baseUrl) ? DiscordWebhookConfig.DefaultBaseUrl : baseUrl;
        var link = DiscordWebhookService.Link(root, href) ?? DiscordWebhookService.Link(root, DiscordWebhookService.FallbackRoute(type));
        var items = new List<IActionRowComponentProperties?>();
        if (RecordId(type, href) is { } id)
        {
            items.Add(type switch
            {
                NotificationType.JobAssigned or NotificationType.JobDueSoon => ActionButton("cjd", "Erledigt", "✅", id),
                // a reminder exists only for announcements that need it
                NotificationType.Announcement when acknowledgeable => ActionButton("cack", "Quittieren", "✍️", id),
                NotificationType.AcknowledgmentReminder => ActionButton("cack", "Quittieren", "✍️", id),
                NotificationType.PublicTipCreated => ActionButton("ctip", "Übernehmen", "🙋", id),
                NotificationType.PublicTicketCreated => ActionButton("ctk", "Übernehmen", "🙋", id),
                NotificationType.Recruiting => ActionButton("cbw", "Übernehmen", "🙋", id),
                _ => null,
            });
            if (HasDetails(type))
            {
                items.Add(DiscordCards.Button(BotIds.Make("nd", ((int)type).ToString(CultureInfo.InvariantCulture), id), "Details", "🔎"));
            }
        }
        items.Add(type switch
        {
            NotificationType.AppointmentAssigned or NotificationType.MeetingScheduled or NotificationType.MeetingReminder
                => ActionButton("cdates", "Meine Termine", "📅"),
            NotificationType.InactivityNudge => ActionButton("habs", "Abmeldung eintragen", "🏖️"),
            _ => null,
        });
        items.Add(OpenButton(link));
        return DiscordCards.Row(items);
    }

    private static ButtonProperties ActionButton(string action, string label, string emoji, params string[] args)
        => DiscordCards.Button(BotIds.Make(action, args), label, emoji, ButtonStyle.Success);

    // own notification lookup
    private static bool HasDetails(NotificationType type)
        => type is NotificationType.JobAssigned or NotificationType.JobDueSoon or NotificationType.Announcement
            or NotificationType.Followup or NotificationType.Recruiting or NotificationType.Mention
            or NotificationType.SavedSearchHit or NotificationType.SituationReport or NotificationType.TaskforceChat
            or NotificationType.TestAssigned or NotificationType.TestDueReminder;

    // id from href
    internal static string? RecordId(NotificationType type, string? href)
    {
        if (string.IsNullOrWhiteSpace(href))
        {
            return null;
        }
        var path = href.Trim();
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(path, UriKind.Absolute, out var uri))
            {
                return null;
            }
            path = uri.AbsolutePath;
        }
        var cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            path = path[..cut];
        }
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || (Collection(type) is { } collection && !parts[0].Equals(collection, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }
        return IsRecordId(parts[1]) ? parts[1] : null;
    }

    // record collection
    private static string? Collection(NotificationType type) => type switch
    {
        NotificationType.JobAssigned or NotificationType.JobDueSoon => "aufgaben",
        NotificationType.Announcement or NotificationType.AcknowledgmentReminder => "brett",
        NotificationType.Recruiting => "bewerbungen",
        NotificationType.PublicTipCreated => "hinweise",
        NotificationType.PublicTicketCreated => "tickets",
        NotificationType.EmergencyAlarm => "einsatzalarme",
        _ => null,
    };

    // custom_id safe
    private static bool IsRecordId(string value)
        => value.Length is > 0 and <= MaxRecordIdChars && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static ActionRowProperties? OpenRow(string? link) => DiscordCards.Row(OpenButton(link));

    private static LinkButtonProperties? OpenButton(string? link)
        => string.IsNullOrWhiteSpace(link) || link.Length > MaxLinkChars ? null : DiscordCards.Open(link, "Öffnen");

    // ---------------------------------------------------------------- pings

    /// <summary>The first ping line for the card (up to max chips) and plain ping messages for the rest; invalid and doubled ids drop out.</summary>
    public static PingLineSet PingLines(IReadOnlyList<string> discordIds, int max = 100)
    {
        var chips = discordIds
            .Where(id => DiscordWebhookService.IsSnowflake(id) && id.Length <= MaxSnowflakeChars)
            .Distinct()
            .Select(id => $"<@{id}>")
            .ToList();
        var size = Math.Max(1, max);
        var rest = new List<string>();
        var line = new StringBuilder();
        var count = 0;
        foreach (var chip in chips.Skip(size))
        {
            // plain message limit
            if (count > 0 && (count >= size || line.Length + 1 + chip.Length > MaxPlainMessageChars))
            {
                rest.Add(line.ToString());
                line.Clear();
                count = 0;
            }
            line.Append(count > 0 ? " " : string.Empty).Append(chip);
            count++;
        }
        if (count > 0)
        {
            rest.Add(line.ToString());
        }
        return new PingLineSet(string.Join(' ', chips.Take(size)), rest);
    }

    /// <summary>A role mention; anything but a role id pings nobody.</summary>
    public static string RolePing(string roleId)
        => DiscordWebhookService.IsSnowflake(roleId) && roleId.Length <= MaxSnowflakeChars ? $"<@&{roleId}>" : string.Empty;

    // ---------------------------------------------------------------- style

    /// <summary>Accent colour, header emoji and header title of a category; the emoji is the one the notice starts with.</summary>
    public static (Color Accent, string Emoji, string Title) Style(NotificationType type)
    {
        var notice = DiscordWebhookService.Notice(type);
        var space = notice.IndexOf(' ');
        return (AccentOf(type), space > 0 ? notice[..space] : "🔔", TitleOf(type));
    }

    private static Color AccentOf(NotificationType type) => type switch
    {
        NotificationType.EmergencyAlarm or NotificationType.AgentTerminated => DiscordCards.Danger,
        NotificationType.PublicWantedPublished or NotificationType.PublicWantedBountyRaised
            or NotificationType.PublicCaptureReported => DiscordCards.Orange,
        NotificationType.DecorationAwarded => DiscordCards.Gold,
        NotificationType.PersonnelEntry or NotificationType.Announcement or NotificationType.SituationReport => DiscordCards.Bronze,
        NotificationType.JobDueSoon or NotificationType.MeetingReminder or NotificationType.TestDueReminder
            or NotificationType.Followup or NotificationType.AcknowledgmentReminder => DiscordCards.Warning,
        NotificationType.PublicTicketCreated or NotificationType.PublicTipCreated
            or NotificationType.PublicPressPublished => DiscordCards.Sand,
        _ => DiscordCards.Steel,
    };

    private static string TitleOf(NotificationType type) => type switch
    {
        NotificationType.Announcement => "Neue Ankündigung",
        NotificationType.Followup => "Wiedervorlage fällig",
        NotificationType.SituationReport => "Neuer Lagebericht",
        NotificationType.Recruiting => "Bewerbungswesen",
        NotificationType.Mention => "Neue Erwähnung",
        NotificationType.JobAssigned => "Aufgabe zugewiesen",
        NotificationType.JobDueSoon => "Aufgabe bald fällig",
        NotificationType.MeetingScheduled => "Besprechung angesetzt",
        NotificationType.MeetingReminder => "Besprechung beginnt bald",
        NotificationType.AppointmentAssigned => "Termin-Einladung",
        NotificationType.PersonnelEntry => "Personalakten-Eintrag",
        NotificationType.AgentTerminated => "Kündigung",
        NotificationType.DecorationAwarded => "Ehrung",
        NotificationType.PublicWantedPublished => "Öffentliche Fahndung",
        NotificationType.PublicWantedBountyRaised => "Kopfgeld erhöht",
        NotificationType.PublicPressPublished => "Pressemitteilung",
        NotificationType.PublicTicketCreated => "Neues Ticket",
        NotificationType.PublicCaptureReported => "Ergreifungsmeldung",
        NotificationType.PublicTipCreated => "Neuer Bürgerhinweis",
        NotificationType.PingDigest => "Tagesüberblick",
        NotificationType.AcknowledgmentReminder => "Quittierung ausstehend",
        NotificationType.SavedSearchHit => "Neue Suchtreffer",
        NotificationType.InactivityNudge => "Rückmeldung erbeten",
        NotificationType.FactionThreatBoard => "Gefahrenlage der Fraktionen",
        NotificationType.TestAssigned => "Test zugewiesen",
        NotificationType.TestDueReminder => "Test bald fällig",
        NotificationType.TaskforceChat => "Taskforce-Chat",
        NotificationType.EmergencyAlarm => "Einsatzalarm",
        _ => NotificationTypeDisplay.Name(type),
    };

    // ---------------------------------------------------------------- frame

    // cut spares ping
    private static IReadOnlyList<IMessageComponentProperties> Assemble(Color accent, string ping,
        IComponentContainerComponentProperties? header, string? body, ActionRowProperties? row)
    {
        var parts = new List<IComponentContainerComponentProperties?> { header };
        if (!string.IsNullOrWhiteSpace(body))
        {
            parts.Add(DiscordCards.Text(body));
        }
        if (row is not null)
        {
            parts.Add(DiscordCards.Line());
            parts.Add(row);
        }
        parts.Add(DiscordCards.Text(DiscordCards.Footer));
        DiscordCards.FitText([DiscordCards.Card(accent, parts)], DiscordCards.MaxText - 100 - ping.Length);
        if (!string.IsNullOrWhiteSpace(ping))
        {
            parts.Insert(0, DiscordCards.Text(ping));
        }
        return [DiscordCards.Card(accent, parts)];
    }

    private static string LogoUrl(string? baseUrl)
        => (string.IsNullOrWhiteSpace(baseUrl) ? DiscordWebhookConfig.DefaultBaseUrl : baseUrl).TrimEnd('/') + DiscordCards.Logo;

    // origin of link
    private static string LogoOf(string? link)
        => LogoUrl(link is not null && Uri.TryCreate(link, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.GetLeftPart(UriPartial.Authority)
            : null);

    // inline fields share lines
    private static string WantedBody(IReadOnlyList<WantedDiscordField> fields)
    {
        var lines = new List<string>();
        var run = new List<string>();
        foreach (var field in fields.Where(f => !string.IsNullOrWhiteSpace(f.Value)))
        {
            var multiline = field.Value.Contains('\n');
            var text = multiline ? $"**{field.Name}:**\n{field.Value}" : $"**{field.Name}:** {field.Value}";
            if (field.Inline && !multiline)
            {
                run.Add(text);
                continue;
            }
            if (run.Count > 0)
            {
                lines.Add(string.Join(" · ", run));
                run.Clear();
            }
            lines.Add(text);
        }
        if (run.Count > 0)
        {
            lines.Add(string.Join(" · ", run));
        }
        return string.Join('\n', lines);
    }

    // chips stay chips
    private static string Chip(string? value) => MentionChip(value) ?? Dash(DiscordCards.Inline(value));

    // bold name, chip beside it
    private static string Subject(string? display, string? chip)
        => $"**{Dash(DiscordCards.Inline(display, 200))}**" + (MentionChip(chip) is { } mention ? $" ( {mention} )" : string.Empty);

    // exactly a user mention, else null
    private static string? MentionChip(string? value)
    {
        var chip = value?.Trim();
        return chip is { Length: > 3 } && chip.StartsWith("<@", StringComparison.Ordinal) && chip.EndsWith('>')
            && DiscordWebhookService.IsSnowflake(chip[2..^1]) && chip.Length <= MaxSnowflakeChars + 3
                ? chip
                : null;
    }

    // typed text
    private static string Reason(string? text) => DiscordCards.Escape(DiscordCards.Clip(text?.Trim(), MaxReasonChars));

    private static string Dash(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    // Berlin local time
    private static string Stamp(DateTime value, string format)
        => (value.Kind == DateTimeKind.Utc ? TimeZoneInfo.ConvertTimeFromUtc(value, BerlinTime.Zone) : value).ToString(format, German);
}
