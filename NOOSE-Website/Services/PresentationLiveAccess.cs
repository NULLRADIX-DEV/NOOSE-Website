using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Infrastructure.Presentations;

namespace NOOSE_Website.Services;

/// <summary>Who watches a running presentation only because its presenter invited them.</summary>
/// <remarks>
/// An invitation lives in the live session, not in the database: it reaches the stage, the banner, the bell and the
/// pictures of the slides, and ends with the session. It never reaches the detail page, the print view, the editor,
/// the notes, the search or anything else that asks <see cref="PresentationVisibility"/>; those stay as they were.
/// </remarks>
public static class PresentationLiveAccess
{
    /// <summary>Whether the actor is invited to the session running now and is still a selectable internal agent.</summary>
    /// <remarks>The invite checked selectability when it was made; a block, a partner switch or the supervision flag
    /// since then ends it here too.</remarks>
    public static async Task<bool> IsInvitedViewerAsync(AppDbContext db, PresentationLiveRegistry registry,
        string presentationId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var me = actor.GetAgentId();
        if (string.IsNullOrEmpty(me) || !actor.IsInternalAgent() || !registry.IsInvited(presentationId, me))
        {
            return false;
        }
        return await db.Users.OnlySelectable().AnyAsync(u => u.Id == me, cancellationToken);
    }
}
