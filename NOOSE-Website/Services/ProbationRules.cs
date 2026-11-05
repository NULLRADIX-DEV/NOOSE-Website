using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Onboarding plan rules shared by the release paths, the plan service and the reminder sweep.</summary>
public static class ProbationRules
{
    public const string SettingKey = "ProbezeitTage";
    public const int DefaultDays = 14;
    public const int MinDays = 1;
    public const int MaxDays = 180;

    /// <summary>Days before the end the first reminder goes out.</summary>
    public const int ReminderLeadDays = 3;

    public static string Route(string agentId) => $"/personal/{agentId}?tab=module";

    /// <summary>Configured probation length, falling back to the default.</summary>
    public static async Task<int> DaysAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        var raw = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == SettingKey)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);
        return int.TryParse(raw, out var days) ? Math.Clamp(days, MinDays, MaxDays) : DefaultDays;
    }

    /// <summary>A mentor must be a selectable agent other than the new one; null means none yet.</summary>
    public static async Task<string?> RequireMentorAsync(AppDbContext db, string agentId, string? mentorId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(mentorId))
        {
            return null;
        }
        if (mentorId == agentId)
        {
            throw new InvalidOperationException("Ein Agent kann nicht sein eigener Pate sein.");
        }
        if (!await db.Users.OnlySelectable().AnyAsync(u => u.Id == mentorId, cancellationToken))
        {
            throw new InvalidOperationException("Der gewählte Pate ist kein aktiver FIB-Agent.");
        }
        return mentorId;
    }

    /// <summary>Opens the plan on the tracked context, reusing a still-open one; the caller saves.</summary>
    public static async Task<AgentProbation> OpenAsync(AppDbContext db, string agentId, string? mentorId, DateTime nowUtc,
        CancellationToken cancellationToken = default)
    {
        var days = await DaysAsync(db, cancellationToken);
        var plan = await db.AgentProbations
            .FirstOrDefaultAsync(p => p.AgentId == agentId && p.ClosedAt == null, cancellationToken);
        if (plan is null)
        {
            plan = new AgentProbation { AgentId = agentId };
            db.AgentProbations.Add(plan);
        }
        plan.MentorAgentId = mentorId;
        plan.StartedAt = nowUtc;
        plan.EndsAt = nowUtc.AddDays(days);
        plan.ReminderStage = ProbationReminderStage.None;
        return plan;
    }

    /// <summary>Green while module progress keeps pace with the elapsed time, red once the end passed with modules open.</summary>
    public static ProbationLight Light(AgentProbation plan, int done, int total, DateTime nowUtc)
    {
        if (plan.Outcome is { } outcome)
        {
            return outcome == ProbationOutcome.Passed ? ProbationLight.Passed : ProbationLight.Failed;
        }
        if (total == 0 || done >= total)
        {
            return ProbationLight.Green;
        }
        if (nowUtc >= plan.EndsAt)
        {
            return ProbationLight.Red;
        }
        var span = (plan.EndsAt - plan.StartedAt).TotalSeconds;
        var elapsed = span <= 0 ? 1 : Math.Clamp((nowUtc - plan.StartedAt).TotalSeconds / span, 0, 1);
        return (double)done / total >= elapsed ? ProbationLight.Green : ProbationLight.Yellow;
    }

    public static string LightLabel(ProbationLight light) => light switch
    {
        ProbationLight.Green => "Im Plan",
        ProbationLight.Yellow => "Im Verzug",
        ProbationLight.Red => "Probezeit abgelaufen",
        ProbationLight.Passed => "Bestanden",
        ProbationLight.Failed => "Nicht bestanden",
        _ => "",
    };

    public static MudBlazor.Color LightColour(ProbationLight light) => light switch
    {
        ProbationLight.Green or ProbationLight.Passed => MudBlazor.Color.Success,
        ProbationLight.Yellow => MudBlazor.Color.Warning,
        _ => MudBlazor.Color.Error,
    };

    /// <summary>Stage the plan should be at; past the end counts as due.</summary>
    public static ProbationReminderStage DesiredStage(DateTime endsAtUtc, DateTime nowUtc)
        => nowUtc >= endsAtUtc ? ProbationReminderStage.Due
            : nowUtc >= endsAtUtc.AddDays(-ReminderLeadDays) ? ProbationReminderStage.Soon
            : ProbationReminderStage.None;

    /// <summary>Mentor plus the leadership over the new agent: Section Chiefs of the home division and the Board.</summary>
    public static async Task<List<string>> RecipientsAsync(AppDbContext db, string agentId, string? mentorId,
        CancellationToken cancellationToken = default)
    {
        var subject = await db.Users.AsNoTracking().Where(u => u.Id == agentId)
            .Select(u => new { u.Rank, u.Division, u.IsTRU, u.PartnerAgency })
            .FirstOrDefaultAsync(cancellationToken);
        var home = subject is null
            ? null
            : DivisionRules.Of(subject.Rank, subject.Division, subject.IsTRU, subject.PartnerAgency is null).Home;

        var leaders = await db.Users.AsNoTracking().OnlySelectable()
            .Where(u => u.Rank >= Rank.SectionChief && u.Id != agentId)
            .Select(u => new { u.Id, u.Rank, u.Division, u.IsTRU })
            .ToListAsync(cancellationToken);
        var recipients = leaders
            .Where(l => DivisionRules.Of(l.Rank, l.Division, l.IsTRU) is var unit
                        && (unit.IsBoard || (home is not null && unit.Home == home)))
            .Select(l => l.Id)
            .ToList();

        if (mentorId is not null && mentorId != agentId
            && await db.Users.OnlySelectable().AnyAsync(u => u.Id == mentorId, cancellationToken))
        {
            recipients.Add(mentorId);
        }
        return recipients.Distinct().ToList();
    }
}
