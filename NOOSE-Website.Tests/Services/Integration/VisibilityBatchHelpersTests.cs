using NOOSE_Website.Authorization;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Meetings;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The batched twins of the per-record visibility gates. They exist because a result list spans many
/// parents, and the per-record helpers are one round trip each.</summary>
public class VisibilityBatchHelpersTests
{
    private static readonly DateTime Now = new(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);

    // ---- MeetingVisibility.OpenIdsAsync ----

    private static Meeting Meeting(string id, DateTime start, DateTime? end = null)
        => new() { Id = id, Title = "Lagebesprechung " + id, CaseNumber = "FIB-B-2026-" + id, Start = start, End = end };

    [Fact]
    public async Task OpenIds_returns_only_meetings_past_the_grace_window_for_a_plain_agent()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.Meetings.Add(Meeting("old", Now.AddHours(-5)));      // start+2h long passed
            db.Meetings.Add(Meeting("fresh", Now.AddMinutes(-30))); // still inside the window
            db.Meetings.Add(Meeting("future", Now.AddHours(3)));
            await db.SaveChangesAsync();
        }
        var scope = new ViewerScope(false, false, "me", null, MayAgenda: false);

        await using var read = ctx.NewContext();
        var open = await MeetingVisibility.OpenIdsAsync(read, new[] { "old", "fresh", "future" }, scope, Now);

        Assert.Equal(new[] { "old" }, open.OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task OpenIds_gives_the_agenda_rank_every_meeting_regardless_of_time()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.Meetings.Add(Meeting("future", Now.AddHours(3)));
            await db.SaveChangesAsync();
        }
        var scope = new ViewerScope(true, true, "lead", null, MayAgenda: true);

        await using var read = ctx.NewContext();
        var open = await MeetingVisibility.OpenIdsAsync(read, new[] { "future" }, scope, Now);

        Assert.Equal(new[] { "future" }, open.ToArray());
    }

    [Fact]
    public async Task OpenIds_never_admits_a_partner()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.Meetings.Add(Meeting("old", Now.AddHours(-5)));
            await db.SaveChangesAsync();
        }
        var scope = new ViewerScope(false, false, "p1", PartnerAgency.DoJ, MayAgenda: false);

        await using var read = ctx.NewContext();
        var open = await MeetingVisibility.OpenIdsAsync(read, new[] { "old" }, scope, Now);

        Assert.Empty(open);
    }

    [Fact]
    public async Task OpenIds_measures_from_end_when_the_meeting_has_one()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            // started 5h ago, but ended only 1h ago -> end+2h not yet reached
            db.Meetings.Add(Meeting("long", Now.AddHours(-5), Now.AddHours(-1)));
            await db.SaveChangesAsync();
        }
        var scope = new ViewerScope(false, false, "me", null, MayAgenda: false);

        await using var read = ctx.NewContext();
        var open = await MeetingVisibility.OpenIdsAsync(read, new[] { "long" }, scope, Now);

        Assert.Empty(open);
    }

    [Fact]
    public async Task OpenIds_of_an_unknown_id_is_absent_rather_than_open()
    {
        using var ctx = new SqliteTestContext();
        var scope = new ViewerScope(true, true, "lead", null, MayAgenda: true);

        await using var read = ctx.NewContext();
        var open = await MeetingVisibility.OpenIdsAsync(read, new[] { "ghost" }, scope, Now);

        Assert.Empty(open);
    }

    // ---- PartnerVisibility.VisibleChildIdsAsync ----

    [Fact]
    public async Task VisibleChildIds_takes_every_child_of_a_parent_released_whole()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.PartnerShares.Add(new PartnerShare
            {
                EntityType = nameof(Person), EntityId = "p1", Agency = PartnerAgency.DoJ, IncludesChildren = true,
            });
            await db.SaveChangesAsync();
        }

        await using var read = ctx.NewContext();
        var visible = await PartnerVisibility.VisibleChildIdsAsync(read, nameof(Comment),
            [(nameof(Person), "p1", "c1"), (nameof(Person), "p1", "c2")], PartnerAgency.DoJ, "partner-1");

        Assert.Equal(new[] { "c1", "c2" }, visible.OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task VisibleChildIds_takes_only_individually_released_children_of_a_shell_release()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            // parent released, but NOT with its children
            db.PartnerShares.Add(new PartnerShare
            {
                EntityType = nameof(Person), EntityId = "p1", Agency = PartnerAgency.DoJ, IncludesChildren = false,
            });
            db.PartnerShares.Add(new PartnerShare
            {
                EntityType = nameof(Comment), EntityId = "c2", Agency = PartnerAgency.DoJ,
            });
            await db.SaveChangesAsync();
        }

        await using var read = ctx.NewContext();
        var visible = await PartnerVisibility.VisibleChildIdsAsync(read, nameof(Comment),
            [(nameof(Person), "p1", "c1"), (nameof(Person), "p1", "c2")], PartnerAgency.DoJ, "partner-1");

        Assert.Equal(new[] { "c2" }, visible.ToArray());
    }

    [Fact]
    public async Task VisibleChildIds_mixes_whole_and_shell_parents_in_one_pass()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.PartnerShares.Add(new PartnerShare
            {
                EntityType = nameof(Person), EntityId = "p1", Agency = PartnerAgency.DoJ, IncludesChildren = true,
            });
            db.PartnerShares.Add(new PartnerShare
            {
                EntityType = nameof(Faction), EntityId = "f1", Agency = PartnerAgency.DoJ, IncludesChildren = false,
            });
            await db.SaveChangesAsync();
        }

        await using var read = ctx.NewContext();
        var visible = await PartnerVisibility.VisibleChildIdsAsync(read, nameof(Comment),
            [(nameof(Person), "p1", "c1"), (nameof(Faction), "f1", "c2")], PartnerAgency.DoJ, "partner-1");

        Assert.Equal(new[] { "c1" }, visible.ToArray());
    }

    [Fact]
    public async Task VisibleChildIds_ignores_a_release_to_another_agency()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.PartnerShares.Add(new PartnerShare
            {
                EntityType = nameof(Person), EntityId = "p1", Agency = PartnerAgency.LSPD, IncludesChildren = true,
            });
            await db.SaveChangesAsync();
        }

        await using var read = ctx.NewContext();
        var visible = await PartnerVisibility.VisibleChildIdsAsync(read, nameof(Comment),
            [(nameof(Person), "p1", "c1")], PartnerAgency.DoJ, "partner-1");

        Assert.Empty(visible);
    }

    [Fact]
    public async Task VisibleChildIds_ignores_a_release_pinned_to_another_partner_account()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.PartnerShares.Add(new PartnerShare
            {
                EntityType = nameof(Person), EntityId = "p1", Agency = PartnerAgency.DoJ,
                PartnerAgentId = "someone-else", IncludesChildren = true,
            });
            await db.SaveChangesAsync();
        }

        await using var read = ctx.NewContext();
        var visible = await PartnerVisibility.VisibleChildIdsAsync(read, nameof(Comment),
            [(nameof(Person), "p1", "c1")], PartnerAgency.DoJ, "partner-1");

        Assert.Empty(visible);
    }

    [Fact]
    public async Task VisibleChildIds_of_an_unshared_parent_is_empty()
    {
        using var ctx = new SqliteTestContext();

        await using var read = ctx.NewContext();
        var visible = await PartnerVisibility.VisibleChildIdsAsync(read, nameof(Comment),
            [(nameof(Person), "p1", "c1")], PartnerAgency.DoJ, "partner-1");

        Assert.Empty(visible);
    }

    // ---- LibraryVisibility.OnlyVisible ----

    private static LibraryFile File(string id, bool classified = false, bool tru = false, bool hrb = false)
        => new()
        {
            Id = id, Title = "Datei " + id, OriginalName = id + ".pdf", FileNameSaved = id,
            ContentType = "application/pdf", SizeBytes = 1,
            IsClassified = classified, IsTRUClassified = tru, IsHRBClassified = hrb,
        };

    [Fact]
    public async Task Library_plain_agent_sees_only_unclassified_files()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.LibraryFiles.AddRange(File("open"), File("lead", classified: true), File("tru", tru: true), File("hrb", hrb: true));
            await db.SaveChangesAsync();
        }
        var scope = new DocumentViewerScope(false, false, false, false, false, "me");

        await using var read = ctx.NewContext();
        var ids = read.LibraryFiles.OnlyVisible(scope).Select(f => f.Id).OrderBy(x => x).ToList();

        Assert.Equal(new[] { "open" }, ids.ToArray());
    }

    [Fact]
    public async Task Library_tru_agent_adds_the_tru_tier_and_nothing_else()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.LibraryFiles.AddRange(File("open"), File("lead", classified: true), File("tru", tru: true), File("hrb", hrb: true));
            await db.SaveChangesAsync();
        }
        var scope = new DocumentViewerScope(false, true, false, false, false, "me");

        await using var read = ctx.NewContext();
        var ids = read.LibraryFiles.OnlyVisible(scope).Select(f => f.Id).OrderBy(x => x).ToList();

        Assert.Equal(new[] { "open", "tru" }, ids.ToArray());
    }

    [Fact]
    public async Task Library_leadership_sees_every_tier()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.LibraryFiles.AddRange(File("open"), File("lead", classified: true), File("tru", tru: true), File("hrb", hrb: true));
            await db.SaveChangesAsync();
        }
        var scope = new DocumentViewerScope(true, false, false, true, false, "me");

        await using var read = ctx.NewContext();
        var ids = read.LibraryFiles.OnlyVisible(scope).Select(f => f.Id).OrderBy(x => x).ToList();

        Assert.Equal(new[] { "hrb", "lead", "open", "tru" }, ids.ToArray());
    }
}
