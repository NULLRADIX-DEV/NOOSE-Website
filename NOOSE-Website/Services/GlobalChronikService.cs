using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Timeline;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IGlobalChronikService" />
public class GlobalChronikService(IDbContextFactory<AppDbContext> dbFactory) : IGlobalChronikService
{
    // one day never renders more than this; a busier day shows its newest events
    private const int SliceCap = 500;
    // day probes per page; bounds the work when filters reject nearly everything
    private const int MaxRounds = 8;
    // absolute score delta worth a chronicle line; the leadership alarm has its own configurable threshold
    private const int ScoreJumpThreshold = 10;
    // shown when a referenced record is classified or of a type the chronicle does not render
    private const string HiddenRecord = "nicht sichtbare Akte";
    // rows the band aggregates at most; beyond it the caption says the band is clipped
    private const int DensityCap = 20_000;
    // runaway guard; the unit thresholds keep every real window far below it
    private const int MaxBuckets = 200;
    // widest custom range the band honours
    private const int MaxWindowDays = 1100;

    /// <summary>Record types the chronicle anchors its events on.</summary>
    public static readonly string[] RecordTypes =
    {
        nameof(Person), nameof(Faction), nameof(PersonGroup), nameof(Party), nameof(Operation),
        nameof(Case), nameof(Taskforce), nameof(Job), nameof(Document), nameof(Law),
    };

    public async Task<ChronikResult> GetEventsAsync(ChronikQuery query, ClaimsPrincipal viewer, CancellationToken cancellationToken = default)
    {
        var scope = ViewerScope.From(viewer);
        if (scope.IsPartner)
        {
            return new ChronikResult(Array.Empty<ChronikEvent>(), null, false);
        }

        var filter = SliceFilter.From(query);
        if (filter.RecordTypes.Length == 0)
        {
            return new ChronikResult(Array.Empty<ChronikEvent>(), null, false);
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var events = new List<ChronikEvent>();
        var cursor = query.BeforeUtc ?? query.ToUtc;

        // walk whole local days backwards, skipping empty stretches via a cheap newest-timestamp probe
        for (var round = 0; round < MaxRounds && cursor > query.FromUtc && events.Count < query.MinEvents; round++)
        {
            var newest = await NewestBeforeAsync(db, query, filter, cursor, cancellationToken);
            if (newest is null)
            {
                cursor = query.FromUtc;
                break;
            }

            var dayStart = LocalDayStartUtc(newest.Value);
            var from = dayStart < query.FromUtc ? query.FromUtc : dayStart;
            events.AddRange(await LoadSliceAsync(db, scope, query, filter, from, cursor, cancellationToken));
            cursor = from;
        }

        var hasMore = cursor > query.FromUtc;
        return new ChronikResult(
            events.OrderByDescending(e => e.Timestamp).ToList(),
            hasMore ? cursor : null,
            hasMore);
    }

    public async Task<ChronikDensity> GetDensityAsync(ChronikQuery query, ClaimsPrincipal viewer, CancellationToken cancellationToken = default)
    {
        var scope = ViewerScope.From(viewer);
        if (scope.IsPartner)
        {
            return ChronikDensity.Empty;
        }

        var filter = SliceFilter.From(query);
        if (filter.RecordTypes.Length == 0)
        {
            return ChronikDensity.Empty;
        }

        var (fromUtc, toUtc) = ClampWindow(query.FromUtc, query.ToUtc);
        var unit = UnitFor(fromUtc, toUtc);
        var windowDays = Math.Max(1, (int)Math.Ceiling((toUtc - fromUtc).TotalDays));

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // ---- 1) audit spine, newest first so the cap drops the oldest rows ----
        var auditRows = filter.WantsAudit
            ? await AuditSlice(db, filter, fromUtc, toUtc).AsNoTracking()
                .OrderByDescending(a => a.Timestamp).ThenByDescending(a => a.Id).Take(DensityCap + 1)
                .Select(a => new DensityRow(a.Timestamp, a.EntityType, a.EntityId, a.Action, a.AgentId))
                .ToListAsync(cancellationToken)
            : new List<DensityRow>();

        var capped = auditRows.Count > DensityCap;
        if (capped)
        {
            auditRows.RemoveRange(DensityCap, auditRows.Count - DensityCap);
        }

        // ---- 2) child rows back to their owning record, exactly as the feed resolves them ----
        var childRefs = auditRows.Where(r => ChronikParentResolver.IsChild(r.EntityType))
            .Select(r => (r.EntityType, r.EntityId)).Distinct().ToList();
        var parents = await ChronikParentResolver.ResolveAsync(db, childRefs, cancellationToken);

        var hits = new List<DensityHit>(auditRows.Count);
        foreach (var row in auditRows)
        {
            string parentType, parentId;
            if (ChronikParentResolver.IsChild(row.EntityType))
            {
                if (!parents.TryGetValue((row.EntityType, row.EntityId), out var parent))
                {
                    continue; // owner gone, or an auto link the feed drops too
                }
                (parentType, parentId) = (parent.Type, parent.Id);
            }
            else
            {
                (parentType, parentId) = (row.EntityType, row.EntityId);
            }
            if (!filter.RecordTypes.Contains(parentType))
            {
                continue;
            }
            var (category, _) = TimelineDisplay.MapAudit(row.EntityType, row.Action);
            if (!Wanted(query, category))
            {
                continue;
            }
            hits.Add(new DensityHit(row.Timestamp, parentType, parentId, category, row.AgentId));
        }

        // ---- 3) classification history ----
        if (filter.WantsClassification && filter.ClassificationTypes.Length > 0
            && Wanted(query, TimelineCategory.Classification))
        {
            foreach (var e in await ClassificationSlice(db, filter, fromUtc, toUtc).AsNoTracking()
                .Select(e => new { e.Timestamp, e.EntityType, e.EntityId, e.AgentId })
                .ToListAsync(cancellationToken))
            {
                hits.Add(new DensityHit(e.Timestamp, e.EntityType, e.EntityId, TimelineCategory.Classification, e.AgentId));
            }
        }

        // ---- 4) threat-score jumps ----
        if (Wanted(query, TimelineCategory.ThreatScore))
        {
            foreach (var s in await ScoreJumpsAsync(db, filter, fromUtc, toUtc, cancellationToken))
            {
                hits.Add(new DensityHit(s.Timestamp, s.EntityType, s.EntityId, TimelineCategory.ThreatScore, null));
            }
        }

        // ---- 5) one visibility pass; bounded by the record count, not the row count ----
        var byType = new Dictionary<string, HashSet<string>>();
        foreach (var hit in hits)
        {
            if (!byType.TryGetValue(hit.EntityType, out var set))
            {
                set = new HashSet<string>();
                byType[hit.EntityType] = set;
            }
            set.Add(hit.EntityId);
        }
        var visible = await ResolveVisibleAsync(db, scope, byType, cancellationToken);
        hits.RemoveAll(h => !visible.ContainsKey((h.EntityType, h.EntityId)));

        return Bucketize(hits, fromUtc, toUtc, unit, windowDays, capped);
    }

    public async Task<ChronikFilterOptions> GetFilterOptionsAsync(ClaimsPrincipal viewer, CancellationToken cancellationToken = default)
    {
        var scope = ViewerScope.From(viewer);
        if (scope.IsPartner)
        {
            return new ChronikFilterOptions(Array.Empty<string>(), Array.Empty<ChronikAgentOption>());
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agentOpts = (await AgentDirectory.AllAsync(db, cancellationToken))
            .Select(a => new ChronikAgentOption(a.Id, a.Codename))
            .ToList();
        return new ChronikFilterOptions(RecordTypes.ToList(), agentOpts);
    }

    // ---------------------------------------------------------------- slicing

    /// <summary>Everything the SQL side of one slice needs, derived once per call.</summary>
    private sealed record SliceFilter(
        string[] RecordTypes, string[] AuditTypes, string[]? AgentIds, string? ExcludeAgentId,
        bool WantsAudit, bool WantsClassification, bool WantsScore)
    {
        public static SliceFilter From(ChronikQuery query)
        {
            var types = query.Types is { Count: > 0 } t
                ? GlobalChronikService.RecordTypes.Where(t.Contains).ToArray()
                : GlobalChronikService.RecordTypes;
            var agents = query.AgentIds is { Count: > 0 } a ? a.ToArray() : null;
            bool Wants(TimelineCategory category)
                => query.Categories is not { Count: > 0 } c || c.Contains(category);
            return new SliceFilter(
                types,
                ChronikParentResolver.AuditTypesFor(types),
                agents,
                string.IsNullOrEmpty(query.ExcludeAgentId) ? null : query.ExcludeAgentId,
                // classification and score are the only categories the audit spine never produces;
                // asking for just those must not make the day probe chase audit-only days
                query.Categories is not { Count: > 0 } cats
                    || cats.Any(c => c is not (TimelineCategory.Classification or TimelineCategory.ThreatScore)),
                Wants(TimelineCategory.Classification),
                // score snapshots have no actor, so an agent filter rules them out entirely
                agents is null && Wants(TimelineCategory.ThreatScore));
        }

        public string[] ClassificationTypes => RecordTypes
            .Where(t => t is nameof(Person) or nameof(Faction) or nameof(PersonGroup)).ToArray();

        public string[] ScoreTypes => RecordTypes
            .Where(t => t is nameof(Person) or nameof(Faction)).ToArray();
    }

    private static IQueryable<AuditLog> AuditSlice(AppDbContext db, SliceFilter filter, DateTime fromUtc, DateTime toUtc)
    {
        var q = db.AuditLogs.Where(a => filter.AuditTypes.Contains(a.EntityType)
            && a.Timestamp >= fromUtc && a.Timestamp < toUtc);
        if (filter.ExcludeAgentId is { } excluded)
        {
            // actorless rows stay
            q = q.Where(a => a.AgentId != excluded);
        }
        return filter.AgentIds is null ? q : q.Where(a => a.AgentId != null && filter.AgentIds.Contains(a.AgentId));
    }

    // newest event timestamp strictly below the cursor across all three sources; null when the window is exhausted
    private static async Task<DateTime?> NewestBeforeAsync(
        AppDbContext db, ChronikQuery query, SliceFilter filter, DateTime cursor, CancellationToken ct)
    {
        DateTime? newest = filter.WantsAudit
            ? await AuditSlice(db, filter, query.FromUtc, cursor)
                .OrderByDescending(a => a.Timestamp).Select(a => (DateTime?)a.Timestamp).FirstOrDefaultAsync(ct)
            : null;

        if (filter.WantsClassification && filter.ClassificationTypes.Length > 0)
        {
            var stamp = await ClassificationSlice(db, filter, query.FromUtc, cursor)
                .OrderByDescending(e => e.Timestamp).Select(e => (DateTime?)e.Timestamp).FirstOrDefaultAsync(ct);
            newest = Later(newest, stamp);
        }

        if (filter.WantsScore && filter.ScoreTypes.Length > 0)
        {
            var stamp = await ScoreSlice(db, filter, query.FromUtc, cursor)
                .OrderByDescending(h => h.Timestamp).Select(h => (DateTime?)h.Timestamp).FirstOrDefaultAsync(ct);
            newest = Later(newest, stamp);
        }

        return newest;

        static DateTime? Later(DateTime? a, DateTime? b)
            => a is null ? b : b is null ? a : a > b ? a : b;
    }

    private static IQueryable<ClassificationHistory> ClassificationSlice(
        AppDbContext db, SliceFilter filter, DateTime fromUtc, DateTime toUtc)
    {
        var q = db.ClassificationHistory.Where(e => filter.ClassificationTypes.Contains(e.EntityType)
            && e.Timestamp >= fromUtc && e.Timestamp < toUtc);
        if (filter.ExcludeAgentId is { } excluded)
        {
            q = q.Where(e => e.AgentId != excluded);
        }
        return filter.AgentIds is null ? q : q.Where(e => e.AgentId != null && filter.AgentIds.Contains(e.AgentId));
    }

    private static IQueryable<ThreatScoreHistory> ScoreSlice(
        AppDbContext db, SliceFilter filter, DateTime fromUtc, DateTime toUtc)
        => db.ThreatScoreHistory.Where(h => filter.ScoreTypes.Contains(h.EntityType)
            && h.Timestamp >= fromUtc && h.Timestamp < toUtc);

    private sealed record AuditRow(
        DateTime Timestamp, string EntityType, string EntityId, AuditAction Action, string? AgentName, string? ChangesJson);

    private sealed record Pending(AuditRow Row, string ParentType, string ParentId);

    private sealed record ClassRow(DateTime Timestamp, string EntityType, string EntityId, Classification Value, string? Justification, string? AgentName);

    private sealed record ScoreRow(DateTime Timestamp, string EntityType, string EntityId, int Previous, int Current);

    private sealed record DensityRow(
        DateTime Timestamp, string EntityType, string EntityId, AuditAction Action, string? AgentId);

    private sealed record DensityHit(
        DateTime Timestamp, string EntityType, string EntityId, TimelineCategory Category, string? AgentId);

    private async Task<List<ChronikEvent>> LoadSliceAsync(
        AppDbContext db, ViewerScope scope, ChronikQuery query, SliceFilter filter,
        DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        // ---- 1) audit spine ----
        var auditRows = filter.WantsAudit
            ? await AuditSlice(db, filter, fromUtc, toUtc)
                .OrderByDescending(a => a.Timestamp).ThenByDescending(a => a.Id).Take(SliceCap)
                .Select(a => new AuditRow(a.Timestamp, a.EntityType, a.EntityId, a.Action, a.AgentName, a.ChangesJson))
                .ToListAsync(ct)
            : new List<AuditRow>();

        // ---- 2) child rows back to the record that owns them ----
        var childRefs = auditRows.Where(r => ChronikParentResolver.IsChild(r.EntityType))
            .Select(r => (r.EntityType, r.EntityId)).Distinct().ToList();
        var parents = await ChronikParentResolver.ResolveAsync(db, childRefs, ct);

        var pending = new List<Pending>(auditRows.Count);
        foreach (var row in auditRows)
        {
            string parentType, parentId;
            if (ChronikParentResolver.IsChild(row.EntityType))
            {
                if (!parents.TryGetValue((row.EntityType, row.EntityId), out var parent))
                {
                    continue; // owner gone, or an auto link we deliberately drop
                }
                (parentType, parentId) = (parent.Type, parent.Id);
            }
            else
            {
                (parentType, parentId) = (row.EntityType, row.EntityId);
            }
            // polymorphic children hang off any record, so the type filter can only bite here
            if (!filter.RecordTypes.Contains(parentType))
            {
                continue;
            }
            pending.Add(new Pending(row, parentType, parentId));
        }

        // ---- 3) classification history ----
        var classRows = filter.WantsClassification && filter.ClassificationTypes.Length > 0
            ? await ClassificationSlice(db, filter, fromUtc, toUtc)
                .Select(e => new ClassRow(e.Timestamp, e.EntityType, e.EntityId, e.Value, e.Justification, e.AgentName))
                .ToListAsync(ct)
            : new List<ClassRow>();

        // ---- 4) threat-score jumps ----
        var scoreRows = await ScoreJumpsAsync(db, filter, fromUtc, toUtc, ct);

        // ---- 5) detail payloads for the child events ----
        var detailRefs = pending.Where(p => ChronikParentResolver.IsChild(p.Row.EntityType))
            .Select(p => (p.Row.EntityType, p.Row.EntityId)).Distinct().ToList();
        var details = await ChronikDetails.LoadAsync(db, detailRefs, ct);

        // ---- 6) one visibility pass over every record any event names ----
        var byType = new Dictionary<string, HashSet<string>>();
        void Note(string type, string id)
        {
            if (!byType.TryGetValue(type, out var set))
            {
                set = new HashSet<string>();
                byType[type] = set;
            }
            set.Add(id);
        }
        foreach (var p in pending) { Note(p.ParentType, p.ParentId); }
        foreach (var c in classRows) { Note(c.EntityType, c.EntityId); }
        foreach (var s in scoreRows) { Note(s.EntityType, s.EntityId); }
        foreach (var d in details.Values)
        {
            // a detail that names another record must clear the same gate as an anchor
            if (d.Reference is { } r && RecordTypes.Contains(r.Type)) { Note(r.Type, r.Id); }
        }

        var records = await ResolveVisibleAsync(db, scope, byType, ct);

        // ---- 7) build ----
        var events = new List<ChronikEvent>(pending.Count + classRows.Count + scoreRows.Count);

        foreach (var p in pending)
        {
            if (!records.TryGetValue((p.ParentType, p.ParentId), out var record))
            {
                continue;
            }
            var (category, title) = TimelineDisplay.MapAudit(p.Row.EntityType, p.Row.Action);
            var detail = details.TryGetValue((p.Row.EntityType, p.Row.EntityId), out var d) ? Render(d, records) : null;
            // the feed reads at a glance; the full before/after stays in the audit log view
            var changes = AuditDisplay.Parse(p.Row.ChangesJson, maxValueLength: 90, entityType: p.Row.EntityType);
            // same rule as the record timeline: a tip's actor is the submitting account, and naming it here would
            // out an agent who reported through his civilian identity
            var actor = TipAnonymity.HidesActor(p.Row.EntityType) ? null : p.Row.AgentName;
            events.Add(new ChronikEvent(p.Row.Timestamp, category, p.ParentType, p.ParentId, record.Name, title,
                detail, actor, Href(p.ParentType, p.ParentId), record.Deleted,
                changes.Count == 0 ? null : changes));
        }

        foreach (var c in classRows)
        {
            if (!records.TryGetValue((c.EntityType, c.EntityId), out var record))
            {
                continue;
            }
            events.Add(new ChronikEvent(c.Timestamp, TimelineCategory.Classification, c.EntityType, c.EntityId,
                record.Name, $"Einstufung: {ClassificationDisplay.Name(c.Value)}",
                TimelineDisplay.Truncate(c.Justification), c.AgentName,
                Href(c.EntityType, c.EntityId), record.Deleted));
        }

        foreach (var s in scoreRows)
        {
            if (!records.TryGetValue((s.EntityType, s.EntityId), out var record))
            {
                continue;
            }
            var arrow = s.Current > s.Previous ? "gestiegen" : "gefallen";
            events.Add(new ChronikEvent(s.Timestamp, TimelineCategory.ThreatScore, s.EntityType, s.EntityId,
                record.Name, $"Bedrohungs-Score {arrow}: {s.Previous} → {s.Current}", null, null,
                Href(s.EntityType, s.EntityId), record.Deleted));
        }

        return Postfilter(events, query);
    }

    // category and free text can only be applied once names and details exist
    private static List<ChronikEvent> Postfilter(List<ChronikEvent> events, ChronikQuery query)
    {
        IEnumerable<ChronikEvent> result = events;
        if (query.Categories is { Count: > 0 } categories)
        {
            result = result.Where(e => categories.Contains(e.Category));
        }
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            var needle = query.Text.Trim();
            result = result.Where(e =>
                e.Name.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || e.Title.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || (e.Detail?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false)
                || (e.ActorName?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));
        }
        return result.ToList();
    }

    private static string? Render(ChronikDetails.Detail detail, Dictionary<(string, string), RecordRef> records)
    {
        if (detail.Reference is not { } reference)
        {
            return detail.Text;
        }
        var name = records.TryGetValue(reference, out var record) ? record.Name : HiddenRecord;
        return $"{detail.Prefix}{name}{detail.Suffix}";
    }

    // history rows only exist where the score actually changed, so consecutive rows per record are the jumps
    private static async Task<List<ScoreRow>> ScoreJumpsAsync(
        AppDbContext db, SliceFilter filter, DateTime fromUtc, DateTime toUtc, CancellationToken ct)
    {
        if (!filter.WantsScore || filter.ScoreTypes.Length == 0)
        {
            return new List<ScoreRow>();
        }

        // reach back far enough to find the predecessor of the slice's oldest snapshot
        var lookback = fromUtc.AddDays(-90);
        var rows = await db.ThreatScoreHistory
            .Where(h => filter.ScoreTypes.Contains(h.EntityType) && h.Timestamp >= lookback && h.Timestamp < toUtc)
            .Select(h => new { h.EntityType, h.EntityId, h.Score, h.Timestamp })
            .ToListAsync(ct);

        var jumps = new List<ScoreRow>();
        foreach (var group in rows.GroupBy(h => (h.EntityType, h.EntityId)))
        {
            var ordered = group.OrderBy(h => h.Timestamp).ToList();
            for (var i = 1; i < ordered.Count; i++)
            {
                var previous = ordered[i - 1].Score;
                var current = ordered[i].Score;
                var at = ordered[i].Timestamp;
                if (previous is null || current is null || at < fromUtc
                    || Math.Abs(current.Value - previous.Value) < ScoreJumpThreshold)
                {
                    continue;
                }
                jumps.Add(new ScoreRow(at, group.Key.EntityType, group.Key.EntityId, previous.Value, current.Value));
            }
        }
        return jumps;
    }

    // ---------------------------------------------------------------- visibility

    /// <summary>Display name plus trash state of a record the viewer may see.</summary>
    private readonly record struct RecordRef(string Name, bool Deleted);

    // resolves names ONLY for records the viewer may see; IgnoreQueryFilters so deletion/restore events survive
    private static async Task<Dictionary<(string Type, string Id), RecordRef>> ResolveVisibleAsync(
        AppDbContext db, ViewerScope scope, Dictionary<string, HashSet<string>> byType, CancellationToken ct)
    {
        var result = new Dictionary<(string, string), RecordRef>();
        List<string> Ids(string type) => byType.TryGetValue(type, out var s) ? s.ToList() : new List<string>();

        // in-memory because the delete filter is off here; the secrecy rules themselves stay the canonical ones
        bool VisRestricted(bool c, bool tru, bool hrb) => RecordVisibility.IsVisible(scope, c, tru, hrb);
        bool VisDocument(bool c, bool tru, bool hrb) => scope.CanSee(DocumentVisibility.LevelOf(c, tru, hrb));

        var personIds = Ids(nameof(Person));
        if (personIds.Count > 0)
        {
            foreach (var x in await db.People.IgnoreQueryFilters().Where(p => personIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Name, p.IsClassified, p.IsTRUClassified, p.IsHRBClassified, p.IsDeleted }).ToListAsync(ct))
            {
                if (VisRestricted(x.IsClassified, x.IsTRUClassified, x.IsHRBClassified)) { result[(nameof(Person), x.Id)] = new RecordRef(x.Name, x.IsDeleted); }
            }
        }

        var factionIds = Ids(nameof(Faction));
        if (factionIds.Count > 0)
        {
            foreach (var x in await db.Factions.IgnoreQueryFilters().Where(f => factionIds.Contains(f.Id))
                .Select(f => new { f.Id, f.Name, f.IsClassified, f.IsTRUClassified, f.IsHRBClassified, f.IsDeleted }).ToListAsync(ct))
            {
                if (VisRestricted(x.IsClassified, x.IsTRUClassified, x.IsHRBClassified)) { result[(nameof(Faction), x.Id)] = new RecordRef(x.Name, x.IsDeleted); }
            }
        }

        var groupIds = Ids(nameof(PersonGroup));
        if (groupIds.Count > 0)
        {
            foreach (var x in await db.PersonGroups.IgnoreQueryFilters().Where(g => groupIds.Contains(g.Id))
                .Select(g => new { g.Id, g.Name, g.IsClassified, g.IsTRUClassified, g.IsHRBClassified, g.IsDeleted }).ToListAsync(ct))
            {
                if (VisRestricted(x.IsClassified, x.IsTRUClassified, x.IsHRBClassified)) { result[(nameof(PersonGroup), x.Id)] = new RecordRef(x.Name, x.IsDeleted); }
            }
        }

        var partyIds = Ids(nameof(Party));
        if (partyIds.Count > 0)
        {
            foreach (var x in await db.Parties.IgnoreQueryFilters().Where(p => partyIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Name, p.IsClassified, p.IsTRUClassified, p.IsHRBClassified, p.IsDeleted }).ToListAsync(ct))
            {
                if (VisRestricted(x.IsClassified, x.IsTRUClassified, x.IsHRBClassified)) { result[(nameof(Party), x.Id)] = new RecordRef(x.Name, x.IsDeleted); }
            }
        }

        var operationIds = Ids(nameof(Operation));
        if (operationIds.Count > 0)
        {
            foreach (var x in await db.Operations.IgnoreQueryFilters().Where(o => operationIds.Contains(o.Id))
                .Select(o => new { o.Id, o.Title, o.IsClassified, o.IsTRUClassified, o.IsHRBClassified, o.IsDeleted }).ToListAsync(ct))
            {
                if (VisRestricted(x.IsClassified, x.IsTRUClassified, x.IsHRBClassified)) { result[(nameof(Operation), x.Id)] = new RecordRef(x.Title, x.IsDeleted); }
            }
        }

        var caseIds = Ids(nameof(Case));
        if (caseIds.Count > 0)
        {
            foreach (var x in await db.Cases.IgnoreQueryFilters().Where(v => caseIds.Contains(v.Id))
                .Select(v => new { v.Id, v.Title, v.IsClassified, v.IsTRUClassified, v.IsHRBClassified, v.IsDeleted }).ToListAsync(ct))
            {
                if (VisRestricted(x.IsClassified, x.IsTRUClassified, x.IsHRBClassified)) { result[(nameof(Case), x.Id)] = new RecordRef(x.Title, x.IsDeleted); }
            }
        }

        var documentIds = Ids(nameof(Document));
        if (documentIds.Count > 0)
        {
            foreach (var x in await db.Documents.IgnoreQueryFilters().Where(d => documentIds.Contains(d.Id))
                .Select(d => new { d.Id, d.Title, d.IsClassified, d.IsTRUClassified, d.IsHRBClassified, d.IsDeleted }).ToListAsync(ct))
            {
                if (VisDocument(x.IsClassified, x.IsTRUClassified, x.IsHRBClassified))
                {
                    result[(nameof(Document), x.Id)] = new RecordRef(string.IsNullOrWhiteSpace(x.Title) ? "Dokument" : x.Title, x.IsDeleted);
                }
            }
        }

        var lawIds = Ids(nameof(Law));
        if (lawIds.Count > 0)
        {
            foreach (var x in await db.Laws.IgnoreQueryFilters().Where(g => lawIds.Contains(g.Id))
                .Select(g => new { g.Id, g.Paragraph, g.Title, g.IsDeleted }).ToListAsync(ct))
            {
                result[(nameof(Law), x.Id)] = new RecordRef($"{x.Paragraph} {x.Title}".Trim(), x.IsDeleted);
            }
        }

        var tfIds = Ids(nameof(Taskforce));
        if (tfIds.Count > 0)
        {
            var visible = await TaskforceVisibility.VisibleIdsAsync(db, tfIds, scope.MayAllTaskforces, scope.MeId, ct);
            if (visible.Count > 0)
            {
                foreach (var x in await db.Taskforces.IgnoreQueryFilters().Where(t => visible.Contains(t.Id))
                    .Select(t => new { t.Id, t.Name, t.IsDeleted }).ToListAsync(ct))
                {
                    result[(nameof(Taskforce), x.Id)] = new RecordRef(x.Name, x.IsDeleted);
                }
            }
        }

        var jobIds = Ids(nameof(Job));
        if (jobIds.Count > 0)
        {
            var visible = await JobVisibility.VisibleIdsAsync(db, jobIds, scope.MayAllTaskforces, scope.MeId, ct);
            if (visible.Count > 0)
            {
                foreach (var x in await db.Jobs.IgnoreQueryFilters().Where(a => visible.Contains(a.Id))
                    .Select(a => new { a.Id, a.Title, a.IsDeleted }).ToListAsync(ct))
                {
                    result[(nameof(Job), x.Id)] = new RecordRef(x.Title, x.IsDeleted);
                }
            }
        }

        return result;
    }

    // ---------------------------------------------------------------- band buckets

    // the band honours the category chips; free text needs names it never loads
    private static bool Wanted(ChronikQuery query, TimelineCategory category)
        => query.Categories is not { Count: > 0 } categories || categories.Contains(category);

    // a custom range may be reversed or arbitrarily wide; both would wreck the axis and the average
    private static (DateTime FromUtc, DateTime ToUtc) ClampWindow(DateTime fromUtc, DateTime toUtc)
    {
        if (toUtc <= fromUtc)
        {
            return (toUtc.AddDays(-1), toUtc);
        }
        var widest = toUtc.AddDays(-MaxWindowDays);
        return (fromUtc < widest ? widest : fromUtc, toUtc);
    }

    // derived from the window, never chosen: keeps the bar count readable at every preset
    private static ChronikBucketUnit UnitFor(DateTime fromUtc, DateTime toUtc)
        => (toUtc - fromUtc).TotalDays switch
        {
            <= 2 => ChronikBucketUnit.Hour,
            <= 92 => ChronikBucketUnit.Day,
            <= 400 => ChronikBucketUnit.Week,
            _ => ChronikBucketUnit.Month,
        };

    private static ChronikDensity Bucketize(
        List<DensityHit> hits, DateTime fromUtc, DateTime toUtc,
        ChronikBucketUnit unit, int windowDays, bool capped)
    {
        var counts = new Dictionary<DateTime, int[]>();
        foreach (var hit in hits)
        {
            var start = BucketStartLocal(hit.Timestamp, unit);
            if (!counts.TryGetValue(start, out var slots))
            {
                slots = new int[ActivityBandDisplay.Slots];
                counts[start] = slots;
            }
            slots[ActivityBandDisplay.Slot(hit.Category)]++;
        }

        // gap-fill, so a quiet stretch reads as zero instead of "not loaded"
        var buckets = new List<ChronikDensityBucket>();
        var cursor = BucketStartLocal(fromUtc, unit);
        var last = BucketStartLocal(toUtc.AddTicks(-1), unit);
        while (cursor <= last && buckets.Count < MaxBuckets)
        {
            var segments = new List<ChronikDensitySegment>();
            var total = 0;
            if (counts.TryGetValue(cursor, out var slots))
            {
                for (var slot = 0; slot < slots.Length; slot++)
                {
                    if (slots[slot] > 0)
                    {
                        segments.Add(new ChronikDensitySegment(slot, slots[slot]));
                        total += slots[slot];
                    }
                }
            }
            buckets.Add(new ChronikDensityBucket(cursor, total, segments));
            cursor = Advance(cursor, unit);
        }

        // the headline counts what the bars show, so the KPI tile can never contradict the plot
        return new ChronikDensity(
            buckets,
            unit,
            buckets.Sum(b => b.Total),
            hits.Where(h => h.AgentId is not null).Select(h => h.AgentId!).Distinct().Count(),
            hits.Select(h => (h.EntityType, h.EntityId)).Distinct().Count(),
            windowDays,
            capped);
    }

    /// <summary>Local start of the bucket a UTC instant falls into.</summary>
    private static DateTime BucketStartLocal(DateTime utc, ChronikBucketUnit unit)
    {
        var local = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
        var midnight = new DateTime(local.Year, local.Month, local.Day, 0, 0, 0, DateTimeKind.Local);
        return unit switch
        {
            ChronikBucketUnit.Hour => midnight.AddHours(local.Hour),
            ChronikBucketUnit.Day => midnight,
            // Monday-based, matching the German week
            ChronikBucketUnit.Week => midnight.AddDays(-(((int)local.DayOfWeek + 6) % 7)),
            _ => new DateTime(local.Year, local.Month, 1, 0, 0, 0, DateTimeKind.Local),
        };
    }

    private static DateTime Advance(DateTime start, ChronikBucketUnit unit) => unit switch
    {
        ChronikBucketUnit.Hour => start.AddHours(1),
        ChronikBucketUnit.Day => start.AddDays(1),
        ChronikBucketUnit.Week => start.AddDays(7),
        _ => start.AddMonths(1),
    };

    // ---------------------------------------------------------------- helpers

    /// <summary>Start of the local day that contains the given UTC instant, back in UTC.</summary>
    private static DateTime LocalDayStartUtc(DateTime utc)
    {
        var local = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime();
        var start = new DateTime(local.Year, local.Month, local.Day, 0, 0, 0, DateTimeKind.Local);
        return start.ToUniversalTime();
    }

    private static string? Href(string type, string id) => type switch
    {
        nameof(Person) => $"/personen/{id}",
        nameof(Faction) => $"/fraktionen/{id}",
        nameof(PersonGroup) => $"/personengruppen/{id}",
        nameof(Party) => $"/parteien/{id}",
        nameof(Operation) => $"/operationen/{id}",
        nameof(Case) => $"/vorgaenge/{id}",
        nameof(Taskforce) => $"/taskforces/{id}",
        nameof(Job) => $"/aufgaben/{id}",
        nameof(Document) => $"/dokumente/{id}",
        nameof(Law) => $"/gesetze/{id}",
        _ => null,
    };
}
