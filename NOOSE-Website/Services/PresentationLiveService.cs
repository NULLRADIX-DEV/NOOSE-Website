using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Presentations;
using NOOSE_Website.Infrastructure.Presentations;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Live presenting: start, page, switch the follow mode, invite, end, and join as a viewer.</summary>
/// <remarks>Every call checks rights against the database before it touches the in-memory registry: joining is
/// visibility or an invitation to the running session, presenting is ownership or leadership. The registry itself
/// trusts its caller.</remarks>
public interface IPresentationLiveService
{
    /// <summary>Starts presenting; <paramref name="inviteIds"/> are invited to this session at once.</summary>
    Task<LiveSessionSnapshot> StartAsync(string presentationId, ClaimsPrincipal actor,
        IReadOnlyCollection<string>? inviteIds = null, CancellationToken cancellationToken = default);
    void GoTo(string presentationId, int slideIndex, ClaimsPrincipal actor);
    void SetMode(string presentationId, PresentationFollowMode mode, ClaimsPrincipal actor);
    void End(string presentationId, ClaimsPrincipal actor);

    /// <summary>Invites selectable agents to the running session; returns how many were not invited before.</summary>
    /// <remarks>The presenter only. The invited watch live without any other right on the presentation, and the
    /// invitation ends with the session.</remarks>
    Task<int> InviteAsync(string presentationId, IReadOnlyCollection<string> agentIds, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    /// <summary>Takes an invitation to the running session back; the presenter only.</summary>
    void Uninvite(string presentationId, string agentId, ClaimsPrincipal actor);

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
        IReadOnlyCollection<string>? inviteIds = null, CancellationToken cancellationToken = default)
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
        var me = actor.GetAgentId()!;
        var invites = await SelectableAsync(db, inviteIds, me, cancellationToken);
        var already = registry.Get(presentationId) is not null;
        var session = registry.Start(presentationId, presentation.Title, me, actor.GetCodename() ?? "Agent", DateTime.UtcNow)
            ?? throw new InvalidOperationException("Diese Präsentation wird gerade von jemand anderem vorgeführt.");
        // before the start is announced, so the banner of an invited agent shows it with the first event
        var added = registry.Invite(presentationId, me, invites) ?? [];
        session = registry.Get(presentationId) ?? session;
        if (already)
        {
            if (added.Count > 0)
            {
                broadcaster.Report(presentationId, PresentationLiveEvent.InvitesChanged);
                await NotifyInvitedAsync(added, session);
            }
            return session;
        }
        broadcaster.Report(presentationId, PresentationLiveEvent.Started);

        var audience = new List<string>();
        try
        {
            audience = await AudienceAsync(db, presentation, cancellationToken);
            // bell only: who is in a closed group or on an invite list is not for a shared Discord channel
            await notifications.NotifyManyInAppAsync(audience, NotificationType.PresentationLive,
                $"{session.PresenterCodename} präsentiert jetzt: {presentation.Title}",
                $"/praesentationen/{presentationId}/live", me, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Live notice for presentation {PresentationId} failed.", presentationId);
        }
        // whoever already heard of the start gets no second bell
        await NotifyInvitedAsync(added.Except(audience, StringComparer.Ordinal).ToList(), session);
        return session;
    }

    public async Task<int> InviteAsync(string presentationId, IReadOnlyCollection<string> agentIds, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequirePresentationWrite(actor);
        var me = actor.GetAgentId() ?? string.Empty;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var presentation = await db.Presentations.AsNoTracking().FirstOrDefaultAsync(p => p.Id == presentationId, cancellationToken)
            ?? throw new InvalidOperationException("Präsentation nicht gefunden.");
        if (!PresentationVisibility.MayManage(actor, presentation))
        {
            throw new UnauthorizedAccessException("Einladen darf nur, wer die Präsentation angelegt hat, oder die Führung.");
        }
        var session = registry.Get(presentationId)
            ?? throw new InvalidOperationException("Die Präsentation wird gerade nicht vorgeführt.");
        if (session.PresenterId != me)
        {
            throw new UnauthorizedAccessException("Einladen darf nur, wer gerade vorführt.");
        }
        var allowed = await SelectableAsync(db, agentIds, me, cancellationToken);
        var added = registry.Invite(presentationId, me, allowed)
            ?? throw new InvalidOperationException("Die Vorführung ist inzwischen beendet.");
        if (added.Count == 0)
        {
            return 0;
        }
        broadcaster.Report(presentationId, PresentationLiveEvent.InvitesChanged);
        await NotifyInvitedAsync(added, registry.Get(presentationId) ?? session);
        return added.Count;
    }

    public void Uninvite(string presentationId, string agentId, ClaimsPrincipal actor)
    {
        Permission.RequirePresentationWrite(actor);
        var session = registry.Get(presentationId);
        if (session is null)
        {
            return;
        }
        if (session.PresenterId != actor.GetAgentId())
        {
            throw new UnauthorizedAccessException("Ausladen darf nur, wer gerade vorführt.");
        }
        // the page of the uninvited checks again and gives its seat back
        if (registry.Uninvite(presentationId, session.PresenterId, agentId))
        {
            broadcaster.Report(presentationId, PresentationLiveEvent.InvitesChanged);
        }
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
        if (!await PresentationVisibility.IsVisibleAsync(db, presentationId, ViewerScope.From(actor), cancellationToken)
            && !await PresentationLiveAccess.IsInvitedViewerAsync(db, registry, presentationId, actor, cancellationToken))
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
        var me = actor.GetAgentId() ?? string.Empty;
        var invited = sessions.Where(s => !visible.Contains(s.PresentationId) && s.InvitedIds.Contains(me)).ToList();
        // same rule as joining: an invite made before a block or a partner switch shows nothing
        if (invited.Count > 0 && !await db.Users.OnlySelectable().AnyAsync(u => u.Id == me, cancellationToken))
        {
            invited = [];
        }
        return sessions.Where(s => visible.Contains(s.PresentationId)).Concat(invited).OrderBy(s => s.StartedAt).ToList();
    }

    /// <summary>The selectable agents among <paramref name="agentIds"/>, never the presenter.</summary>
    private static async Task<List<string>> SelectableAsync(AppDbContext db, IReadOnlyCollection<string>? agentIds,
        string presenterId, CancellationToken cancellationToken)
    {
        var wanted = (agentIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id) && id != presenterId)
            .Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0)
        {
            return [];
        }
        // the picker's own predicate, or the SignalR path stays open; the stored ids decide, as MariaDB ignores case
        var found = await db.Users.OnlySelectable().Where(u => wanted.Contains(u.Id)).Select(u => u.Id).ToListAsync(cancellationToken);
        return found.Where(id => !string.Equals(id, presenterId, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>The bell for the newly invited; after the invite stands, so it runs to the end.</summary>
    private async Task NotifyInvitedAsync(IReadOnlyCollection<string> agentIds, LiveSessionSnapshot session)
    {
        if (agentIds.Count == 0)
        {
            return;
        }
        try
        {
            // bell only, like the start: an invite list is not for a shared Discord channel
            await notifications.NotifyManyInAppAsync(agentIds, NotificationType.PresentationLive,
                $"{session.PresenterCodename} lädt dich zur Vorführung ein: {session.Title}",
                $"/praesentationen/{session.PresentationId}/live", session.PresenterId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Live invite notice for presentation {PresentationId} failed.", session.PresentationId);
        }
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
