using System.Net;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Personnel;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IProbationService" />
public class ProbationService(IDbContextFactory<AppDbContext> dbFactory, INotificationService notifications) : IProbationService
{
    public async Task<ProbationView?> GetAsync(string agentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireInternalAgent(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var plan = await db.AgentProbations.AsNoTracking()
            .Where(p => p.AgentId == agentId)
            .OrderByDescending(p => p.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (plan is null)
        {
            return null;
        }

        var mandatory = await db.TrainingModules.AsNoTracking()
            .Where(m => m.IsActive && m.IsMandatory)
            .OrderBy(m => m.Sorting).ThenBy(m => m.Name)
            .Select(m => new { m.Id, m.Name })
            .ToListAsync(cancellationToken);
        var ids = mandatory.Select(m => m.Id).ToList();
        var done = await db.AgentModuleCompletions.AsNoTracking()
            .Where(c => c.AgentId == agentId && ids.Contains(c.ModuleId))
            .Select(c => c.ModuleId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var open = mandatory.Where(m => !done.Contains(m.Id)).Select(m => m.Name).ToList();

        var mentor = plan.MentorAgentId is null
            ? null
            : await db.Users.AsNoTracking().Where(u => u.Id == plan.MentorAgentId)
                .Select(u => u.Codename).FirstOrDefaultAsync(cancellationToken);

        return new ProbationView(plan.Id, plan.MentorAgentId, mentor, plan.StartedAt, plan.EndsAt, plan.ClosedAt,
            plan.Outcome, plan.ClosedByName, done.Count, mandatory.Count, open,
            ProbationRules.Light(plan, done.Count, mandatory.Count, DateTime.UtcNow));
    }

    public async Task StartAsync(string agentId, string? mentorId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadershipWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var codename = await SelectableCodenameAsync(db, agentId, cancellationToken);
        if (await db.AgentProbations.AnyAsync(p => p.AgentId == agentId && p.ClosedAt == null, cancellationToken))
        {
            throw new InvalidOperationException("Für diesen Agenten läuft bereits eine Einarbeitung.");
        }
        var mentor = await ProbationRules.RequireMentorAsync(db, agentId, mentorId, cancellationToken);
        await ProbationRules.OpenAsync(db, agentId, mentor, DateTime.UtcNow, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await NotifyMentorAsync(mentor, agentId, codename, actor, cancellationToken);
    }

    public async Task SetMentorAsync(string agentId, string? mentorId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadershipWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var codename = await SelectableCodenameAsync(db, agentId, cancellationToken);
        var plan = await OpenPlanAsync(db, agentId, cancellationToken);
        var mentor = await ProbationRules.RequireMentorAsync(db, agentId, mentorId, cancellationToken);
        if (plan.MentorAgentId == mentor)
        {
            return;
        }
        plan.MentorAgentId = mentor;
        await db.SaveChangesAsync(cancellationToken);
        await NotifyMentorAsync(mentor, agentId, codename, actor, cancellationToken);
    }

    public async Task ExtendAsync(string agentId, DateTime endsAtUtc, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadershipWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var plan = await OpenPlanAsync(db, agentId, cancellationToken);
        var now = DateTime.UtcNow;
        if (endsAtUtc <= now)
        {
            throw new InvalidOperationException("Das neue Probezeit-Ende muss in der Zukunft liegen.");
        }
        if (endsAtUtc > plan.StartedAt.AddDays(ProbationRules.MaxDays * 2))
        {
            throw new InvalidOperationException("So lange kann eine Probezeit nicht laufen.");
        }
        plan.EndsAt = endsAtUtc;
        // a later end rearms the reminders that already went out
        var desired = ProbationRules.DesiredStage(endsAtUtc, now);
        if (desired < plan.ReminderStage)
        {
            plan.ReminderStage = desired;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task CloseAsync(string agentId, ProbationOutcome outcome, string? note, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        // same gate as any other personnel note
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);
        if (!Enum.IsDefined(outcome))
        {
            throw new InvalidOperationException("Unbekanntes Ergebnis.");
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var plan = await OpenPlanAsync(db, agentId, cancellationToken);
        var now = DateTime.UtcNow;
        plan.ClosedAt = now;
        plan.Outcome = outcome;
        plan.ClosedByName = actor.GetCodename();

        var verdict = outcome == ProbationOutcome.Passed ? "Probezeit bestanden." : "Probezeit nicht bestanden.";
        var text = $"<p>{verdict}</p>";
        if (!string.IsNullOrWhiteSpace(note))
        {
            text += $"<p>{WebUtility.HtmlEncode(note.Trim())}</p>";
        }
        db.AgentNotes.Add(new AgentNote
        {
            AgentId = agentId,
            Kind = AgentNoteKind.Training,
            ArtFreetext = "Probezeit",
            EntryDate = now,
            Text = text,
            AuthorName = actor.GetCodename(),
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> GetDaysAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await ProbationRules.DaysAsync(db, cancellationToken);
    }

    public async Task SetDaysAsync(int days, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        // the module catalogue's own gate
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);
        if (days < ProbationRules.MinDays || days > ProbationRules.MaxDays)
        {
            throw new InvalidOperationException(
                $"Die Probezeit muss zwischen {ProbationRules.MinDays} und {ProbationRules.MaxDays} Tagen liegen.");
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == ProbationRules.SettingKey, cancellationToken);
        if (row is null)
        {
            db.SystemSettings.Add(new SystemSetting { Key = ProbationRules.SettingKey, Value = days.ToString() });
        }
        else
        {
            row.Value = days.ToString();
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> RemindAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var window = nowUtc.AddDays(ProbationRules.ReminderLeadDays);
        var plans = await db.AgentProbations
            .Where(p => p.ClosedAt == null && p.ReminderStage < ProbationReminderStage.Due && p.EndsAt <= window)
            .ToListAsync(cancellationToken);
        if (plans.Count == 0)
        {
            return 0;
        }

        var agentIds = plans.Select(p => p.AgentId).ToList();
        // departed or partner accounts have no probation left to watch
        var codenames = await db.Users.OnlySelectable()
            .Where(u => agentIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Codename, cancellationToken);

        var advanced = 0;
        foreach (var plan in plans)
        {
            var desired = ProbationRules.DesiredStage(plan.EndsAt, nowUtc);
            if (desired <= plan.ReminderStage)
            {
                continue;
            }
            plan.ReminderStage = desired;
            advanced++;
            if (!codenames.TryGetValue(plan.AgentId, out var codename))
            {
                continue;
            }

            var recipients = await ProbationRules.RecipientsAsync(db, plan.AgentId, plan.MentorAgentId, cancellationToken);
            var local = plan.EndsAt.ToLocalTime();
            var title = desired == ProbationReminderStage.Due
                ? $"Probezeit von {codename} ist abgelaufen: bitte abschließen oder verlängern."
                : $"Probezeit von {codename} endet am {local:dd.MM.yyyy}.";
            try
            {
                await notifications.NotifyManyInAppAsync(recipients, NotificationType.Account, title,
                    ProbationRules.Route(plan.AgentId), triggerId: null, cancellationToken);
            }
            catch { /* best effort */ }
        }
        await db.SaveChangesAsync(cancellationToken);
        return advanced;
    }

    private static async Task<string> SelectableCodenameAsync(AppDbContext db, string agentId, CancellationToken cancellationToken)
        => await db.Users.OnlySelectable().Where(u => u.Id == agentId).Select(u => u.Codename)
               .FirstOrDefaultAsync(cancellationToken)
           ?? throw new InvalidOperationException("Eine Einarbeitung gibt es nur für aktive FIB-Agents.");

    private static async Task<AgentProbation> OpenPlanAsync(AppDbContext db, string agentId, CancellationToken cancellationToken)
        => await db.AgentProbations.FirstOrDefaultAsync(p => p.AgentId == agentId && p.ClosedAt == null, cancellationToken)
           ?? throw new InvalidOperationException("Für diesen Agenten läuft keine Einarbeitung.");

    private async Task NotifyMentorAsync(string? mentorId, string agentId, string codename, ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (mentorId is null || mentorId == actor.GetAgentId())
        {
            return;
        }
        try
        {
            await notifications.NotifyAsync(mentorId, NotificationType.Account,
                $"Du bist Pate von {codename}.", ProbationRules.Route(agentId), cancellationToken);
        }
        catch { /* best effort */ }
    }
}
