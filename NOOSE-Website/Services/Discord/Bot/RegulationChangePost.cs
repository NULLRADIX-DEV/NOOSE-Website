using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NetCord;
using NetCord.Rest;
using NOOSE_Website.Data;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services.Discord.Bot;

/// <summary>What happened to one paragraph since the last post.</summary>
public enum RegulationChangeKind
{
    Introduced,
    Changed,
    Correction,
    Repealed,
}

/// <summary>One paragraph's change since the last post: the diff for a change, the text for a new one, the reason for a repeal.</summary>
public sealed record RegulationChange(string Label, RegulationChangeKind Kind, string? DiffHtml, string? ContentHtml,
    string? Note, string? Reason, IReadOnlyList<string> PublisherIds);

/// <summary>The collected regulation post in the style of the hand-written ones; pure, no clock and no database.</summary>
public static partial class RegulationChangePost
{
    /// <summary>Quiet time after the last change before the post goes out.</summary>
    public static readonly TimeSpan QuietFor = TimeSpan.FromMinutes(15);

    // FitText's default budget
    private const int Budget = DiscordCards.MaxText - 100;

    // Text's clip
    private const int MaxBlock = 3800;

    /// <summary>One component list per Discord message; only the first greets the role, only the last signs and has buttons.</summary>
    public static IReadOnlyList<IReadOnlyList<IMessageComponentProperties>> Render(IReadOnlyList<RegulationChange> changes,
        string roleId, IReadOnlyList<(string Chip, string Rank)> signers, string baseUrl)
    {
        if (changes.Count == 0)
        {
            return [];
        }
        var greeting = string.IsNullOrEmpty(roleId) ? "Liebes FIB," : $"Liebes <@&{roleId}>,";
        var blocks = new List<string> { $"# Änderung der Dienstverordnung\n{greeting}\nhiermit ergeht eine Änderung der Dienstverordnung." };
        blocks.AddRange(changes.Where(c => c.Kind != RegulationChangeKind.Correction).Select(Block));
        var corrections = changes.Where(c => c.Kind == RegulationChangeKind.Correction).ToList();
        if (corrections.Count > 0)
        {
            blocks.Add("**Sonstiges**\n" + string.Join("\n", corrections.Select(c => "> " + DiscordCards.Inline(c.Label, 300)
                + (string.IsNullOrWhiteSpace(c.Note) ? "" : ": " + DiscordCards.Inline(c.Note, RegulationRules.MaxChangeNote)))));
        }

        // paragraph boundaries only
        var messages = new List<List<string>> { new() };
        var size = 0;
        foreach (var block in blocks.Select(b => DiscordCards.Clip(b, MaxBlock)))
        {
            if (messages[^1].Count > 0 && size + block.Length > Budget)
            {
                messages.Add([]);
                size = 0;
            }
            messages[^1].Add(block);
            size += block.Length;
        }
        var closing = Closing(changes.Any(c => c.Kind == RegulationChangeKind.Changed), signers);
        if (size + closing.Length > Budget)
        {
            messages.Add([]);
        }

        var result = new List<IReadOnlyList<IMessageComponentProperties>>();
        for (var i = 0; i < messages.Count; i++)
        {
            var parts = messages[i].Select(b => (IComponentContainerComponentProperties?)DiscordCards.Text(b)).ToList();
            if (i == messages.Count - 1)
            {
                parts.Add(DiscordCards.Line());
                parts.Add(DiscordCards.Text(closing));
                parts.Add(DiscordCards.Row(
                    DiscordCards.Button(BotIds.Make("hub", "ack"), "Zur Kenntnis nehmen", "✍️", ButtonStyle.Primary),
                    DiscordCards.Open($"{baseUrl}/dienstverordnung", "Öffnen")));
            }
            IMessageComponentProperties[] message = [DiscordCards.Card(DiscordCards.Bronze, parts)];
            DiscordCards.FitText(message);
            result.Add(message);
        }
        return result;
    }

    private static string Block(RegulationChange change) => change.Kind switch
    {
        RegulationChangeKind.Introduced
            => $"**Neueinführung: {DiscordCards.Inline(change.Label, 300)}**\n{Quote(Markdown(change.ContentHtml))}".TrimEnd(),
        RegulationChangeKind.Repealed
            => $"**Aufgehoben: {DiscordCards.Inline(change.Label, 300)}**\n{Quote(DiscordCards.Escape(change.Reason))}".TrimEnd(),
        _ => $"### {DiscordCards.Inline(change.Label, 300)}\n{Markdown(change.DiffHtml)}".TrimEnd(),
    };

    private static string Closing(bool hasDiff, IReadOnlyList<(string Chip, string Rank)> signers)
    {
        var text = new StringBuilder();
        if (hasDiff)
        {
            text.Append("**Fett:** Neuerung / Änderung\n~~Durchgestrichen:~~ Entfernungen\n\n");
        }
        text.Append("Bitte lest euch die Punkte durch.\n\nMit freundlichen Grüßen");
        if (signers.Count == 0)
        {
            text.Append("\nFederal Investigation Bureau");
        }
        foreach (var (chip, rank) in signers)
        {
            text.Append('\n').Append(chip);
            if (!string.IsNullOrWhiteSpace(rank))
            {
                text.Append(" | ").Append(DiscordCards.Inline(rank, 60));
            }
        }
        return text.ToString();
    }

    private static string Quote(string text)
        => string.Join("\n", text.Split('\n').Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => "> " + l));

    [GeneratedRegex(@"<[^>]*>|[^<]+")]
    private static partial Regex Pieces();

    [GeneratedRegex(@"^<\s*(/?)\s*([a-zA-Z][a-zA-Z0-9]*)")]
    private static partial Regex TagName();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Discord Markdown from stored or diffed HTML: ins bold, del struck, blocks as lines, everything else plain and escaped.</summary>
    public static string Markdown(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }
        var lines = new List<StringBuilder> { new() };
        var inserted = false;
        var deleted = false;
        foreach (Match piece in Pieces().Matches(MentionParser.Strip(html)))
        {
            if (piece.Value[0] != '<')
            {
                var text = Whitespace().Replace(WebUtility.HtmlDecode(piece.Value), " ");
                Append(lines[^1], text, inserted ? "**" : deleted ? "~~" : null);
                continue;
            }
            var tag = TagName().Match(piece.Value);
            if (!tag.Success)
            {
                continue;
            }
            var closing = tag.Groups[1].Value == "/";
            switch (tag.Groups[2].Value.ToLowerInvariant())
            {
                case "ins":
                    inserted = !closing;
                    break;
                case "del":
                    deleted = !closing;
                    break;
                case "br":
                    lines.Add(new());
                    break;
                case "li" when !closing:
                    lines.Add(new StringBuilder("- "));
                    break;
                case "p" or "div" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "blockquote" or "tr" or "ul" or "ol"
                    when closing:
                    lines.Add(new());
                    break;
            }
        }

        // collapse blank lines
        var result = new StringBuilder();
        var blank = false;
        foreach (var line in lines.Select(l => l.ToString().Trim()))
        {
            if (line.Length == 0 || line == "-")
            {
                blank = result.Length > 0;
                continue;
            }
            if (result.Length > 0)
            {
                result.Append(blank ? "\n\n" : "\n");
            }
            result.Append(line);
            blank = false;
        }
        return result.ToString();
    }

    // marks hug the words
    private static void Append(StringBuilder line, string text, string? mark)
    {
        var core = text.Trim();
        if (mark is null || core.Length == 0)
        {
            line.Append(DiscordCards.Escape(text));
            return;
        }
        var lead = text.Length - text.TrimStart().Length;
        var trail = text.Length - text.TrimEnd().Length;
        line.Append(text, 0, lead).Append(mark).Append(DiscordCards.Escape(core)).Append(mark).Append(text, text.Length - trail, trail);
    }
}

/// <summary>Reads the regulation events since the last post and keeps the post state; the database half of the post.</summary>
public interface IRegulationChangeCollector
{
    /// <summary>One entry per paragraph a plain internal agent reads, in document order, and the newest event time.</summary>
    Task<(IReadOnlyList<RegulationChange> Changes, DateTime? NewestUtc)> CollectAsync(DateTime sinceUtc,
        CancellationToken cancellationToken = default);

    /// <summary>The publishers of the changes as mention chip or escaped name, with their rank.</summary>
    Task<IReadOnlyList<(string Chip, string Rank)>> SignersAsync(IReadOnlyList<RegulationChange> changes,
        CancellationToken cancellationToken = default);

    /// <summary>UTC time of the last posted event, or null before the first run.</summary>
    Task<DateTime?> GetStateAsync(CancellationToken cancellationToken = default);

    /// <summary>Stores the state without an audit row.</summary>
    Task SetStateAsync(DateTime newestUtc, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IRegulationChangeCollector" />
public sealed class RegulationChangeCollector(IDbContextFactory<AppDbContext> dbFactory) : IRegulationChangeCollector
{
    // plain internal agent
    private static readonly ViewerScope PlainAgent = new(false, false, null, null, IsInternalAgent: true);

    public async Task<(IReadOnlyList<RegulationChange> Changes, DateTime? NewestUtc)> CollectAsync(DateTime sinceUtc,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var paragraphs = await db.RegulationParagraphs.AsNoTracking().OnlyVisible(db, PlainAgent)
            .Where(p => p.RepealedAt > sinceUtc
                || db.RegulationVersions.Any(v => v.ParagraphId == p.Id && v.PublishedAt > sinceUtc))
            .Select(p => new { p.Id, p.SectionId, p.SortOrder, p.Heading, p.RepealedAt, p.RepealedById, p.RepealReason })
            .ToListAsync(cancellationToken);
        if (paragraphs.Count == 0)
        {
            return ([], null);
        }
        var ids = paragraphs.Select(p => p.Id).ToList();
        var versions = (await db.RegulationVersions.AsNoTracking()
            .Where(v => ids.Contains(v.ParagraphId))
            .Select(v => new { v.ParagraphId, v.Version, v.ContentHtml, v.ChangeNote, v.RequiresAcknowledgment, v.PublishedById, v.PublishedAt })
            .ToListAsync(cancellationToken))
            .ToLookup(v => v.ParagraphId);
        var labels = await RegulationVisibility.LabelsAsync(db, ids, cancellationToken);
        var numbers = await RegulationVisibility.SectionNumbersAsync(db, cancellationToken);

        var changes = new List<RegulationChange>();
        DateTime? newest = null;
        // document order
        foreach (var p in paragraphs.OrderBy(p => numbers.GetValueOrDefault(p.SectionId).Number ?? 0)
                     .ThenBy(p => p.SortOrder).ThenBy(p => p.Heading, StringComparer.Ordinal))
        {
            var all = versions[p.Id].OrderBy(v => v.Version).ToList();
            var after = all.Where(v => v.PublishedAt > sinceUtc).ToList();
            var before = all.LastOrDefault(v => v.PublishedAt <= sinceUtc);
            var repealed = p.RepealedAt > sinceUtc;
            var times = after.Select(v => v.PublishedAt).ToList();
            if (repealed)
            {
                times.Add(p.RepealedAt!.Value);
            }
            if (times.Count > 0 && (newest is null || times.Max() > newest))
            {
                newest = times.Max();
            }

            var label = labels.GetValueOrDefault(p.Id) ?? p.Heading;
            var publishers = after.Select(v => v.PublishedById).OfType<string>().Where(id => id.Length > 0).Distinct().ToList();
            var notes = string.Join("; ", after.Select(v => v.ChangeNote.Trim()).Where(n => n.Length > 0).Distinct());
            if (repealed)
            {
                // never announced
                if (before is not null)
                {
                    changes.Add(new RegulationChange(label, RegulationChangeKind.Repealed, null, null, null, p.RepealReason,
                        string.IsNullOrEmpty(p.RepealedById) ? [] : [p.RepealedById]));
                }
            }
            else if (after.Count == 0)
            {
                continue;
            }
            else if (before is null)
            {
                changes.Add(new RegulationChange(label, RegulationChangeKind.Introduced, null, after[^1].ContentHtml,
                    notes.Length > 0 ? notes : null, null, publishers));
            }
            else if (after.Any(v => v.RequiresAcknowledgment))
            {
                changes.Add(new RegulationChange(label, RegulationChangeKind.Changed,
                    HtmlDiff.Compare(before.ContentHtml, after[^1].ContentHtml).Html, null, notes.Length > 0 ? notes : null, null,
                    publishers));
            }
            else
            {
                changes.Add(new RegulationChange(label, RegulationChangeKind.Correction, null, null,
                    notes.Length > 0 ? notes : null, null, publishers));
            }
        }
        return (changes, newest is { } last ? DateTime.SpecifyKind(last, DateTimeKind.Utc) : null);
    }

    public async Task<IReadOnlyList<(string Chip, string Rank)>> SignersAsync(IReadOnlyList<RegulationChange> changes,
        CancellationToken cancellationToken = default)
    {
        var ids = changes.SelectMany(c => c.PublisherIds).Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agents = await db.Users.AsNoTracking().IgnoreQueryFilters()
            .Where(a => ids.Contains(a.Id))
            .Select(a => new { a.Id, a.DiscordId, a.Rank, a.Codename })
            .ToDictionaryAsync(a => a.Id, cancellationToken);
        // newest name snapshot
        var published = await db.RegulationVersions.AsNoTracking()
            .Where(v => v.PublishedById != null && ids.Contains(v.PublishedById!))
            .Select(v => new { Id = v.PublishedById!, Name = v.PublishedByName, At = v.PublishedAt })
            .ToListAsync(cancellationToken);
        var repealed = await db.RegulationParagraphs.AsNoTracking().IgnoreQueryFilters()
            .Where(p => p.RepealedById != null && p.RepealedAt != null && ids.Contains(p.RepealedById!))
            .Select(p => new { Id = p.RepealedById!, Name = p.RepealedByName, At = p.RepealedAt!.Value })
            .ToListAsync(cancellationToken);
        var names = published.Concat(repealed)
            .Where(n => !string.IsNullOrWhiteSpace(n.Name))
            .GroupBy(n => n.Id)
            .ToDictionary(g => g.Key, g => g.MaxBy(n => n.At)!.Name!);

        return ids.Select(id =>
        {
            var agent = agents.GetValueOrDefault(id);
            var chip = DiscordBotSettingsService.ParseId(agent?.DiscordId) is { } discordId
                ? $"<@{discordId.ToString(CultureInfo.InvariantCulture)}>"
                : DiscordCards.Inline(names.GetValueOrDefault(id) ?? agent?.Codename ?? "Unbekannt", 60);
            return (chip, agent?.Rank is { } rank ? RankDisplay.Name(rank) : "");
        }).ToList();
    }

    public async Task<DateTime?> GetStateAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var value = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == SystemSettingKeys.DiscordDvoState)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);
        return DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;
    }

    // no audit row
    public async Task SetStateAsync(DateTime newestUtc, CancellationToken cancellationToken = default)
    {
        var key = SystemSettingKeys.DiscordDvoState;
        var value = DateTime.SpecifyKind(newestUtc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var updated = await db.SystemSettings.Where(s => s.Key == key)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.Value, value), cancellationToken);
        if (updated == 0)
        {
            var now = DateTime.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO SystemEinstellungen (Schluessel, Wert, ErstelltAm) VALUES ({key}, {value}, {now})", cancellationToken);
        }
    }
}
