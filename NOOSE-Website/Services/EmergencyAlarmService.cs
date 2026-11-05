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
    INotificationService notifications) : IEmergencyAlarmService
{
    private const int ListLimit = 100;

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

        try
        {
            var href = Href(alarm.Id);
            await notifications.NotifyManyAsync(recipientIds.Where(id => id != meId).ToList(), NotificationType.EmergencyAlarm,
                EmergencyAlarmText.Title(situation, location), href, meId, cancellationToken);
            if (isEmergency)
            {
                var leadership = await db.Users.AsNoTracking().OnlySelectable()
                    .Where(u => u.IsAdmin || u.Rank >= Rank.SectionChief)
                    .Where(u => u.Id != meId)
                    .Select(u => u.Id)
                    .ToListAsync(cancellationToken);
                await notifications.NotifyManyAsync(leadership, NotificationType.EmergencyAlarmByAgent,
                    EmergencyAlarmText.ByAgentTitle(alarm.TriggeredByName, situation), href, meId, cancellationToken);
            }
        }
        catch { /* best effort */ }

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

        alarm.RepingedAt = DateTime.UtcNow;
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
            // coming first, open last
            .OrderBy(r => r.Answer is { } a ? (int)a : int.MaxValue)
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

    private static string Href(string alarmId) => $"/einsatzalarme/{alarmId}";
}
