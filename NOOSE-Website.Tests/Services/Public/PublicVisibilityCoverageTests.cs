using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Tests.Services.Public;

/// <summary>The guarantee behind "nothing reaches the public area by accident".</summary>
/// <remarks>
/// Reflects over every <c>DbSet</c> on <see cref="AppDbContext"/>. An entity must either stand in
/// <see cref="PublicVisibility.Publishable"/> with what leaves the house, or in
/// <see cref="PublicVisibility.NeverPublic"/> with why it never does. A new table therefore turns the build red until
/// someone has decided — which is the mechanism, not the review.
/// </remarks>
public class PublicVisibilityCoverageTests
{
    private static IReadOnlyList<string> EntityNames()
        => typeof(AppDbContext).GetProperties()
            .Where(p => p.PropertyType.IsGenericType
                && p.PropertyType.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Select(p => p.PropertyType.GetGenericArguments()[0].Name)
            // ASP.NET Identity's own tables are framework plumbing, never NOOSE records
            .Where(n => !n.StartsWith("Identity", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void Every_entity_is_decided_either_way()
    {
        var undecided = EntityNames().Where(n => !PublicVisibility.IsDecided(n)).ToArray();

        Assert.True(undecided.Length == 0,
            "Diese Entitäten sind öffentlich unentschieden – in PublicVisibility eintragen: "
            + string.Join(", ", undecided));
    }

    [Fact]
    public void Every_decision_carries_a_reason()
    {
        var mute = PublicVisibility.Publishable.Concat(PublicVisibility.NeverPublic)
            .Where(e => string.IsNullOrWhiteSpace(e.Value))
            .Select(e => e.Key)
            .ToArray();

        Assert.True(mute.Length == 0, "Ohne Begründung eingetragen: " + string.Join(", ", mute));
    }

    [Fact]
    public void No_entity_is_both_publishable_and_never_public()
    {
        var contradictory = PublicVisibility.Publishable.Keys
            .Where(PublicVisibility.NeverPublic.ContainsKey)
            .ToArray();

        Assert.True(contradictory.Length == 0, "Widersprüchlich eingetragen: " + string.Join(", ", contradictory));
    }

    [Fact]
    public void No_decision_names_an_entity_that_no_longer_exists()
    {
        // a stale entry silently absolves the author of a table that was renamed
        var known = typeof(AppDbContext).Assembly.GetTypes()
            .Select(t => t.Name)
            .ToHashSet(StringComparer.Ordinal);

        var ghosts = PublicVisibility.Publishable.Keys.Concat(PublicVisibility.NeverPublic.Keys)
            .Where(k => !known.Contains(k))
            .ToArray();

        Assert.True(ghosts.Length == 0, "Benennt keine existierende Entität mehr: " + string.Join(", ", ghosts));
    }

    [Fact]
    public void The_agent_account_goes_out_only_as_the_roster()
    {
        // names leave through /fuehrung alone; the record shapes are pinned in PublicLeadershipServiceTests
        Assert.True(PublicVisibility.MayBePublished("Agent"));
        Assert.Contains("/fuehrung", PublicVisibility.Publishable["Agent"]);
        Assert.False(PublicVisibility.MayBePublished("OeffentlichesFuehrungsprofil"));
    }

    [Fact]
    public void A_record_file_is_never_publishable_itself()
    {
        // person and faction files go out as a publish snapshot, never as themselves — phase 4 adds the snapshot
        // tables to Publishable, and these two must not drift along with them
        Assert.False(PublicVisibility.MayBePublished("Person"));
        Assert.False(PublicVisibility.MayBePublished("Faction"));
        Assert.False(PublicVisibility.MayBePublished("Document"));
    }
}
