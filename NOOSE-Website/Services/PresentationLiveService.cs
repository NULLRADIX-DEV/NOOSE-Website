using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Presentations;
using NOOSE_Website.Infrastructure.Presentations;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Live presenting: start, page, switch the follow mode, end, and join as a viewer.</summary>
/// <remarks>Every call checks rights against the database before it touches the in-memory registry: joining is
/// visibility, presenting is ownership or leadership. The registry itself trusts its caller.</remarks>
public interface IPresentationLiveService
{
    Task<LiveSessionSnapshot> StartAsync(string presentationId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    void GoTo(string presentationId, int slideIndex, ClaimsPrincipal actor);
    void SetMode(string presentationId, PresentationFollowMode mode, ClaimsPrincipal actor);
    void End(string presentationId, ClaimsPrincipal actor);

    /// <summary>Counts the viewer's page; null when nothing is live or the viewer may not see it.</summary>
    Task<LiveSessionSnapshot?> JoinAsync(string presentationId, string connectionKey, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    void Leave(string presentationId, string connectionKey);

    /// <summary>Marks the presenter's page open; false when the actor is not this session's presenter.</summary>
    bool PresenterConnected(string presentationId, string connectionKey, ClaimsPrincipal actor);
    void PresenterDisconnected(string presentationId, string connectionKey);

    /// <summary>Running sessions the viewer may see, for the banner.</summary>
    Task<List<LiveSessionSnapshot>> GetVisibleLiveAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IPresentationLiveService" />
public sealed class PresentationLiveService(
    IDbContextFactory<AppDbContext> dbFactory,
    PresentationLiveRegistry registry,
    PresentationBroadcaster broadcaster,
    INotificationService notifications,
    ILogger<PresentationLiveService> logger) : IPresentationLiveService
{
    public async Task<LiveSessionSnapshot> StartAsync(string presentationId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequirePresentationWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var presentation = await db.Presentations.AsNoTracking().FirstOrDefaultAsync(p => p.Id == presentationId, cancellationToken)
            ?? throw new InvalidOperationException("Präsentation nicht gefunden.");
        if (!PresentationVisibility.MayManage(actor, presentation))
        {
            throw new UnauthorizedAccessException("Vorführen darf nur, wer die Präsentation angelegt hat, oder die Führung.");
        }
        if (!await db.PresentationSlides.AnyAsync(s => s.PresentationId == presentationId, cancellationToken))
        {
            throw new InvalidOperationException("Die Präsentation hat noch keine Folien.");
        }
        var already = registry.Get(presentationId) is not null;
        var session = registry.Start(presentationId, presentation.Title, actor.GetAgentId()!, actor.GetCodename() ?? "Agent",
                DateTime.UtcNow)
            ?? throw new InvalidOperationException("Diese Präsentation wird gerade von jemand anderem vorgeführt.");
        if (already)
        {
            return session;
        }
        broadcaster.Report(presentationId, PresentationLiveEvent.Started);

        try
        {
            var recipients = await AudienceAsync(db, presentation, cancellationToken);
            // bell only: who is in a closed group or on an invite list is not for a shared Discord channel
            await notifications.NotifyManyInAppAsync(recipients, NotificationType.PresentationLive,
                $"{session.PresenterCodename} präsentiert jetzt: {presentation.Title}",
                $"/praesentationen/{presentationId}/live", actor.GetAgentId(), cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Live notice for presentation {PresentationId} failed.", presentationId);
        }
        return session;
    }

    public void GoTo(string presentationId, int slideIndex, ClaimsPrincipal actor)
    {
        Permission.RequirePresentationWrite(actor);
        if (!registry.SetSlide(presentationId, actor.GetAgentId() ?? string.Empty, slideIndex))
        {
            throw new UnauthorizedAccessException("Nur der Vortragende blättert.");
        }
        broadcaster.Report(presentationId, PresentationLiveEvent.SlideChanged);
    }

    public void SetMode(string presentationId, PresentationFollowMode mode, ClaimsPrincipal actor)
    {
        Permission.RequirePresentationWrite(actor);
        if (!registry.SetMode(presentationId, actor.GetAgentId() ?? string.Empty, mode))
        {
            throw new UnauthorizedAccessException("Nur der Vortragende wählt den Modus.");
        }
        broadcaster.Report(presentationId, PresentationLiveEvent.ModeChanged);
    }

    public void End(string presentationId, ClaimsPrincipal actor)
    {
        Permission.RequirePresentationWrite(actor);
        var session = registry.Get(presentationId);
        if (session is null)
        {
            return;
        }
        // leadership may end a session someone left running; nobody else may end another's
        if (session.PresenterId != actor.GetAgentId() && !actor.IsLeadership())
        {
            throw new UnauthorizedAccessException("Beenden darf nur der Vortragende oder die Führung.");
        }
        if (registry.End(presentationId))
        {
            broadcaster.Report(presentationId, PresentationLiveEvent.Ended);
        }
    }

    public async Task<LiveSessionSnapshot?> JoinAsync(string presentationId, string connectionKey, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireInternalAgent(actor);
        if (registry.Get(presentationId) is null)
        {
            return null;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await PresentationVisibility.IsVisibleAsync(db, presentationId, ViewerScope.From(actor), cancellationToken))
        {
            // a page that rejoins after the audience narrowed loses its seat, not only the next view
            Leave(presentationId, connectionKey);
            return null;
        }
        var session = registry.Join(presentationId, connectionKey, actor.GetCodename() ?? "Agent");
        if (session is not null)
        {
            broadcaster.Report(presentationId, PresentationLiveEvent.ViewersChanged);
        }
        return session;
    }

    public void Leave(string presentationId, string connectionKey)
    {
        if (registry.Leave(presentationId, connectionKey))
        {
            broadcaster.Report(presentationId, PresentationLiveEvent.ViewersChanged);
        }
    }

    public bool PresenterConnected(string presentationId, string connectionKey, ClaimsPrincipal actor)
        => actor.MayWrite() && registry.PresenterConnected(presentationId, actor.GetAgentId() ?? string.Empty, connectionKey);

    public void PresenterDisconnected(string presentationId, string connectionKey)
        => registry.PresenterDisconnected(presentationId, connectionKey, DateTime.UtcNow);

    public async Task<List<LiveSessionSnapshot>> GetVisibleLiveAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var sessions = registry.All();
        if (sessions.Count == 0 || !actor.IsInternalAgent())
        {
            return [];
        }
        var ids = sessions.Select(s => s.PresentationId).ToList();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var visible = await PresentationVisibility.OnlyVisible(db.Presentations.AsNoTracking(), db, ViewerScope.From(actor))
            .Where(p => ids.Contains(p.Id)).Select(p => p.Id).ToListAsync(cancellationToken);
        return sessions.Where(s => visible.Contains(s.PresentationId)).OrderBy(s => s.StartedAt).ToList();
    }

    /// <summary>Selectable agents who may see the presentation, for the live notice.</summary>
    private static async Task<List<string>> AudienceAsync(AppDbContext db, Presentation presentation, CancellationToken cancellationToken)
        => presentation.Access switch
        {
            PresentationAccess.Public => await db.Users.OnlySelectable().Select(u => u.Id).ToListAsync(cancellationToken),
            PresentationAccess.Closed when presentation.Audience is { } audience
                => await AudienceRules.RecipientIdsAsync(db, audience, presentation.TargetId, presentation.MinRank, cancellationToken),
            PresentationAccess.Private => await db.PresentationInvites.Where(i => i.PresentationId == presentation.Id)
                .Join(db.Users.OnlySelectable(), i => i.AgentId, u => u.Id, (i, u) => u.Id).ToListAsync(cancellationToken),
            _ => [],
        };
}
