using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Appointments;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Appointments;

namespace NOOSE_Website.Services;

public class AppointmentService(
    IDbContextFactory<AppDbContext> dbFactory,
    ICaseNumberService caseNumber,
    INotificationService notifications) : IAppointmentService
{
    public async Task<Appointment?> GetDetailAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Appointments
            .OnlyVisible(db, actor.MayClassifiedRead(), actor.GetAgentId())
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
    }

    public async Task<List<Appointment>> GetTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Appointments.IgnoreQueryFilters()
            .Where(t => t.IsDeleted)
            .OrderByDescending(t => t.DeletedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<Appointment>> SearchAsync(string? searchText, bool mayAll, string? meId, int max = 20, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Appointments.OnlyVisible(db, mayAll, meId);

        var s = searchText?.Trim();
        if (!string.IsNullOrEmpty(s))
        {
            query = query.Where(t => t.Title.Contains(s) || t.CaseNumber.Contains(s));
        }

        return await query
            .OrderByDescending(t => t.Start)
            .Take(max)
            .ToListAsync(cancellationToken);
    }

    public async Task<Appointment> CreateAsync(AppointmentInput input, IReadOnlyList<string> agentIds,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var (startUtc, endUtc) = TimesFromInput(input);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var appointment = new Appointment
        {
            CaseNumber = await caseNumber.NextAsync(db, "TM", cancellationToken),
            Title = input.Title.Trim(),
            Category = input.Category,
            Status = input.Status,
            Location = input.Location.TrimToNull(),
            Start = startUtc,
            End = endUtc,
            AllDay = input.AllDay,
            Description = input.Description.TrimToNull(),
            Visibility = input.Visibility,
        };
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync(cancellationToken);

        // Only selectable agents, deduplicated; silently dropped so a stale picker cannot fail the create.
        var valid = agentIds.Count == 0
            ? new List<string>()
            : await db.Users.OnlySelectable()
                .Where(u => agentIds.Contains(u.Id))
                .Select(u => u.Id)
                .ToListAsync(cancellationToken);
        foreach (var agentId in valid.Distinct())
        {
            db.AppointmentAssignments.Add(new AppointmentAssignment { AppointmentId = appointment.Id, AgentId = agentId });
        }
        if (valid.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);

        // Notify after commit, creator excluded; one Discord post pings only the invited.
        var creatorId = actor.GetAgentId();
        await notifications.NotifyManyAsync(valid, NotificationType.AppointmentAssigned,
            ParticipantTitle(appointment), $"/kalender/{appointment.Id}", creatorId, cancellationToken);

        await LeadershipNotifyAsync(db, appointment, creatorId, cancellationToken);

        await MentionNotify.DeltaAsync(notifications, null, appointment.Description, "einem Termin",
            nameof(Appointment), appointment.Id, actor, cancellationToken);

        return appointment;
    }

    /// <summary>Best effort; announces a new appointment in the leadership bell, never on Discord.</summary>
    /// <remarks>
    /// Every visibility level is announced: leadership already sees any appointment via
    /// <see cref="AppointmentVisibility"/>, so withholding the restricted ones would only hide them from the
    /// people entitled to see them. Location and description stay out of the title.
    /// </remarks>
    private async Task LeadershipNotifyAsync(AppDbContext db, Appointment appointment, string? creatorId,
        CancellationToken cancellationToken)
    {
        try
        {
            var recipients = await db.Users.AsNoTracking().OnlySelectable()
                .Where(u => u.IsAdmin || u.Rank >= Rank.SupervisorySpecialAgent)
                .Select(u => u.Id)
                .ToListAsync(cancellationToken);

            var title = $"Neuer Termin: „{appointment.Title}“ am {When(appointment)} "
                      + $"({AppointmentCategoryDisplay.Name(appointment.Category)}).";

            await notifications.NotifyManyInAppAsync(recipients, NotificationType.AppointmentScheduled,
                title.Length > 300 ? title[..297] + "…" : title,
                $"/kalender/{appointment.Id}", creatorId, cancellationToken);
        }
        catch { /* best effort */ }
    }

    private static string ParticipantTitle(Appointment appointment)
        => $"Du bist als Teilnehmer eingetragen: „{appointment.Title}“.";

    // all-day appointments carry no meaningful clock time
    private static string When(Appointment appointment)
    {
        var local = appointment.Start.ToLocalTime();
        return appointment.AllDay ? local.ToString("dd.MM.yyyy") : $"{local:dd.MM.yyyy HH:mm} Uhr";
    }

    public async Task RefreshAsync(string id, AppointmentInput input, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        var (startUtc, endUtc) = TimesFromInput(input);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var appointment = await db.Appointments.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Termin '{id}' nicht gefunden.");
        RequireCreatorOrLeadership(appointment, actor);

        var oldMentions = appointment.Description;
        appointment.Title = input.Title.Trim();
        appointment.Category = input.Category;
        appointment.Status = input.Status;
        appointment.Location = input.Location.TrimToNull();
        appointment.Start = startUtc;
        appointment.End = endUtc;
        appointment.AllDay = input.AllDay;
        appointment.Description = input.Description.TrimToNull();
        appointment.Visibility = input.Visibility;
        await db.SaveChangesAsync(cancellationToken);

        await MentionNotify.DeltaAsync(notifications, oldMentions, appointment.Description, "einem Termin",
            nameof(Appointment), appointment.Id, actor, cancellationToken);
    }

    public async Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var appointment = await db.Appointments.FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Termin '{id}' nicht gefunden.");
        RequireCreatorOrLeadership(appointment, actor);
        db.Appointments.Remove(appointment);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var appointment = await db.Appointments.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Termin '{id}' nicht gefunden.");

        appointment.IsDeleted = false;
        appointment.DeletedAt = null;
        appointment.DeletedById = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<AppointmentAssignment>> GetParticipantAsync(string appointmentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.AppointmentAssignments
            .Where(z => z.AppointmentId == appointmentId)
            .Include(z => z.Agent)
            .OrderBy(z => z.Agent!.Codename)
            .ToListAsync(cancellationToken);
    }

    public async Task AgentAssignAsync(string appointmentId, string agentId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var appointment = await db.Appointments.FirstOrDefaultAsync(t => t.Id == appointmentId, cancellationToken)
            ?? throw new InvalidOperationException($"Termin '{appointmentId}' nicht gefunden.");
        RequireCreatorOrLeadership(appointment, actor);

        // same rule as the picker: a stale circuit must not assign a team lead, partner or ex-agent
        if (!await db.Users.OnlySelectable().AnyAsync(u => u.Id == agentId, cancellationToken))
        {
            throw new InvalidOperationException("Der gewählte Agent wurde nicht gefunden oder ist nicht zuteilbar.");
        }
        if (await db.AppointmentAssignments.AnyAsync(z => z.AppointmentId == appointmentId && z.AgentId == agentId, cancellationToken))
        {
            throw new InvalidOperationException("Dieser Agent ist dem Termin bereits zugeteilt.");
        }

        db.AppointmentAssignments.Add(new AppointmentAssignment { AppointmentId = appointmentId, AgentId = agentId });
        await db.SaveChangesAsync(cancellationToken);

        var actorId = actor.GetAgentId();
        if (agentId != actorId)
        {
            // many-path pings on Discord
            await notifications.NotifyManyAsync([agentId], NotificationType.AppointmentAssigned,
                ParticipantTitle(appointment), $"/kalender/{appointment.Id}", actorId, cancellationToken);
        }
    }

    public async Task AgentRemoveAsync(string assignmentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var assignment = await db.AppointmentAssignments
            .Include(z => z.Appointment)
            .FirstOrDefaultAsync(z => z.Id == assignmentId, cancellationToken);
        if (assignment is null)
        {
            return;
        }
        if (assignment.Appointment is not null)
        {
            RequireCreatorOrLeadership(assignment.Appointment, actor);
        }
        db.AppointmentAssignments.Remove(assignment);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Local input time to UTC; treats unspecified kinds as local.</summary>
    private static DateTime LocalByUtc(DateTime local)
        => DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();

    /// <summary>Validates and normalizes start/end to UTC; all-day snaps to date, end may not precede start.</summary>
    private static (DateTime Start, DateTime? End) TimesFromInput(AppointmentInput input)
    {
        if (input.Start is null)
        {
            throw new InvalidOperationException("Ein Termin braucht einen Beginn.");
        }
        var startLocal = input.AllDay ? input.Start.Value.Date : input.Start.Value;
        DateTime? endLocal = input.End is { } e ? (input.AllDay ? e.Date : e) : null;
        if (endLocal is { } el && el < startLocal)
        {
            throw new InvalidOperationException("Das Ende darf nicht vor dem Beginn liegen.");
        }
        return (LocalByUtc(startLocal), endLocal is { } x ? LocalByUtc(x) : null);
    }

    private static void RequireCreatorOrLeadership(Appointment appointment, ClaimsPrincipal actor)
    {
        if (actor.IsLeadership())
        {
            return;
        }
        var meId = actor.GetAgentId();
        if (!string.IsNullOrEmpty(meId) && appointment.CreatedById == meId)
        {
            return;
        }
        throw new UnauthorizedAccessException("Diesen Termin darf nur sein Ersteller oder die Führung bearbeiten.");
    }
}
