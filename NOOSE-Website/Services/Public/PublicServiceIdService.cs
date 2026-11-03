using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Public;

namespace NOOSE_Website.Services.Public;

/// <inheritdoc />
/// <remarks>
/// Only agents who switched themselves onto the public roster answer at all, so the check reveals no one the staff
/// page does not already list. Of those, an active holder is valid and a terminated one reads as invalid without a
/// name or rank. Everything else, above all suspended, pending and supervision accounts, is a plain miss.
/// </remarks>
public sealed class PublicServiceIdService(
    IDbContextFactory<AppDbContext> dbFactory,
    IPublicModuleService modules) : IPublicServiceIdService
{
    private sealed record Row(AgentStatus Status, string Codename, Rank? Rank);

    public async Task<PublicServiceIdCheck?> CheckAsync(string? number, CancellationToken cancellationToken = default)
    {
        // the shape first: a made-up string never reaches the module read or the database
        var canonical = Canonical(number);
        if (canonical is null || !await IsLiveAsync(cancellationToken))
        {
            return null;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Users.AsNoTracking()
            .Where(a => a.BadgeNumber != null && a.BadgeNumber.ToUpper() == canonical)
            .Where(a => !a.IsTeamLead && a.PartnerAgency == null && !a.HiddenFromPublicRoster)
            .Where(a => a.Status == AgentStatus.Active || a.Status == AgentStatus.Terminated)
            .Select(a => new Row(a.Status, a.Codename, a.Rank))
            .ToListAsync(cancellationToken);

        var active = rows.Where(r => r.Status == AgentStatus.Active).ToList();
        if (active.Count > 1)
        {
            // a legacy duplicate: naming either would be a guess
            return null;
        }
        if (active.Count == 1)
        {
            var holder = active[0];
            return holder.Rank is { } rank && !string.IsNullOrWhiteSpace(holder.Codename)
                ? new PublicServiceIdCheck(true, holder.Codename.Trim(), RankDisplay.Name(rank))
                : null;
        }
        return rows.Count > 0 ? new PublicServiceIdCheck(false, null, null) : null;
    }

    /// <summary>The upper-case badge number when it is one of the allowed numerals, else null.</summary>
    private static string? Canonical(string? number)
    {
        var trimmed = number?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > 8)
        {
            return null;
        }
        return BadgeNumbers.All.FirstOrDefault(x => string.Equals(x, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Module on and kill switch off; an unreachable snapshot reads as off.</summary>
    private async Task<bool> IsLiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await modules.IsEnabledAsync(PublicModules.ServiceId, cancellationToken);
        }
        catch
        {
            /* best effort */
            return false;
        }
    }
}
