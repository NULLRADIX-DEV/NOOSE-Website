using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Notifications;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Data.Entities.Requests;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.CurrentUser;
using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Infrastructure.Authorization;

/// <summary>Blocks all writes for read-only supervisors and partners; registered first in the interceptor chain.</summary>
/// <remarks>
/// Three carve-outs, on three different axes: a partner authors agency content (create only), a partner files its
/// own application to join, and every account here except the demo visitor files what belongs to its own civilian
/// identity. Editing again is limited to the row the account created itself, so no carve-out reaches a stranger's row.
/// </remarks>
public class ReadOnlyBarrierInterceptor(ICurrentUserService currentUserService) : SaveChangesInterceptor
{
    private static readonly HashSet<Type> Whitelist =
    [
        typeof(AuditLog),
        typeof(AccessLog),
        typeof(Notification),
    ];

    // Agency content a partner may author; create only — modify/delete of existing rows stays blocked. The
    // read-only supervision writes none of this: it is record material, whoever the account belongs to.
    private static readonly HashSet<Type> PartnerAuthorable =
    [
        typeof(Document),
        typeof(Source),
        typeof(TaskforceMessage),
    ];

    // What an account files out of its own CIVILIAN identity — the identity itself, its tickets and its tips.
    // The supervision is in as well: behind the account sits a private person, and none of this is record material.
    private static readonly HashSet<Type> CitizenAuthorable =
    [
        typeof(BuergerProfil),
        typeof(Ticket),
        typeof(TicketNachricht),
        typeof(Hinweis),
        typeof(HinweisNachricht),
    ];

    // What the private person behind a PARTNER account files about JOINING: the application, its replies and the
    // aptitude test. A third axis, because it is neither agency content nor part of the civilian identity — and
    // because the read-only supervision stays out: that account is an oversight seat, not a candidate.
    private static readonly HashSet<Type> PartnerApplicationAuthorable =
    [
        typeof(Bewerbung),
        typeof(BewerbungMessage),
        typeof(BewerbungTestAnswer),
    ];

    // Rows either side may change again afterwards — their own only; the create side is handled above.
    private static readonly HashSet<Type> PartnerEditableOwn = [typeof(Document)];

    // a saved draft is written again on every autosave until the attempt is handed in
    private static readonly HashSet<Type> PartnerApplicationEditableOwn = [typeof(BewerbungTestAnswer)];

    private static readonly HashSet<Type> CitizenEditableOwn =
    [
        // a reply in the citizen thread moves the status and the activity stamp of the ticket carrying it
        typeof(Ticket),
        // only the insert race in SaveOwnAsync lands here; from the first save the name is locked
        typeof(BuergerProfil),
    ];

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        var user = await currentUserService.GetAsync();
        Require(eventData.Context, user);
        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        var user = currentUserService.Get();
        Require(eventData.Context, user);
        return base.SavingChanges(eventData, result);
    }

    private static void Require(DbContext? context, CurrentUserInfo user)
    {
        if (context is null || (!user.IsOnlyReader && !user.IsPartner && !user.IsDemo))
        {
            return;
        }

        // partners may author agency content (create only); the supervision may not
        bool partnerMayAuthor = user.IsPartner && !user.IsOnlyReader && !user.IsDemo;
        // the civilian identity behind the account is open to both — only the demo visitor is nobody
        bool mayActAsCitizen = !user.IsDemo;

        context.ChangeTracker.DetectChanges();
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }
            var type = entry.Entity.GetType();
            if (Whitelist.Contains(type))
            {
                continue;
            }
            if (entry.State == EntityState.Added
                && ((partnerMayAuthor && PartnerAuthorable.Contains(type))
                    || (partnerMayAuthor && PartnerApplicationAuthorable.Contains(type))
                    || (partnerMayAuthor && entry.Entity is Request { Type: RequestType.PartnerAnfrage })
                    || (mayActAsCitizen && CitizenAuthorable.Contains(type))))
            {
                continue;
            }
            // a row they created themselves may be changed again (create handled above)
            if (entry.State == EntityState.Modified
                && ((partnerMayAuthor && PartnerEditableOwn.Contains(type))
                    || (partnerMayAuthor && PartnerApplicationEditableOwn.Contains(type))
                    || (mayActAsCitizen && CitizenEditableOwn.Contains(type)))
                && entry.Entity is IAuditable own && own.CreatedById == user.Id)
            {
                continue;
            }
            throw new UnauthorizedAccessException(
                "Nur-Lese-Modus: Änderungen sind in der Aufsichtsrolle nicht möglich.");
        }
    }
}
