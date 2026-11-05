using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Feedback;
using FeedbackEntity = NOOSE_Website.Data.Entities.Feedback.Feedback;

namespace NOOSE_Website.Services;

/// <summary>Agents file feedback about the site; every internal agent reads all of it, leadership decides and reads the trash.</summary>
public class FeedbackService(
    IDbContextFactory<AppDbContext> dbFactory,
    INotificationService notifications) : IFeedbackService
{
    public async Task<string> CreateAsync(FeedbackInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);

        var agentId = actor.GetAgentId()
            ?? throw new UnauthorizedAccessException("Ohne Agenten-Kontext ist kein Feedback möglich.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var feedback = new FeedbackEntity
        {
            AgentId = agentId,
            Kind = input.Kind,
            PageRoute = string.IsNullOrWhiteSpace(input.PageRoute) ? null : input.PageRoute.Trim(),
            PageTab = string.IsNullOrWhiteSpace(input.PageTab) ? null : input.PageTab.Trim(),
            Text = input.Text.Trim(),
        };
        db.Feedbacks.Add(feedback);
        await db.SaveChangesAsync(cancellationToken);

        await LeadershipNotifyAsync(db, feedback, agentId, cancellationToken);
        return feedback.Id;
    }

    public async Task<IReadOnlyList<FeedbackRow>> GetMyAsync(ClaimsPrincipal viewer, CancellationToken cancellationToken = default)
    {
        var meId = viewer.GetAgentId();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Feedbacks.AsNoTracking()
            .Where(f => f.AgentId == meId)
            .OrderByDescending(f => f.CreatedAt)
            .Select(f => new FeedbackRow(
                f.Id, f.Kind, f.Status, f.PageRoute, f.PageTab, f.Text, f.AgentId, f.Agent!.Codename, f.CreatedAt,
                f.Response, f.DeciderName, f.DecidedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FeedbackRow>> GetInboxAsync(ClaimsPrincipal viewer, CancellationToken cancellationToken = default)
    {
        // twin of Policies.InternalAgent, which gates the page carrying this section
        Permission.RequireInternalAgent(viewer);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Feedbacks.AsNoTracking()
            .OrderByDescending(f => f.CreatedAt)
            .Select(f => new FeedbackRow(
                f.Id, f.Kind, f.Status, f.PageRoute, f.PageTab, f.Text, f.AgentId, f.Agent!.Codename, f.CreatedAt,
                f.Response, f.DeciderName, f.DecidedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task SetStatusAsync(string id, FeedbackStatus status, string? response, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var feedback = await db.Feedbacks.FirstOrDefaultAsync(f => f.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Feedback-Meldung '{id}' nicht gefunden.");

        var altStatus = feedback.Status;
        var altResponse = feedback.Response;
        feedback.Status = status;
        feedback.Response = response.TrimToNull();
        feedback.DeciderName = actor.GetCodename();
        feedback.DecidedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        // only a real change is news, and never to the agent who caused it
        if ((altStatus != status || altResponse != feedback.Response) && feedback.AgentId != actor.GetAgentId())
        {
            await ReporterNotifyAsync(feedback, cancellationToken);
        }
    }

    public async Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var feedback = await db.Feedbacks.FirstOrDefaultAsync(f => f.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Feedback-Meldung '{id}' nicht gefunden.");

        if (!actor.IsLeadership() && feedback.AgentId != actor.GetAgentId())
        {
            throw new UnauthorizedAccessException(
                "Eine Feedback-Meldung darf nur der meldende Agent selbst oder die Führung löschen.");
        }

        // soft delete via interceptor
        db.Feedbacks.Remove(feedback);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<FeedbackEntity>> GetTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Feedbacks.AsNoTracking()
            .IgnoreQueryFilters()
            // the trash page shows the codename, not the raw agent id
            .Include(f => f.Agent)
            .Where(f => f.IsDeleted)
            .OrderByDescending(f => f.DeletedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var feedback = await db.Feedbacks.IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Feedback-Meldung '{id}' nicht gefunden.");

        feedback.IsDeleted = false;
        feedback.DeletedAt = null;
        feedback.DeletedById = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Best effort; the reply text deliberately stays out of the title.</summary>
    private async Task ReporterNotifyAsync(FeedbackEntity feedback, CancellationToken cancellationToken)
    {
        try
        {
            await notifications.NotifyAsync(feedback.AgentId, NotificationType.Feedback,
                $"Deine Feedback-Meldung ist jetzt: {FeedbackStatusDisplay.Name(feedback.Status)}",
                "/feedback?tab=meine", cancellationToken);
        }
        catch { /* best effort */ }
    }

    /// <summary>Best effort; the free text deliberately stays out of the title.</summary>
    private async Task LeadershipNotifyAsync(AppDbContext db, FeedbackEntity feedback, string actorId, CancellationToken cancellationToken)
    {
        try
        {
            var recipients = await db.Users.AsNoTracking().OnlySelectable()
                .Where(u => u.IsAdmin || u.Rank >= Rank.SectionChief)
                .Select(u => u.Id)
                .ToListAsync(cancellationToken);

            var title = $"Neue Feedback-Meldung: {FeedbackKindDisplay.Name(feedback.Kind)}";

            await notifications.NotifyManyAsync(recipients, NotificationType.Feedback,
                title, "/feedback?tab=eingang", actorId, cancellationToken);
        }
        catch { /* best effort */ }
    }
}
