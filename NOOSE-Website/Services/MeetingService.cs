using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Meetings;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Meetings;

namespace NOOSE_Website.Services;

/// <summary>Meetings interlock with absences: the attendance state is derived while open and frozen once closed.</summary>
public class MeetingService(
    IDbContextFactory<AppDbContext> dbFactory,
    ICaseNumberService caseNumber,
    INotificationService notifications,
    ILinkService links) : IMeetingService
{
    /// <summary>Sparse ordering leaves room to move items without renumbering.</summary>
    private const int SortingStep = 10;

    /// <summary>Roster of a meeting: the selectable agents, narrowed to those already released back then.</summary>
    private static IQueryable<Agent> Roster(AppDbContext db, DateTime meetingStartUtc)
        => db.Users.OnlySelectable().Where(u => (u.ReleasedAt ?? u.RegisteredAt) <= meetingStartUtc);

    // ---- reads ----

    public async Task<List<MeetingListItem>> GetListAsync(string? meId, DateOnly? from = null, DateOnly? to = null,
        int skip = 0, int take = 50, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var query = Window(db.Meetings.AsNoTracking(), from, to);

        return await query
            .OrderByDescending(m => m.Start)
            .Skip(skip).Take(take)
            .Select(m => new MeetingListItem(
                m.Id, m.CaseNumber, m.Title, m.Start, m.End, m.Location, m.Status,
                db.MeetingAgendaItems.Count(p => p.MeetingId == m.Id),
                m.AttendanceClosedAt,
                meId != null && db.MeetingSignOffs.Any(s => s.MeetingId == m.Id && s.AgentId == meId)))
            .ToListAsync(cancellationToken);
    }

    public async Task<int> GetCountAsync(DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await Window(db.Meetings.AsNoTracking(), from, to).CountAsync(cancellationToken);
    }

    /// <summary>Day bounds are local; the stored start is UTC, so convert the window instead of the rows.</summary>
    private static IQueryable<Meeting> Window(IQueryable<Meeting> query, DateOnly? from, DateOnly? to)
    {
        if (from is { } f)
        {
            var fromUtc = MeetingTime.ToUtc(f.ToDateTime(TimeOnly.MinValue));
            query = query.Where(m => m.Start >= fromUtc);
        }
        if (to is { } t)
        {
            var toUtc = MeetingTime.ToUtc(t.AddDays(1).ToDateTime(TimeOnly.MinValue));
            query = query.Where(m => m.Start < toUtc);
        }
        return query;
    }

    public async Task<Meeting?> GetDetailAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Meetings.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, cancellationToken);
    }

    public async Task<List<Meeting>> SearchAsync(string? searchText, int max = 20, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var query = db.Meetings.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var term = searchText.Trim();
            query = query.Where(m => m.Title.Contains(term) || m.CaseNumber.Contains(term));
        }
        return await query.OrderByDescending(m => m.Start).Take(max).ToListAsync(cancellationToken);
    }

    public async Task<List<Meeting>> GetTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Meetings.AsNoTracking()
            .IgnoreQueryFilters()
            .Where(m => m.IsDeleted)
            .OrderByDescending(m => m.DeletedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<bool> HasNextAsync(string meetingId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Meetings.AnyAsync(m => m.PreviousMeetingId == meetingId, cancellationToken);
    }

    // ---- meeting writes ----

    public async Task<Meeting> CreateAsync(MeetingInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireMeetingWrite(actor);
        var (start, end) = TimesFromInput(input);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var meeting = new Meeting
        {
            CaseNumber = await caseNumber.NextAsync(db, "BS", cancellationToken),
            Title = input.Title.Trim(),
            Start = start,
            End = end,
            Location = string.IsNullOrWhiteSpace(input.Location) ? null : input.Location.Trim(),
            Status = input.Status,
        };
        db.Meetings.Add(meeting);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        await AnnounceAsync(db, meeting, actor, cancellationToken);
        return meeting;
    }

    public async Task RefreshAsync(string id, MeetingInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireMeetingWrite(actor);
        var (start, end) = TimesFromInput(input);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var meeting = await db.Meetings.FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Besprechung '{id}' nicht gefunden.");

        // re-notify on a moved start by clearing both dedupe stamps
        if (meeting.Start != start)
        {
            meeting.ReminderDaySentAt = null;
            meeting.ReminderSoonSentAt = null;
        }

        meeting.Title = input.Title.Trim();
        meeting.Start = start;
        meeting.End = end;
        meeting.Location = string.IsNullOrWhiteSpace(input.Location) ? null : input.Location.Trim();
        meeting.Status = input.Status;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireMeetingWrite(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var meeting = await db.Meetings.FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Besprechung '{id}' nicht gefunden.");

        db.Meetings.Remove(meeting);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var meeting = await db.Meetings.IgnoreQueryFilters()
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Besprechung '{id}' nicht gefunden.");

        meeting.IsDeleted = false;
        meeting.DeletedAt = null;
        meeting.DeletedById = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static (DateTime Start, DateTime? End) TimesFromInput(MeetingInput input)
    {
        if (input.Start is not { } start)
        {
            throw new InvalidOperationException("Bitte einen Beginn angeben.");
        }
        if (input.End is { } end && end < start)
        {
            throw new InvalidOperationException("Das Ende darf nicht vor dem Beginn liegen.");
        }
        return (MeetingTime.ToUtc(start), input.End is { } e ? MeetingTime.ToUtc(e) : null);
    }

    // ---- clone ----

    public async Task<Meeting> NextCreateAsync(string sourceMeetingId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireMeetingWrite(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var source = await db.Meetings.FirstOrDefaultAsync(m => m.Id == sourceMeetingId, cancellationToken)
            ?? throw new InvalidOperationException($"Besprechung '{sourceMeetingId}' nicht gefunden.");
        // check past the soft-delete filter: the unique index still holds a trashed clone's row
        if (await db.Meetings.IgnoreQueryFilters().AnyAsync(m => m.PreviousMeetingId == sourceMeetingId, cancellationToken))
        {
            throw new InvalidOperationException("Für diese Besprechung wurde bereits eine Folgebesprechung angelegt.");
        }

        // local round-trip keeps the wall-clock time across a DST boundary
        var localNext = MeetingTime.Local(source.Start).AddDays(14);
        var meeting = new Meeting
        {
            CaseNumber = await caseNumber.NextAsync(db, "BS", cancellationToken),
            Title = source.Title,
            Location = source.Location,
            Start = MeetingTime.ToUtc(localNext),
            End = source.End is { } e ? MeetingTime.ToUtc(localNext + (e - source.Start)) : null,
            PreviousMeetingId = source.Id,
        };
        db.Meetings.Add(meeting);
        await db.SaveChangesAsync(cancellationToken);

        var open = await db.MeetingAgendaItems
            .Where(p => p.MeetingId == sourceMeetingId && !p.Done)
            .OrderBy(p => p.Sorting)
            .ToListAsync(cancellationToken);

        var sorting = SortingStep;
        var idMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in open)
        {
            // built field by field so a future column is not silently carried over
            var copy = new MeetingAgendaItem
            {
                MeetingId = meeting.Id,
                Title = item.Title,
                Sorting = sorting,
                CarriedFromItemId = item.Id,
            };
            db.MeetingAgendaItems.Add(copy);
            idMap[item.Id] = copy.Id;
            sorting += SortingStep;
        }
        await db.SaveChangesAsync(cancellationToken);

        await ItemLinksCopyAsync(db, idMap, cancellationToken);
        await ItemMeasuresCopyAsync(db, idMap, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        await AnnounceAsync(db, meeting, actor, cancellationToken);
        return meeting;
    }

    /// <summary>Carries the measures of copied items over, so the next meeting sees what came of a decision.</summary>
    private static async Task ItemMeasuresCopyAsync(AppDbContext db, Dictionary<string, string> idMap,
        CancellationToken cancellationToken)
    {
        if (idMap.Count == 0)
        {
            return;
        }

        var oldIds = idMap.Keys.ToList();
        var measures = await db.MeetingMeasures.AsNoTracking()
            .Where(m => oldIds.Contains(m.AgendaItemId))
            .ToListAsync(cancellationToken);
        foreach (var measure in measures)
        {
            // a second pointer at the same job or follow-up; the old item keeps its own
            db.MeetingMeasures.Add(new MeetingMeasure
            {
                AgendaItemId = idMap[measure.AgendaItemId],
                Kind = measure.Kind,
                TargetId = measure.TargetId,
            });
        }
    }

    /// <summary>Carries the links of copied items over; a carried item without its record reference is worthless.</summary>
    private static async Task ItemLinksCopyAsync(AppDbContext db, Dictionary<string, string> idMap, CancellationToken cancellationToken)
    {
        if (idMap.Count == 0)
        {
            return;
        }

        var oldIds = idMap.Keys.ToList();
        var links = await db.Links
            .Where(v => (v.SourceType == nameof(MeetingAgendaItem) && oldIds.Contains(v.SourceId))
                     || (v.TargetType == nameof(MeetingAgendaItem) && oldIds.Contains(v.TargetId)))
            .ToListAsync(cancellationToken);

        foreach (var link in links)
        {
            // remap each endpoint independently: a link between two copied items must point at both new ids
            var newSource = link.SourceType == nameof(MeetingAgendaItem) && idMap.TryGetValue(link.SourceId, out var s) ? s : link.SourceId;
            var newTarget = link.TargetType == nameof(MeetingAgendaItem) && idMap.TryGetValue(link.TargetId, out var t) ? t : link.TargetId;
            db.Links.Add(new Link
            {
                SourceType = link.SourceType,
                SourceId = newSource,
                TargetType = link.TargetType,
                TargetId = newTarget,
                Label = link.Label,
                Kind = link.Kind,
                Automatic = link.Automatic,
            });
        }
    }

    // ---- agenda ----

    public async Task<List<MeetingAgendaItem>> GetAgendaAsync(string meetingId, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // fail-closed: rank/supervision see it at once, everyone else only 2h after the meeting
        if (!scope.MayAgenda && !await AgendaOpenAsync(db, meetingId, scope, cancellationToken))
        {
            return new();
        }

        return await db.MeetingAgendaItems.AsNoTracking()
            .Where(p => p.MeetingId == meetingId)
            .OrderBy(p => p.Sorting)
            .ToListAsync(cancellationToken);
    }

    public async Task<string?> GetItemNoteAsync(string itemId, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!scope.MayAgenda && !await ItemAgendaOpenAsync(db, itemId, scope, cancellationToken))
        {
            return null;
        }

        return await db.MeetingAgendaItems.AsNoTracking()
            .Where(p => p.Id == itemId).Select(p => p.NotesHtml)
            .FirstOrDefaultAsync(cancellationToken);
    }

    // recompute the time gate server-side so a caller can never widen it
    private static async Task<bool> AgendaOpenAsync(AppDbContext db, string meetingId, ViewerScope scope, CancellationToken cancellationToken)
    {
        var when = await db.Meetings.AsNoTracking()
            .Where(m => m.Id == meetingId)
            .Select(m => new { m.Start, m.End })
            .FirstOrDefaultAsync(cancellationToken);
        return when is not null && MeetingVisibility.MayReadAgenda(scope, when.Start, when.End, DateTime.UtcNow);
    }

    private static async Task<bool> ItemAgendaOpenAsync(AppDbContext db, string itemId, ViewerScope scope, CancellationToken cancellationToken)
    {
        var when = await db.MeetingAgendaItems.AsNoTracking()
            .Where(p => p.Id == itemId)
            .Join(db.Meetings, p => p.MeetingId, m => m.Id, (p, m) => new { m.Start, m.End })
            .FirstOrDefaultAsync(cancellationToken);
        return when is not null && MeetingVisibility.MayReadAgenda(scope, when.Start, when.End, DateTime.UtcNow);
    }

    public async Task<Dictionary<string, IReadOnlyList<LinkDisplay>>> GetAgendaLinksAsync(
        string meetingId, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, IReadOnlyList<LinkDisplay>>(StringComparer.Ordinal);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!scope.MayAgenda && !await AgendaOpenAsync(db, meetingId, scope, cancellationToken))
        {
            return result;
        }

        var itemIds = await db.MeetingAgendaItems.AsNoTracking()
            .Where(p => p.MeetingId == meetingId).Select(p => p.Id)
            .ToListAsync(cancellationToken);

        // delegate to the same resolver the expanded panel uses, so a chip never names
        // what the panel hides and never drops a type the panel would show
        foreach (var itemId in itemIds)
        {
            result[itemId] = await links.GetForRecordAsync(nameof(MeetingAgendaItem), itemId, scope, LinkKind.Default, cancellationToken);
        }
        return result;
    }

    public async Task<MeetingAgendaItem> AgendaItemCreateAsync(string meetingId, MeetingAgendaItemInput input,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireMeetingWrite(actor);
        if (string.IsNullOrWhiteSpace(input.Title))
        {
            throw new InvalidOperationException("Bitte einen Titel für den Tagesordnungspunkt angeben.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Meetings.AnyAsync(m => m.Id == meetingId, cancellationToken))
        {
            throw new InvalidOperationException($"Besprechung '{meetingId}' nicht gefunden.");
        }

        var max = await db.MeetingAgendaItems
            .Where(p => p.MeetingId == meetingId)
            .Select(p => (int?)p.Sorting)
            .MaxAsync(cancellationToken) ?? 0;

        var item = new MeetingAgendaItem
        {
            MeetingId = meetingId,
            Title = input.Title.Trim(),
            Sorting = max + SortingStep,
        };
        db.MeetingAgendaItems.Add(item);
        await db.SaveChangesAsync(cancellationToken);
        return item;
    }

    public async Task AgendaItemRefreshAsync(string itemId, MeetingAgendaItemInput input, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireMeetingWrite(actor);
        if (string.IsNullOrWhiteSpace(input.Title))
        {
            throw new InvalidOperationException("Bitte einen Titel für den Tagesordnungspunkt angeben.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.MeetingAgendaItems.FirstOrDefaultAsync(p => p.Id == itemId, cancellationToken)
            ?? throw new InvalidOperationException($"Tagesordnungspunkt '{itemId}' nicht gefunden.");

        item.Title = input.Title.Trim();
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AgendaItemRemoveAsync(string itemId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireMeetingWrite(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.MeetingAgendaItems.FirstOrDefaultAsync(p => p.Id == itemId, cancellationToken)
            ?? throw new InvalidOperationException($"Tagesordnungspunkt '{itemId}' nicht gefunden.");

        db.MeetingAgendaItems.Remove(item);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AgendaItemMoveAsync(string itemId, int delta, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireMeetingWrite(actor);
        if (delta == 0)
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.MeetingAgendaItems.FirstOrDefaultAsync(p => p.Id == itemId, cancellationToken)
            ?? throw new InvalidOperationException($"Tagesordnungspunkt '{itemId}' nicht gefunden.");

        // swap sort keys with the adjacent item in the requested direction
        var neighbour = delta < 0
            ? await db.MeetingAgendaItems
                .Where(p => p.MeetingId == item.MeetingId && p.Sorting < item.Sorting)
                .OrderByDescending(p => p.Sorting).FirstOrDefaultAsync(cancellationToken)
            : await db.MeetingAgendaItems
                .Where(p => p.MeetingId == item.MeetingId && p.Sorting > item.Sorting)
                .OrderBy(p => p.Sorting).FirstOrDefaultAsync(cancellationToken);
        if (neighbour is null)
        {
            return;
        }

        (item.Sorting, neighbour.Sorting) = (neighbour.Sorting, item.Sorting);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AgendaItemNoteAsync(string itemId, string? html, bool done, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.MeetingAgendaItems.FirstOrDefaultAsync(p => p.Id == itemId, cancellationToken)
            ?? throw new InvalidOperationException($"Tagesordnungspunkt '{itemId}' nicht gefunden.");

        item.NotesHtml = string.IsNullOrWhiteSpace(html) ? null : HtmlCleanup.Clean(html);
        if (done != item.Done)
        {
            item.Done = done;
            item.DoneAt = done ? DateTime.UtcNow : null;
            item.DoneById = done ? actor.GetAgentId() : null;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task MinutesAsync(string meetingId, string? html, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var meeting = await db.Meetings.FirstOrDefaultAsync(m => m.Id == meetingId, cancellationToken)
            ?? throw new InvalidOperationException($"Besprechung '{meetingId}' nicht gefunden.");

        meeting.MinutesHtml = string.IsNullOrWhiteSpace(html) ? null : HtmlCleanup.Clean(html);
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- attendance ----

    public async Task<List<MeetingAttendanceRow>> GetAttendanceAsync(string meetingId, bool mayReason, string? meId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var meeting = await db.Meetings.AsNoTracking()
            .Where(m => m.Id == meetingId)
            .Select(m => new { m.Start, m.AttendanceClosedAt })
            .FirstOrDefaultAsync(cancellationToken);
        if (meeting is null)
        {
            return new();
        }

        // frozen history: read the snapshot, never re-derive
        if (meeting.AttendanceClosedAt is not null)
        {
            var frozen = await db.MeetingAttendances.AsNoTracking()
                .Where(t => t.MeetingId == meetingId)
                .OrderBy(t => t.AgentCodename)
                .Select(t => new
                {
                    t.AgentId,
                    t.AgentCodename,
                    LiveCodename = t.Agent!.Codename,
                    Rank = t.Agent!.Rank,
                    t.Status,
                    t.Origin,
                    t.Reason,
                    t.MarkedAt,
                })
                .ToListAsync(cancellationToken);

            // fall back to the live codename for rows ticked before the agent left the roster
            return frozen.Select(t => new MeetingAttendanceRow(
                t.AgentId, t.AgentCodename ?? t.LiveCodename ?? "—", t.Rank, t.Status, t.Origin,
                mayReason || t.AgentId == meId ? t.Reason : null,
                null, null, t.MarkedAt)).ToList();
        }

        // materialised before the query: a time-zone conversion is not translatable
        var day = MeetingTime.Day(meeting.Start);

        var roster = await Roster(db, meeting.Start).AsNoTracking()
            .Select(u => new { u.Id, u.Codename, u.Rank })
            .ToListAsync(cancellationToken);
        var marked = await db.MeetingAttendances.AsNoTracking()
            .Where(t => t.MeetingId == meetingId)
            .Select(t => new { t.AgentId, t.Status, t.MarkedAt })
            .ToListAsync(cancellationToken);
        var signOff = await db.MeetingSignOffs.AsNoTracking()
            .Where(s => s.MeetingId == meetingId)
            .Select(s => new { s.AgentId, s.Reason })
            .ToListAsync(cancellationToken);
        // an absence only excuses a meeting if it was filed before the meeting began
        var meetingStart = meeting.Start;
        var covering = await db.Absences.AsNoTracking().Covering(day)
            .Where(a => a.CreatedAt <= meetingStart)
            .OrderBy(a => a.FromDate).ThenBy(a => a.ToDate)
            .Select(a => new { a.AgentId, a.FromDate, a.ToDate, a.Category, a.Reason })
            .ToListAsync(cancellationToken);

        var markedBy = marked.ToDictionary(t => t.AgentId, StringComparer.Ordinal);
        var signOffBy = signOff.ToDictionary(s => s.AgentId, StringComparer.Ordinal);
        // deterministic pick when two absences overlap the same day
        var absenceBy = covering
            .GroupBy(a => a.AgentId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var rows = new List<MeetingAttendanceRow>(roster.Count);
        foreach (var agent in roster)
        {
            var mine = agent.Id == meId;
            markedBy.TryGetValue(agent.Id, out var mark);

            // present beats everything: showing up despite a sign-off still counts as attending
            if (mark is { Status: MeetingAttendanceStatus.Present })
            {
                rows.Add(new MeetingAttendanceRow(agent.Id, agent.Codename, agent.Rank,
                    MeetingAttendanceStatus.Present, MeetingAbsenceOrigin.None, null, null, null, mark.MarkedAt));
                continue;
            }
            if (mark is { Status: MeetingAttendanceStatus.Missing })
            {
                rows.Add(new MeetingAttendanceRow(agent.Id, agent.Codename, agent.Rank,
                    MeetingAttendanceStatus.Missing, MeetingAbsenceOrigin.Manual, null, null, null, mark.MarkedAt));
                continue;
            }
            // leadership marked them excused by hand
            if (mark is { Status: MeetingAttendanceStatus.SignedOff })
            {
                rows.Add(new MeetingAttendanceRow(agent.Id, agent.Codename, agent.Rank,
                    MeetingAttendanceStatus.SignedOff, MeetingAbsenceOrigin.Manual, null, null, null, mark.MarkedAt));
                continue;
            }
            // the per-meeting sign-off is the more specific explanation
            if (signOffBy.TryGetValue(agent.Id, out var s))
            {
                rows.Add(new MeetingAttendanceRow(agent.Id, agent.Codename, agent.Rank,
                    MeetingAttendanceStatus.SignedOff, MeetingAbsenceOrigin.MeetingSignOff,
                    mayReason || mine ? s.Reason : null, null, null, null));
                continue;
            }
            if (absenceBy.TryGetValue(agent.Id, out var a))
            {
                rows.Add(new MeetingAttendanceRow(agent.Id, agent.Codename, agent.Rank,
                    MeetingAttendanceStatus.SignedOff, MeetingAbsenceOrigin.Absence,
                    mayReason || mine ? a.Reason : null, a.FromDate, a.ToDate, null));
                continue;
            }
            rows.Add(new MeetingAttendanceRow(agent.Id, agent.Codename, agent.Rank,
                MeetingAttendanceStatus.Open, MeetingAbsenceOrigin.None, null, null, null, null));
        }

        return rows
            .OrderBy(r => r.Status switch
            {
                MeetingAttendanceStatus.Missing => 0,
                MeetingAttendanceStatus.Open => 1,
                MeetingAttendanceStatus.Present => 2,
                _ => 3,
            })
            .ThenBy(r => r.Codename)
            .ToList();
    }

    public async Task AttendanceSetAsync(string meetingId, string agentId, MeetingAttendanceStatus status,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var meeting = await db.Meetings.AsNoTracking()
            .Where(m => m.Id == meetingId)
            .Select(m => new { m.AttendanceClosedAt })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Besprechung '{meetingId}' nicht gefunden.");
        if (meeting.AttendanceClosedAt is not null)
        {
            throw new InvalidOperationException("Die Anwesenheit ist bereits abgeschlossen und kann nicht mehr geändert werden.");
        }

        var existing = await db.MeetingAttendances
            .Where(t => t.MeetingId == meetingId && t.AgentId == agentId)
            .ToListAsync(cancellationToken);

        // Open means "no explicit decision", so the row goes away entirely.
        if (status == MeetingAttendanceStatus.Open)
        {
            if (existing.Count > 0)
            {
                db.MeetingAttendances.RemoveRange(existing);
                await db.SaveChangesAsync(cancellationToken);
            }
            return;
        }

        var row = existing.FirstOrDefault();
        if (row is null)
        {
            row = new MeetingAttendance { MeetingId = meetingId, AgentId = agentId };
            db.MeetingAttendances.Add(row);
        }
        row.Status = status;
        // a hand-set Missing or SignedOff is a leadership decision; Present carries no absence reason
        row.Origin = status is MeetingAttendanceStatus.Missing or MeetingAttendanceStatus.SignedOff
            ? MeetingAbsenceOrigin.Manual : MeetingAbsenceOrigin.None;
        row.MarkedAt = DateTime.UtcNow;
        row.MarkedById = actor.GetAgentId();

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // the unique index caught a concurrent tick; the page must reload to see the winner
            throw new InvalidOperationException(
                "Die Anwesenheit wurde gerade von jemand anderem geändert. Bitte die Seite neu laden.");
        }
    }

    public async Task CloseAttendanceAsync(string meetingId, bool confirmAllMissing, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var meeting = await db.Meetings.FirstOrDefaultAsync(m => m.Id == meetingId, cancellationToken)
            ?? throw new InvalidOperationException($"Besprechung '{meetingId}' nicht gefunden.");
        if (meeting.AttendanceClosedAt is not null)
        {
            throw new InvalidOperationException("Die Anwesenheit dieser Besprechung ist bereits abgeschlossen.");
        }

        var day = MeetingTime.Day(meeting.Start);

        var roster = await Roster(db, meeting.Start).AsNoTracking()
            .Select(u => new { u.Id, u.Codename })
            .ToListAsync(cancellationToken);
        var existing = await db.MeetingAttendances
            .Where(t => t.MeetingId == meetingId)
            .ToListAsync(cancellationToken);
        var signOff = await db.MeetingSignOffs.AsNoTracking()
            .Where(s => s.MeetingId == meetingId)
            .Select(s => new { s.AgentId, s.Reason })
            .ToListAsync(cancellationToken);
        // an absence only excuses a meeting if it was filed before the meeting began
        var meetingStart = meeting.Start;
        var covering = await db.Absences.AsNoTracking().Covering(day)
            .Where(a => a.CreatedAt <= meetingStart)
            .OrderBy(a => a.FromDate).ThenBy(a => a.ToDate)
            .Select(a => new { a.AgentId, a.Category, a.Reason })
            .ToListAsync(cancellationToken);

        var existingBy = existing.ToDictionary(t => t.AgentId, StringComparer.Ordinal);
        var signOffBy = signOff.ToDictionary(s => s.AgentId, StringComparer.Ordinal);
        var absenceBy = covering
            .GroupBy(a => a.AgentId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var fresh = new List<MeetingAttendance>();
        foreach (var agent in roster)
        {
            if (existingBy.TryGetValue(agent.Id, out var row))
            {
                row.AgentCodename = agent.Codename;
                continue;
            }

            var created = new MeetingAttendance
            {
                MeetingId = meetingId,
                AgentId = agent.Id,
                AgentCodename = agent.Codename,
                MarkedAt = DateTime.UtcNow,
            };
            if (signOffBy.TryGetValue(agent.Id, out var s))
            {
                created.Status = MeetingAttendanceStatus.SignedOff;
                created.Origin = MeetingAbsenceOrigin.MeetingSignOff;
                created.Reason = s.Reason;
            }
            else if (absenceBy.TryGetValue(agent.Id, out var a))
            {
                created.Status = MeetingAttendanceStatus.SignedOff;
                created.Origin = MeetingAbsenceOrigin.Absence;
                created.Reason = a.Reason ?? AbsenceCategoryDisplay.Name(a.Category);
            }
            else
            {
                created.Status = MeetingAttendanceStatus.Missing;
                created.Origin = MeetingAbsenceOrigin.None;
            }
            fresh.Add(created);
        }

        // a roster that is 100% missing almost always means nobody ticked anything
        var all = existing.Select(t => t.Status).Concat(fresh.Select(t => t.Status)).ToList();
        if (!confirmAllMissing && all.Count > 0 && all.All(s => s == MeetingAttendanceStatus.Missing))
        {
            throw new InvalidOperationException(
                "Es wurde niemand als anwesend oder abgemeldet erfasst. Bitte den Abschluss ausdrücklich bestätigen.");
        }

        db.MeetingAttendances.AddRange(fresh);
        meeting.AttendanceClosedAt = DateTime.UtcNow;
        // do not clobber a deliberate Canceled/Postponed; only a planned meeting becomes held
        if (meeting.Status == MeetingStatus.Planned)
        {
            meeting.Status = MeetingStatus.Held;
        }
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task ReopenAttendanceAsync(string meetingId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var meeting = await db.Meetings.FirstOrDefaultAsync(m => m.Id == meetingId, cancellationToken)
            ?? throw new InvalidOperationException($"Besprechung '{meetingId}' nicht gefunden.");
        // without this a second, stale reopen would wipe rows that were already re-ticked
        if (meeting.AttendanceClosedAt is null)
        {
            throw new InvalidOperationException("Die Anwesenheit dieser Besprechung ist nicht abgeschlossen.");
        }

        var rows = await db.MeetingAttendances
            .Where(t => t.MeetingId == meetingId)
            .ToListAsync(cancellationToken);
        db.MeetingAttendances.RemoveRange(rows);

        meeting.AttendanceClosedAt = null;
        // reopening undoes only the close-induced Held; a Canceled/Postponed meeting keeps its status
        if (meeting.Status == MeetingStatus.Held)
        {
            meeting.Status = MeetingStatus.Planned;
        }
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    // ---- per-meeting sign-off ----

    public async Task SignOffAsync(string meetingId, string? reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);

        var agentId = actor.GetAgentId()
            ?? throw new UnauthorizedAccessException("Ohne Agenten-Kontext ist keine Abmeldung möglich.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var meeting = await db.Meetings.AsNoTracking()
            .Where(m => m.Id == meetingId)
            .Select(m => new { m.Start, m.AttendanceClosedAt })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Besprechung '{meetingId}' nicht gefunden.");
        if (meeting.AttendanceClosedAt is not null)
        {
            throw new InvalidOperationException("Diese Besprechung ist bereits abgeschlossen.");
        }
        // a no-show must not excuse themselves afterwards; only leadership may still correct the roster
        if (meeting.Start <= DateTime.UtcNow)
        {
            throw new InvalidOperationException(
                "Die Besprechung hat bereits begonnen – eine nachträgliche Abmeldung ist nicht möglich.");
        }

        if (await db.MeetingSignOffs.AnyAsync(s => s.MeetingId == meetingId && s.AgentId == agentId, cancellationToken))
        {
            return;
        }

        db.MeetingSignOffs.Add(new MeetingSignOff
        {
            MeetingId = meetingId,
            AgentId = agentId,
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim(),
        });
        await db.SaveChangesAsync(cancellationToken);

        await MentionNotify.DeltaAsync(notifications, null, reason, "einer Abmeldung",
            nameof(Meeting), meetingId, actor, cancellationToken);
    }

    public async Task SignOffRevokeAsync(string meetingId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);

        var agentId = actor.GetAgentId()
            ?? throw new UnauthorizedAccessException("Ohne Agenten-Kontext ist keine Abmeldung möglich.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.MeetingSignOffs
            .Where(s => s.MeetingId == meetingId && s.AgentId == agentId)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return;
        }

        db.MeetingSignOffs.RemoveRange(rows);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Best effort; absent agents are told too, a new meeting is news for them as well.</summary>
    private async Task AnnounceAsync(AppDbContext db, Meeting meeting, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        try
        {
            var recipients = await Roster(db, meeting.Start).AsNoTracking()
                .Select(u => u.Id)
                .ToListAsync(cancellationToken);

            var title = $"Neue Besprechung: „{meeting.Title}“ am {MeetingTime.Text(meeting.Start)} Uhr.";
            await notifications.NotifyManyAsync(recipients, NotificationType.MeetingScheduled,
                title.Length > 300 ? title[..297] + "…" : title,
                $"/besprechungen/{meeting.Id}", actor.GetAgentId(), cancellationToken);
        }
        catch { /* best effort */ }
    }
}
