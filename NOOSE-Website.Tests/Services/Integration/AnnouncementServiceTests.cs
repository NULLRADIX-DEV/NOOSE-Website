using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Announcements;
using NOOSE_Website.Infrastructure.Announcements;
using NOOSE_Website.Models.Announcements;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="AnnouncementService"/> over in-memory SQLite.</summary>
public sealed class AnnouncementServiceTests
{
    private const string CaseNo = "NOOSE-N-2026-0001";
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static (AnnouncementService Svc, INotificationService Notifications,
        AcknowledgmentBroadcaster Broadcaster, List<string> Reported) Build(SqliteTestContext ctx)
    {
        var caseNo = Substitute.For<ICaseNumberService>();
        caseNo.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(CaseNo);
        var notifications = Substitute.For<INotificationService>();
        var broadcaster = new AcknowledgmentBroadcaster();
        var reported = new List<string>();
        broadcaster.Modified += id => reported.Add(id);
        var svc = new AnnouncementService(ctx.Factory, caseNo, notifications, broadcaster,
            NullLogger<AnnouncementService>.Instance);
        return (svc, notifications, broadcaster, reported);
    }

    // Rank >= SupervisorySpecialAgent(4) => IsLeadership().
    private static ClaimsPrincipal Leader(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).Build();

    // Junior agent: not leadership, not admin.
    private static ClaimsPrincipal Junior(string id = "me")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.JuniorAgent).Build();

    private static Announcement Ann(string title = "Titel", Action<Announcement>? configure = null)
    {
        var a = new Announcement
        {
            CaseNumber = "NOOSE-N-2026-" + System.Guid.NewGuid().ToString("N").Substring(0, 8),
            Title = title,
            Content = string.Empty,
            Audience = AnnouncementAudience.AllActive,
            CreatedAt = T0,
        };
        configure?.Invoke(a);
        return a;
    }

    private static AnnouncementAcknowledgment Ack(string announcementId, string agentId, DateTime? acknowledgedAt = null)
        => new()
        {
            AnnouncementId = announcementId,
            AgentId = agentId,
            AcknowledgedAt = acknowledgedAt,
            CreatedAt = T0,
        };

    // ---------- CreateAsync ----------

    [Fact]
    public async Task CreateAsync_SimpleAnnouncement_Persists()
    {
        using var ctx = new SqliteTestContext();
        var (svc, notifications, _, _) = Build(ctx);

        var input = new AnnouncementInput
        {
            Title = "  Wichtige Info  ",
            Content = "Hallo Team",
            Audience = AnnouncementAudience.AllActive,
        };

        var created = await svc.CreateAsync(input, Junior());

        Assert.Equal(CaseNo, created.CaseNumber);
        Assert.Equal("Wichtige Info", created.Title);
        Assert.Equal("Hallo Team", created.Content);
        Assert.False(created.AsBroadcast);
        Assert.False(created.AcknowledgmentRequired);

        using var db = ctx.NewContext();
        var stored = Assert.Single(db.Announcements.ToList());
        Assert.Equal("Wichtige Info", stored.Title);
        Assert.Equal("Hallo Team", stored.Content);
        Assert.Equal(AnnouncementAudience.AllActive, stored.Audience);
        // Simple note snapshots no recipients and pushes nothing.
        Assert.Empty(db.AnnouncementAcknowledgments.ToList());
        await notifications.DidNotReceive().NotifyManyAsync(
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<NotificationType>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_EmptyEditorContent_NormalizesToEmpty()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);

        // Quill emits <p><br></p> for an empty editor; it must collapse to "".
        var input = new AnnouncementInput { Title = "Nur Titel", Content = "<p><br></p>" };

        var created = await svc.CreateAsync(input, Junior());

        Assert.Equal(string.Empty, created.Content);
    }

    [Fact]
    public async Task CreateAsync_BroadcastFeature_NonLeadership_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);

        var input = new AnnouncementInput
        {
            Title = "Push",
            Audience = AnnouncementAudience.AllActive,
            AsBroadcast = true,
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateAsync(input, Junior()));

        using var db = ctx.NewContext();
        Assert.Empty(db.Announcements.ToList());
    }

    [Fact]
    public async Task CreateAsync_AcknowledgmentRequired_SnapshotsRecipients_ExcludesAuthor()
    {
        using var ctx = new SqliteTestContext();
        var (svc, notifications, _, _) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("lead", Rank.Director, AgentStatus.Active));
            db.Users.Add(Seed.Agent("a1", Rank.JuniorAgent, AgentStatus.Active));
            db.Users.Add(Seed.Agent("a2", Rank.SpecialAgent, AgentStatus.Active));
            db.SaveChanges();
        }

        var input = new AnnouncementInput
        {
            Title = "Bitte quittieren",
            Audience = AnnouncementAudience.AllActive,
            AcknowledgmentRequired = true,
        };

        var created = await svc.CreateAsync(input, Leader("lead"));

        using var check = ctx.NewContext();
        var acks = check.AnnouncementAcknowledgments.Where(q => q.AnnouncementId == created.Id).ToList();
        // Author excluded; a1 and a2 snapshotted as open.
        Assert.Equal(2, acks.Count);
        Assert.Equal(new HashSet<string> { "a1", "a2" }, acks.Select(x => x.AgentId).ToHashSet());
        Assert.All(acks, x => Assert.Null(x.AcknowledgedAt));
        // Acknowledgment alone does not push.
        await notifications.DidNotReceive().NotifyManyAsync(
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<NotificationType>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_AsBroadcast_NotifiesRecipients()
    {
        using var ctx = new SqliteTestContext();
        var (svc, notifications, _, _) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", Rank.JuniorAgent, AgentStatus.Active));
            db.Users.Add(Seed.Agent("a2", Rank.SpecialAgent, AgentStatus.Active));
            db.SaveChanges();
        }

        var input = new AnnouncementInput
        {
            Title = "Broadcast",
            Audience = AnnouncementAudience.AllActive,
            AsBroadcast = true,
        };

        var created = await svc.CreateAsync(input, Leader("lead"));

        // No acknowledgment snapshot when only pushing.
        using (var db = ctx.NewContext())
        {
            Assert.Empty(db.AnnouncementAcknowledgments.ToList());
        }
        await notifications.Received(1).NotifyManyAsync(
            Arg.Is<IReadOnlyCollection<string>>(c => c.Contains("a1") && c.Contains("a2")),
            NotificationType.Announcement,
            Arg.Any<string>(),
            $"/brett/{created.Id}",
            "lead",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_TaskforceAudience_MissingTarget_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);

        var input = new AnnouncementInput
        {
            Title = "TF",
            Audience = AnnouncementAudience.Taskforce,
            TargetId = null,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateAsync(input, Leader()));
    }

    [Fact]
    public async Task CreateAsync_FromRankAudience_MissingMinRank_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);

        var input = new AnnouncementInput
        {
            Title = "Rank",
            Audience = AnnouncementAudience.FromRank,
            MinRank = null,
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateAsync(input, Leader()));
    }

    // ---------- RefreshAsync ----------

    [Fact]
    public async Task RefreshAsync_Creator_UpdatesEditableFields()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);

        var a = Ann("Old", x => { x.CreatedById = "me"; x.Content = "old"; x.Important = false; });
        using (var db = ctx.NewContext())
        {
            db.Announcements.Add(a);
            db.SaveChanges();
        }

        var input = new AnnouncementInput { Title = "  New  ", Content = "New body", Important = true };
        await svc.RefreshAsync(a.Id, input, Junior("me"));

        using var check = ctx.NewContext();
        var stored = check.Announcements.Single(x => x.Id == a.Id);
        Assert.Equal("New", stored.Title);
        Assert.Equal("New body", stored.Content);
        Assert.True(stored.Important);
    }

    [Fact]
    public async Task RefreshAsync_NonCreatorNonLeadership_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);

        var a = Ann("Old", x => x.CreatedById = "other");
        using (var db = ctx.NewContext())
        {
            db.Announcements.Add(a);
            db.SaveChanges();
        }

        var input = new AnnouncementInput { Title = "Hacked" };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RefreshAsync(a.Id, input, Junior("me")));

        using var check = ctx.NewContext();
        Assert.Equal("Old", check.Announcements.Single(x => x.Id == a.Id).Title);
    }

    [Fact]
    public async Task RefreshAsync_UnknownId_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);

        var input = new AnnouncementInput { Title = "X" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RefreshAsync("missing", input, Leader()));
    }

    // ---------- DeleteAsync ----------

    [Fact]
    public async Task DeleteAsync_Creator_RemovesRow()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);

        var a = Ann("Doomed", x => x.CreatedById = "me");
        using (var db = ctx.NewContext())
        {
            db.Announcements.Add(a);
            db.SaveChanges();
        }

        await svc.DeleteAsync(a.Id, Junior("me"));

        // No soft-delete interceptor in tests => hard delete; row gone entirely.
        using var check = ctx.NewContext();
        Assert.False(check.Announcements.IgnoreQueryFilters().Any(x => x.Id == a.Id));
    }

    [Fact]
    public async Task DeleteAsync_NonCreatorNonLeadership_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);

        var a = Ann("Keep", x => x.CreatedById = "other");
        using (var db = ctx.NewContext())
        {
            db.Announcements.Add(a);
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.DeleteAsync(a.Id, Junior("me")));

        using var check = ctx.NewContext();
        Assert.True(check.Announcements.Any(x => x.Id == a.Id));
    }

    // ---------- RestoreAsync ----------

    [Fact]
    public async Task RestoreAsync_Leadership_ClearsSoftDelete()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);

        var a = Ann("Trashed", x =>
        {
            x.IsDeleted = true;
            x.DeletedAt = T0;
            x.DeletedById = "someone";
        });
        using (var db = ctx.NewContext())
        {
            db.Announcements.Add(a);
            db.SaveChanges();
        }

        await svc.RestoreAsync(a.Id, Leader());

        using var check = ctx.NewContext();
        // Now visible through the normal (soft-delete-filtered) set.
        var stored = check.Announcements.Single(x => x.Id == a.Id);
        Assert.False(stored.IsDeleted);
        Assert.Null(stored.DeletedAt);
        Assert.Null(stored.DeletedById);
    }

    [Fact]
    public async Task RestoreAsync_NonLeadership_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);

        // Guard runs before the load, so any id fails for a non-leader.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RestoreAsync("any", Junior("me")));
    }

    [Fact]
    public async Task RestoreAsync_UnknownId_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RestoreAsync("missing", Leader()));
    }

    // ---------- AcknowledgeAsync ----------

    [Fact]
    public async Task AcknowledgeAsync_OpenRow_SetsTimestamp_AndReports()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, reported) = Build(ctx);

        var a = Ann("Ack me", x => x.AcknowledgmentRequired = true);
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("me", Rank.JuniorAgent, AgentStatus.Active));
            db.Announcements.Add(a);
            db.AnnouncementAcknowledgments.Add(Ack(a.Id, "me"));
            db.SaveChanges();
        }

        await svc.AcknowledgeAsync(a.Id, ClaimsPrincipalBuilder.Agent("me").Build());

        using var check = ctx.NewContext();
        var row = check.AnnouncementAcknowledgments.Single(q => q.AnnouncementId == a.Id && q.AgentId == "me");
        Assert.NotNull(row.AcknowledgedAt);
        Assert.Equal(new[] { "me" }, reported);
    }

    [Fact]
    public async Task AcknowledgeAsync_AlreadyAcknowledged_Idempotent()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, reported) = Build(ctx);

        var when = T0.AddDays(3);
        var a = Ann("Ack me", x => x.AcknowledgmentRequired = true);
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("me", Rank.JuniorAgent, AgentStatus.Active));
            db.Announcements.Add(a);
            db.AnnouncementAcknowledgments.Add(Ack(a.Id, "me", when));
            db.SaveChanges();
        }

        await svc.AcknowledgeAsync(a.Id, ClaimsPrincipalBuilder.Agent("me").Build());

        using var check = ctx.NewContext();
        var row = check.AnnouncementAcknowledgments.Single(q => q.AnnouncementId == a.Id && q.AgentId == "me");
        Assert.Equal(when, row.AcknowledgedAt);
        // Early return skips the broadcast.
        Assert.Empty(reported);
    }

    [Fact]
    public async Task AcknowledgeAsync_Anonymous_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.AcknowledgeAsync("some-id", ClaimsPrincipalBuilder.Anonymous()));
    }

    [Fact]
    public async Task AcknowledgeAsync_NoRowForCaller_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.AcknowledgeAsync("some-id", ClaimsPrincipalBuilder.Agent("me").Build()));
    }

    // ---------- GetOpenAcknowledgmentsCountAsync ----------

    [Fact]
    public async Task GetOpenAcknowledgmentsCountAsync_CountsOpenRequiredForCaller()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);

        var required = Ann("Required", x => x.AcknowledgmentRequired = true);
        var acked = Ann("Acked", x => x.AcknowledgmentRequired = true);
        var notRequired = Ann("NotRequired", x => x.AcknowledgmentRequired = false);
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("me", Rank.JuniorAgent, AgentStatus.Active));
            db.Announcements.AddRange(required, acked, notRequired);
            db.AnnouncementAcknowledgments.Add(Ack(required.Id, "me"));               // open + required => counts
            db.AnnouncementAcknowledgments.Add(Ack(acked.Id, "me", T0.AddDays(1)));   // already acknowledged => no
            db.AnnouncementAcknowledgments.Add(Ack(notRequired.Id, "me"));            // announcement not required => no
            db.SaveChanges();
        }

        var count = await svc.GetOpenAcknowledgmentsCountAsync(ClaimsPrincipalBuilder.Agent("me").Build());
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task GetOpenAcknowledgmentsCountAsync_Anonymous_ReturnsZero()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);

        Assert.Equal(0, await svc.GetOpenAcknowledgmentsCountAsync(ClaimsPrincipalBuilder.Anonymous()));
    }

    // ---------- GetBoardAsync ----------

    [Fact]
    public async Task GetBoardAsync_NonLeadership_SeesAllActiveAndOwn_HidesOthers()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);

        var open = Ann("Open", x => x.CreatedById = "other");
        var tru = Ann("Tru", x => { x.Audience = AnnouncementAudience.TruUnit; x.CreatedById = "other"; });
        var mine = Ann("Mine", x => { x.Audience = AnnouncementAudience.TruUnit; x.CreatedById = "me"; });
        using (var db = ctx.NewContext())
        {
            db.Announcements.AddRange(open, tru, mine);
            db.SaveChanges();
        }

        // Junior "me", not TRU: sees the open board post and their own, not the foreign TRU one.
        var rows = await svc.GetBoardAsync(Junior("me"));

        Assert.Equal(2, rows.Count);
        var ids = rows.Select(r => r.Id).ToHashSet();
        Assert.Contains(open.Id, ids);
        Assert.Contains(mine.Id, ids);
        Assert.DoesNotContain(tru.Id, ids);
    }

    [Fact]
    public async Task GetBoardAsync_Leadership_SeesAll_ImportantThenNewest()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);

        var oldPlain = Ann("A-old", x => { x.Important = false; x.CreatedAt = T0; });
        var important = Ann("B-important", x => { x.Audience = AnnouncementAudience.TruUnit; x.Important = true; x.CreatedAt = T0.AddDays(-1); });
        var newPlain = Ann("C-new", x => { x.Important = false; x.CreatedAt = T0.AddDays(1); });
        using (var db = ctx.NewContext())
        {
            db.Announcements.AddRange(oldPlain, important, newPlain);
            db.SaveChanges();
        }

        var rows = await svc.GetBoardAsync(Leader());

        // Important first, then remaining by CreatedAt descending.
        Assert.Equal(new[] { "B-important", "C-new", "A-old" }, rows.Select(r => r.Title).ToArray());
        Assert.All(rows, r => Assert.True(r.MayManage));
    }

    [Fact]
    public async Task GetBoardAsync_ComputesAcknowledgmentCounts()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);

        var a = Ann("Ack board", x => { x.AcknowledgmentRequired = true; x.CreatedById = "lead"; });
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("me", Rank.JuniorAgent, AgentStatus.Active));
            db.Users.Add(Seed.Agent("a2", Rank.SpecialAgent, AgentStatus.Active));
            db.Announcements.Add(a);
            db.AnnouncementAcknowledgments.Add(Ack(a.Id, "me"));                  // open
            db.AnnouncementAcknowledgments.Add(Ack(a.Id, "a2", T0.AddDays(1)));   // acknowledged
            db.SaveChanges();
        }

        var rows = await svc.GetBoardAsync(Junior("me"));

        var row = Assert.Single(rows);
        Assert.Equal(1, row.AcknowledgedCount);
        Assert.Equal(2, row.TotalCount);
        Assert.True(row.MustAcknowledge);
        Assert.False(row.AlreadyAcknowledged);
        Assert.False(row.MayManage);
    }

    // ---------- GetDetailAsync ----------

    [Fact]
    public async Task GetDetailAsync_Leadership_ReturnsViewWithAcknowledgmentList()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);

        var a = Ann("Detail", x => { x.AcknowledgmentRequired = true; x.CreatedById = "lead"; });
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("me", Rank.JuniorAgent, AgentStatus.Active));
            db.Users.Add(Seed.Agent("a2", Rank.SpecialAgent, AgentStatus.Active));
            db.Announcements.Add(a);
            db.AnnouncementAcknowledgments.Add(Ack(a.Id, "me"));                  // open
            db.AnnouncementAcknowledgments.Add(Ack(a.Id, "a2", T0.AddDays(1)));   // acknowledged
            db.SaveChanges();
        }

        var view = await svc.GetDetailAsync(a.Id, Leader("lead"));

        Assert.NotNull(view);
        Assert.Equal("Detail", view!.Row.Title);
        Assert.True(view.Row.MayManage);
        Assert.Equal(1, view.Row.AcknowledgedCount);
        Assert.Equal(2, view.Row.TotalCount);
        // Managers get the full list; open entries sort first.
        Assert.Equal(2, view.Acknowledgments.Count);
        Assert.Null(view.Acknowledgments[0].AcknowledgedAt);
        Assert.NotNull(view.Acknowledgments[1].AcknowledgedAt);
    }

    [Fact]
    public async Task GetDetailAsync_NonRecipient_ReturnsNull()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);

        var a = Ann("Secret", x => { x.Audience = AnnouncementAudience.TruUnit; x.CreatedById = "other"; });
        using (var db = ctx.NewContext())
        {
            db.Announcements.Add(a);
            db.SaveChanges();
        }

        // Junior "me", not TRU, not creator, not leadership => not allowed to see.
        var view = await svc.GetDetailAsync(a.Id, Junior("me"));
        Assert.Null(view);
    }

    [Fact]
    public async Task GetDetailAsync_UnknownId_ReturnsNull()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);

        Assert.Null(await svc.GetDetailAsync("missing", Leader()));
    }

    // ---------- GetTrashAsync ----------

    [Fact]
    public async Task GetTrashAsync_ReturnsOnlySoftDeleted_NewestFirst()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);

        var newer = Ann("Newer", x => { x.IsDeleted = true; x.DeletedAt = T0.AddDays(2); });
        var older = Ann("Older", x => { x.IsDeleted = true; x.DeletedAt = T0.AddDays(1); });
        var live = Ann("Live");
        using (var db = ctx.NewContext())
        {
            db.Announcements.AddRange(newer, older, live);
            db.SaveChanges();
        }

        var trash = await svc.GetTrashAsync();

        Assert.Equal(new[] { "Newer", "Older" }, trash.Select(t => t.Title).ToArray());
        Assert.DoesNotContain(trash, t => t.Id == live.Id);
    }
}
