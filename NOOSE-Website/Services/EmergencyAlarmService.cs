using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Infrastructure.Chat;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Operations;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IEmergencyAlarmService" />
public class EmergencyAlarmService(IDbContextFactory<AppDbContext> dbFactory, EmergencyAlarmBroadcaster broadcaster,
    INotificationService notifications, IOperationService operations) : IEmergencyAlarmService
{
    private const int ListLimit = 100;

    /// <summary>Column length of an operation title; a longer situation is cut to it.</summary>
    public const int MaxOperationTitle = 200;

    public async Task<string> TriggerAsync(EmergencyAlarmInput input, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        if (!EmergencyAlarmRules.MayTrigger(actor))
        {
            throw new UnauthorizedAccessException("Einen Einsatzalarm lösen nur interne Agenten mit Schreibrecht aus.");
        }
        var situation = input.Situation?.Trim();
        if (string.IsNullOrEmpty(situation))
        {
            throw new InvalidOperationException("Bitte die Lage angeben.");
        }
        if (situation.Length > EmergencyAlarmRules.MaxSituation)
        {
            throw new InvalidOperationException($"Die Lage darf höchstens {EmergencyAlarmRules.MaxSituation} Zeichen lang sein.");
        }
        var location = input.Location.TrimToNull();
        if (location is { Length: > EmergencyAlarmRules.MaxLocation })
        {
            throw new InvalidOperationException($"Der Ort darf höchstens {EmergencyAlarmRules.MaxLocation} Zeichen lang sein.");
        }
        if (input.Target is { } target && !Enum.IsDefined(target))
        {
            throw new InvalidOperationException("Unbekannte Abteilung.");
        }
        var isEmergency = EmergencyAlarmRules.NeedsReason(actor);
        var reason = isEmergency ? CriticalChange.RequireReason(input.Reason, "Ein Einsatzalarm bei Gefahr im Verzug") : null;
        var meId = actor.GetAgentId()!;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var recipientIds = await RecipientIdsAsync(db, input.Target, now, cancellationToken);
        var meName = await db.Users.Where(u => u.Id == meId).Select(u => u.Codename).FirstOrDefaultAsync(cancellationToken);

        var alarm = new EmergencyAlarm
        {
            Situation = situation,
            Location = location,
            Target = input.Target,
            IsEmergency = isEmergency,
            Reason = reason,
            TriggeredById = meId,
            TriggeredByName = string.IsNullOrWhiteSpace(meName) ? actor.GetCodename() ?? string.Empty : meName,
            TriggeredAt = now,
            Expiry = now + EmergencyAlarmRules.Duration,
        };
        // triggerer stands in the list as coming, inside the target or not
        alarm.Recipients.Add(new EmergencyAlarmRecipient
        {
            AgentId = meId, Answer = EmergencyAlarmAnswer.Coming, AnsweredAt = now,
        });
        foreach (var agentId in recipientIds.Where(id => id != meId))
        {
            alarm.Recipients.Add(new EmergencyAlarmRecipient { AgentId = agentId });
        }
        db.EmergencyAlarms.Add(alarm);
        await db.SaveChangesAsync(cancellationToken);

        var href = Href(alarm.Id);
        try
        {
            await notifications.NotifyManyAsync(recipientIds.Where(id => id != meId).ToList(), NotificationType.EmergencyAlarm,
                EmergencyAlarmText.Title(situation, location), href, meId, cancellationToken);
        }
        catch { /* best effort */ }
        // own try: a failed call-out must not swallow it
        if (isEmergency)
        {
            try
            {
                var leadership = await db.Users.AsNoTracking().OnlySelectable()
                    .Where(u => u.IsAdmin || u.Rank >= Rank.SectionChief)
                    .Where(u => u.Id != meId)
                    .Select(u => u.Id)
                    .ToListAsync(cancellationToken);
                await notifications.NotifyManyAsync(leadership, NotificationType.EmergencyAlarmByAgent,
                    EmergencyAlarmText.ByAgentTitle(alarm.TriggeredByName, situation), href, meId, cancellationToken);
            }
            catch { /* best effort */ }
        }

        broadcaster.Report(alarm.Id);
        return alarm.Id;
    }

    public async Task RespondAsync(string alarmId, EmergencyAlarmAnswer answer, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        RequireReader(actor);
        if (!Enum.IsDefined(answer))
        {
            throw new InvalidOperationException("Unbekannte Antwort.");
        }
        var meId = actor.GetAgentId();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var alarm = await FindAsync(db, alarmId, cancellationToken);
        if (!EmergencyAlarmRules.IsOpen(alarm, DateTime.UtcNow))
        {
            throw new InvalidOperationException("Der Einsatzalarm ist beendet, Antworten sind nicht mehr möglich.");
        }
        var row = await db.EmergencyAlarmRecipients
            .FirstOrDefaultAsync(r => r.AlarmId == alarmId && r.AgentId == meId, cancellationToken)
            ?? throw new UnauthorizedAccessException("Dieser Einsatzalarm ruft dich nicht.");

        row.Answer = answer;
        row.AnsweredAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        broadcaster.Report(alarmId);
    }

    public async Task RepingAsync(string alarmId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        RequireReader(actor);
        var meId = actor.GetAgentId();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var alarm = await FindAsync(db, alarmId, cancellationToken);
        if (alarm.TriggeredById != meId)
        {
            throw new UnauthorizedAccessException("Nachpingen kann nur, wer den Einsatzalarm ausgelöst hat.");
        }
        if (!EmergencyAlarmRules.IsOpen(alarm, DateTime.UtcNow))
        {
            throw new InvalidOperationException("Der Einsatzalarm ist beendet.");
        }
        if (alarm.RepingedAt is not null)
        {
            throw new InvalidOperationException("Dieser Einsatzalarm wurde schon einmal nachgepingt.");
        }

        // claim once: two tabs may race here
        var now = DateTime.UtcNow;
        var claimed = await db.EmergencyAlarms
            .Where(a => a.Id == alarmId && a.RepingedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.RepingedAt, now)
                .SetProperty(a => a.ModifiedAt, now)
                .SetProperty(a => a.ModifiedById, meId), cancellationToken);
        if (claimed == 0)
        {
            throw new InvalidOperationException("Dieser Einsatzalarm wurde schon einmal nachgepingt.");
        }
        // ExecuteUpdate bypasses the interceptors
        db.AuditLogs.Add(ManualAudit.Row(nameof(EmergencyAlarm), alarmId, AuditAction.Modified, actor,
            ManualAudit.Change("Nachgepingt", null, now)));
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var open = await db.EmergencyAlarmRecipients.AsNoTracking()
                .Where(r => r.AlarmId == alarmId && r.Answer == null && r.AgentId != meId)
                .Select(r => r.AgentId)
                .ToListAsync(cancellationToken);
            await notifications.NotifyManyAsync(open, NotificationType.EmergencyAlarm,
                EmergencyAlarmText.RepingTitle(alarm.Situation, alarm.Location), Href(alarmId), meId, cancellationToken);
        }
        catch { /* best effort */ }

        broadcaster.Report(alarmId);
    }

    public async Task EndAsync(string alarmId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        RequireReader(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var alarm = await FindAsync(db, alarmId, cancellationToken);
        if (!EmergencyAlarmRules.MayEnd(actor, alarm))
        {
            throw new UnauthorizedAccessException("Beenden kann nur, wer den Einsatzalarm ausgelöst hat, oder die Führung.");
        }
        // expired ones are closed already, the sweep stamps them
        var now = DateTime.UtcNow;
        if (!EmergencyAlarmRules.IsOpen(alarm, now))
        {
            return;
        }

        alarm.EndedAt = now;
        alarm.EndedById = actor.GetAgentId();
        await db.SaveChangesAsync(cancellationToken);

        broadcaster.Report(alarmId);
    }

    public async Task<string> ConvertToOperationAsync(string alarmId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        RequireReader(actor);
        var meId = actor.GetAgentId();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var alarm = await FindConvertibleAsync(db, alarmId, actor, cancellationToken);
        var coming = await ComingAsync(db, alarmId, cancellationToken);

        // creator becomes investigation lead
        var operation = await operations.CreateAsync(new OperationInput
        {
            Title = alarm.Situation.Length <= MaxOperationTitle ? alarm.Situation : alarm.Situation[..MaxOperationTitle],
            Status = OperationStatus.Running,
            Location = alarm.Location,
            // already UTC, must not shift
            Start = DateTime.SpecifyKind(alarm.TriggeredAt, DateTimeKind.Utc),
        }, actor, cancellationToken);
        foreach (var agentId in coming.Where(id => id != meId))
        {
            await operations.AgentAllocateAsync(operation.Id, agentId, false, actor, cancellationToken);
        }

        alarm.OperationId = operation.Id;
        await db.SaveChangesAsync(cancellationToken);

        broadcaster.Report(alarmId);
        return operation.Id;
    }

    public async Task AttachToOperationAsync(string alarmId, string operationId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        RequireReader(actor);
        var meId = actor.GetAgentId();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var alarm = await FindConvertibleAsync(db, alarmId, actor, cancellationToken);
        var operation = await db.Operations.AsNoTracking().FirstOrDefaultAsync(o => o.Id == operationId, cancellationToken)
            ?? throw new InvalidOperationException("Diese Operation gibt es nicht.");
        // checked up front: with nobody new to allocate the allocation would never ask
        Permission.RequireMaySeeClassified(actor, operation.SecrecyLevel);
        if (!actor.IsLeadership() && !await db.OperationAgents.AnyAsync(
                a => a.OperationId == operationId && a.AgentId == meId && a.IsInvestigationLead, cancellationToken))
        {
            throw new UnauthorizedAccessException(
                "An eine Operation hängen dürfen nur die Führung oder ein Ermittlungsleiter dieser Akte.");
        }
        var allocated = (await db.OperationAgents.AsNoTracking()
                .Where(a => a.OperationId == operationId)
                .Select(a => a.AgentId)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var agentId in (await ComingAsync(db, alarmId, cancellationToken)).Where(id => !allocated.Contains(id)))
        {
            await operations.AgentAllocateAsync(operationId, agentId, false, actor, cancellationToken);
        }

        alarm.OperationId = operationId;
        await db.SaveChangesAsync(cancellationToken);

        broadcaster.Report(alarmId);
    }

    public async Task<List<EmergencyAlarmSummary>> GetListAsync(ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        if (!EmergencyAlarmRules.MayRead(actor))
        {
            return new();
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await SummariesAsync(db.EmergencyAlarms.AsNoTracking()
            .OrderByDescending(a => a.TriggeredAt)
            .Take(ListLimit), cancellationToken);
    }

    public async Task<List<EmergencyAlarmSummary>> GetOpenForMeAsync(ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        var meId = actor.GetAgentId();
        if (!EmergencyAlarmRules.MayRead(actor) || string.IsNullOrEmpty(meId))
        {
            return new();
        }
        var now = DateTime.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await SummariesAsync(db.EmergencyAlarms.AsNoTracking()
            .Where(a => a.EndedAt == null && a.Expiry > now)
            .Where(a => a.Recipients.Any(r => r.AgentId == meId && r.Answer == null))
            .OrderByDescending(a => a.TriggeredAt), cancellationToken);
    }

    public async Task<EmergencyAlarmDetail?> GetDetailAsync(string alarmId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        if (!EmergencyAlarmRules.MayRead(actor))
        {
            return null;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var alarm = await db.EmergencyAlarms.AsNoTracking().FirstOrDefaultAsync(a => a.Id == alarmId, cancellationToken);
        if (alarm is null)
        {
            return null;
        }
        var rows = (await db.EmergencyAlarmRecipients.AsNoTracking()
                .Where(r => r.AlarmId == alarmId)
                .Select(r => new EmergencyAlarmRow(r.AgentId, r.Agent!.Codename, r.Answer, r.AnsweredAt))
                .ToListAsync(cancellationToken))
            .OrderBy(r => AnswerOrder(r.Answer))
            .ThenBy(r => r.Codename, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var now = DateTime.UtcNow;
        var isOpen = EmergencyAlarmRules.IsOpen(alarm, now);
        var meId = actor.GetAgentId();
        var mine = rows.FirstOrDefault(r => r.AgentId == meId);
        var mayEnd = EmergencyAlarmRules.MayEnd(actor, alarm);
        var summary = Summary(alarm, isOpen,
            rows.Count(r => r.Answer == EmergencyAlarmAnswer.Coming),
            rows.Count(r => r.Answer == EmergencyAlarmAnswer.NotComing),
            rows.Count(r => r.Answer is null));

        return new EmergencyAlarmDetail(summary, alarm.Location, alarm.Reason, alarm.Expiry,
            MayAnswer: isOpen && mine is not null && actor.MayWrite(),
            MyAnswer: mine?.Answer,
            MayReping: isOpen && alarm.RepingedAt is null && actor.MayWrite() && alarm.TriggeredById == meId,
            MayEnd: isOpen && mayEnd,
            MayConvert: !isOpen && alarm.OperationId is null && mayEnd,
            Rows: rows);
    }

    // selectable ∩ target − absent today
    private static async Task<List<string>> RecipientIdsAsync(AppDbContext db, Division? target, DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var agents = await db.Users.AsNoTracking().OnlySelectable().ToListAsync(cancellationToken);
        var absent = (await db.Absences.AsNoTracking().Covering(MeetingTime.Day(nowUtc))
                .Select(a => a.AgentId)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        return agents
            .Where(a => target is not { } division || DivisionRules.IsMember(a, division))
            .Where(a => !absent.Contains(a.Id))
            .Select(a => a.Id)
            .ToList();
    }

    private static async Task<List<EmergencyAlarmSummary>> SummariesAsync(IQueryable<EmergencyAlarm> alarms,
        CancellationToken cancellationToken)
    {
        var rows = await alarms
            .Select(a => new
            {
                Alarm = a,
                Coming = a.Recipients.Count(r => r.Answer == EmergencyAlarmAnswer.Coming),
                NotComing = a.Recipients.Count(r => r.Answer == EmergencyAlarmAnswer.NotComing),
                Open = a.Recipients.Count(r => r.Answer == null),
            })
            .ToListAsync(cancellationToken);
        var now = DateTime.UtcNow;
        return rows
            .Select(r => Summary(r.Alarm, EmergencyAlarmRules.IsOpen(r.Alarm, now), r.Coming, r.NotComing, r.Open))
            .ToList();
    }

    private static EmergencyAlarmSummary Summary(EmergencyAlarm alarm, bool isOpen, int coming, int notComing, int open)
        => new(alarm.Id, alarm.Situation, alarm.Target, alarm.TriggeredByName, alarm.TriggeredAt, isOpen,
            coming, notComing, open, alarm.OperationId, alarm.IsEmergency);

    private static void RequireReader(ClaimsPrincipal actor)
    {
        if (!EmergencyAlarmRules.MayRead(actor))
        {
            throw new UnauthorizedAccessException("Einsatzalarme sind internen Agenten vorbehalten.");
        }
    }

    private static async Task<EmergencyAlarm> FindAsync(AppDbContext db, string alarmId, CancellationToken cancellationToken)
        => await db.EmergencyAlarms.FirstOrDefaultAsync(a => a.Id == alarmId, cancellationToken)
           ?? throw new InvalidOperationException("Diesen Einsatzalarm gibt es nicht.");

    // ended, not yet linked, triggerer or leadership
    private static async Task<EmergencyAlarm> FindConvertibleAsync(AppDbContext db, string alarmId, ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var alarm = await FindAsync(db, alarmId, cancellationToken);
        if (!EmergencyAlarmRules.MayEnd(actor, alarm))
        {
            throw new UnauthorizedAccessException("Überführen kann nur, wer den Einsatzalarm ausgelöst hat, oder die Führung.");
        }
        if (EmergencyAlarmRules.IsOpen(alarm, DateTime.UtcNow))
        {
            throw new InvalidOperationException("Der Einsatzalarm läuft noch, erst beenden.");
        }
        if (alarm.OperationId is not null)
        {
            throw new InvalidOperationException("Dieser Einsatzalarm gehört schon zu einer Operation.");
        }
        return alarm;
    }

    // stale accounts would make the allocation throw
    private static Task<List<string>> ComingAsync(AppDbContext db, string alarmId, CancellationToken cancellationToken)
    {
        var selectable = db.Users.OnlySelectable().Select(u => u.Id);
        return db.EmergencyAlarmRecipients.AsNoTracking()
            .Where(r => r.AlarmId == alarmId && r.Answer == EmergencyAlarmAnswer.Coming)
            .Where(r => selectable.Contains(r.AgentId))
            .Select(r => r.AgentId)
            .ToListAsync(cancellationToken);
    }

    // coming, then not coming, then open
    private static int AnswerOrder(EmergencyAlarmAnswer? answer) => answer switch
    {
        EmergencyAlarmAnswer.Coming => 0,
        EmergencyAlarmAnswer.NotComing => 1,
        _ => 2,
    };

    private static string Href(string alarmId) => $"/einsatzalarme/{alarmId}";
}
