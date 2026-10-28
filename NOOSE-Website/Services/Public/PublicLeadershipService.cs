using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Public;

namespace NOOSE_Website.Services.Public;

/// <inheritdoc />
public partial class PublicLeadershipService(
    IDbContextFactory<AppDbContext> dbFactory,
    IPublicModuleService modules,
    IPublicLeadershipPhotoStorageService storage,
    IMemoryCache cache) : IPublicLeadershipService
{
    private const string CacheKey = "public:roster";
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    public const int MaxPhoneLength = 32;

    private static readonly StringComparer ByName = StringComparer.Create(CultureInfo.GetCultureInfo("de-DE"), true);

    private sealed record RosterRow(string Id, string Codename, string Name, Rank? Rank, bool Hidden)
    {
        public bool IsLeadership => Rank is { } r && r >= LeadershipChart.RankFloor;
    }

    private sealed record CardData(string Phone, string? PhotoFileName);

    /// <summary>Where the anonymous endpoint serves a card photo; the random file name is the only handle.</summary>
    public static string PhotoUrl(string fileName) => $"/fuehrung/foto/{fileName}";

    // the storage names every copy <32 hex>.<ext>; anything else never reaches the database
    [GeneratedRegex(@"^[0-9a-f]{32}\.(jpg|png|webp|gif)$")]
    private static partial Regex PhotoFileNamePattern();

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
        // whoever opted out is simply absent; nothing on the page counts or hints at them
        var rows = (await RosterAsync(db, cancellationToken)).Where(r => !r.Hidden).ToList();
        var leaders = rows.Where(r => r.IsLeadership).ToList();
        var cards = await CardsAsync(db, leaders.Select(r => r.Id).ToList(), cancellationToken);

        var roster = new PublicRoster(
            OrderLeadership(leaders)
                .GroupBy(r => r.Rank)
                .Select(g => new PublicLeadershipTier(RankDisplay.Name(g.Key),
                    g.Select(r => Card(r, cards.GetValueOrDefault(r.Id))).ToList()))
                .ToList(),
            rows.Where(r => !r.IsLeadership)
                .OrderBy(r => r.Name, ByName)
                .Select(r => new PublicRosterMember(r.Name))
                .ToList());

        cache.Set(CacheKey, roster, CacheFor);
        return roster;
    }

    /// <inheritdoc />
    /// <remarks>
    /// One answer for every miss. A photo goes out only while the page shows it, so a demotion or a termination
    /// takes the picture down with the card; the file stays and comes back if the agent returns to the band.
    /// </remarks>
    public async Task<PublicLeadershipPhoto?> GetPublishedPhotoAsync(string fileName,
        CancellationToken cancellationToken = default)
    {
        if (!PhotoFileNamePattern().IsMatch(fileName))
        {
            return null;
        }
        // the cached roster answers whether the page shows it; module off reads as empty
        var url = PhotoUrl(fileName);
        var roster = await GetPublicAsync(cancellationToken);
        if (!roster.Leadership.SelectMany(t => t.Cards).Any(c => c.PhotoUrl == url))
        {
            return null;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var type = await db.OeffentlicheTelefonnummern.AsNoTracking()
            .Where(p => p.PhotoFileName == fileName)
            .Select(p => p.PhotoContentType)
            .FirstOrDefaultAsync(cancellationToken);
        return new PublicLeadershipPhoto(fileName, type ?? "application/octet-stream");
    }

    // ---- settings ----

    public async Task<IReadOnlyList<PublicLeadershipEdit>> GetLeadershipAsync(ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        // not the supervision: row order, phone and photo pair each codename with its public card
        Permission.RequireLeadershipNoReader(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var leaders = (await RosterAsync(db, cancellationToken)).Where(r => r.IsLeadership).ToList();
        var cards = await CardsAsync(db, leaders.Select(r => r.Id).ToList(), cancellationToken);
        return OrderLeadership(leaders)
            .Select(r =>
            {
                var card = Card(r, cards.GetValueOrDefault(r.Id));
                return new PublicLeadershipEdit(r.Id, r.Codename, r.Name, r.Rank!.Value, card.Phone, card.PhotoUrl,
                    r.Hidden);
            })
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
            await RequireLeaderAsync(db, agentId, cancellationToken);
        }

        var row = await db.OeffentlicheTelefonnummern.FirstOrDefaultAsync(p => p.AgentId == agentId, cancellationToken);
        if (number is null)
        {
            if (row is null || row.PhoneNumber.Length == 0)
            {
                return;
            }
            if (row.PhotoFileName is null)
            {
                db.OeffentlicheTelefonnummern.Remove(row);
            }
            else
            {
                row.PhoneNumber = string.Empty;
            }
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
        await SaveAsync(db, cancellationToken);
    }

    public async Task SetPhotoAsync(string agentId, Stream content, string contentType, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);
        if (!storage.IsAllowedType(contentType))
        {
            throw new InvalidOperationException("Dieses Bildformat wird nicht unterstützt.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await RequireLeaderAsync(db, agentId, cancellationToken);
        var row = await db.OeffentlicheTelefonnummern.FirstOrDefaultAsync(p => p.AgentId == agentId, cancellationToken);
        if (row is null)
        {
            row = new OeffentlicheTelefonnummer { AgentId = agentId };
            db.OeffentlicheTelefonnummern.Add(row);
        }

        var previous = row.PhotoFileName;
        // a COPY under the public path; the agent's own avatar stays behind Policies.ActiveAgent
        var saved = await storage.SaveAsync(content, contentType, cancellationToken);
        row.PhotoFileName = saved;
        row.PhotoContentType = contentType;
        try
        {
            await SaveAsync(db, cancellationToken);
        }
        catch
        {
            DeleteFile(saved);
            throw;
        }
        if (previous is not null)
        {
            DeleteFile(previous);
        }
    }

    public async Task RemovePhotoAsync(string agentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.OeffentlicheTelefonnummern.FirstOrDefaultAsync(p => p.AgentId == agentId, cancellationToken);
        if (row?.PhotoFileName is not { } previous)
        {
            return;
        }
        if (row.PhoneNumber.Length == 0)
        {
            db.OeffentlicheTelefonnummern.Remove(row);
        }
        else
        {
            row.PhotoFileName = null;
            row.PhotoContentType = null;
        }
        await SaveAsync(db, cancellationToken);
        DeleteFile(previous);
    }

    public async Task<bool> IsOwnHiddenAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var agentId = actor.GetAgentId();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Users.AsNoTracking()
            .Where(u => u.Id == agentId)
            .Select(u => u.HiddenFromPublicRoster)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Only ever the actor's own row, by ExecuteUpdate: <c>UserManager.UpdateAsync</c> would write it back from a
    /// stale copy. A personal preference like the nav blob, so no audit row.
    /// </remarks>
    public async Task SetOwnHiddenAsync(bool hidden, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        var agentId = actor.GetAgentId();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Users.Where(u => u.Id == agentId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.HiddenFromPublicRoster, hidden), cancellationToken);
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

    private async Task SaveAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // two first saves of the same card at once trip the unique index
            throw new InvalidOperationException(
                "Die Karte wurde gerade von jemand anderem geändert. Bitte neu laden und erneut speichern.");
        }
        cache.Remove(CacheKey);
    }

    private void DeleteFile(string fileName)
    {
        try { storage.Delete(fileName); }
        catch { /* best effort */ }
    }

    private static PublicLeadershipCard Card(RosterRow row, CardData? data)
        => new(row.Name,
            string.IsNullOrEmpty(data?.Phone) ? null : data.Phone,
            data?.PhotoFileName is { } file ? PhotoUrl(file) : null);

    /// <summary>Everyone who may appear: selectable, with a confirmed real name.</summary>
    private static async Task<List<RosterRow>> RosterAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.Users.AsNoTracking()
            .OnlySelectable()
            .Where(u => u.RealName != null && u.RealName != "")
            .Select(u => new { u.Id, u.Codename, u.RealName, u.Rank, u.HiddenFromPublicRoster })
            .ToListAsync(cancellationToken);
        return rows
            .Select(u => new RosterRow(u.Id, u.Codename, u.RealName!.Trim(), u.Rank, u.HiddenFromPublicRoster))
            .Where(r => r.Name.Length > 0)
            .ToList();
    }

    private static async Task<bool> IsLeaderAsync(AppDbContext db, string agentId, CancellationToken cancellationToken)
        => (await RosterAsync(db, cancellationToken))
            .Any(r => r.IsLeadership && string.Equals(r.Id, agentId, StringComparison.Ordinal));

    private static async Task RequireLeaderAsync(AppDbContext db, string agentId, CancellationToken cancellationToken)
    {
        if (!await IsLeaderAsync(db, agentId, cancellationToken))
        {
            throw new InvalidOperationException(
                "Eine Führungskarte gibt es nur für aktive Agenten ab Supervisory Special Agent mit Klarnamen.");
        }
    }

    private static async Task<Dictionary<string, CardData>> CardsAsync(AppDbContext db, List<string> agentIds,
        CancellationToken cancellationToken)
    {
        if (agentIds.Count == 0)
        {
            return [];
        }
        return await db.OeffentlicheTelefonnummern.AsNoTracking()
            .Where(p => agentIds.Contains(p.AgentId))
            .ToDictionaryAsync(p => p.AgentId, p => new CardData(p.PhoneNumber, p.PhotoFileName),
                StringComparer.Ordinal, cancellationToken);
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
