using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Services.Search;

namespace NOOSE_Website.Services.Discord.Bot;

/// <summary>One interaction as the command code sees it.</summary>
public sealed class BotCall
{
    public required BotContext Context { get; init; }
    public required DiscordActor Actor { get; init; }
    public required BotOptions Options { get; init; }

    /// <summary>The answer goes to the channel for everyone (teilen); otherwise only the caller sees it.</summary>
    public bool Shared { get; init; }

    public CancellationToken Ct { get; init; }

    public ClaimsPrincipal User => Actor.Principal;
    public string AgentId => Actor.Agent.Id;
    public ViewerScope Scope => ViewerScope.From(Actor.Principal);
}

/// <summary>A record named in a command: picked from autocomplete ("Typ:Id") or typed.</summary>
public sealed record BotRecordRef(string Type, string Id, string Name, string? CaseNumber);

/// <summary>Shared lookups for the command modules: record names, links, access log.</summary>
public sealed class BotRecords(ISearchService search, IDiscordWebhookService webhook, IAccessLogService accessLog,
    IDbContextFactory<AppDbContext> dbFactory)
{
    private string? _baseUrl;

    /// <summary>Agents a picker may offer (the one rule in AgentSelection).</summary>
    public async Task<List<(string Id, string Codename)>> SelectableAgentsAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return (await db.Users.AsNoTracking().OnlySelectable()
                .Where(u => u.Codename != "")
                .Select(u => new { u.Id, u.Codename })
                .ToListAsync(ct))
            .Where(u => u.Id != Infrastructure.DemoIdentity.AgentId)
            .Select(u => (u.Id, u.Codename))
            .ToList();
    }

    /// <summary>Quick-search hits for a typed record name, filtered to the given types.</summary>
    public async Task<List<QuickHit>> CandidatesAsync(string? input, BotCall call, IReadOnlyCollection<string> onlyTypes)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }
        return (await search.QuickSearchAsync(CaseNumbers.Normalize(text) ?? text, call.User, 20, call.Ct))
            .Where(h => onlyTypes.Contains(h.Category))
            .ToList();
    }

    public async Task<string> BaseUrlAsync(CancellationToken ct)
        => _baseUrl ??= (await webhook.GetConfigAsync(ct)).SiteBaseUrl.TrimEnd('/');

    public async Task<string> LinkAsync(string path, CancellationToken ct) => await BaseUrlAsync(ct) + path;

    public async Task<string?> RecordLinkAsync(string type, string id, CancellationToken ct)
        => SearchCatalog.Route(type, id) is { } route ? await BaseUrlAsync(ct) + route : null;

    /// <summary>Discord lookups count in the access log under their own type, so they are no visit of the record.</summary>
    public async Task LogAsync(string type, string id)
    {
        try
        {
            await accessLog.LogViewAsync("Discord", ChannelShareRules.LogEntry(type, id));
        }
        catch { /* best effort */ }
    }

    /// <summary>Autocomplete value "Typ:Id", or a case number / name resolved through the quick search.</summary>
    public async Task<(BotRecordRef? Record, string? Problem)> ResolveAsync(string? input, BotCall call,
        IReadOnlyCollection<string>? onlyTypes = null)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return (null, "Bitte eine Akte angeben.");
        }
        var cut = text.IndexOf(':');
        if (cut > 0 && SearchCatalog.Find(text[..cut]) is not null && text.Length - cut > 10)
        {
            var type = text[..cut];
            var id = text[(cut + 1)..];
            if (onlyTypes is not null && !onlyTypes.Contains(type))
            {
                return (null, "Diese Akte passt hier nicht.");
            }
            // the picked id is re-checked by the service that reads it
            return (new BotRecordRef(type, id, string.Empty, null), null);
        }

        var normalized = CaseNumbers.Normalize(text) ?? text;
        var hits = await search.QuickSearchAsync(normalized, call.User, 12, call.Ct);
        if (onlyTypes is not null)
        {
            hits = hits.Where(h => onlyTypes.Contains(h.Category)).ToList();
        }
        var exact = hits.FirstOrDefault(h => string.Equals(h.CaseNumber, normalized, StringComparison.OrdinalIgnoreCase))
            ?? (hits.Count(h => string.Equals(h.Name, text, StringComparison.OrdinalIgnoreCase)) == 1
                ? hits.First(h => string.Equals(h.Name, text, StringComparison.OrdinalIgnoreCase))
                : null)
            ?? (hits.Count == 1 ? hits[0] : null);
        if (exact is not null)
        {
            return (new BotRecordRef(exact.Category, exact.TargetId, exact.Name, exact.CaseNumber), null);
        }
        if (hits.Count == 0)
        {
            return (null, $"Keine Akte zu „{DiscordCards.Inline(text, 60)}“ gefunden.");
        }
        var options = string.Join("\n", hits.Take(6).Select(h => $"• {DiscordCards.Inline(h.Name, 60)} `{h.CaseNumber}`"));
        return (null, $"Mehrere Treffer, bitte aus der Vorschlagsliste wählen oder genauer suchen:\n{options}");
    }

    public static string Emoji(string type) => type switch
    {
        "Person" => "👤",
        "Faction" => "🏴",
        "Case" => "📁",
        "Operation" => "🎯",
        "Document" => "📄",
        "Taskforce" => "🛡️",
        "Party" => "🏛️",
        "PersonGroup" => "👥",
        "Law" => "⚖️",
        "Job" => "📌",
        "Meeting" => "🗓️",
        _ => "🗂️",
    };

    /// <summary>Autocomplete choices for a record field.</summary>
    public async Task<BotReply> SuggestAsync(string? typed, BotCall call, IReadOnlyCollection<string>? onlyTypes = null)
    {
        var text = typed?.Trim() ?? string.Empty;
        if (text.Length < 2)
        {
            return BotReply.Autocomplete([]);
        }
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(call.Ct);
        budget.CancelAfter(TimeSpan.FromSeconds(2));
        List<QuickHit> hits;
        try
        {
            hits = await search.QuickSearchAsync(CaseNumbers.Normalize(text) ?? text, call.User, 20, budget.Token);
        }
        catch (OperationCanceledException)
        {
            return BotReply.Autocomplete([]);
        }
        return BotReply.Autocomplete(hits
            .Where(h => onlyTypes is null || onlyTypes.Contains(h.Category))
            .Select(h => new NetCord.Rest.ApplicationCommandOptionChoiceProperties(
                DiscordCards.Clip($"{RecordTypeDisplay.Name(h.Category)}: {DiscordCards.OneLine(h.Name)} ({h.CaseNumber})", 100),
                DiscordCards.Clip($"{h.Category}:{h.TargetId}", 100))));
    }
}

/// <summary>Typo-tolerant matching for the bot's small lists (codenames, law books, radio channels, routes).</summary>
public static class BotFuzzy
{
    /// <summary>Lower is better: 0 prefix, 1 contains, 2 + edit distance for a fuzzy hit; null means no match.</summary>
    public static int? Score(string? typed, params string?[] candidates)
    {
        var text = typed?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }
        int? best = null;
        var words = TextSimilarity.Tokens(text);
        foreach (var candidate in candidates.Where(c => !string.IsNullOrWhiteSpace(c)))
        {
            int? score = candidate!.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 0
                : candidate.Contains(text, StringComparison.OrdinalIgnoreCase) ? 1
                : words.Count > 0 && TextSimilarity.PhraseSimilar(words, TextSimilarity.Tokens(candidate), out var distance) ? 2 + distance
                : null;
            if (score is { } s && (best is null || s < best))
            {
                best = s;
            }
        }
        return best;
    }

    /// <summary>Matching items, best first.</summary>
    public static IEnumerable<T> Rank<T>(IEnumerable<T> items, string? typed, Func<T, string?[]> texts)
        => items.Select(item => (Item: item, Score: Score(typed, texts(item))))
            .Where(x => x.Score is not null)
            .OrderBy(x => x.Score)
            .Select(x => x.Item);

    /// <summary>The one clear best match: an exact hit, or a single item with the best score.</summary>
    public static T? Single<T>(IEnumerable<T> items, string? typed, Func<T, string?[]> texts) where T : class
    {
        var scored = items.Select(item => (Item: item, Score: Score(typed, texts(item)))).Where(x => x.Score is not null).ToList();
        if (scored.Count == 0)
        {
            return null;
        }
        var exact = scored.Where(x => texts(x.Item).Any(t => string.Equals(t, typed?.Trim(), StringComparison.OrdinalIgnoreCase))).ToList();
        if (exact.Count == 1)
        {
            return exact[0].Item;
        }
        var top = scored.Min(x => x.Score);
        var best = scored.Where(x => x.Score == top).ToList();
        return best.Count == 1 ? best[0].Item : null;
    }
}
