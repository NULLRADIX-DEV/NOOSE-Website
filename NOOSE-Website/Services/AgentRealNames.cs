using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;

namespace NOOSE_Website.Services;

/// <summary>Real names of in-house agents by account id, shown beside a codename. Empty for viewers outside <see cref="AgentPrincipalExtensions.MayRealNameSee" />.</summary>
public interface IAgentRealNames
{
    /// <summary>Id → real name for the current viewer; empty when the viewer may not see real names.</summary>
    Task<IReadOnlyDictionary<string, string>> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>Per-circuit lookup over a short shared cache.</summary>
public sealed class AgentRealNames(
    IDbContextFactory<AppDbContext> dbFactory,
    IMemoryCache cache,
    AuthenticationStateProvider auth) : IAgentRealNames
{
    private const string CacheKey = "agent-real-names";
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    private static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>();

    private Task<IReadOnlyDictionary<string, string>>? _pending;
    private DateTime _pendingAt;

    public Task<IReadOnlyDictionary<string, string>> GetAsync(CancellationToken cancellationToken = default)
    {
        // shared per circuit so labels after the first render synchronously
        var now = DateTime.UtcNow;
        if (_pending is null || _pending.IsFaulted || _pending.IsCanceled || now - _pendingAt > Ttl)
        {
            _pending = LoadForViewerAsync(CancellationToken.None);
            _pendingAt = now;
        }
        return _pending;
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadForViewerAsync(CancellationToken cancellationToken)
    {
        ClaimsPrincipal user;
        try
        {
            user = (await auth.GetAuthenticationStateAsync()).User;
        }
        catch (InvalidOperationException)
        {
            return None;
        }
        if (!user.MayRealNameSee())
        {
            return None;
        }
        if (cache.TryGetValue(CacheKey, out IReadOnlyDictionary<string, string>? hit) && hit is not null)
        {
            return hit;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var map = await LoadAsync(db, cancellationToken);
        cache.Set(CacheKey, map, Ttl);
        return map;
    }

    /// <summary>Released in-house accounts only: never supervision, partners or citizen accounts.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> LoadAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        var rows = await db.Users
            .Where(a => a.Rank != null && a.PartnerAgency == null && !a.IsTeamLead
                        && a.RealName != null && a.RealName != "")
            .Select(a => new { a.Id, a.RealName })
            .ToListAsync(cancellationToken);
        return rows
            .Where(r => !string.IsNullOrWhiteSpace(r.RealName))
            .ToDictionary(r => r.Id, r => r.RealName!.Trim(), StringComparer.Ordinal);
    }
}
