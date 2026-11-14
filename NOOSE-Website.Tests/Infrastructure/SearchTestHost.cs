using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Search;

namespace NOOSE_Website.Tests.Infrastructure;

/// <summary>Builds a real <see cref="SearchService"/> over the registered provider set.</summary>
/// <remarks>
/// Goes through <c>AddSearchProviders</c> rather than a hand-written list, so a provider missing from the
/// composition root fails here too.
/// MaxConcurrency is pinned to 1: <see cref="SqliteTestContext"/> hands every context the same open connection,
/// and two concurrent commands on one SQLite connection are undefined behaviour.
/// </remarks>
public static class SearchTestHost
{
    public static SearchService NewService(
        SqliteTestContext ctx,
        IPartnerVisibilityPolicyService? partnerPolicy = null,
        SearchOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<AppDbContext>>(ctx.Factory);
        services.AddSearchProviders();
        var resolved = services.BuildServiceProvider().GetServices<ISearchProvider>().ToList();

        var settings = options ?? new SearchOptions();
        settings.MaxConcurrency = 1;

        return new SearchService(
            resolved,
            ctx.Factory,
            partnerPolicy ?? new UnrestrictedPartnerPolicy(),
            Options.Create(settings),
            NullLogger<SearchService>.Instance);
    }

    /// <summary>All providers the composition root registers, for tests that assert over the roster.</summary>
    public static IReadOnlyList<ISearchProvider> Providers(SqliteTestContext ctx)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<AppDbContext>>(ctx.Factory);
        services.AddSearchProviders();
        return services.BuildServiceProvider().GetServices<ISearchProvider>().ToList();
    }

    /// <summary>The default: no partner rank is configured, so no rank narrows anything.</summary>
    public sealed class UnrestrictedPartnerPolicy : IPartnerVisibilityPolicyService
    {
        public Task<IReadOnlySet<string>?> GetAllowedTypesAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlySet<string>?>(null);

        public Task<IReadOnlySet<string>?> GetVisibleTabsAsync(
            ClaimsPrincipal user, string typeKey, string recordId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlySet<string>?>(null);
    }

    /// <summary>A partner rank restricted to the named type keys.</summary>
    public sealed class RankRestrictedPartnerPolicy(params string[] allowed) : IPartnerVisibilityPolicyService
    {
        private readonly IReadOnlySet<string> _allowed = allowed.ToHashSet(StringComparer.Ordinal);

        public Task<IReadOnlySet<string>?> GetAllowedTypesAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlySet<string>?>(user.IsPartner() ? _allowed : null);

        public Task<IReadOnlySet<string>?> GetVisibleTabsAsync(
            ClaimsPrincipal user, string typeKey, string recordId, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlySet<string>?>(null);
    }
}
