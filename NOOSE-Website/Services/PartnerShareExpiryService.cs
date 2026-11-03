using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Takes releases past their end date off the books; the read gate already ignores them by date.</summary>
public interface IPartnerShareExpiryService
{
    /// <summary>Revokes every release whose end date has passed and logs it on the record; returns how many.</summary>
    Task<int> ExpireDueAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IPartnerShareExpiryService" />
/// <remarks>No actor and no guard: nobody is acting. Not a security control either, because
/// <see cref="PartnerVisibility.ActiveShares"/> filters by date, so a late worker leaks nothing.</remarks>
public class PartnerShareExpiryService(IDbContextFactory<AppDbContext> dbFactory) : IPartnerShareExpiryService
{
    /// <summary>Rows per pass; a backlog drains over the next ticks.</summary>
    private const int Batch = 200;

    public async Task<int> ExpireDueAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var due = await db.PartnerShares
            .Where(s => s.ValidUntil != null && s.ValidUntil <= now)
            .OrderBy(s => s.ValidUntil)
            .Take(Batch)
            .ToListAsync(cancellationToken);
        if (due.Count == 0)
        {
            return 0;
        }

        foreach (var share in due)
        {
            var label = await PartnerShareService.LabelAsync(db, share.Agency, share.PartnerAgentId, cancellationToken);
            // soft-delete via interceptor, like a manual revoke; the record row below says it ran out instead
            db.PartnerShares.Remove(share);
            db.AuditLogs.Add(PartnerShareService.RecordRow(share.EntityType, share.EntityId, label, actor: null,
                "freigegeben, " + PartnerShareExpiry.Until(share.ValidUntil), "abgelaufen"));
        }
        await db.SaveChangesAsync(cancellationToken);
        return due.Count;
    }
}
