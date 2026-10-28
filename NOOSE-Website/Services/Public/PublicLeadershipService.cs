using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Public;

namespace NOOSE_Website.Services.Public;

/// <inheritdoc />
public class PublicLeadershipService(
    IDbContextFactory<AppDbContext> dbFactory,
    IPublicModuleService modules,
    IMemoryCache cache) : IPublicLeadershipService
{
    private const string CacheKey = "public:roster";
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    public const int MaxPhoneLength = 32;

    private static readonly StringComparer ByName = StringComparer.Create(CultureInfo.GetCultureInfo("de-DE"), true);

    private sealed record RosterRow(string Id, string Codename, string Name, Rank? Rank)
    {
        public bool IsLeadership => Rank is { } r && r >= LeadershipChart.RankFloor;
    }

    // ---- outward ----

    public async Task<PublicRoster> GetPublicAsync(CancellationToken cancellationToken = default)
    {
        if (!await IsLiveAsync(cancellationToken))
        {
            return PublicRoster.Empty;
        }
        if (cache.TryGetValue(CacheKey, out PublicRoster? cached) && cached is not null)
        {
            return cached;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await RosterAsync(db, cancellationToken);
        var leaders = rows.Where(r => r.IsLeadership).ToList();
        var phones = await PhonesAsync(db, leaders.Select(r => r.Id).ToList(), cancellationToken);

        var roster = new PublicRoster(
            OrderLeadership(leaders)
                .Select(r => new PublicLeadershipCard(r.Name, RankDisplay.Name(r.Rank), phones.GetValueOrDefault(r.Id)))
                .ToList(),
            rows.Where(r => !r.IsLeadership)
                .OrderBy(r => r.Name, ByName)
                .Select(r => new PublicRosterMember(r.Name))
                .ToList());

        cache.Set(CacheKey, roster, CacheFor);
        return roster;
    }

    // ---- settings ----

    public async Task<IReadOnlyList<PublicLeadershipEdit>> GetLeadershipAsync(ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        // not the supervision: row order and phone pair each codename with its public card
        Permission.RequireLeadershipNoReader(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var leaders = (await RosterAsync(db, cancellationToken)).Where(r => r.IsLeadership).ToList();
        var phones = await PhonesAsync(db, leaders.Select(r => r.Id).ToList(), cancellationToken);
        return OrderLeadership(leaders)
            .Select(r => new PublicLeadershipEdit(r.Id, r.Codename, r.Name, r.Rank!.Value, phones.GetValueOrDefault(r.Id)))
            .ToList();
    }

    public async Task SetPhoneAsync(string agentId, string? phone, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        // write guard first: otherwise the read-only supervision gets as far as the roster check
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);
        var number = CleanPhone(phone);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // a raw id off the wire must name someone the card is shown for; removing works for anyone
        if (number is not null)
        {
            var leader = (await RosterAsync(db, cancellationToken))
                .Any(r => r.IsLeadership && string.Equals(r.Id, agentId, StringComparison.Ordinal));
            if (!leader)
            {
                throw new InvalidOperationException(
                    "Eine Telefonnummer gibt es nur für aktive Agenten ab Supervisory Special Agent mit Klarnamen.");
            }
        }

        var row = await db.OeffentlicheTelefonnummern.FirstOrDefaultAsync(p => p.AgentId == agentId, cancellationToken);
        if (number is null)
        {
            if (row is null)
            {
                return;
            }
            db.OeffentlicheTelefonnummern.Remove(row);
        }
        else if (row is null)
        {
            db.OeffentlicheTelefonnummern.Add(new OeffentlicheTelefonnummer { AgentId = agentId, PhoneNumber = number });
        }
        else if (row.PhoneNumber != number)
        {
            row.PhoneNumber = number;
        }
        else
        {
            return;
        }
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // two leaders saving the same card at once trip the unique index
            throw new InvalidOperationException(
                "Die Nummer wurde gerade von jemand anderem geändert. Bitte neu laden und erneut speichern.");
        }
        cache.Remove(CacheKey);
    }

    /// <summary>Trimmed number, null for blank; throws on anything a phone number does not contain.</summary>
    public static string? CleanPhone(string? phone)
    {
        var text = (phone ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return null;
        }
        if (text.Length > MaxPhoneLength)
        {
            throw new InvalidOperationException($"Die Telefonnummer darf höchstens {MaxPhoneLength} Zeichen haben.");
        }
        if (!text.Any(char.IsAsciiDigit) || !text.All(c => char.IsAsciiDigit(c) || " +-/().".Contains(c)))
        {
            throw new InvalidOperationException("Die Telefonnummer darf nur Ziffern, Leerzeichen und + - / ( ) . enthalten.");
        }
        return text;
    }

    /// <summary>Everyone who may appear: selectable, with a confirmed real name.</summary>
    private static async Task<List<RosterRow>> RosterAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.Users.AsNoTracking()
            .OnlySelectable()
            .Where(u => u.RealName != null && u.RealName != "")
            .Select(u => new { u.Id, u.Codename, u.RealName, u.Rank })
            .ToListAsync(cancellationToken);
        return rows
            .Select(u => new RosterRow(u.Id, u.Codename, u.RealName!.Trim(), u.Rank))
            .Where(r => r.Name.Length > 0)
            .ToList();
    }

    private static async Task<Dictionary<string, string>> PhonesAsync(AppDbContext db, List<string> agentIds,
        CancellationToken cancellationToken)
    {
        if (agentIds.Count == 0)
        {
            return [];
        }
        return await db.OeffentlicheTelefonnummern.AsNoTracking()
            .Where(p => agentIds.Contains(p.AgentId))
            .ToDictionaryAsync(p => p.AgentId, p => p.PhoneNumber, StringComparer.Ordinal, cancellationToken);
    }

    private static IEnumerable<RosterRow> OrderLeadership(IEnumerable<RosterRow> leaders)
        => leaders.OrderByDescending(r => r.Rank).ThenBy(r => r.Name, ByName);

    /// <summary>Module on and kill switch off; the same pair every outward read asks.</summary>
    private async Task<bool> IsLiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await modules.GetAsync(cancellationToken);
            return snapshot.IsEnabled(PublicModules.Leadership);
        }
        catch
        {
            /* best effort: an unreachable snapshot reads as off */
            return false;
        }
    }
}
