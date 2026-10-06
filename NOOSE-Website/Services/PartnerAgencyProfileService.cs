using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IPartnerAgencyProfileService" />
public class PartnerAgencyProfileService(IDbContextFactory<AppDbContext> dbFactory, IMemoryCache cache) : IPartnerAgencyProfileService
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);

    private static string CacheKey(PartnerAgency agency) => $"PartnerBehoerdenProfil:{(int)agency}";

    public async Task<PartnerAgencyProfileView> GetAsync(PartnerAgency agency, CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey(agency), out PartnerAgencyProfileView? view) && view is not null)
        {
            return view;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var profile = await db.PartnerAgencyProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Agency == agency, cancellationToken);
        var rules = await db.PartnerReleaseRules.AsNoTracking()
            .Where(r => r.Agency == agency)
            .Select(r => new PartnerRuleView(r.EntityType, r.Scope, r.IncludesChildren))
            .ToListAsync(cancellationToken);
        view = new PartnerAgencyProfileView(agency, profile?.Features ?? PartnerFeature.None,
            profile?.BlockedContent ?? PartnerContent.None, rules);
        cache.Set(CacheKey(agency), view, CacheDuration);
        return view;
    }

    public async Task<PartnerFeature> GetFeaturesAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
        => user.GetPartnerAgency() is { } agency
            ? (await GetAsync(agency, cancellationToken)).Features
            : PartnerFeature.None;

    public async Task<bool> HasFeatureAsync(ClaimsPrincipal user, PartnerFeature feature, CancellationToken cancellationToken = default)
        => (await GetFeaturesAsync(user, cancellationToken)).HasFlag(feature);

    public async Task SaveAsync(PartnerAgency agency, PartnerFeature features, PartnerContent blocked, IReadOnlyList<PartnerRuleView> rules,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireAdmin(actor);
        foreach (var rule in rules)
        {
            Validate(rule);
        }
        if (rules.GroupBy(r => r.EntityType).Any(g => g.Count() > 1))
        {
            throw new ArgumentException("Je Aktentyp gibt es höchstens eine Regel.", nameof(rules));
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var profile = await db.PartnerAgencyProfiles.FirstOrDefaultAsync(p => p.Agency == agency, cancellationToken);
        if (profile is null)
        {
            profile = new PartnerAgencyProfile { Agency = agency };
            db.PartnerAgencyProfiles.Add(profile);
        }
        profile.Features = features;
        profile.BlockedContent = blocked;

        var existing = await db.PartnerReleaseRules.Where(r => r.Agency == agency).ToListAsync(cancellationToken);
        foreach (var rule in rules)
        {
            var row = existing.FirstOrDefault(r => r.EntityType == rule.EntityType);
            if (row is null)
            {
                db.PartnerReleaseRules.Add(new PartnerReleaseRule
                {
                    Agency = agency, EntityType = rule.EntityType, Scope = rule.Scope, IncludesChildren = rule.IncludesChildren,
                });
                continue;
            }
            row.Scope = rule.Scope;
            row.IncludesChildren = rule.IncludesChildren;
        }
        db.PartnerReleaseRules.RemoveRange(existing.Where(r => rules.All(n => n.EntityType != r.EntityType)));

        await db.SaveChangesAsync(cancellationToken);
        cache.Remove(CacheKey(agency));
    }

    /// <summary>A rule names a releasable type and a scope that type supports.</summary>
    private static void Validate(PartnerRuleView rule)
    {
        if (!PartnerTabCatalog.All.Any(t => t.TypeKey == rule.EntityType))
        {
            throw new ArgumentException($"Für „{rule.EntityType}“ gibt es keine Partnerfreigabe.", nameof(rule));
        }
        var fits = rule.Scope switch
        {
            PartnerRuleScope.All => true,
            PartnerRuleScope.BadFactions => rule.EntityType == nameof(Faction),
            PartnerRuleScope.BadFactionMembers => rule.EntityType == nameof(Person),
            _ => false,
        };
        if (!fits)
        {
            throw new ArgumentException($"Der Umfang „{PartnerProfileDisplay.Name(rule.Scope)}“ passt nicht zu diesem Aktentyp.", nameof(rule));
        }
    }
}
