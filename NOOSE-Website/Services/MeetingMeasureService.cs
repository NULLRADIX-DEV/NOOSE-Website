using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Meetings;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Jobs;
using NOOSE_Website.Models.Meetings;

namespace NOOSE_Website.Services;

/// <summary>Measures decided under an agenda item: a job or a follow-up, and their live status.</summary>
public interface IMeetingMeasureService
{
    /// <summary>Creates the job or follow-up through its own service and ties it to the agenda item.</summary>
    Task<MeetingMeasureRow> CreateAsync(string agendaItemId, MeetingMeasureInput input, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    /// <summary>Measures of every item of one meeting, keyed by item id; empty when the agenda is not readable.</summary>
    Task<Dictionary<string, IReadOnlyList<MeetingMeasureRow>>> GetForMeetingAsync(string meetingId,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IMeetingMeasureService" />
/// <remarks>
/// Builds no job and no follow-up itself: <c>JobService</c> keeps its case number, assignment rule and notification,
/// <c>FollowupService</c> its visibility gate and responsible rule. This service only adds the row that says which
/// item the measure belongs to. A follow-up hangs off the meeting, not the item, because the dashboard, the calendar
/// and the reminder resolve a meeting but not an agenda item; it would vanish from all three.
/// </remarks>
public sealed class MeetingMeasureService(
    IDbContextFactory<AppDbContext> dbFactory,
    IJobService jobs,
    IFollowupService followups) : IMeetingMeasureService
{
    /// <summary>Job title column.</summary>
    public const int MaxTitleLength = 200;

    public const int MaxNoteLength = 500;

    /// <summary>Follow-up note column.</summary>
    private const int MaxFollowupNoteLength = 500;

    public async Task<MeetingMeasureRow> CreateAsync(string agendaItemId, MeetingMeasureInput input,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        // the same right as ticking an item off; write check first, so the read-only supervision fails before a
        // job number is minted
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);

        var title = (input.Title ?? string.Empty).Trim();
        if (input.Kind == MeetingMeasureKind.Aufgabe && title.Length == 0)
        {
            throw new InvalidOperationException("Eine Aufgabe braucht einen Titel.");
        }
        if (title.Length > MaxTitleLength)
        {
            throw new InvalidOperationException($"Der Titel fasst höchstens {MaxTitleLength} Zeichen.");
        }
        if ((input.Note?.Trim().Length ?? 0) > MaxNoteLength)
        {
            throw new InvalidOperationException($"Die Notiz fasst höchstens {MaxNoteLength} Zeichen.");
        }
        if (input.Kind == MeetingMeasureKind.Wiedervorlage && input.DueAt is null)
        {
            throw new InvalidOperationException("Eine Wiedervorlage braucht ein Fälligkeitsdatum.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var item = await db.MeetingAgendaItems.AsNoTracking()
            .Where(p => p.Id == agendaItemId)
            .Join(db.Meetings, p => p.MeetingId, m => m.Id,
                (p, m) => new { p.Id, p.Title, p.MeetingId })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Tagesordnungspunkt nicht gefunden.");
        // leadership sees every agenda today; the check keeps it that way if the right above ever widens
        if (!await Visibility.IsRecordVisibleAsync(db, nameof(MeetingAgendaItem), item.Id, ViewerScope.From(actor),
                cancellationToken))
        {
            throw new UnauthorizedAccessException("Diesen Tagesordnungspunkt darfst du nicht sehen.");
        }

        string targetId;
        if (input.Kind == MeetingMeasureKind.Aufgabe)
        {
            // the mention makes the meeting a link in the job's description, the way back from the job
            var origin = $"Maßnahme aus der Besprechung {MentionParser.Token(nameof(Meeting), item.MeetingId)}, "
                + $"Punkt „{item.Title}“.";
            var job = await jobs.CreateAsync(new JobInput
            {
                Title = title,
                Description = string.IsNullOrWhiteSpace(input.Note) ? origin : $"{origin}\n\n{input.Note.Trim()}",
                DueDate = input.DueAt,
            }, ResponsibleOf(input, actor), actor, cancellationToken);
            targetId = job.Id;
        }
        else
        {
            var note = string.IsNullOrWhiteSpace(input.Note)
                ? $"Punkt „{item.Title}“"
                : $"Punkt „{item.Title}“: {input.Note.Trim()}";
            // the follow-up column is narrower than an item title plus a note
            if (note.Length > MaxFollowupNoteLength)
            {
                note = note[..MaxFollowupNoteLength].TrimEnd();
            }
            targetId = await followups.CreateAsync(nameof(Meeting), item.MeetingId,
                new FollowupInput(input.DueAt!.Value, note, input.ResponsibleAgentId), actor, cancellationToken);
        }

        var measure = new MeetingMeasure { AgendaItemId = item.Id, Kind = input.Kind, TargetId = targetId };
        db.MeetingMeasures.Add(measure);
        await db.SaveChangesAsync(cancellationToken);

        var rows = await ResolveAsync(db, [measure], item.MeetingId, actor, cancellationToken);
        return rows.Single();
    }

    public async Task<Dictionary<string, IReadOnlyList<MeetingMeasureRow>>> GetForMeetingAsync(string meetingId,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, IReadOnlyList<MeetingMeasureRow>>(StringComparer.Ordinal);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var itemIds = await db.MeetingAgendaItems.AsNoTracking()
            .Where(p => p.MeetingId == meetingId)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);
        // agenda visibility hangs on the meeting and its time, so one item answers for all of them
        if (itemIds.Count == 0
            || !await Visibility.IsRecordVisibleAsync(db, nameof(MeetingAgendaItem), itemIds[0],
                ViewerScope.From(actor), cancellationToken))
        {
            return result;
        }

        var measures = await db.MeetingMeasures.AsNoTracking()
            .Where(m => itemIds.Contains(m.AgendaItemId))
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .ToListAsync(cancellationToken);
        if (measures.Count == 0)
        {
            return result;
        }

        var rows = await ResolveAsync(db, measures, meetingId, actor, cancellationToken);
        var byId = rows.ToDictionary(r => r.Id, StringComparer.Ordinal);
        foreach (var group in measures.GroupBy(m => m.AgendaItemId))
        {
            result[group.Key] = group.Where(m => byId.ContainsKey(m.Id)).Select(m => byId[m.Id]).ToList();
        }
        return result;
    }

    /// <summary>Picked assignee, or the creator; the same default a follow-up applies.</summary>
    private static IReadOnlyList<string> ResponsibleOf(MeetingMeasureInput input, ClaimsPrincipal actor)
        => !string.IsNullOrWhiteSpace(input.ResponsibleAgentId)
            ? [input.ResponsibleAgentId!]
            : actor.GetAgentId() is { Length: > 0 } me ? [me] : [];

    /// <summary>Reads each measure's target; a deleted target or a job the viewer may not see drops out.</summary>
    private static async Task<List<MeetingMeasureRow>> ResolveAsync(AppDbContext db,
        IReadOnlyCollection<MeetingMeasure> measures, string meetingId, ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var jobIds = measures.Where(m => m.Kind == MeetingMeasureKind.Aufgabe).Select(m => m.TargetId).ToList();
        var followupIds = measures.Where(m => m.Kind == MeetingMeasureKind.Wiedervorlage)
            .Select(m => m.TargetId).ToList();

        var jobRows = jobIds.Count == 0
            ? []
            : await db.Jobs.AsNoTracking()
                .OnlyVisible(db, actor.MayClassifiedRead(), actor.GetAgentId())
                .Where(j => jobIds.Contains(j.Id))
                .Select(j => new { j.Id, j.Title, j.Status, j.DueDate })
                .ToListAsync(cancellationToken);
        var assignees = jobRows.Count == 0
            ? []
            : await db.JobAssignments.AsNoTracking()
                .Where(a => jobIds.Contains(a.JobId))
                .Join(db.Users, a => a.AgentId, u => u.Id, (a, u) => new { a.JobId, u.Codename })
                .ToListAsync(cancellationToken);
        var followupRows = followupIds.Count == 0
            ? []
            : await db.Followups.AsNoTracking()
                .Where(f => followupIds.Contains(f.Id))
                .Select(f => new
                {
                    f.Id,
                    f.Note,
                    f.Done,
                    f.DueAt,
                    Responsible = db.Users.Where(u => u.Id == f.ResponsibleAgentId).Select(u => u.Codename)
                        .FirstOrDefault(),
                })
                .ToListAsync(cancellationToken);

        var rows = new List<MeetingMeasureRow>();
        foreach (var measure in measures)
        {
            if (measure.Kind == MeetingMeasureKind.Aufgabe
                && jobRows.FirstOrDefault(j => j.Id == measure.TargetId) is { } job)
            {
                var names = assignees.Where(a => a.JobId == job.Id).Select(a => a.Codename).ToList();
                rows.Add(new MeetingMeasureRow(measure.Id, measure.Kind, job.Title,
                    JobStatusDisplay.IsCompleted(job.Status), JobStatusDisplay.Name(job.Status), job.DueDate,
                    names.Count == 0 ? null : string.Join(", ", names), $"/aufgaben/{job.Id}"));
            }
            else if (measure.Kind == MeetingMeasureKind.Wiedervorlage
                && followupRows.FirstOrDefault(f => f.Id == measure.TargetId) is { } followup)
            {
                rows.Add(new MeetingMeasureRow(measure.Id, measure.Kind, followup.Note ?? "Wiedervorlage",
                    followup.Done, followup.Done ? "Erledigt" : "Offen", followup.DueAt.ToLocalTime(),
                    followup.Responsible, $"/besprechungen/{meetingId}"));
            }
        }
        return rows;
    }
}
