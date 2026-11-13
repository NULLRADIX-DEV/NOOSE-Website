using System.Globalization;
using System.Text;
using NetCord;
using NetCord.Rest;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services.Discord.Bot;

/// <summary>Building blocks of the bot's components-v2 cards: one look for every answer.</summary>
public static class DiscordCards
{
    public static readonly Color Bronze = new(0x71614A);
    public static readonly Color Sand = new(0xB5A488);
    public static readonly Color Danger = new(0xC62828);
    public static readonly Color Warning = new(0xF9A825);
    public static readonly Color Orange = new(0xEF6C00);
    public static readonly Color Success = new(0x2E7D32);
    public static readonly Color Steel = new(0x546E7A);

    /// <summary>Discord's limit on text across one components-v2 message.</summary>
    public const int MaxText = 4000;

    public const string Footer = "-# 🛡️ FIB · Federal Investigation Bureau";

    public static ComponentContainerProperties Card(Color accent, params IComponentContainerComponentProperties?[] parts)
        => new ComponentContainerProperties(parts.OfType<IComponentContainerComponentProperties>()).WithAccentColor(accent);

    public static ComponentContainerProperties Card(Color accent, IEnumerable<IComponentContainerComponentProperties?> parts)
        => new ComponentContainerProperties(parts.OfType<IComponentContainerComponentProperties>()).WithAccentColor(accent);

    public static TextDisplayProperties Text(string markdown) => new(Clip(markdown, 3800));

    /// <summary>"### emoji Title" plus a small grey subtitle line.</summary>
    public static TextDisplayProperties Header(string emoji, string title, string? subtitle = null)
        => new($"### {emoji} {Escape(title)}" + (string.IsNullOrWhiteSpace(subtitle) ? "" : "\n-# " + subtitle));

    /// <summary>Header next to a picture (record photo as attachment://, or an https url).</summary>
    public static IComponentContainerComponentProperties HeaderWithThumbnail(string emoji, string title, string? subtitle,
        string? thumbnailUrl, string? body = null)
    {
        var text = $"### {emoji} {Escape(title)}" + (string.IsNullOrWhiteSpace(subtitle) ? "" : "\n-# " + subtitle)
            + (string.IsNullOrWhiteSpace(body) ? "" : "\n" + body);
        if (string.IsNullOrEmpty(thumbnailUrl))
        {
            return new TextDisplayProperties(Clip(text, 3800));
        }
        return new ComponentSectionProperties(new ComponentSectionThumbnailProperties(new ComponentMediaProperties(thumbnailUrl)))
        {
            new TextDisplayProperties(Clip(text, 3800)),
        };
    }

    /// <summary>A line of text with a button on its right.</summary>
    public static ComponentSectionProperties WithButton(string markdown, IComponentSectionAccessoryComponentProperties button)
        => new(button) { new TextDisplayProperties(Clip(markdown, 1500)) };

    public static ComponentSeparatorProperties Line(bool large = false)
        => new ComponentSeparatorProperties().WithDivider(true).WithSpacing(large ? ComponentSeparatorSpacingSize.Large : ComponentSeparatorSpacingSize.Small);

    public static ComponentSeparatorProperties Gap()
        => new ComponentSeparatorProperties().WithDivider(false).WithSpacing(ComponentSeparatorSpacingSize.Small);

    public static ActionRowProperties? Row(params IActionRowComponentProperties?[] items)
    {
        var list = items.OfType<IActionRowComponentProperties>().Take(5).ToList();
        return list.Count == 0 ? null : new ActionRowProperties(list);
    }

    public static ActionRowProperties? Row(IEnumerable<IActionRowComponentProperties?> items) => Row(items.ToArray());

    public static LinkButtonProperties Open(string url, string label = "Auf fibhq.com öffnen")
        => new(url, label, EmojiProperties.Standard("🔗"));

    public static ButtonProperties Button(string customId, string label, string? emoji = null,
        ButtonStyle style = ButtonStyle.Secondary)
        => emoji is null ? new ButtonProperties(customId, label, style) : new ButtonProperties(customId, label, EmojiProperties.Standard(emoji), style);

    public static MediaGalleryProperties? Gallery(IEnumerable<string> urls)
    {
        var items = urls.Take(10).Select(u => new MediaGalleryItemProperties(new ComponentMediaProperties(u))).ToList();
        return items.Count == 0 ? null : new MediaGalleryProperties(items);
    }

    /// <summary>Keeps the text of a whole message under Discord's total; the longest blocks are cut first.</summary>
    public static void FitText(IEnumerable<IMessageComponentProperties> components, int budget = MaxText - 100)
    {
        var texts = new List<TextDisplayProperties>();
        void Walk(IEnumerable<object> items)
        {
            foreach (var item in items)
            {
                switch (item)
                {
                    case TextDisplayProperties text:
                        texts.Add(text);
                        break;
                    case ComponentContainerProperties container:
                        Walk(container.Components);
                        break;
                    case ComponentSectionProperties section:
                        Walk(section.Components);
                        break;
                }
            }
        }
        Walk(components);
        var total = texts.Sum(t => t.Content?.Length ?? 0);
        while (total > budget)
        {
            var longest = texts.MaxBy(t => t.Content?.Length ?? 0);
            if (longest?.Content is not { Length: > 40 } content)
            {
                break;
            }
            var cut = Math.Min(content.Length - 20, total - budget + 2);
            longest.Content = Clip(content, content.Length - cut);
            total -= cut;
        }
    }

    /// <summary>A one-line notice card (errors, confirmations).</summary>
    public static ComponentContainerProperties Notice(string emoji, string text, Color accent)
        => Card(accent, Text($"{emoji} {text}"));

    /// <summary>"**Label:** value" lines; empty values are skipped.</summary>
    public static string Fields(params (string Label, string? Value)[] fields)
        => string.Join("\n", fields.Where(f => !string.IsNullOrWhiteSpace(f.Value)).Select(f => $"**{f.Label}:** {f.Value}"));

    /// <summary>Discord timestamp that every client shows in its own locale; R relative, f date and time, d date.</summary>
    public static string When(DateTime utc, char style = 'R')
    {
        var value = DateTime.SpecifyKind(utc, utc.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : utc.Kind);
        return $"<t:{new DateTimeOffset(value.ToUniversalTime()).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)}:{style}>";
    }

    /// <summary>A Berlin calendar day as a Discord date stamp.</summary>
    public static string Day(DateOnly day) => When(BerlinTime.ToUtc(day, new TimeOnly(12, 0)), 'D');

    public static string Money(decimal amount)
        => "$" + amount.ToString("#,0", CultureInfo.GetCultureInfo("de-DE"));

    /// <summary>User text never formats the card or pings anyone.</summary>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        var builder = new StringBuilder(text.Length + 8);
        foreach (var c in text)
        {
            if (c is '\\' or '*' or '_' or '~' or '`' or '|' or '>' or '#' or '[' or ']' or '(' or ')' or '<' or '@' or '-')
            {
                builder.Append('\\');
            }
            builder.Append(c);
        }
        return builder.ToString();
    }

    /// <summary>Single-line, escaped and cut.</summary>
    public static string Inline(string? text, int max = 200) => Escape(Clip(OneLine(text), max));

    public static string OneLine(string? text) => (text ?? string.Empty).ReplaceLineEndings(" ").Trim();

    public static string Clip(string? text, int max)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= max)
        {
            return text ?? string.Empty;
        }
        return text[..Math.Max(0, max - 1)].TrimEnd() + "…";
    }

    /// <summary>Plain text from stored HTML, for previews.</summary>
    public static string PlainFromHtml(string? html, int max)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }
        var withBreaks = System.Text.RegularExpressions.Regex.Replace(html, @"<\s*(br|/p|/li|/h\d|/div)\s*/?>", "\n",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var text = System.Text.RegularExpressions.Regex.Replace(withBreaks, "<[^>]+>", string.Empty);
        text = System.Net.WebUtility.HtmlDecode(text);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\n{3,}", "\n\n").Trim();
        return Clip(MentionParser.Strip(text), max);
    }

    public static Color ForClassification(Classification classification) => classification switch
    {
        Classification.ReviewCase => Warning,
        Classification.SuspicionCase => Orange,
        Classification.SecuredStateThreatening => Danger,
        _ => Bronze,
    };

    public static string ClassificationEmoji(Classification classification) => classification switch
    {
        Classification.ReviewCase => "🟡",
        Classification.SuspicionCase => "🟠",
        Classification.SecuredStateThreatening => "🔴",
        _ => "⚪",
    };

    public static Color ForSituation(PublicSituationLevel level)
        => int.TryParse(PublicSituationLevelDisplay.Hex(level).TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)
            ? new Color(rgb)
            : Bronze;

    public static string SituationEmoji(PublicSituationLevel level) => level switch
    {
        PublicSituationLevel.Niedrig => "🟢",
        PublicSituationLevel.Erhoeht => "🟡",
        PublicSituationLevel.Hoch => "🟠",
        PublicSituationLevel.Schwer => "🔴",
        _ => "🚨",
    };

    /// <summary>DEFCON number of a level (Niedrig = 5 … Kritisch = 1).</summary>
    public static int Defcon(PublicSituationLevel level) => 5 - (int)level;

    public static string HazardEmoji(HazardLevel level) => level switch
    {
        HazardLevel.Critical => "🔴",
        HazardLevel.High => "🟠",
        HazardLevel.Medium => "🟡",
        HazardLevel.Low => "🟢",
        _ => "⚪",
    };

    public static string LifeEmoji(LifeStatus status) => status switch
    {
        LifeStatus.Dead => "⚰️",
        LifeStatus.Fugitive => "🏃",
        _ => "🫀",
    };

    public static string DivisionEmoji(Division? division) => division switch
    {
        Division.Cid => "🕵️",
        Division.Ctd => "🛰️",
        Division.Hrt => "🎯",
        _ => "🛡️",
    };
}

/// <summary>Europe/Berlin, the agency's clock.</summary>
public static class BerlinTime
{
    public static readonly TimeZoneInfo Zone = Find();

    public static DateOnly Today(DateTime utcNow) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utcNow, Zone));

    public static DateTime ToUtc(DateOnly day, TimeOnly time)
    {
        var local = DateTime.SpecifyKind(day.ToDateTime(time), DateTimeKind.Unspecified);
        // the hour skipped at the spring change does not exist
        if (Zone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }
        return TimeZoneInfo.ConvertTimeToUtc(local, Zone);
    }

    private static TimeZoneInfo Find()
    {
        foreach (var id in new[] { "Europe/Berlin", "W. Europe Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch { /* ignore */ }
        }
        return TimeZoneInfo.Local;
    }
}
