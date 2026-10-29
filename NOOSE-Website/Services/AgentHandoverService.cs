using System.Linq.Expressions;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Appointments;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Financing;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Informants;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Personnel;

namespace NOOSE_Website.Services;

/// <summary>Handover of a leaving agent's open work to successors.</summary>
public interface IAgentHandoverService
{
    /// <summary>Everything still hanging on the agent that a successor can take over or the departure closes.</summary>
    Task<HandoverPlan> GetAsync(string agentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Moves the chosen rows in one save and sends each successor one summary notice.</summary>
    Task<HandoverResult> ApplyAsync(string agentId, IReadOnlyList<HandoverChoice> choices, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAgentHandoverService" />
/// <remarks>
/// Writes the assignment tables directly instead of going through the eight owning services: their add and remove
/// calls each save on their own, so a handover broken off halfway would leave records with neither agent. Their
/// rules still hold here: only selectable successors, leadership with write access, and nothing the operator could
/// not open. A row is re-read on apply, so one that changed hands while the dialog was open is skipped.
/// </remarks>
public sealed class AgentHandoverService(
    IDbContextFactory<AppDbContext> dbFactory,
    INotificationService notifications) : IAgentHandoverService
{
    /// <summary>Decision note on an approved request the departure rejects.</summary>
    public const string DepartureNote = "Antragsteller ausgeschieden";

    private const string CaseLead = "Fallführer";
    private const string InvestigationLead = "Ermittlungsleiter";

    /// <summary>A row before the visibility check, with the record that decides it.</summary>
    private sealed record Candidate(HandoverRow Row, string RecordType, string RecordId);

    public async Task<HandoverPlan> GetAsync(string agentId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        RequireOperator(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var codename = await LeavingAgentAsync(db, agentId, actor, cancellationToken);
        var (rows, hidden) = await CollectAsync(db, agentId, ViewerScope.From(actor), cancellationToken);
        return new HandoverPlan(agentId, codename, rows, hidden);
    }

    public async Task<HandoverResult> ApplyAsync(string agentId, IReadOnlyList<HandoverChoice> choices,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireOperator(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var codename = await LeavingAgentAsync(db, agentId, actor, cancellationToken);

        var passing = choices.Where(c => !HandoverDisplay.Closes(c.Kind)).ToList();
        if (passing.Any(c => string.IsNullOrWhiteSpace(c.SuccessorId)))
        {
            throw new InvalidOperationException("Jede übergebene Zeile braucht einen Nachfolger.");
        }
        var successorIds = passing.Select(c => c.SuccessorId!).Distinct().ToList();
        var selectable = await db.Users.OnlySelectable().Where(u => successorIds.Contains(u.Id))
            .CountAsync(cancellationToken);
        // the leaving agent is never selectable once terminated, but may still be while only absent
        if (selectable != successorIds.Count || successorIds.Contains(agentId))
        {
            throw new InvalidOperationException("Ein gewählter Nachfolger wurde nicht gefunden oder ist nicht zuteilbar.");
        }

        // exactly what the dialog may list: still on the agent, still open, and open to the operator
        var (open, _) = await CollectAsync(db, agentId, ViewerScope.From(actor), cancellationToken);
        var listed = open.Select(r => (r.Kind, r.Id)).ToHashSet();
        var taken = new Dictionary<string, List<HandoverKind>>();
        var closed = 0;
        foreach (var choice in choices.DistinctBy(c => (c.Kind, c.Id)).Where(c => listed.Contains((c.Kind, c.Id))))
        {
            if (HandoverDisplay.Closes(choice.Kind))
            {
                await CloseAsync(db, agentId, choice, actor, cancellationToken);
                closed++;
                continue;
            }
            await PassAsync(db, agentId, choice.Kind, choice.Id, choice.SuccessorId!, cancellationToken);
            if (!taken.TryGetValue(choice.SuccessorId!, out var kinds))
            {
                taken[choice.SuccessorId!] = kinds = [];
            }
            kinds.Add(choice.Kind);
        }
        await db.SaveChangesAsync(cancellationToken);

        foreach (var (successorId, kinds) in taken.Where(t => t.Key != actor.GetAgentId()))
        {
            await notifications.NotifyAsync(successorId, NotificationType.HandoverReceived,
                Summary(codename, kinds), "/meine-schicht", cancellationToken);
        }
        return new HandoverResult(taken.Sum(t => t.Value.Count), closed);
    }

    /// <summary>"Du hast 3 Einträge von X übernommen: 1 Wiedervorlage, 2 Aufgaben."</summary>
    private static string Summary(string codename, List<HandoverKind> kinds)
    {
        var parts = kinds.GroupBy(k => k).OrderBy(g => g.Key).Select(g => HandoverDisplay.Count(g.Key, g.Count()));
        var entries = kinds.Count == 1 ? "1 Eintrag" : $"{kinds.Count} Einträge";
        return $"Du hast {entries} von {codename} übernommen: {string.Join(", ", parts)}.";
    }

    // the same right as terminating; write check first, so the read-only supervision is refused as such
    private static void RequireOperator(ClaimsPrincipal actor)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);
    }

    private static async Task<string> LeavingAgentAsync(AppDbContext db, string agentId, ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (agentId == actor.GetAgentId())
        {
            throw new InvalidOperationException("Die eigene Arbeit übergibst du nicht über die Personalakte.");
        }
        var agent = await db.Users.AsNoTracking()
            .Where(u => u.Id == agentId)
            .Select(u => new { u.Codename, u.Status })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Agent nicht gefunden.");
        if (agent.Status == AgentStatus.Applicant)
        {
            throw new InvalidOperationException("Bewerber haben keine Arbeit zu übergeben.");
        }
        return agent.Codename;
    }

    private static async Task<(List<HandoverRow> Rows, int Hidden)> CollectAsync(AppDbContext db, string agentId,
        ViewerScope scope, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var candidates = new List<Candidate>();

        var followups = await db.Followups.AsNoTracking()
            .Where(f => f.ResponsibleAgentId == agentId && !f.Done)
            .Select(f => new { f.Id, f.EntityType, f.EntityId, f.Note, f.DueAt })
            .ToListAsync(cancellationToken);
        candidates.AddRange(followups.Select(f => new Candidate(
            new HandoverRow(HandoverKind.Followup, f.Id, f.Note ?? "Wiedervorlage",
                $"{AuditEntityDisplay.Label(f.EntityType)} · fällig {MeetingTime.Day(f.DueAt):dd.MM.yyyy}",
                AuditEntityDisplay.Route(f.EntityType, f.EntityId), null),
            f.EntityType, f.EntityId)));

        var jobs = await db.JobAssignments.AsNoTracking()
            .Where(a => a.AgentId == agentId)
            .Join(db.Jobs.Where(j => j.Status == JobStatus.Open || j.Status == JobStatus.InProcessing),
                a => a.JobId, j => j.Id, (a, j) => new { j.Id, j.Title, j.CaseNumber })
            .ToListAsync(cancellationToken);
        candidates.AddRange(jobs.Select(j => Record(HandoverKind.Job, nameof(Job), j.Id, j.Title, j.CaseNumber,
            $"/aufgaben/{j.Id}", null)));

        var cases = await db.CaseAgents.AsNoTracking()
            .Where(a => a.AgentId == agentId)
            .Join(db.Cases.Where(c => !c.IsArchived && (c.Status == CaseStatus.Open
                    || c.Status == CaseStatus.InProcessing || c.Status == CaseStatus.Dormant)),
                a => a.CaseId, c => c.Id, (a, c) => new { c.Id, c.Title, c.CaseNumber, a.IsCaseLead })
            .ToListAsync(cancellationToken);
        candidates.AddRange(cases.Select(c => Record(HandoverKind.Case, nameof(Case), c.Id, c.Title, c.CaseNumber,
            $"/vorgaenge/{c.Id}", c.IsCaseLead ? CaseLead : null)));

        var operations = await db.OperationAgents.AsNoTracking()
            .Where(a => a.AgentId == agentId)
            .Join(db.Operations.Where(o => !o.IsArchived
                    && (o.Status == OperationStatus.Planned || o.Status == OperationStatus.Running)),
                a => a.OperationId, o => o.Id, (a, o) => new { o.Id, o.Title, o.CaseNumber, a.IsInvestigationLead })
            .ToListAsync(cancellationToken);
        candidates.AddRange(operations.Select(o => Record(HandoverKind.Operation, nameof(Operation), o.Id, o.Title,
            o.CaseNumber, $"/operationen/{o.Id}", o.IsInvestigationLead ? InvestigationLead : null)));

        var taskforces = await db.TaskforceAgents.AsNoTracking()
            .Where(a => a.AgentId == agentId)
            .Join(db.Taskforces.Where(t => !t.IsArchived
                    && (t.Status == TaskforceStatus.Requested || t.Status == TaskforceStatus.Approved)),
                a => a.TaskforceId, t => t.Id, (a, t) => new { t.Id, t.Name, t.CaseNumber, a.Role })
            .ToListAsync(cancellationToken);
        candidates.AddRange(taskforces.Select(t => Record(HandoverKind.Taskforce, nameof(Taskforce), t.Id, t.Name,
            t.CaseNumber, $"/taskforces/{t.Id}", TaskforceRoleDisplay.IsLead(t.Role) ? TaskforceRoleDisplay.Name(t.Role) : null)));

        // the name comes from the linked person record when there is one
        var informants = await db.Informants.AsNoTracking()
            .Where(i => i.HandlerId == agentId && i.Status != InformantStatus.Burned)
            .Select(i => new
            {
                i.Id, i.CaseNumber,
                Name = i.RealName ?? db.People.Where(p => p.Id == i.PersonId).Select(p => p.Name).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);
        candidates.AddRange(informants.Select(i => Record(HandoverKind.Informant, nameof(Informant), i.Id,
            i.Name ?? i.CaseNumber, i.CaseNumber, $"/informanten/{i.Id}", null)));

        var factions = await db.FactionAgents.AsNoTracking()
            .Where(a => a.AgentId == agentId)
            .Join(db.Factions.Where(f => !f.IsArchived), a => a.FactionId, f => f.Id,
                (a, f) => new { f.Id, f.Name, f.CaseNumber, a.IsInvestigationLead })
            .ToListAsync(cancellationToken);
        candidates.AddRange(factions.Select(f => Record(HandoverKind.Faction, nameof(Faction), f.Id, f.Name,
            f.CaseNumber, $"/fraktionen/{f.Id}", f.IsInvestigationLead ? InvestigationLead : null)));

        var groups = await db.PersonGroupAgents.AsNoTracking()
            .Where(a => a.AgentId == agentId)
            .Join(db.PersonGroups.Where(g => !g.IsArchived), a => a.PersonGroupId, g => g.Id,
                (a, g) => new { g.Id, g.Name, g.CaseNumber, a.IsInvestigationLead })
            .ToListAsync(cancellationToken);
        candidates.AddRange(groups.Select(g => Record(HandoverKind.PersonGroup, nameof(PersonGroup), g.Id, g.Name,
            g.CaseNumber, $"/personengruppen/{g.Id}", g.IsInvestigationLead ? InvestigationLead : null)));

        var parties = await db.PartyAgents.AsNoTracking()
            .Where(a => a.AgentId == agentId)
            .Join(db.Parties.Where(p => !p.IsArchived), a => a.PartyId, p => p.Id,
                (a, p) => new { p.Id, p.Name, p.CaseNumber, a.IsInvestigationLead })
            .ToListAsync(cancellationToken);
        candidates.AddRange(parties.Select(p => Record(HandoverKind.Party, nameof(Party), p.Id, p.Name,
            p.CaseNumber, $"/parteien/{p.Id}", p.IsInvestigationLead ? InvestigationLead : null)));

        var requests = await db.FinancingRequests.AsNoTracking()
            .Where(r => r.AgentId == agentId
                && (r.Status == FinancingStatus.Requested || r.Status == FinancingStatus.Approved))
            .Select(r => new { r.Id, r.Justification, r.CaseNumber, r.Status })
            .ToListAsync(cancellationToken);
        candidates.AddRange(requests.Select(r => Record(HandoverKind.FinancingRequest, nameof(FinancingRequest), r.Id,
            r.Justification, $"{r.CaseNumber} · {FinancingStatusDisplay.Name(r.Status)}", $"/finanzierungen/{r.Id}", null)));

        var appointments = await db.AppointmentAssignments.AsNoTracking()
            .Where(a => a.AgentId == agentId)
            .Join(db.Appointments.Where(t => t.Start > now && t.Status != AppointmentStatus.Canceled),
                a => a.AppointmentId, t => t.Id, (a, t) => new { t.Id, t.Title, t.Start })
            .ToListAsync(cancellationToken);
        candidates.AddRange(appointments.Select(t => Record(HandoverKind.Appointment, nameof(Appointment), t.Id,
            t.Title, MeetingTime.Text(t.Start), $"/kalender/{t.Id}", null)));

        var rows = new List<HandoverRow>();
        foreach (var candidate in candidates)
        {
            if (await Visibility.IsRecordVisibleAsync(db, candidate.RecordType, candidate.RecordId, scope,
                    cancellationToken))
            {
                rows.Add(candidate.Row);
            }
        }
        var ordered = rows.OrderBy(r => r.Kind).ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
        return (ordered, candidates.Count - rows.Count);
    }

    private static Candidate Record(HandoverKind kind, string recordType, string id, string title, string? detail,
        string href, string? role)
        => new(new HandoverRow(kind, id, title, detail, href, role), recordType, id);

    private static async Task PassAsync(AppDbContext db, string agentId, HandoverKind kind, string id,
        string successor, CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case HandoverKind.Followup:
                (await db.Followups.FirstAsync(f => f.Id == id, cancellationToken)).ResponsibleAgentId = successor;
                break;
            case HandoverKind.Informant:
                (await db.Informants.FirstAsync(i => i.Id == id, cancellationToken)).HandlerId = successor;
                break;
            case HandoverKind.Job:
                await SwapAsync(db.JobAssignments, a => a.JobId == id && a.AgentId == agentId,
                    a => a.JobId == id && a.AgentId == successor,
                    _ => new JobAssignment { JobId = id, AgentId = successor }, (_, _) => { }, cancellationToken);
                break;
            case HandoverKind.Case:
                await SwapAsync(db.CaseAgents, a => a.CaseId == id && a.AgentId == agentId,
                    a => a.CaseId == id && a.AgentId == successor,
                    old => new CaseAgent { CaseId = id, AgentId = successor, IsCaseLead = old.IsCaseLead },
                    (old, own) => own.IsCaseLead |= old.IsCaseLead, cancellationToken);
                break;
            case HandoverKind.Operation:
                await SwapAsync(db.OperationAgents, a => a.OperationId == id && a.AgentId == agentId,
                    a => a.OperationId == id && a.AgentId == successor,
                    old => new OperationAgent { OperationId = id, AgentId = successor, IsInvestigationLead = old.IsInvestigationLead },
                    (old, own) => own.IsInvestigationLead |= old.IsInvestigationLead, cancellationToken);
                break;
            case HandoverKind.Taskforce:
                // a successor who already leads keeps their own role
                await SwapAsync(db.TaskforceAgents, a => a.TaskforceId == id && a.AgentId == agentId,
                    a => a.TaskforceId == id && a.AgentId == successor,
                    old => new TaskforceAgent { TaskforceId = id, AgentId = successor, Role = old.Role },
                    (old, own) => own.Role = TaskforceRoleDisplay.IsLead(own.Role) ? own.Role : old.Role,
                    cancellationToken);
                break;
            case HandoverKind.Faction:
                await SwapAsync(db.FactionAgents, a => a.FactionId == id && a.AgentId == agentId,
                    a => a.FactionId == id && a.AgentId == successor,
                    old => new FactionAgent { FactionId = id, AgentId = successor, IsInvestigationLead = old.IsInvestigationLead },
                    (old, own) => own.IsInvestigationLead |= old.IsInvestigationLead, cancellationToken);
                break;
            case HandoverKind.PersonGroup:
                await SwapAsync(db.PersonGroupAgents, a => a.PersonGroupId == id && a.AgentId == agentId,
                    a => a.PersonGroupId == id && a.AgentId == successor,
                    old => new PersonGroupAgent { PersonGroupId = id, AgentId = successor, IsInvestigationLead = old.IsInvestigationLead },
                    (old, own) => own.IsInvestigationLead |= old.IsInvestigationLead, cancellationToken);
                break;
            case HandoverKind.Party:
                await SwapAsync(db.PartyAgents, a => a.PartyId == id && a.AgentId == agentId,
                    a => a.PartyId == id && a.AgentId == successor,
                    old => new PartyAgent { PartyId = id, AgentId = successor, IsInvestigationLead = old.IsInvestigationLead },
                    (old, own) => own.IsInvestigationLead |= old.IsInvestigationLead, cancellationToken);
                break;
        }
    }

    /// <summary>Replaces the leaving agent's membership row; a successor already there keeps theirs and gains the lead.</summary>
    private static async Task SwapAsync<T>(DbSet<T> rows, Expression<Func<T, bool>> leaving,
        Expression<Func<T, bool>> successors, Func<T, T> successorRow, Action<T, T> keepLead,
        CancellationToken cancellationToken) where T : class
    {
        var old = await rows.FirstAsync(leaving, cancellationToken);
        var own = await rows.FirstOrDefaultAsync(successors, cancellationToken);
        if (own is null)
        {
            rows.Add(successorRow(old));
        }
        else
        {
            keepLead(old, own);
        }
        rows.Remove(old);
    }

    private static async Task CloseAsync(AppDbContext db, string agentId, HandoverChoice choice, ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (choice.Kind == HandoverKind.Appointment)
        {
            db.AppointmentAssignments.Remove(await db.AppointmentAssignments
                .FirstAsync(a => a.AppointmentId == choice.Id && a.AgentId == agentId, cancellationToken));
            return;
        }

        var request = await db.FinancingRequests.Include(r => r.Lines)
            .FirstAsync(r => r.Id == choice.Id, cancellationToken);
        if (request.Status == FinancingStatus.Requested)
        {
            request.Status = FinancingStatus.Withdrawn;
            return;
        }
        // an approval cannot be withdrawn; rejecting it hands the reservation back to the budget
        FinancingService.ClearApproval(request, struckLines: true);
        request.Status = FinancingStatus.Rejected;
        request.DeciderName = actor.GetCodename();
        request.DecidedAt = DateTime.UtcNow;
        request.DecisionNote = DepartureNote;
    }
}
