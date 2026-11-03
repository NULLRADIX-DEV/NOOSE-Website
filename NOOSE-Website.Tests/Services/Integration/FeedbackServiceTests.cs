using System.Security.Claims;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Feedback;
using NOOSE_Website.Services;
using NSubstitute;
using FeedbackEntity = NOOSE_Website.Data.Entities.Feedback.Feedback;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="FeedbackService"/> over in-memory SQLite.</summary>
public class FeedbackServiceTests
{
    // The soft-delete rewrite interceptor is DI-only and absent here, so DeleteAsync HARD-deletes.

    private static (FeedbackService Svc, INotificationService Notifications) Build(SqliteTestContext ctx)
    {
        var notifications = Substitute.For<INotificationService>();
        return (new FeedbackService(ctx.Factory, notifications), notifications);
    }

    private static FeedbackEntity SeedFeedback(SqliteTestContext ctx, string agentId,
        Action<FeedbackEntity>? configure = null)
    {
        var entry = new FeedbackEntity
        {
            AgentId = agentId,
            Kind = FeedbackKind.Bug,
            PageRoute = "/statistik",
            PageTab = "bestand",
            Text = "Die Seite lädt ewig.",
            CreatedAt = DateTime.UtcNow,
        };
        configure?.Invoke(entry);

        using var db = ctx.NewContext();
        if (!db.Users.Any(u => u.Id == agentId))
        {
            db.Users.Add(Seed.Agent(agentId));
        }
        db.Feedbacks.Add(entry);
        db.SaveChanges();
        return entry;
    }

    private static ClaimsPrincipal Owner(string id) => ClaimsPrincipalBuilder.Agent(id).Build();

    // the codename claim is what SetStatusAsync stamps as DeciderName
    private static ClaimsPrincipal Leader(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.SpecialAgentInCharge)
            .WithCodename($"Codename-{id}").Build();

    private static ClaimsPrincipal OnlyReader(string id = "tl")
        => ClaimsPrincipalBuilder.Agent(id).AsTeamLead().Build();

    private static ClaimsPrincipal Partner(string id = "partner")
        => ClaimsPrincipalBuilder.Agent(id).AsPartner(PartnerAgency.DoJ, PartnerRank.Member).Build();

    // read-only supervision that also carries leadership rank: passes the read gate, fails the write gate
    private static ClaimsPrincipal LeadingOnlyReader(string id = "tl-lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.SpecialAgentInCharge).AsTeamLead().Build();

    // ---------- CreateAsync ----------

    [Fact]
    public async Task CreateAsync_PersistsEntry_WithAllFields()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var input = new FeedbackInput
        {
            Kind = FeedbackKind.FeatureRequest,
            PageRoute = "/einstellungen",
            PageTab = "tags",
            Text = "Dark Mode bitte noch dunkler.",
        };

        var id = await svc.CreateAsync(input, Owner("a1"));

        using var db = ctx.NewContext();
        var row = Assert.Single(db.Feedbacks.ToList());
        Assert.Equal(id, row.Id);
        Assert.Equal("a1", row.AgentId);
        Assert.Equal(FeedbackKind.FeatureRequest, row.Kind);
        Assert.Equal("/einstellungen", row.PageRoute);
        Assert.Equal("tags", row.PageTab);
        Assert.Equal("Dark Mode bitte noch dunkler.", row.Text);
        Assert.Equal(FeedbackStatus.New, row.Status);
        Assert.Null(row.Response);
        Assert.Null(row.DeciderName);
        Assert.Null(row.DecidedAt);
    }

    [Fact]
    public async Task CreateAsync_OnlyReader_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var input = new FeedbackInput { Kind = FeedbackKind.Improvement, Text = "Mehr Kontrast." };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateAsync(input, OnlyReader()));

        using var db = ctx.NewContext();
        Assert.Empty(db.Feedbacks.ToList());
    }

    // ---------- GetMyAsync ----------

    [Fact]
    public async Task GetMyAsync_ReturnsOnlyOwn()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        SeedFeedback(ctx, "a1");
        SeedFeedback(ctx, "a2");

        var rows = await svc.GetMyAsync(Owner("a1"));

        var row = Assert.Single(rows);
        Assert.Equal("Codename-a1", row.AgentCodename);
        Assert.Equal(FeedbackKind.Bug, row.Kind);
        Assert.Equal("/statistik", row.PageRoute);
        Assert.Equal("bestand", row.PageTab);
        Assert.Equal("Die Seite lädt ewig.", row.Text);
    }

    [Fact]
    public async Task GetMyAsync_ProjectsStatusAndDecision()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var decided = new DateTime(2026, 8, 7, 10, 0, 0, DateTimeKind.Utc);
        SeedFeedback(ctx, "a1", f =>
        {
            f.Status = FeedbackStatus.Done;
            f.Response = "Ist drin.";
            f.DeciderName = "Codename-lead";
            f.DecidedAt = decided;
        });

        var row = Assert.Single(await svc.GetMyAsync(Owner("a1")));
        Assert.Equal(FeedbackStatus.Done, row.Status);
        Assert.Equal("Ist drin.", row.Response);
        Assert.Equal("Codename-lead", row.DeciderName);
        Assert.Equal(decided, row.DecidedAt);
    }

    // ---------- GetInboxAsync ----------

    [Fact]
    public async Task GetInboxAsync_Partner_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        SeedFeedback(ctx, "a1");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.GetInboxAsync(Partner()));
    }

    [Fact]
    public async Task GetInboxAsync_PlainAgent_ReadsEveryReport()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        SeedFeedback(ctx, "a1");
        SeedFeedback(ctx, "a2");

        // every internal agent reads the whole board, not just their own reports
        var rows = await svc.GetInboxAsync(Owner("a1"));

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.AgentId == "a2");
    }

    [Fact]
    public async Task GetInboxAsync_Leadership_ReturnsAll()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        SeedFeedback(ctx, "a1");
        SeedFeedback(ctx, "a2");

        var rows = await svc.GetInboxAsync(Leader());

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.AgentCodename == "Codename-a1");
        Assert.Contains(rows, r => r.AgentCodename == "Codename-a2");
    }

    [Fact]
    public async Task GetInboxAsync_OnlyReader_ReadsAll()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        SeedFeedback(ctx, "a1", f => f.Status = FeedbackStatus.Rejected);

        // read-only supervision is internal, so it reads the board like everyone else
        var row = Assert.Single(await svc.GetInboxAsync(OnlyReader()));
        Assert.Equal(FeedbackStatus.Rejected, row.Status);
    }

    // ---------- SetStatusAsync ----------

    [Fact]
    public async Task SetStatusAsync_Leadership_PersistsStatusResponseAndDecider()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var entry = SeedFeedback(ctx, "a1");

        await svc.SetStatusAsync(entry.Id, FeedbackStatus.InProgress, "Kommt nächste Woche.", Leader());

        using var db = ctx.NewContext();
        var row = db.Feedbacks.Single(f => f.Id == entry.Id);
        Assert.Equal(FeedbackStatus.InProgress, row.Status);
        Assert.Equal("Kommt nächste Woche.", row.Response);
        Assert.Equal("Codename-lead", row.DeciderName);
        Assert.NotNull(row.DecidedAt);
    }

    [Fact]
    public async Task SetStatusAsync_Reporter_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var entry = SeedFeedback(ctx, "a1");

        // the reporter must not decide on their own report
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.SetStatusAsync(entry.Id, FeedbackStatus.Done, null, Owner("a1")));

        using var db = ctx.NewContext();
        Assert.Equal(FeedbackStatus.New, db.Feedbacks.Single(f => f.Id == entry.Id).Status);
    }

    [Fact]
    public async Task SetStatusAsync_LeadingOnlyReader_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var entry = SeedFeedback(ctx, "a1");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.SetStatusAsync(entry.Id, FeedbackStatus.Accepted, "Nö.", LeadingOnlyReader()));

        using var db = ctx.NewContext();
        var row = db.Feedbacks.Single(f => f.Id == entry.Id);
        Assert.Equal(FeedbackStatus.New, row.Status);
        Assert.Null(row.Response);
    }

    [Fact]
    public async Task SetStatusAsync_UnknownId_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SetStatusAsync("nope", FeedbackStatus.Done, null, Leader()));
    }

    [Fact]
    public async Task SetStatusAsync_BlankResponse_StoresNull()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var entry = SeedFeedback(ctx, "a1");

        await svc.SetStatusAsync(entry.Id, FeedbackStatus.Deferred, "   ", Leader());

        using var db = ctx.NewContext();
        Assert.Null(db.Feedbacks.Single(f => f.Id == entry.Id).Response);
    }

    [Fact]
    public async Task SetStatusAsync_AnyTransitionAllowed()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var entry = SeedFeedback(ctx, "a1", f => f.Status = FeedbackStatus.Done);

        // there is no state machine on purpose, so a wrong pick stays correctable
        await svc.SetStatusAsync(entry.Id, FeedbackStatus.New, null, Leader());

        using var db = ctx.NewContext();
        Assert.Equal(FeedbackStatus.New, db.Feedbacks.Single(f => f.Id == entry.Id).Status);
    }

    [Fact]
    public async Task SetStatusAsync_StatusChanged_NotifiesReporter()
    {
        using var ctx = new SqliteTestContext();
        var (svc, notifications) = Build(ctx);

        var entry = SeedFeedback(ctx, "a1");

        await svc.SetStatusAsync(entry.Id, FeedbackStatus.Accepted, null, Leader());

        await notifications.Received(1).NotifyAsync("a1", NotificationType.Feedback,
            Arg.Any<string>(), "/feedback?tab=meine", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetStatusAsync_ResponseChangedOnly_NotifiesReporter()
    {
        using var ctx = new SqliteTestContext();
        var (svc, notifications) = Build(ctx);

        var entry = SeedFeedback(ctx, "a1");

        await svc.SetStatusAsync(entry.Id, FeedbackStatus.New, "Schau ich mir an.", Leader());

        await notifications.Received(1).NotifyAsync("a1", NotificationType.Feedback,
            Arg.Any<string>(), "/feedback?tab=meine", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetStatusAsync_NothingChanged_DoesNotNotify()
    {
        using var ctx = new SqliteTestContext();
        var (svc, notifications) = Build(ctx);

        var entry = SeedFeedback(ctx, "a1");

        await svc.SetStatusAsync(entry.Id, FeedbackStatus.New, null, Leader());

        await notifications.DidNotReceive().NotifyAsync(Arg.Any<string>(), Arg.Any<NotificationType>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // the quiet path still stamps the decider
        using var db = ctx.NewContext();
        Assert.Equal("Codename-lead", db.Feedbacks.Single(f => f.Id == entry.Id).DeciderName);
    }

    [Fact]
    public async Task SetStatusAsync_ActorIsReporter_DoesNotNotify()
    {
        using var ctx = new SqliteTestContext();
        var (svc, notifications) = Build(ctx);

        var entry = SeedFeedback(ctx, "lead");

        await svc.SetStatusAsync(entry.Id, FeedbackStatus.Accepted, null, Leader());

        await notifications.DidNotReceive().NotifyAsync(Arg.Any<string>(), Arg.Any<NotificationType>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---------- DeleteAsync ----------

    [Fact]
    public async Task DeleteAsync_Owner_HardDeletesRow()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var entry = SeedFeedback(ctx, "a1");

        await svc.DeleteAsync(entry.Id, Owner("a1"));

        using var db = ctx.NewContext();
        Assert.False(db.Feedbacks.Any(f => f.Id == entry.Id));
        // no soft-delete interceptor in tests => physically gone, not just flagged
        Assert.False(db.Feedbacks.IgnoreQueryFilters().Any(f => f.Id == entry.Id));
    }

    [Fact]
    public async Task DeleteAsync_ForeignNonLeadership_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var entry = SeedFeedback(ctx, "a1");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.DeleteAsync(entry.Id, Owner("a2")));

        using var db = ctx.NewContext();
        Assert.True(db.Feedbacks.Any(f => f.Id == entry.Id));
    }

    [Fact]
    public async Task DeleteAsync_ForeignLeadership_HardDeletesRow()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var entry = SeedFeedback(ctx, "a1");

        await svc.DeleteAsync(entry.Id, Leader());

        using var db = ctx.NewContext();
        Assert.False(db.Feedbacks.IgnoreQueryFilters().Any(f => f.Id == entry.Id));
    }

    // ---------- GetTrashAsync ----------

    [Fact]
    public async Task GetTrashAsync_ReturnsOnlyDeleted_WithAgent()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var deleted = SeedFeedback(ctx, "a1",
            f => { f.IsDeleted = true; f.DeletedAt = DateTime.UtcNow; });
        SeedFeedback(ctx, "a2"); // live, excluded

        var trash = await svc.GetTrashAsync();

        var row = Assert.Single(trash);
        Assert.Equal(deleted.Id, row.Id);
        Assert.NotNull(row.Agent);
        Assert.Equal("Codename-a1", row.Agent!.Codename);
    }

    // ---------- RestoreAsync ----------

    [Fact]
    public async Task RestoreAsync_Leadership_UndeletesRow()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var entry = SeedFeedback(ctx, "a1",
            f => { f.IsDeleted = true; f.DeletedAt = DateTime.UtcNow; f.DeletedById = "x"; });

        await svc.RestoreAsync(entry.Id, Leader());

        using var db = ctx.NewContext();
        var row = db.Feedbacks.Single(f => f.Id == entry.Id); // visible again in the filtered set
        Assert.False(row.IsDeleted);
        Assert.Null(row.DeletedAt);
        Assert.Null(row.DeletedById);
    }

    [Fact]
    public async Task RestoreAsync_NonLeadership_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var entry = SeedFeedback(ctx, "a1",
            f => { f.IsDeleted = true; f.DeletedAt = DateTime.UtcNow; });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RestoreAsync(entry.Id, Owner("a1")));

        using var db = ctx.NewContext();
        Assert.True(db.Feedbacks.IgnoreQueryFilters().Single(f => f.Id == entry.Id).IsDeleted);
    }
}
