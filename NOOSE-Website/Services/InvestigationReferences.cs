using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Investigations;
using NOOSE_Website.Models.Common;

namespace NOOSE_Website.Services;

/// <summary>The only resolver for @-references to an investigation file and its entries, per viewer.</summary>
/// <remarks>
/// A reference resolves exactly as far as the viewer could open that file or entry right now: file visible, tab
/// readable, entry not limited to others, nothing trashed. Anything else is left out of the map and renders like a
/// missing target. These types must never go into <see cref="RecordsReference"/>, <c>LinkService.KnownTypes</c> or
/// <c>ReferenceTypes</c>, the partner-releasable mention types or <c>PartnerVisibility.IsReleasableType</c>: those
/// paths know no participants and would hand titles to anyone who asks.
/// </remarks>
public static class InvestigationReferences
{
    /// <summary>Most files one call resolves; references to any further file stay unresolved.</summary>
    public const int MaxInvestigations = 25;

    private const int TaskLabelLength = 60;
    private const int FileCandidates = 5;
    private const int EntryCandidatesPerType = 10;
    private const int EntryCandidates = 30;
    private const int Candidates = 8;

    // files whose entries the picker searches for a participant, newest first; older files stay reachable by reference
    private const int PickerFileScan = 200;

    /// <summary>The referencable types: the file plus every entry type a participant can limit.</summary>
    public static readonly IReadOnlyList<string> Types =
    [
        nameof(Investigation), nameof(InvestigationCharge), nameof(InvestigationPerson), nameof(InvestigationEvidence),
        nameof(InvestigationInterrogation), nameof(InvestigationTask), nameof(InvestigationNote),
    ];

    /// <summary>True for the file and the six entry types.</summary>
    public static bool IsType(string? type) => type is not null && Types.Contains(type, StringComparer.Ordinal);

    /// <summary>Where a reference leads: the file, or the entry's tab with the row to highlight.</summary>
    public static string Href(string type, string investigationId, string id)
        => type == nameof(Investigation)
            ? $"/ermittlungen/{investigationId}"
            : $"/ermittlungen/{investigationId}?tab={InvestigationTabs.ForChildType(type)}&eintrag={id}";

    private sealed record EntryRow(string Type, string Id, string InvestigationId, string Label);

    /// <summary>Labels and links for the references this viewer may follow; everything else is simply absent.</summary>
    public static async Task<Dictionary<(string Type, string Id), RecordsReference.Resolution>> ResolveAsync(
        AppDbContext db, IReadOnlyCollection<(string Type, string Id)> refs, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        if (!refs.Any(r => IsType(r.Type)) || await ViewerAsync(db, scope, cancellationToken) is not { } viewer)
        {
            return new();
        }
        return await ResolveForAsync(db, refs, viewer, cancellationToken);
    }

    /// <summary>Point check for one entry, for <see cref="Visibility"/>; the same rules as the references.</summary>
    public static async Task<bool> IsEntryVisibleAsync(AppDbContext db, string type, string id, ViewerScope scope,
        CancellationToken cancellationToken = default)
        => IsType(type) && (await ResolveAsync(db, [(type, id)], scope, cancellationToken)).ContainsKey((type, id));

    /// <summary>Picker hits: files by title or case number, entries by their name fields, context file first.</summary>
    /// <remarks>Every hit has passed <see cref="ResolveAsync"/>, so the picker never offers what the reader would not
    /// see. Notes are not searched, their text must not leak through a match.</remarks>
    public static async Task<List<MentionHit>> CandidatesAsync(AppDbContext db, string? text, ViewerScope scope,
        string? contextInvestigationId = null, CancellationToken cancellationToken = default)
    {
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s) || await ViewerAsync(db, scope, cancellationToken) is not { } viewer)
        {
            return [];
        }
        var ctx = contextInvestigationId ?? string.Empty;
        var visible = db.Investigations.OnlyVisible(db, viewer).Select(i => i.Id);
        int? number = s.StartsWith('#') && int.TryParse(s.AsSpan(1), out var n) ? n : null;

        var picked = new List<(string Type, string Id, string InvestigationId)>();
        foreach (var id in await db.Investigations.AsNoTracking().OnlyVisible(db, viewer)
                     .Where(i => i.Title.Contains(s) || i.CaseNumber.Contains(s))
                     .OrderByDescending(i => i.Id == ctx).ThenByDescending(i => i.OpenedAt)
                     .Take(FileCandidates).Select(i => i.Id).ToListAsync(cancellationToken))
        {
            picked.Add((nameof(Investigation), id, id));
        }

        // Narrow each entry type to the files whose tab the viewer reads and leave out entries limited to others, before
        // the Take: otherwise the newest hits from unreadable tabs fill every slot and a readable older one is never fetched.
        // Leadership and supervision read every tab and every entry, so for them the visible files are the whole gate.
        Dictionary<string, List<string>>? readableByType = null;
        HashSet<string> hiddenEntries = new(StringComparer.Ordinal);
        if (viewer.PartnerAgency is not null || !viewer.MayAllTaskforces)
        {
            var fileIds = await db.Investigations.AsNoTracking().OnlyVisible(db, viewer)
                .OrderByDescending(i => i.Id == ctx).ThenByDescending(i => i.OpenedAt)
                .Take(PickerFileScan).Select(i => i.Id).ToListAsync(cancellationToken);
            var access = await InvestigationAccess.ResolveReadManyAsync(db, fileIds, viewer, cancellationToken);
            readableByType = Types.Where(t => t != nameof(Investigation)).ToDictionary(t => t,
                t => access.Where(a => a.Value.CanRead(InvestigationTabs.ForChildType(t)!)).Select(a => a.Key).ToList(),
                StringComparer.Ordinal);
            hiddenEntries = (await InvestigationAccess.HiddenItemIdsAsync(db, access, Types, cancellationToken))
                .Select(h => h.ItemId).ToHashSet(StringComparer.Ordinal);
        }
        var hiddenIds = hiddenEntries.ToList();

        var entries = new List<(string Type, string Id, string InvestigationId)>();
        async Task Add<T>(IQueryable<T> rows, string type) where T : InvestigationRow
        {
            if (readableByType is not null)
            {
                var readable = readableByType[type];
                if (readable.Count == 0)
                {
                    return;
                }
                rows = rows.Where(e => readable.Contains(e.InvestigationId));
                if (hiddenIds.Count > 0)
                {
                    rows = rows.Where(e => !hiddenIds.Contains(e.Id));
                }
            }
            else
            {
                rows = rows.Where(e => visible.Contains(e.InvestigationId));
            }
            foreach (var r in await rows
                         .OrderByDescending(e => e.InvestigationId == ctx).ThenByDescending(e => e.CreatedAt)
                         .Take(EntryCandidatesPerType).Select(e => new { e.Id, e.InvestigationId }).ToListAsync(cancellationToken))
            {
                entries.Add((type, r.Id, r.InvestigationId));
            }
        }
        await Add(db.InvestigationEvidence.AsNoTracking().Where(e => e.Name.Contains(s) || (number != null && e.Number == number)),
            nameof(InvestigationEvidence));
        await Add(db.InvestigationPeople.AsNoTracking().Where(p => p.Name.Contains(s)), nameof(InvestigationPerson));
        await Add(db.InvestigationCharges.AsNoTracking().Where(c => c.Paragraph.Contains(s) || c.LawBook.Contains(s)),
            nameof(InvestigationCharge));
        await Add(db.InvestigationInterrogations.AsNoTracking().Where(v => v.Name.Contains(s)), nameof(InvestigationInterrogation));
        await Add(db.InvestigationTasks.AsNoTracking().Where(t => t.Text.Contains(s)), nameof(InvestigationTask));
        // stable: the context file first, the rest in query order
        picked.AddRange(entries.OrderBy(e => e.InvestigationId == ctx ? 0 : 1).Take(EntryCandidates));
        picked = picked.OrderBy(p => p.InvestigationId == ctx ? 0 : 1).ToList();

        var resolved = await ResolveForAsync(db, picked.Select(p => (p.Type, p.Id)).ToList(), viewer, cancellationToken);
        return picked
            .Where(p => resolved.ContainsKey((p.Type, p.Id)))
            .Take(Candidates)
            .Select(p => new MentionHit(p.Type, p.Id, resolved[(p.Type, p.Id)].Display, null))
            .ToList();
    }

    /// <summary>The viewer as the account row says; null when there is no account to ask for.</summary>
    internal static async Task<ViewerScope?> ViewerAsync(AppDbContext db, ViewerScope scope, CancellationToken cancellationToken)
    {
        if (scope.MeId is not { Length: > 0 } me)
        {
            return null;
        }
        var row = await db.Users.AsNoTracking().Where(u => u.Id == me)
            .Select(u => new { u.PartnerAgency }).FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }
        // the row decides partner status: a caller that forgot the agency must not take the leadership arm
        return row.PartnerAgency is { } agency ? new ViewerScope(false, false, me, agency) : scope;
    }

    private static async Task<Dictionary<(string Type, string Id), RecordsReference.Resolution>> ResolveForAsync(
        AppDbContext db, IReadOnlyCollection<(string Type, string Id)> refs, ViewerScope viewer, CancellationToken cancellationToken)
    {
        var map = new Dictionary<(string Type, string Id), RecordsReference.Resolution>();
        var wanted = refs.Where(r => IsType(r.Type)).Distinct().ToList();
        if (wanted.Count == 0)
        {
            return map;
        }
        var entries = (await EntriesAsync(db, wanted, cancellationToken)).ToDictionary(e => (e.Type, e.Id));

        // in order of appearance, so the first files of a long text are the ones that resolve
        var fileIds = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in wanted)
        {
            var invId = r.Type == nameof(Investigation) ? r.Id : entries.GetValueOrDefault(r)?.InvestigationId;
            if (invId is not null && seen.Add(invId))
            {
                fileIds.Add(invId);
            }
        }
        // fail-closed cap: hundreds of tokens must not fan out into hundreds of access checks
        if (fileIds.Count > MaxInvestigations)
        {
            fileIds = fileIds.Take(MaxInvestigations).ToList();
        }

        // the soft-delete filter drops a trashed file here, and its entries with it
        var files = await db.Investigations.AsNoTracking()
            .Where(i => fileIds.Contains(i.Id))
            .OnlyVisible(db, viewer)
            .Select(i => new { i.Id, i.CaseNumber, i.Title })
            .ToDictionaryAsync(i => i.Id, cancellationToken);

        foreach (var r in wanted.Where(r => r.Type == nameof(Investigation)))
        {
            if (files.TryGetValue(r.Id, out var file))
            {
                map[r] = new RecordsReference.Resolution($"{file.CaseNumber} · {file.Title}", false, Href(r.Type, r.Id, r.Id));
            }
        }

        // every file in one batch: a picker fires per keystroke, a resolution per file would be a query burst each time
        var inFiles = entries.Values.Where(e => files.ContainsKey(e.InvestigationId)).ToList();
        if (inFiles.Count == 0)
        {
            return map;
        }
        var access = await InvestigationAccess.ResolveReadManyAsync(db, inFiles.Select(e => e.InvestigationId).Distinct().ToList(),
            viewer, cancellationToken);
        var hidden = await InvestigationAccess.HiddenItemIdsAsync(db, access, inFiles.Select(e => e.Type).Distinct().ToList(),
            cancellationToken);
        foreach (var e in inFiles)
        {
            if (access.GetValueOrDefault(e.InvestigationId) is not { } view
                || !view.CanRead(InvestigationTabs.ForChildType(e.Type)!)
                || hidden.Contains((e.InvestigationId, e.Type, e.Id)))
            {
                continue;
            }
            map[(e.Type, e.Id)] = new RecordsReference.Resolution($"{files[e.InvestigationId].CaseNumber} · {e.Label}", false,
                Href(e.Type, e.InvestigationId, e.Id));
        }
        return map;
    }

    /// <summary>One query per entry type; a GUID asked for under the wrong type finds nothing.</summary>
    private static async Task<List<EntryRow>> EntriesAsync(AppDbContext db, IReadOnlyCollection<(string Type, string Id)> refs,
        CancellationToken cancellationToken)
    {
        List<string> Ids(string type) => refs.Where(r => r.Type == type).Select(r => r.Id).Distinct().ToList();
        var rows = new List<EntryRow>();

        var chargeIds = Ids(nameof(InvestigationCharge));
        if (chargeIds.Count > 0)
        {
            rows.AddRange((await db.InvestigationCharges.AsNoTracking().Where(c => chargeIds.Contains(c.Id))
                    .Select(c => new { c.Id, c.InvestigationId, c.Paragraph, c.LawBook }).ToListAsync(cancellationToken))
                .Select(c => new EntryRow(nameof(InvestigationCharge), c.Id, c.InvestigationId,
                    Squash($"Tatvorwurf {c.Paragraph} {c.LawBook}"))));
        }

        var personIds = Ids(nameof(InvestigationPerson));
        if (personIds.Count > 0)
        {
            rows.AddRange((await db.InvestigationPeople.AsNoTracking().Where(p => personIds.Contains(p.Id))
                    .Select(p => new { p.Id, p.InvestigationId, p.Name, p.Role }).ToListAsync(cancellationToken))
                .Select(p => new EntryRow(nameof(InvestigationPerson), p.Id, p.InvestigationId,
                    string.IsNullOrWhiteSpace(p.Role) ? p.Name : $"{p.Name} ({p.Role})")));
        }

        var evidenceIds = Ids(nameof(InvestigationEvidence));
        if (evidenceIds.Count > 0)
        {
            rows.AddRange((await db.InvestigationEvidence.AsNoTracking().Where(e => evidenceIds.Contains(e.Id))
                    .Select(e => new { e.Id, e.InvestigationId, e.Number, e.Name }).ToListAsync(cancellationToken))
                .Select(e => new EntryRow(nameof(InvestigationEvidence), e.Id, e.InvestigationId, $"Beweismittel #{e.Number} {e.Name}")));
        }

        var interrogationIds = Ids(nameof(InvestigationInterrogation));
        if (interrogationIds.Count > 0)
        {
            rows.AddRange((await db.InvestigationInterrogations.AsNoTracking().Where(v => interrogationIds.Contains(v.Id))
                    .Select(v => new { v.Id, v.InvestigationId, v.Name }).ToListAsync(cancellationToken))
                .Select(v => new EntryRow(nameof(InvestigationInterrogation), v.Id, v.InvestigationId, $"Vernehmung {v.Name}")));
        }

        var taskIds = Ids(nameof(InvestigationTask));
        if (taskIds.Count > 0)
        {
            // tokens stripped: a label never resolves a reference of its own
            rows.AddRange((await db.InvestigationTasks.AsNoTracking().Where(t => taskIds.Contains(t.Id))
                    .Select(t => new { t.Id, t.InvestigationId, t.Text }).ToListAsync(cancellationToken))
                .Select(t => new EntryRow(nameof(InvestigationTask), t.Id, t.InvestigationId,
                    $"Aufgabe: {ClipLabel(MentionParser.Strip(t.Text), TaskLabelLength)}")));
        }

        var noteIds = Ids(nameof(InvestigationNote));
        if (noteIds.Count > 0)
        {
            // the date only: a note's text would travel into every document that cites it
            rows.AddRange((await db.InvestigationNotes.AsNoTracking().Where(n => noteIds.Contains(n.Id))
                    .Select(n => new { n.Id, n.InvestigationId, n.CreatedAt }).ToListAsync(cancellationToken))
                .Select(n => new EntryRow(nameof(InvestigationNote), n.Id, n.InvestigationId,
                    $"Notiz vom {n.CreatedAt.ToLocalTime():dd.MM.yyyy HH:mm}")));
        }
        return rows;
    }

    private static string Squash(string value) => string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string ClipLabel(string value, int max)
        => value.Length <= max ? value : string.Concat(value.AsSpan(0, max).TrimEnd(), "…");
}
