using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Changelog;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.Changelog;
using NOOSE_Website.Infrastructure.CurrentUser;
using NOOSE_Website.Models.Changelog;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Changelog;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The changelog: the seeder's promise never to overwrite an edit, and what the page shows.</summary>
public sealed class ChangelogTests
{
    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).WithCodename("Falcon").Build();

    private static ClaimsPrincipal Agent()
        => ClaimsPrincipalBuilder.Agent("agent").WithRank(Rank.SpecialAgent).WithCodename("Wren").Build();

    private static ClaimsPrincipal OnlyReader()
        => ClaimsPrincipalBuilder.Agent("aufsicht").WithRank(Rank.Director).AsTeamLead().Build();

    private sealed class FixedUser : ICurrentUserService
    {
        public Task<CurrentUserInfo> GetAsync() => Task.FromResult(Get());

        public CurrentUserInfo Get() => new("lead", "Falcon", true, false, false);
    }

    /// <summary>The service with the audit interceptor attached, as in production.</summary>
    /// <remarks>
    /// The interceptor is what rewrites a <c>Remove</c> into a soft delete, so the recycle-bin test would
    /// exercise a hard delete without it — and the seeder's "do not revive" rule would silently pass.
    /// </remarks>
    private static (ChangelogService Service, TestDbContextFactory Factory) NewHost(SqliteTestContext ctx)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(ctx.Connection)
            .AddInterceptors(new AuditSaveChangesInterceptor(new FixedUser()))
            .Options;
        var factory = new TestDbContextFactory(options);
        return (new ChangelogService(factory), factory);
    }

    /// <summary>Seeds through an intercepted context, as production does.</summary>
    /// <remarks>
    /// Not a detail: the line CreatedAt the update window compares against is written by the audit interceptor.
    /// Seeding through a bare context leaves it at default and the "new since your last visit" count reads zero.
    /// </remarks>
    private static async Task SeedAsync(
        SqliteTestContext ctx,
        IReadOnlyList<ChangelogContent.SeededRelease> releases,
        int revision,
        string build)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(ctx.Connection)
            .AddInterceptors(new AuditSaveChangesInterceptor(new FixedUser()))
            .Options;
        await using var db = new AppDbContext(options);
        await ChangelogSeeder.SeedAsync(db, releases, revision, build);
    }

    private static ChangelogContent.SeededRelease[] OneRelease(string title = "Erste Zeile.")
        =>
        [
            new("1.0", new DateTime(2026, 8, 25), "Der Anfang",
            [
                new("1.0-a", ChangelogKind.Neu, title, "Fahndung"),
                new("1.0-b", ChangelogKind.Behoben, "Zweite Zeile.", null),
            ]),
        ];

    // --- the seeder -------------------------------------------------------

    [Fact]
    public async Task Seeding_twice_creates_each_line_once()
    {
        using var ctx = new SqliteTestContext();

        await SeedAsync(ctx, OneRelease(), 1, "1.0.100");
        await SeedAsync(ctx, OneRelease(), 1, "1.0.100");

        await using var check = ctx.NewContext();
        Assert.Equal(1, await check.Aenderungsfassungen.CountAsync());
        Assert.Equal(2, await check.Aenderungseintraege.CountAsync());
    }

    [Fact]
    public async Task Legacy_versions_and_keys_are_renamed_without_duplicates()
    {
        using var ctx = new SqliteTestContext();
        ChangelogContent.SeededRelease[] legacy =
        [
            new("1.4", new DateTime(2026, 9, 12), "Handbuch",
                [new("1.4-handbuch", ChangelogKind.Neu, "Handbuch.", "Handbuch")]),
        ];
        await SeedAsync(ctx, legacy, 1, "1.0.100");

        string releaseId;
        string entryId;
        await using (var before = ctx.NewContext())
        {
            releaseId = (await before.Aenderungsfassungen.SingleAsync()).Id;
            entryId = (await before.Aenderungseintraege.SingleAsync()).Id;
        }

        ChangelogContent.SeededRelease[] current =
        [
            new("2.0.00", new DateTime(2026, 9, 12), "Handbuch",
                [new("2.0.02-handbuch", ChangelogKind.Neu, "Handbuch.", "Handbuch")], "1.4"),
        ];
        await SeedAsync(ctx, current, 1, "1.0.200");

        await using var check = ctx.NewContext();
        var release = await check.Aenderungsfassungen.SingleAsync();
        var entry = await check.Aenderungseintraege.SingleAsync();
        Assert.Equal(releaseId, release.Id);
        Assert.Equal("2.0.00", release.Version);
        Assert.Equal(entryId, entry.Id);
        Assert.Equal("2.0.02-handbuch", entry.SeedKey);
        Assert.Equal(releaseId, entry.ReleaseId);
    }

    [Fact]
    public async Task A_line_moved_into_another_release_keeps_its_row()
    {
        using var ctx = new SqliteTestContext();
        ChangelogContent.SeededRelease[] before =
        [
            new("2.1.00", new DateTime(2026, 9, 14), "Vorher",
            [
                new("2.1.00-alt", ChangelogKind.Neu, "Bleibt hier.", "Akten"),
                new("2.1.01-funkplan", ChangelogKind.Neu, "Es gibt einen Funkplan.", "Ermittlung"),
            ]),
        ];
        await SeedAsync(ctx, before, 1, "1.0.100");

        string movedId;
        await using (var first = ctx.NewContext())
        {
            movedId = (await first.Aenderungseintraege.SingleAsync(e => e.SeedKey == "2.1.01-funkplan")).Id;
        }

        // the line is re-cut into its own release: new version, new number, same line
        ChangelogContent.SeededRelease[] after =
        [
            new("2.1.00", new DateTime(2026, 9, 14), "Vorher",
                [new("2.1.00-alt", ChangelogKind.Neu, "Bleibt hier.", "Akten")]),
            new("2.2.00", new DateTime(2026, 9, 22), "Nachher",
            [
                new("2.2.00-funkplan", ChangelogKind.Neu, "Es gibt einen Funkplan.", "Ermittlung",
                    "2.1.01-funkplan"),
                new("2.2.01-neu", ChangelogKind.Neu, "Nie zuvor ausgeliefert.", "Bedienung"),
            ]),
        ];
        // the raised revision is what lets the update branch write the new order onto the moved row
        await SeedAsync(ctx, after, 2, "1.0.200");

        await using var check = ctx.NewContext();
        var neu = await check.Aenderungsfassungen.SingleAsync(r => r.Version == "2.2.00");
        var moved = await check.Aenderungseintraege.SingleAsync(e => e.SeedKey == "2.2.00-funkplan");

        // the same row, moved - not a copy beside an orphan nobody sees any more
        Assert.Equal(movedId, moved.Id);
        Assert.Equal(neu.Id, moved.ReleaseId);
        Assert.Equal(3, await check.Aenderungseintraege.IgnoreQueryFilters().CountAsync());
        Assert.False(await check.Aenderungseintraege.IgnoreQueryFilters()
            .AnyAsync(e => e.SeedKey == "2.1.01-funkplan"));

        // and it arrives in front of the line that was never shipped before: a row that kept the number
        // of its old release would sort above everything in the new one
        var fresh = await check.Aenderungseintraege.SingleAsync(e => e.SeedKey == "2.2.01-neu");
        Assert.True(moved.SortOrder < fresh.SortOrder,
            $"Die umgezogene Zeile steht auf {moved.SortOrder}, die neue auf {fresh.SortOrder} — die alte Reihenfolge ist mitgewandert.");
    }

    [Fact]
    public async Task An_untouched_line_follows_a_new_revision()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, OneRelease("Alter Text."), 1, "1.0.100");

        await SeedAsync(ctx, OneRelease("Neuer Text."), 2, "1.0.100");

        await using var check = ctx.NewContext();
        var row = await check.Aenderungseintraege.SingleAsync(e => e.SeedKey == "1.0-a");
        Assert.Equal("Neuer Text.", row.Title);
        Assert.Equal(2, row.SeedRevision);
    }

    [Fact]
    public async Task An_edited_line_is_never_overwritten()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, OneRelease("Alter Text."), 1, "1.0.100");

        var (service, _) = NewHost(ctx);
        string id;
        await using (var db = ctx.NewContext())
        {
            id = (await db.Aenderungseintraege.SingleAsync(e => e.SeedKey == "1.0-a")).Id;
        }
        var release = (await service.GetReleasesAsync()).Single();
        await service.RefreshEntryAsync(id,
            new ChangelogEntryInput(release.Id, ChangelogKind.Neu, "Von Hand umformuliert.", "Fahndung", 0, true),
            Leader());

        // a later deploy ships a newer revision of the very same line
        await SeedAsync(ctx, OneRelease("Neuer Text."), 2, "1.0.100");

        await using var check = ctx.NewContext();
        var row = await check.Aenderungseintraege.SingleAsync(e => e.SeedKey == "1.0-a");
        Assert.Equal("Von Hand umformuliert.", row.Title);
        Assert.True(row.IsCustomised);
    }

    [Fact]
    public async Task A_deleted_line_is_not_revived()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, OneRelease(), 1, "1.0.100");

        var (service, _) = NewHost(ctx);
        string id;
        await using (var db = ctx.NewContext())
        {
            id = (await db.Aenderungseintraege.SingleAsync(e => e.SeedKey == "1.0-a")).Id;
        }
        await service.DeleteEntryAsync(id, Leader());

        await SeedAsync(ctx, OneRelease(), 2, "1.0.100");

        await using var check = ctx.NewContext();
        var row = await check.Aenderungseintraege.IgnoreQueryFilters()
            .SingleAsync(e => e.SeedKey == "1.0-a");
        Assert.True(row.IsDeleted);
        Assert.Single(await check.Aenderungseintraege.IgnoreQueryFilters()
            .Where(e => e.SeedKey == "1.0-a").ToListAsync());
    }

    [Fact]
    public async Task The_build_stamp_lands_on_the_newest_release_and_stays_put()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, OneRelease(), 1, "1.0.100");

        ChangelogContent.SeededRelease[] two =
        [
            .. OneRelease(),
            new("1.1", new DateTime(2026, 9, 10), "Danach",
                [new("1.1-a", ChangelogKind.Verbessert, "Etwas wurde besser.", null)]),
        ];
        await SeedAsync(ctx, two, 1, "1.0.200");

        await using var check = ctx.NewContext();
        // the older release keeps the build it shipped with; only the new one is stamped
        Assert.Equal("1.0.100", (await check.Aenderungsfassungen.SingleAsync(r => r.Version == "1.0")).BuildNumber);
        Assert.Equal("1.0.200", (await check.Aenderungsfassungen.SingleAsync(r => r.Version == "1.1")).BuildNumber);
    }

    [Fact]
    public async Task A_restart_does_not_stamp_an_older_release()
    {
        using var ctx = new SqliteTestContext();
        ChangelogContent.SeededRelease[] two =
        [
            .. OneRelease(),
            new("1.1", new DateTime(2026, 9, 10), "Danach",
                [new("1.1-a", ChangelogKind.Verbessert, "Etwas wurde besser.", null)]),
        ];

        await SeedAsync(ctx, two, 1, "1.0.500");
        await SeedAsync(ctx, two, 1, "1.0.500");

        await using var check = ctx.NewContext();
        Assert.Equal(1, await check.Aenderungsfassungen.CountAsync(r => r.BuildNumber != null));
    }

    // --- the service ------------------------------------------------------

    [Fact]
    public async Task A_release_without_visible_lines_does_not_appear()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, OneRelease(), 1, "1.0.100");

        var (service, _) = NewHost(ctx);
        Assert.Single(await service.GetTimelineAsync());

        await using (var db = ctx.NewContext())
        {
            foreach (var e in await db.Aenderungseintraege.ToListAsync())
            {
                e.IsVisible = false;
            }
            await db.SaveChangesAsync();
        }

        Assert.Empty(await service.GetTimelineAsync());
    }

    [Fact]
    public async Task A_first_visit_is_told_about_nothing()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, OneRelease(), 1, "1.0.100");

        var (service, _) = NewHost(ctx);
        var flash = await service.GetNewsSinceAsync(null);

        Assert.Equal(0, flash.Count);
    }

    [Fact]
    public async Task Only_releases_that_arrived_after_the_last_visit_are_counted()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, OneRelease(), 1, "1.0.100");

        var (service, _) = NewHost(ctx);
        var afterFirstSeeding = DateTime.UtcNow;
        Assert.Equal(0, (await service.GetNewsSinceAsync(afterFirstSeeding)).Count);

        await Task.Delay(20);
        ChangelogContent.SeededRelease[] two =
        [
            .. OneRelease(),
            new("1.1", new DateTime(2026, 9, 10), "Danach",
                [new("1.1-a", ChangelogKind.Verbessert, "Etwas wurde besser.", null)]),
        ];
        await SeedAsync(ctx, two, 1, "1.0.200");

        var flash = await service.GetNewsSinceAsync(afterFirstSeeding);
        Assert.Equal(1, flash.Count);
        Assert.Equal("1.1", flash.NewestVersion);
    }

    /// <summary>A release gathers lines over several deploys; the ones appended later are news too.</summary>
    [Fact]
    public async Task A_line_appended_to_an_existing_release_is_news()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, OneRelease(), 1, "1.0.100");

        var (service, _) = NewHost(ctx);
        var afterFirstSeeding = DateTime.UtcNow;
        await Task.Delay(20);

        ChangelogContent.SeededRelease[] grown =
        [
            new("1.0", new DateTime(2026, 8, 25), "Der Anfang",
            [
                new("1.0-a", ChangelogKind.Neu, "Erste Zeile.", "Fahndung"),
                new("1.0-b", ChangelogKind.Behoben, "Zweite Zeile.", null),
                new("1.0-c", ChangelogKind.Verbessert, "Dritte Zeile.", "Akten"),
            ]),
        ];
        await SeedAsync(ctx, grown, 1, "1.0.200");

        var flash = await service.GetNewsSinceAsync(afterFirstSeeding);

        Assert.Equal(1, flash.Count);
        Assert.Equal("1.0", flash.NewestVersion);
        var release = Assert.Single(flash.Releases);
        var line = Assert.Single(release.Entries);
        Assert.Equal("Dritte Zeile.", line.Title);
        Assert.Equal("Akten", line.Area);
    }

    [Fact]
    public async Task A_reworded_line_is_not_news()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, OneRelease(), 1, "1.0.100");

        var (service, _) = NewHost(ctx);
        var afterFirstSeeding = DateTime.UtcNow;
        await Task.Delay(20);

        await SeedAsync(ctx, OneRelease("Umformuliert."), 2, "1.0.200");

        Assert.Contains((await service.GetTimelineAsync()).Single().Entries, e => e.Title == "Umformuliert.");
        Assert.Equal(0, (await service.GetNewsSinceAsync(afterFirstSeeding)).Count);
    }

    /// <summary>Six lines once moved into release 2.2.00; the move must not announce them a second time.</summary>
    [Fact]
    public async Task A_line_moved_into_another_release_is_not_news()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, OneRelease(), 1, "1.0.100");

        var (service, _) = NewHost(ctx);
        var afterFirstSeeding = DateTime.UtcNow;
        await Task.Delay(20);

        ChangelogContent.SeededRelease[] moved =
        [
            new("1.0", new DateTime(2026, 8, 25), "Der Anfang",
                [new("1.0-a", ChangelogKind.Neu, "Erste Zeile.", "Fahndung")]),
            new("1.1", new DateTime(2026, 9, 10), "Danach",
                [new("1.1-00-b", ChangelogKind.Behoben, "Zweite Zeile.", null, "1.0-b")]),
        ];
        await SeedAsync(ctx, moved, 2, "1.0.200");

        await using (var check = ctx.NewContext())
        {
            var row = await check.Aenderungseintraege.SingleAsync(e => e.SeedKey == "1.1-00-b");
            Assert.Equal("1.1", (await check.Aenderungsfassungen.SingleAsync(r => r.Id == row.ReleaseId)).Version);
        }
        Assert.Equal(0, (await service.GetNewsSinceAsync(afterFirstSeeding)).Count);
    }

    [Fact]
    public async Task News_sits_under_its_releases_newest_first_in_authored_order()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, OneRelease(), 1, "1.0.100");

        var (service, _) = NewHost(ctx);
        var afterFirstSeeding = DateTime.UtcNow;
        await Task.Delay(20);

        ChangelogContent.SeededRelease[] more =
        [
            new("1.0", new DateTime(2026, 8, 25), "Der Anfang",
            [
                new("1.0-a", ChangelogKind.Neu, "Erste Zeile.", "Fahndung"),
                new("1.0-b", ChangelogKind.Behoben, "Zweite Zeile.", null),
                new("1.0-c", ChangelogKind.Verbessert, "Nachgereicht.", null),
            ]),
            new("1.1", new DateTime(2026, 9, 10), "Danach",
            [
                new("1.1-a", ChangelogKind.Neu, "Zuerst gelesen.", null),
                new("1.1-b", ChangelogKind.Behoben, "Danach gelesen.", null),
            ]),
        ];
        await SeedAsync(ctx, more, 1, "1.0.200");

        var flash = await service.GetNewsSinceAsync(afterFirstSeeding);

        Assert.Equal(3, flash.Count);
        Assert.Equal("1.1", flash.NewestVersion);
        Assert.Equal(["1.1", "1.0"], flash.Releases.Select(r => r.Version));
        Assert.Equal(["Zuerst gelesen.", "Danach gelesen."], flash.Releases[0].Entries.Select(e => e.Title));
        Assert.Equal(["Nachgereicht."], flash.Releases[1].Entries.Select(e => e.Title));
        Assert.Equal("Danach", flash.Releases[0].Title);
    }

    /// <summary>Somebody who skipped two updates reads the later one first; the cap then cuts the older.</summary>
    [Fact]
    public async Task Within_a_release_the_latest_update_comes_first()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, OneRelease(), 1, "1.0.100");

        var (service, _) = NewHost(ctx);
        var afterFirstSeeding = DateTime.UtcNow;
        await Task.Delay(20);

        ChangelogContent.SeededEntry[] earlier =
        [
            new("1.0-a", ChangelogKind.Neu, "Erste Zeile.", "Fahndung"),
            new("1.0-b", ChangelogKind.Behoben, "Zweite Zeile.", null),
            new("1.0-c", ChangelogKind.Neu, "Früheres Update, erste.", null),
            new("1.0-d", ChangelogKind.Neu, "Früheres Update, zweite.", null),
        ];
        await SeedAsync(ctx, [new("1.0", new DateTime(2026, 8, 25), "Der Anfang", earlier)], 1, "1.0.200");
        await Task.Delay(20);
        await SeedAsync(ctx,
            [new("1.0", new DateTime(2026, 8, 25), "Der Anfang",
                [.. earlier, new("1.0-e", ChangelogKind.Verbessert, "Späteres Update.", null)])],
            1, "1.0.300");

        var flash = await service.GetNewsSinceAsync(afterFirstSeeding);

        Assert.Equal(
            ["Späteres Update.", "Früheres Update, erste.", "Früheres Update, zweite."],
            flash.Releases.Single().Entries.Select(e => e.Title));
        Assert.Equal("Späteres Update.", flash.Preview(1).Single().Entries.Single().Title);
    }

    /// <summary>A hidden release is off the page, so its lines must not be announced either.</summary>
    [Fact]
    public async Task A_hidden_release_announces_nothing()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, OneRelease(), 1, "1.0.100");

        var (service, _) = NewHost(ctx);
        var afterFirstSeeding = DateTime.UtcNow;
        await Task.Delay(20);

        ChangelogContent.SeededRelease[] two =
        [
            .. OneRelease(),
            new("1.1", new DateTime(2026, 9, 10), "Noch nicht",
                [new("1.1-a", ChangelogKind.Neu, "Kommt später.", null)]),
        ];
        await SeedAsync(ctx, two, 1, "1.0.200");
        await using (var db = ctx.NewContext())
        {
            var release = await db.Aenderungsfassungen.SingleAsync(r => r.Version == "1.1");
            release.IsVisible = false;
            await db.SaveChangesAsync();
        }

        var flash = await service.GetNewsSinceAsync(afterFirstSeeding);

        Assert.Equal(0, flash.Count);
        Assert.Empty(flash.Releases);
        Assert.Null(flash.NewestVersion);
    }

    // --- permissions ------------------------------------------------------

    [Fact]
    public async Task An_agent_may_read_but_not_write()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, OneRelease(), 1, "1.0.100");

        var (service, _) = NewHost(ctx);
        Assert.Single(await service.GetTimelineAsync());

        var release = (await service.GetReleasesAsync()).Single();
        var input = new ChangelogEntryInput(release.Id, ChangelogKind.Neu, "Darf nicht.", null, 0, true);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.CreateEntryAsync(input, Agent()));
    }

    [Fact]
    public async Task The_read_only_supervision_is_refused_before_anything_is_written()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, OneRelease(), 1, "1.0.100");

        var (service, _) = NewHost(ctx);
        var release = (await service.GetReleasesAsync()).Single();
        var input = new ChangelogEntryInput(release.Id, ChangelogKind.Neu, "Darf nicht.", null, 0, true);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.CreateEntryAsync(input, OnlyReader()));

        await using var check = ctx.NewContext();
        Assert.Equal(2, await check.Aenderungseintraege.CountAsync());
    }

    [Fact]
    public async Task Saving_a_shipped_line_marks_it_as_the_author_s()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, OneRelease(), 1, "1.0.100");

        var (service, _) = NewHost(ctx);
        var release = (await service.GetReleasesAsync()).Single();
        string id;
        await using (var db = ctx.NewContext())
        {
            id = (await db.Aenderungseintraege.SingleAsync(e => e.SeedKey == "1.0-a")).Id;
        }

        await service.RefreshEntryAsync(id,
            new ChangelogEntryInput(release.Id, ChangelogKind.Neu, "Umformuliert.", null, 0, true), Leader());

        await using var check = ctx.NewContext();
        Assert.True((await check.Aenderungseintraege.SingleAsync(e => e.Id == id)).IsCustomised);
    }

    [Fact]
    public async Task A_hand_written_line_carries_no_seed_key()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, OneRelease(), 1, "1.0.100");

        var (service, _) = NewHost(ctx);
        var release = (await service.GetReleasesAsync()).Single();
        var created = await service.CreateEntryAsync(
            new ChangelogEntryInput(release.Id, ChangelogKind.Neu, "Von Hand.", null, 99, true), Leader());

        await using var check = ctx.NewContext();
        var row = await check.Aenderungseintraege.SingleAsync(e => e.Id == created.Id);
        Assert.Null(row.SeedKey);
        Assert.True(row.IsCustomised);
    }

    [Theory]
    [InlineData("2.1")]
    [InlineData("2.1.0")]
    [InlineData("2.1.100")]
    public async Task A_release_version_requires_two_update_digits(string version)
    {
        using var ctx = new SqliteTestContext();
        var (service, _) = NewHost(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateReleaseAsync(
            new ChangelogReleaseInput(version, DateTime.Today, null, 0, true), Leader()));
    }

    // --- the shipped content ---------------------------------------------

    [Fact]
    public void Every_shipped_line_has_a_unique_key()
    {
        var keys = ChangelogContent.Releases.SelectMany(r => r.Entries).Select(e => e.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_shipped_legacy_key_names_a_line_that_is_gone()
    {
        var keys = ChangelogContent.Releases.SelectMany(r => r.Entries).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var legacy = ChangelogContent.Releases.SelectMany(r => r.Entries)
            .Where(e => e.LegacyKey is not null).Select(e => e.LegacyKey!).ToList();

        // a legacy key that is still a current key somewhere would make the seeder rename a live line onto
        // another live line - the unique index then rejects the whole seed pass on the first start
        Assert.DoesNotContain(legacy, keys.Contains);
        Assert.Equal(legacy.Count, legacy.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Every_shipped_legacy_key_moves_its_row_without_a_second_one()
    {
        using var ctx = new SqliteTestContext();
        // the layout before the moves: each moved line back under its old key and release
        var shipped = ChangelogContent.Releases;
        var before = shipped
            .Select(r => r with
            {
                LegacyVersion = null,
                Entries = r.Entries.Where(e => e.LegacyKey is null)
                    .Concat(shipped.SelectMany(x => x.Entries)
                        .Where(e => e.LegacyKey is { } old && ReleaseOf(old) == r.Version)
                        .Select(e => e with { Key = e.LegacyKey!, LegacyKey = null }))
                    .ToArray(),
            })
            .Where(r => r.Entries.Length > 0)
            .ToList();
        await SeedAsync(ctx, before, ChangelogContent.Revision - 1, "1.0.1");
        Dictionary<string, string> oldIds;
        await using (var db = ctx.NewContext())
        {
            oldIds = await db.Aenderungseintraege.Where(e => e.SeedKey != null).ToDictionaryAsync(e => e.SeedKey!, e => e.Id);
        }

        await SeedAsync(ctx, shipped, ChangelogContent.Revision, "1.0.2");

        await using var after = ctx.NewContext();
        var rows = await after.Aenderungseintraege.Where(e => e.SeedKey != null).ToListAsync();
        Assert.Equal(oldIds.Count, rows.Count);
        foreach (var moved in shipped.SelectMany(r => r.Entries).Where(e => e.LegacyKey is not null))
        {
            Assert.Equal(oldIds[moved.LegacyKey!], rows.Single(e => e.SeedKey == moved.Key).Id);
        }

        static string ReleaseOf(string key) => key[..key.LastIndexOf('.')] + ".00";
    }

    [Fact]
    public void Every_shipped_release_has_a_unique_version_and_at_least_one_line()
    {
        var versions = ChangelogContent.Releases.Select(r => r.Version).ToList();

        Assert.Equal(versions.Count, versions.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ChangelogContent.Releases, r => Assert.NotEmpty(r.Entries));
    }

    [Fact]
    public void Every_shipped_version_and_key_uses_sequential_two_digit_updates()
    {
        Assert.All(ChangelogContent.Releases, release =>
        {
            Assert.Matches(@"^\d+\.\d+\.\d{2}$", release.Version);
            Assert.InRange(release.Entries.Length, 1, 100);
            var family = release.Version[..release.Version.LastIndexOf('.')];
            for (var i = 0; i < release.Entries.Length; i++)
            {
                Assert.StartsWith($"{family}.{i:00}-", release.Entries[i].Key);
            }
        });
    }

    /// <summary>The real shipped list against the real schema — the closest a test gets to a first start.</summary>
    /// <remarks>
    /// The static checks above cannot catch a unique index rejecting a key or a release the seeder cannot resolve;
    /// only writing the whole thing does.
    /// </remarks>
    [Fact]
    public async Task The_shipped_changelog_seeds_and_renders()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, ChangelogContent.Releases, ChangelogContent.Revision, "1.0.999");

        var (service, _) = NewHost(ctx);
        var timeline = await service.GetTimelineAsync();

        Assert.Equal(ChangelogContent.Releases.Count, timeline.Count);
        Assert.Equal(
            ChangelogContent.Releases.Sum(r => r.Entries.Length),
            timeline.Sum(r => r.Entries.Count));

        // newest first, and only the newest release carries the build of the deploy that brought it
        Assert.True(timeline[0].Date >= timeline[^1].Date);
        Assert.Equal("1.0.999", timeline[0].BuildNumber);
        Assert.All(timeline.Skip(1), r => Assert.Null(r.BuildNumber));
    }

    /// <summary>The wording rule, as far as a test can hold it: no jargon that only the author would use.</summary>
    /// <summary>Every shipped line fits the column the seeder writes it into.</summary>
    /// <remarks>
    /// SQLite ignores the declared length, MySQL rejects the row - and the seeder saves all new lines in one batch,
    /// so a single long line kept every line of a release off the page without a red test anywhere.
    /// </remarks>
    [Fact]
    public void Every_shipped_line_fits_its_column()
    {
        using var ctx = new SqliteTestContext();
        using var db = ctx.NewContext();
        var entry = db.Model.FindEntityType(typeof(ChangelogEntry))!;
        var release = db.Model.FindEntityType(typeof(ChangelogRelease))!;
        int Max(Microsoft.EntityFrameworkCore.Metadata.IEntityType type, string property)
            => type.FindProperty(property)!.GetMaxLength()
               ?? throw new InvalidOperationException($"{type.ClrType.Name}.{property} hat keine Länge.");

        var offenders = ChangelogContent.Releases
            .SelectMany(r => r.Entries.Select(e => (r, e)))
            .Where(x => x.e.Title.Length > Max(entry, nameof(ChangelogEntry.Title))
                || (x.e.Area?.Length ?? 0) > Max(entry, nameof(ChangelogEntry.Area))
                || x.e.Key.Length > Max(entry, nameof(ChangelogEntry.SeedKey))
                || x.r.Title.Length > Max(release, nameof(ChangelogRelease.Title))
                || x.r.Version.Length > Max(release, nameof(ChangelogRelease.Version)))
            .Select(x => $"{x.e.Key} ({x.e.Title.Length} Zeichen)")
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void No_shipped_line_talks_about_the_technology()
    {
        // Substring, not word, on purpose: German compounds are where the jargon hides, and
        // "Datenbanktabelle" has to fail as surely as "Datenbank".
        string[] forbidden =
        [
            "Migration", "Refactor", "Interceptor", "Service", "Endpoint", "Repository",
            "Commit", "Branch", "Datenbank", "Tabelle", "Query", "Cache",
        ];

        var offenders = ChangelogContent.Releases
            .SelectMany(r => r.Entries)
            .Where(e => forbidden.Any(f => e.Title.Contains(f, StringComparison.OrdinalIgnoreCase))
                // "API" is the one entry short enough to hide inside an ordinary German word - it sits in the
                // middle of "Kapitel" - so it is the only one matched as a word rather than as a substring
                || Regex.IsMatch(e.Title, @"\bAPI\b", RegexOptions.IgnoreCase))
            .Select(e => e.Key)
            .ToList();

        Assert.Empty(offenders);
    }

    /// <summary>The window names a version the reader can actually find on the page.</summary>
    /// <remarks>
    /// A release whose lines are all withdrawn is not rendered, so announcing it sent the reader looking for a
    /// version that is not on /neuerungen at all. The count is unaffected - it only ever counted visible lines.
    /// </remarks>
    [Fact]
    public async Task The_flash_names_the_newest_release_that_still_carries_a_line()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx, OneRelease(), 1, "1.0.100");

        var (service, _) = NewHost(ctx);
        var afterFirstSeeding = DateTime.UtcNow;
        await Task.Delay(20);

        ChangelogContent.SeededRelease[] three =
        [
            .. OneRelease(),
            new("1.1", new DateTime(2026, 9, 10), "Danach",
                [new("1.1-a", ChangelogKind.Verbessert, "Etwas wurde besser.", null)]),
            new("1.2", new DateTime(2026, 9, 11), "Zurückgezogen",
                [new("1.2-a", ChangelogKind.Neu, "Wieder entfernt.", null)]),
        ];
        await SeedAsync(ctx, three, 1, "1.0.200");

        await using (var db = ctx.NewContext())
        {
            var withdrawn = await db.Aenderungseintraege.SingleAsync(e => e.SeedKey == "1.2-a");
            withdrawn.IsVisible = false;
            await db.SaveChangesAsync();
        }

        var flash = await service.GetNewsSinceAsync(afterFirstSeeding);

        Assert.Equal(1, flash.Count);
        Assert.Equal("1.1", flash.NewestVersion);
    }
}
