using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Leads;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Leads;

namespace NOOSE_Website.Services;

/// <summary>Proactive, algorithmic investigation leads (link-prediction, new conflicts, stale high-classification, possible duplicates). Read-mostly.</summary>
public interface ILeadService
{
    Task<IReadOnlyList<LeadGroup>> GetFeedAsync(ClaimsPrincipal viewer, CancellationToken cancellationToken = default);
    Task IgnoreAsync(string leadKey, LeadKind kind, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task UndoIgnoreAsync(string leadKey, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ILeadService" />
public class LeadService(IDbContextFactory<AppDbContext> dbFactory, IMemoryCache cache) : ILeadService
{
    private const string CacheKey = "leads:raw:v1";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private const int MaxPerKind = 20;
    private const int HubDegreeCap = 60; // skip super-hubs to bound O(deg^2)
    private const int StaleDays = 30;
    private const int ConflictWindowDays = 14;

    public async Task<IReadOnlyList<LeadGroup>> GetFeedAsync(ClaimsPrincipal viewer, CancellationToken cancellationToken = default)
    {
        var scope = ViewerScope.From(viewer);
        if (scope.IsPartner)
        {
            return Array.Empty<LeadGroup>();
        }
        var mayVs = viewer.MayClassifiedRead();

        var raw = await cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheTtl;
            return await ComputeRawAsync(cancellationToken);
        }) ?? new List<Lead>();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var dismissed = (await db.LeadDismissals.Select(d => d.LeadKey).ToListAsync(cancellationToken)).ToHashSet();

        var visible = raw.Where(l => !dismissed.Contains(l.Key) && (!l.Classified || mayVs));

        return visible
            .GroupBy(l => l.Kind)
            .Select(g => new LeadGroup(g.Key, g.OrderByDescending(l => l.Score).ThenBy(l => l.Title).Take(MaxPerKind).ToList()))
            .OrderBy(g => (int)g.Kind)
            .ToList();
    }

    public async Task IgnoreAsync(string leadKey, LeadKind kind, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        if (string.IsNullOrWhiteSpace(leadKey))
        {
            return;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.LeadDismissals.IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.LeadKey == leadKey, cancellationToken);
        if (existing is null)
        {
            db.LeadDismissals.Add(new LeadDismissal { LeadKey = leadKey, Kind = kind });
        }
        else
        {
            existing.IsDeleted = false;
            existing.DeletedAt = null;
            existing.DeletedById = null;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task UndoIgnoreAsync(string leadKey, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.LeadDismissals.FirstOrDefaultAsync(d => d.LeadKey == leadKey, cancellationToken);
        if (existing is null)
        {
            return;
        }
        existing.IsDeleted = true;
        existing.DeletedAt = DateTime.UtcNow;
        existing.DeletedById = actor.GetAgentId();
        await db.SaveChangesAsync(cancellationToken);
    }

    // viewer-independent candidate set (cached); VS + dismissal are applied per request
    private async Task<List<Lead>> ComputeRawAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var leads = new List<Lead>();
        await AddLinkPredictionAsync(db, leads, cancellationToken);
        await AddNewConflictAsync(db, leads, cancellationToken);
        await AddStaleHighClassificationAsync(db, leads, cancellationToken);
        await AddDuplicatesAsync(db, leads, cancellationToken);
        return leads;
    }

    private sealed record DuplicateCandidate(int Person, PreparedName Name, string? Alias);

    // name against name or alias, never alias against alias (shared nicknames are noise); plus shared phone numbers
    private static async Task AddDuplicatesAsync(AppDbContext db, List<Lead> leads, CancellationToken ct)
    {
        var people = await db.People
            .Select(p => new { p.Id, p.Name, p.CaseNumber, p.IsClassified, p.IsArchived, p.CreatedAt })
            .ToListAsync(ct);
        if (people.Count < 2)
        {
            return;
        }
        var index = people.Select((p, i) => (p.Id, i)).ToDictionary(x => x.Id, x => x.i);

        var candidates = people.Select((p, i) => new DuplicateCandidate(i, PersonNameMatch.Prepare(p.Name), null)).ToList();
        foreach (var a in await db.PersonAliases.Select(a => new { a.PersonId, a.AliasName }).ToListAsync(ct))
        {
            if (index.TryGetValue(a.PersonId, out var i))
            {
                candidates.Add(new DuplicateCandidate(i, PersonNameMatch.Prepare(a.AliasName), a.AliasName));
            }
        }

        var pairs = new Dictionary<(int A, int B), (DuplicateReason? Name, string? Alias, bool Phone)>();
        (int, int) Pair(int x, int y) => x < y ? (x, y) : (y, x);

        // equal word counts only; blocking keys spare the all-vs-all compare
        foreach (var bucket in candidates.GroupBy(c => c.Name.WordCount).Where(g => g.Key > 0))
        {
            var list = bucket.ToList();
            var byKey = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (var i = 0; i < list.Count; i++)
            {
                foreach (var k in PersonNameMatch.BlockingKeys(list[i].Name))
                {
                    if (!byKey.TryGetValue(k, out var holders))
                    {
                        byKey[k] = holders = new List<int>();
                    }
                    holders.Add(i);
                }
            }

            for (var i = 0; i < list.Count; i++)
            {
                var partners = PersonNameMatch.BlockingKeys(list[i].Name, firstWordOnly: true)
                    .SelectMany(k => byKey[k])
                    .Where(j => j > i)
                    .Distinct();
                foreach (var j in partners)
                {
                    var (x, y) = (list[i], list[j]);
                    if (x.Person == y.Person || (x.Alias is not null && y.Alias is not null)
                        || PersonNameMatch.Compare(x.Name, y.Name) is not { } reason)
                    {
                        continue;
                    }
                    var key = Pair(x.Person, y.Person);
                    var current = pairs.GetValueOrDefault(key);
                    if (current.Name is null || reason < current.Name)
                    {
                        pairs[key] = (reason, x.Alias ?? y.Alias, current.Phone);
                    }
                }
            }
        }

        var phones = await db.PersonPhones.Select(t => new { t.PersonId, t.Number }).ToListAsync(ct);
        foreach (var shared in phones
                     .Where(t => index.ContainsKey(t.PersonId) && !string.IsNullOrWhiteSpace(t.Number))
                     .GroupBy(t => t.Number.Trim())
                     .Select(g => g.Select(t => index[t.PersonId]).Distinct().ToList())
                     .Where(ids => ids.Count > 1))
        {
            for (var i = 0; i < shared.Count; i++)
            {
                for (var j = i + 1; j < shared.Count; j++)
                {
                    var key = Pair(shared[i], shared[j]);
                    pairs[key] = pairs.GetValueOrDefault(key) with { Phone = true };
                }
            }
        }

        foreach (var ((ai, bi), match) in pairs)
        {
            // the older record is the natural merge target, so it goes first
            var (older, newer) = (people[ai], people[bi]);
            if (newer.CreatedAt < older.CreatedAt
                || (newer.CreatedAt == older.CreatedAt && string.CompareOrdinal(newer.CaseNumber, older.CaseNumber) < 0))
            {
                (older, newer) = (newer, older);
            }

            var reasons = new List<string>();
            if (match.Name is { } name)
            {
                reasons.Add(DuplicateReasonDisplay.Name(name) + (match.Alias is null ? string.Empty : $" (Alias „{match.Alias}“)"));
            }
            if (match.Phone)
            {
                reasons.Add(DuplicateReasonDisplay.Name(DuplicateReason.SamePhone));
            }
            var on = Display(older.Name, older.CaseNumber);
            var nn = Display(newer.Name, newer.CaseNumber);
            var detail = $"{on} und {nn}: {string.Join(", ", reasons)}.";
            foreach (var archived in new[] { older, newer }.Where(p => p.IsArchived))
            {
                detail += $" {archived.Name} ist archiviert.";
            }

            leads.Add(new Lead(
                LeadKind.Duplicate,
                string.CompareOrdinal(older.Id, newer.Id) < 0 ? $"DU|{older.Id}|{newer.Id}" : $"DU|{newer.Id}|{older.Id}",
                "Mögliche Dublette",
                detail,
                (match.Phone ? 3 : 0) + match.Name switch
                {
                    DuplicateReason.SameName => 3,
                    DuplicateReason.SoundsAlike => 2,
                    DuplicateReason.SpelledAlike => 1,
                    _ => 0,
                },
                older.IsClassified || newer.IsClassified,
                nameof(Person), older.Id, on, $"/personen/{older.Id}",
                nameof(Person), newer.Id, nn, $"/personen/{newer.Id}"));
        }
    }

    private static async Task AddLinkPredictionAsync(AppDbContext db, List<Lead> leads, CancellationToken ct)
    {
        var edges = await GraphEdgeLoader.LoadRawEdgesAsync(db, null, ct);
        var adj = new Dictionary<string, HashSet<string>>();
        void Link(string a, string b)
        {
            if (!adj.TryGetValue(a, out var set))
            {
                set = new HashSet<string>();
                adj[a] = set;
            }
            set.Add(b);
        }
        foreach (var e in edges)
        {
            if (e.Source == e.Target)
            {
                continue;
            }
            Link(e.Source, e.Target);
            Link(e.Target, e.Source);
        }

        const string personPrefix = nameof(Person) + ":";
        var pairCount = new Dictionary<(string A, string B), int>();
        foreach (var (_, neighbors) in adj)
        {
            if (neighbors.Count > HubDegreeCap)
            {
                continue; // hub — too many pairs, skip
            }
            var persons = neighbors.Where(n => n.StartsWith(personPrefix, StringComparison.Ordinal)).ToList();
            for (var i = 0; i < persons.Count; i++)
            {
                for (var j = i + 1; j < persons.Count; j++)
                {
                    var key = string.CompareOrdinal(persons[i], persons[j]) < 0
                        ? (persons[i], persons[j])
                        : (persons[j], persons[i]);
                    pairCount[key] = pairCount.GetValueOrDefault(key) + 1;
                }
            }
        }

        var connected = await ConnectedPersonPairsAsync(db, ct);

        // qualifying pairs: >= 2 shared neighbours and no existing connection of any sort
        var qualifying = pairCount
            .Where(kv => kv.Value >= 2
                && !(adj.TryGetValue(kv.Key.A, out var an) && an.Contains(kv.Key.B))
                && !connected.Contains(kv.Key))
            .OrderByDescending(kv => kv.Value)
            .Take(40)
            .ToList();
        if (qualifying.Count == 0)
        {
            return;
        }

        var ids = qualifying.SelectMany(kv => new[] { Id(kv.Key.A), Id(kv.Key.B) }).Distinct().ToList();
        var people = await db.People.Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.CaseNumber, p.IsClassified })
            .ToDictionaryAsync(p => p.Id, p => p, ct);

        foreach (var kv in qualifying)
        {
            var aid = Id(kv.Key.A);
            var bid = Id(kv.Key.B);
            if (!people.TryGetValue(aid, out var a) || !people.TryGetValue(bid, out var b))
            {
                continue;
            }
            var an = Display(a.Name, a.CaseNumber);
            var bn = Display(b.Name, b.CaseNumber);
            leads.Add(new Lead(
                LeadKind.LinkPrediction,
                $"LP|{aid}|{bid}",
                "Mögliche Verbindung",
                $"{an} & {bn} teilen {kv.Value} gemeinsame Kontakte, sind aber nicht verknüpft.",
                kv.Value,
                a.IsClassified || b.IsClassified,
                nameof(Person), aid, an, $"/personen/{aid}",
                nameof(Person), bid, bn, $"/personen/{bid}"));
        }
    }

    // person pairs that already have a connection, as canonical-ordered graph node keys.
    // GraphEdgeLoader drops automatic (colleague/clique) links, so the adjacency alone would
    // keep proposing pairs that the person file already lists under "Beziehungen".
    private static async Task<HashSet<(string A, string B)>> ConnectedPersonPairsAsync(AppDbContext db, CancellationToken ct)
    {
        var set = new HashSet<(string, string)>();
        void Add(string a, string b)
        {
            var ka = $"{nameof(Person)}:{a}";
            var kb = $"{nameof(Person)}:{b}";
            set.Add(string.CompareOrdinal(ka, kb) < 0 ? (ka, kb) : (kb, ka));
        }

        foreach (var v in await db.Links
            .Where(v => v.SourceType == nameof(Person) && v.TargetType == nameof(Person))
            .Select(v => new { v.SourceId, v.TargetId })
            .ToListAsync(ct))
        {
            Add(v.SourceId, v.TargetId);
        }
        foreach (var b in await db.PersonRelations
            .Select(b => new { b.PersonAId, b.PersonBId })
            .ToListAsync(ct))
        {
            Add(b.PersonAId, b.PersonBId);
        }
        return set;
    }

    private static async Task AddNewConflictAsync(AppDbContext db, List<Lead> leads, CancellationToken ct)
    {
        var since = DateTime.UtcNow.AddDays(-ConflictWindowDays);
        var conflicts = await db.Links
            .Where(v => !v.Automatic && v.Kind == LinkKind.Conflict && v.CreatedAt >= since)
            .Select(v => new { v.Id, v.SourceType, v.SourceId, v.TargetType, v.TargetId, v.CreatedAt })
            .ToListAsync(ct);
        // endpoints must be types whose resolved Classified flag reflects real visibility; membership-gated
        // types (Taskforce/Job/Appointment) resolve as non-classified and would leak into the shared cached feed
        conflicts = conflicts.Where(c => HonestConflictType(c.SourceType) && HonestConflictType(c.TargetType)).ToList();
        if (conflicts.Count == 0)
        {
            return;
        }

        var refs = conflicts
            .SelectMany(c => new[] { (c.SourceType, c.SourceId), (c.TargetType, c.TargetId) })
            .Distinct().ToList();
        var map = await RecordsReference.ResolveAsync(db, refs, ct);

        var now = DateTime.UtcNow;
        foreach (var c in conflicts)
        {
            if (!map.TryGetValue((c.SourceType, c.SourceId), out var src) || !map.TryGetValue((c.TargetType, c.TargetId), out var tgt))
            {
                continue;
            }
            var ageDays = Math.Max(0, (now - c.CreatedAt).TotalDays);
            leads.Add(new Lead(
                LeadKind.NewConflict,
                $"NC|{c.Id}",
                "Neuer Konflikt",
                $"{src.Display} ⚔ {tgt.Display}",
                (int)Math.Round(ConflictWindowDays - ageDays), // newer = higher
                src.Classified || tgt.Classified,
                c.SourceType, c.SourceId, src.Display, src.Href,
                c.TargetType, c.TargetId, tgt.Display, tgt.Href));
        }
    }

    private static async Task AddStaleHighClassificationAsync(AppDbContext db, List<Lead> leads, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var staleBefore = now.AddDays(-StaleDays);

        var people = await db.People.Where(p => p.Classification >= Classification.SuspicionCase)
            .Select(p => new { p.Id, p.Name, p.IsClassified, p.Classification, Touched = p.ModifiedAt ?? p.CreatedAt })
            .ToListAsync(ct);
        foreach (var p in people.Where(p => p.Touched < staleBefore))
        {
            var days = (int)Math.Max(1, (now - p.Touched).TotalDays);
            leads.Add(new Lead(
                LeadKind.StaleHighClassification,
                $"ST|{nameof(Person)}|{p.Id}",
                "Veraltete Einstufung",
                $"{p.Name} ({ClassificationDisplay.Name(p.Classification)}) – seit {days} Tagen nicht aktualisiert.",
                days,
                p.IsClassified,
                nameof(Person), p.Id, p.Name, $"/personen/{p.Id}"));
        }

        // a faction counts as touched only through its four facets, matching the freshness light
        var factions = (await db.Factions.Where(f => f.Classification >= Classification.SuspicionCase)
                .Select(f => new
                {
                    f.Id, f.Name, f.IsClassified, f.Classification, f.CreatedAt,
                    f.MembersRefreshedAt, f.StockRefreshedAt, f.ActivitiesRefreshedAt, f.DocsRefreshedAt,
                })
                .ToListAsync(ct))
            .Select(f => new
            {
                f.Id, f.Name, f.IsClassified, f.Classification,
                Touched = FactionRecency.Reference(f.CreatedAt, f.MembersRefreshedAt, f.StockRefreshedAt,
                    f.ActivitiesRefreshedAt, f.DocsRefreshedAt),
            })
            .ToList();
        foreach (var f in factions.Where(f => f.Touched < staleBefore))
        {
            var days = (int)Math.Max(1, (now - f.Touched).TotalDays);
            leads.Add(new Lead(
                LeadKind.StaleHighClassification,
                $"ST|{nameof(Faction)}|{f.Id}",
                "Veraltete Einstufung",
                $"{f.Name} ({ClassificationDisplay.Name(f.Classification)}) – seit {days} Tagen nicht aktualisiert.",
                days,
                f.IsClassified,
                nameof(Faction), f.Id, f.Name, $"/fraktionen/{f.Id}"));
        }
    }

    private static string Id(string nodeKey)
    {
        var i = nodeKey.IndexOf(':');
        return i > 0 ? nodeKey[(i + 1)..] : nodeKey;
    }

    // canonical "Name (Aktenzeichen)"; falls back to the case number so the label is never empty
    private static string Display(string? name, string caseNumber)
        => string.IsNullOrWhiteSpace(name) ? caseNumber : $"{name} ({caseNumber})";

    private static bool HonestConflictType(string type) => type is nameof(Person) or nameof(Faction)
        or nameof(Party) or nameof(PersonGroup) or nameof(Case) or nameof(Operation);
}
