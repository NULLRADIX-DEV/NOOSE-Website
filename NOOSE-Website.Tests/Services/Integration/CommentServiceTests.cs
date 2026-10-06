using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.CurrentUser;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="CommentService"/> against in-memory SQLite.</summary>
public sealed class CommentServiceTests
{
    private static (CommentService Svc, INotificationService Notifications) Build(SqliteTestContext ctx)
    {
        var notifications = Substitute.For<INotificationService>();
        return (new CommentService(ctx.Factory, notifications), notifications);
    }

    // Rank >= SupervisorySpecialAgent(4) or admin => IsLeadership() + MayClassifiedRead().
    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).WithCodename("Falcon").Build();

    // Junior agent: not leadership, cannot read classified records.
    private static ClaimsPrincipal Junior(string id = "junior")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.JuniorAgent).WithCodename("Rookie").Build();

    // External partner: read-only, sees only shared, non-classified records.
    private static ClaimsPrincipal Partner()
        => ClaimsPrincipalBuilder.Agent("partner1").AsPartner(PartnerAgency.LSPD, PartnerRank.Member).Build();

    // Read-only supervision: IsOnlyReader is derived from IsTeamLead && !IsAdmin, so no admin flag here.
    private static ClaimsPrincipal OnlyReader()
        => ClaimsPrincipalBuilder.Agent("aufsicht").WithRank(Rank.Director).WithCodename("Auge").AsTeamLead().Build();

    private static Comment MakeComment(string entityType, string entityId, string text,
        DateTime createdAt, string? createdById = null)
        => new()
        {
            EntityType = entityType,
            EntityId = entityId,
            Text = text,
            CreatedAt = createdAt,
            CreatedById = createdById,
        };

    // ---------- GetForRecordAsync (ViewerScope overload) ----------

    [Fact]
    public async Task GetForRecordAsync_Scope_ReturnsComments_NewestFirst()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.Comments.Add(MakeComment("Person", "p1", "older", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            db.Comments.Add(MakeComment("Person", "p1", "newer", new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)));
            // a comment on a different record must be excluded
            db.Comments.Add(MakeComment("Person", "other", "elsewhere", new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        // non-classified record is visible even to a junior agent
        var result = await svc.GetForRecordAsync("Person", "p1", ViewerScope.From(Junior()));

        Assert.Equal(new[] { "newer", "older" }, result.Select(c => c.Text).ToArray());
    }

    [Fact]
    public async Task GetForRecordAsync_Scope_ReturnsEmpty_WhenRecordNotVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p2", configure: p => p.IsClassified = true));
            db.Comments.Add(MakeComment("Person", "p2", "secret", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        // classified record: junior cannot read it -> empty, regardless of stored comments
        var result = await svc.GetForRecordAsync("Person", "p2", ViewerScope.From(Junior()));

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetForRecordAsync_Scope_Partner_ReturnsComments_WhenParentSharedWhole()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p3"));
            db.Comments.Add(MakeComment("Person", "p3", "shared", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            // whole-record release to the whole agency (PartnerAgentId null), children included
            db.PartnerShares.Add(new PartnerShare
            {
                EntityType = "Person",
                EntityId = "p3",
                Agency = PartnerAgency.LSPD,
                PartnerAgentId = null,
                IncludesChildren = true,
            });
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var result = await svc.GetForRecordAsync("Person", "p3", ViewerScope.From(Partner()));

        Assert.Single(result);
        Assert.Equal("shared", result[0].Text);
    }

    [Fact]
    public async Task GetForRecordAsync_Scope_Partner_ReturnsEmpty_WhenChildrenNotReleased()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p4"));
            db.Comments.Add(MakeComment("Person", "p4", "hidden", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            // shell-only release: parent visible, but comments are not individually released
            db.PartnerShares.Add(new PartnerShare
            {
                EntityType = "Person",
                EntityId = "p4",
                Agency = PartnerAgency.LSPD,
                PartnerAgentId = null,
                IncludesChildren = false,
            });
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var result = await svc.GetForRecordAsync("Person", "p4", ViewerScope.From(Partner()));

        Assert.Empty(result);
    }

    // ---------- GetForRecordAsync (bool overload) ----------

    [Fact]
    public async Task GetForRecordAsync_Bool_ReturnsComments_WhenLeadershipSeesClassified()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p5", configure: p => p.IsClassified = true));
            db.Comments.Add(MakeComment("Person", "p5", "vs", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var result = await svc.GetForRecordAsync("Person", "p5", isLeadership: true);

        Assert.Single(result);
        Assert.Equal("vs", result[0].Text);
    }

    [Fact]
    public async Task GetForRecordAsync_Bool_ReturnsEmpty_WhenNonLeadershipOnClassified()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p6", configure: p => p.IsClassified = true));
            db.Comments.Add(MakeComment("Person", "p6", "vs", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var result = await svc.GetForRecordAsync("Person", "p6", isLeadership: false);

        Assert.Empty(result);
    }

    // ---------- CreateAsync ----------

    [Fact]
    public async Task CreateAsync_PersistsTrimmedComment_AndStampsCodename()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p7"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var comment = await svc.CreateAsync("Person", "p7", "  Hallo Welt  ", Junior());

        Assert.Equal("Hallo Welt", comment.Text);
        Assert.Equal("Rookie", comment.AuthorName);
        Assert.Equal("Person", comment.EntityType);
        Assert.Equal("p7", comment.EntityId);

        using var check = ctx.NewContext();
        var stored = await check.Comments.SingleAsync(c => c.EntityId == "p7");
        Assert.Equal("Hallo Welt", stored.Text);
        Assert.Equal("Rookie", stored.AuthorName);
    }

    [Fact]
    public async Task CreateAsync_NotifiesMentions()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p8"));
            db.SaveChanges();
        }
        var (svc, notifications) = Build(ctx);

        await svc.CreateAsync("Person", "p8", "Text mit Erwaehnung", Junior());

        await notifications.Received(1).NotifyMentionedAsync(
            Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string?>(),
            "Person", "p8", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>(), "Comment");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateAsync_Throws_OnEmptyOrWhitespaceText(string? text)
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        // empty-text guard runs before any DB/visibility work
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync("Person", "any", text!, Junior()));
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenRecordNotVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p10", configure: p => p.IsClassified = true));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        // classified record: junior cannot access it
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateAsync("Person", "p10", "nope", Junior()));

        using var check = ctx.NewContext();
        Assert.False(await check.Comments.AnyAsync(c => c.EntityId == "p10"));
    }

    [Fact]
    public async Task CreateAsync_Throws_ForReadOnlySupervision()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p11"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        // this measures the guard, not the barrier: SqliteTestContext attaches no interceptors at all,
        // so a write that only the ReadOnlyBarrierInterceptor stopped would go through here
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateAsync("Person", "p11", "Vermerk aus der Kopfzeile", OnlyReader()));

        using var check = ctx.NewContext();
        Assert.False(await check.Comments.AnyAsync(c => c.EntityId == "p11"));
    }

    // ---------- DeleteAsync ----------

    [Fact]
    public async Task DeleteAsync_RemovesComment_WhenAuthor()
    {
        using var ctx = new SqliteTestContext();
        var comment = MakeComment("Person", "p1", "mine", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), createdById: "junior");
        using (var db = ctx.NewContext())
        {
            db.Comments.Add(comment);
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        // author (id "junior") may delete their own comment even without leadership
        await svc.DeleteAsync(comment.Id, Junior("junior"));

        using var check = ctx.NewContext();
        // no soft-delete interceptor in tests => hard delete, row is gone from the filtered set
        Assert.False(await check.Comments.AnyAsync(c => c.Id == comment.Id));
    }

    [Fact]
    public async Task DeleteAsync_RemovesComment_WhenLeadership()
    {
        using var ctx = new SqliteTestContext();
        var comment = MakeComment("Person", "p1", "theirs", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), createdById: "someone-else");
        using (var db = ctx.NewContext())
        {
            db.Comments.Add(comment);
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        // leadership may delete any comment
        await svc.DeleteAsync(comment.Id, Leader());

        using var check = ctx.NewContext();
        Assert.False(await check.Comments.AnyAsync(c => c.Id == comment.Id));
    }

    [Fact]
    public async Task DeleteAsync_NoOp_WhenCommentMissing()
    {
        using var ctx = new SqliteTestContext();
        var comment = MakeComment("Person", "p1", "keep", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), createdById: "x");
        using (var db = ctx.NewContext())
        {
            db.Comments.Add(comment);
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        // unknown id returns silently, touches nothing
        await svc.DeleteAsync("missing", Leader());

        using var check = ctx.NewContext();
        Assert.Equal(1, await check.Comments.CountAsync());
    }

    [Fact]
    public async Task DeleteAsync_Throws_WhenNotAuthorNorLeadership()
    {
        using var ctx = new SqliteTestContext();
        var comment = MakeComment("Person", "p1", "protected", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), createdById: "other");
        using (var db = ctx.NewContext())
        {
            db.Comments.Add(comment);
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        // junior "junior" is neither the author ("other") nor leadership
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.DeleteAsync(comment.Id, Junior("junior")));

        using var check = ctx.NewContext();
        Assert.True(await check.Comments.AnyAsync(c => c.Id == comment.Id));
    }

    // ---------- EditAsync ----------

    [Fact]
    public async Task EditAsync_UpdatesTrimmedText_WhenAuthor()
    {
        using var ctx = new SqliteTestContext();
        var comment = MakeComment("Person", "e1", "alt", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), createdById: "junior");
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("e1"));
            db.Comments.Add(comment);
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var result = await svc.EditAsync(comment.Id, "  neu  ", Junior("junior"));

        Assert.Equal("neu", result.Text);
        using var check = ctx.NewContext();
        Assert.Equal("neu", (await check.Comments.FirstAsync(c => c.Id == comment.Id)).Text);
    }

    [Fact]
    public async Task EditAsync_Throws_WhenNotAuthor()
    {
        using var ctx = new SqliteTestContext();
        var comment = MakeComment("Person", "e2", "fremd", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), createdById: "other");
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("e2"));
            db.Comments.Add(comment);
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.EditAsync(comment.Id, "uebernommen", Junior("junior")));

        using var check = ctx.NewContext();
        Assert.Equal("fremd", (await check.Comments.FirstAsync(c => c.Id == comment.Id)).Text);
    }

    [Fact]
    public async Task EditAsync_Throws_WhenLeadershipButNotAuthor()
    {
        using var ctx = new SqliteTestContext();
        var comment = MakeComment("Person", "e3", "fremd", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), createdById: "other");
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("e3"));
            db.Comments.Add(comment);
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        // deliberate difference to DeleteAsync: leadership may remove a comment, never rewrite it
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.EditAsync(comment.Id, "korrigiert", Leader()));

        using var check = ctx.NewContext();
        Assert.Equal("fremd", (await check.Comments.FirstAsync(c => c.Id == comment.Id)).Text);
    }

    [Fact]
    public async Task EditAsync_Throws_WhenAuthorUnknown()
    {
        using var ctx = new SqliteTestContext();
        // legacy row without an author id: a null == null match would open it to everyone
        var comment = MakeComment("Person", "e4", "herrenlos", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("e4"));
            db.Comments.Add(comment);
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.EditAsync(comment.Id, "neu", Junior("junior")));

        using var check = ctx.NewContext();
        Assert.Equal("herrenlos", (await check.Comments.FirstAsync(c => c.Id == comment.Id)).Text);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task EditAsync_Throws_OnEmptyOrWhitespaceText(string? text)
    {
        using var ctx = new SqliteTestContext();
        var comment = MakeComment("Person", "e5", "bleibt", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), createdById: "junior");
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("e5"));
            db.Comments.Add(comment);
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.EditAsync(comment.Id, text!, Junior("junior")));

        using var check = ctx.NewContext();
        Assert.Equal("bleibt", (await check.Comments.FirstAsync(c => c.Id == comment.Id)).Text);
    }

    [Fact]
    public async Task EditAsync_Throws_WhenCommentMissing()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.EditAsync("missing", "neu", Junior("junior")));
    }

    [Fact]
    public async Task EditAsync_Throws_WhenRecordNotVisible()
    {
        using var ctx = new SqliteTestContext();
        var comment = MakeComment("Person", "e6", "geheim", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), createdById: "junior");
        using (var db = ctx.NewContext())
        {
            // the record was classified after the comment was written
            db.People.Add(Seed.Person("e6", configure: p => p.IsClassified = true));
            db.Comments.Add(comment);
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.EditAsync(comment.Id, "neu", Junior("junior")));

        using var check = ctx.NewContext();
        Assert.Equal("geheim", (await check.Comments.FirstAsync(c => c.Id == comment.Id)).Text);
    }

    [Fact]
    public async Task EditAsync_NotifiesOnlyMentionsAddedByTheEdit()
    {
        using var ctx = new SqliteTestContext();
        var comment = MakeComment("Person", "e7", "ohne", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), createdById: "junior");
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("e7"));
            db.Comments.Add(comment);
            db.SaveChanges();
        }
        var (svc, notifications) = Build(ctx);

        await svc.EditAsync(comment.Id, "jetzt mit Erwaehnung", Junior("junior"));

        // the delta fanout gets both texts; anyone already named at creation stays unpinged
        await notifications.Received(1).NotifyMentionedDeltaAsync(
            "ohne", "jetzt mit Erwaehnung", Arg.Any<string>(), Arg.Any<string?>(),
            "Person", "e7", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>(), "Comment");
        await notifications.DidNotReceive().NotifyMentionedAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>(), Arg.Any<string?>());
    }

    /// <summary>Stub acting agent for the interceptor-backed test.</summary>
    private sealed class FixedUser : ICurrentUserService
    {
        public Task<CurrentUserInfo> GetAsync() => Task.FromResult(Get());

        public CurrentUserInfo Get() => new("lead", "Falcon", false, false, false);
    }

    [Fact]
    public async Task EditAsync_StampsModified_AndLogsOldAndNewText()
    {
        using var ctx = new SqliteTestContext();
        // the shared test context omits the interceptors; the audit trail is the whole point here
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(ctx.Connection)
            .AddInterceptors(new AuditSaveChangesInterceptor(new FixedUser()))
            .Options;
        var comment = MakeComment("Person", "e8", "alter Wortlaut", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        using (var db = new AppDbContext(options))
        {
            db.People.Add(Seed.Person("e8"));
            db.Comments.Add(comment);
            db.SaveChanges(); // stamps CreatedById = "lead"
        }
        var svc = new CommentService(new TestDbContextFactory(options), Substitute.For<INotificationService>());

        await svc.EditAsync(comment.Id, "neuer Wortlaut", Leader());

        using var check = ctx.NewContext();
        var stored = await check.Comments.FirstAsync(c => c.Id == comment.Id);
        Assert.NotNull(stored.ModifiedAt); // drives the "bearbeitet" marker in the panel
        Assert.Equal("lead", stored.ModifiedById);

        var row = await check.AuditLogs.SingleAsync(a => a.EntityType == nameof(Comment) && a.Action == AuditAction.Modified);
        Assert.Contains("alter Wortlaut", row.ChangesJson);
        Assert.Contains("neuer Wortlaut", row.ChangesJson);
    }
}
