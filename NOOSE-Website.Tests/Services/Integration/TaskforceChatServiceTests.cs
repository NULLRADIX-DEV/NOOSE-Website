using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Infrastructure.Chat;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="TaskforceChatService"/> against in-memory SQLite.</summary>
public sealed class TaskforceChatServiceTests
{
    private static (TaskforceChatService Svc, INotificationService Notifications) Build(SqliteTestContext ctx)
    {
        var notifications = Substitute.For<INotificationService>();
        var svc = new TaskforceChatService(ctx.Factory, new TaskforceChatBroadcaster(), notifications);
        return (svc, notifications);
    }

    // Rank >= SpecialAgentInCharge(4) => IsLeadership() + MayAllTaskforcesSee(): sees every taskforce.
    private static ClaimsPrincipal Leader(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).WithCodename("Falcon").Build();

    // Junior agent: not leadership, sees only taskforces it is assigned to.
    private static ClaimsPrincipal Junior(string id = "junior")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).WithCodename("Rookie").Build();

    // External partner: read-only, may post but not retract.
    private static ClaimsPrincipal Partner(string id = "partner1")
        => ClaimsPrincipalBuilder.Agent(id).AsPartner(PartnerAgency.LSPD, PartnerRank.Member).Build();

    private static Taskforce SeedTaskforce(SqliteTestContext ctx, string id, string? memberAgentId = null)
    {
        using var db = ctx.NewContext();
        db.Taskforces.Add(new Taskforce
        {
            Id = id,
            Name = "Taskforce " + id,
            CaseNumber = "FIB-TF-2026-0001",
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        });
        if (memberAgentId is not null)
        {
            db.TaskforceAgents.Add(new TaskforceAgent
            {
                TaskforceId = id,
                AgentId = memberAgentId,
                Role = TaskforceRole.Member,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
        }
        db.SaveChanges();
        return db.Taskforces.Find(id)!;
    }

    private static TaskforceMessage MakeMessage(string taskforceId, string text, DateTime createdAt, string? createdById = null)
        => new()
        {
            TaskforceId = taskforceId,
            Text = text,
            AuthorName = "Author",
            CreatedAt = createdAt,
            CreatedById = createdById,
        };

    // ---------- GetMessagesAsync ----------

    [Fact]
    public async Task GetMessagesAsync_ReturnsMessages_Chronological_WhenLeadershipSeesAll()
    {
        using var ctx = new SqliteTestContext();
        SeedTaskforce(ctx, "tf1");
        using (var db = ctx.NewContext())
        {
            db.TaskforceMessages.Add(MakeMessage("tf1", "first", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            db.TaskforceMessages.Add(MakeMessage("tf1", "second", new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)));
            db.TaskforceMessages.Add(MakeMessage("tf1", "third", new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)));
            // message on another taskforce must be excluded
            db.TaskforceMessages.Add(MakeMessage("other", "elsewhere", new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var result = await svc.GetMessagesAsync("tf1", ViewerScope.From(Leader()));

        // oldest-first for display
        Assert.Equal(new[] { "first", "second", "third" }, result.Select(m => m.Text).ToArray());
    }

    [Fact]
    public async Task GetMessagesAsync_ReturnsEmpty_WhenTaskforceNotVisible()
    {
        using var ctx = new SqliteTestContext();
        SeedTaskforce(ctx, "tf2");
        using (var db = ctx.NewContext())
        {
            db.TaskforceMessages.Add(MakeMessage("tf2", "secret", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        // junior is not assigned to tf2 => not visible => empty regardless of stored messages
        var result = await svc.GetMessagesAsync("tf2", ViewerScope.From(Junior()));

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetMessagesAsync_ReturnsMessages_WhenAssignedMember()
    {
        using var ctx = new SqliteTestContext();
        SeedTaskforce(ctx, "tf3", memberAgentId: "member1");
        using (var db = ctx.NewContext())
        {
            db.TaskforceMessages.Add(MakeMessage("tf3", "hallo", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        // assigned member sees the chat even without leadership
        var result = await svc.GetMessagesAsync("tf3", ViewerScope.From(Junior("member1")));

        Assert.Single(result);
        Assert.Equal("hallo", result[0].Text);
    }

    [Fact]
    public async Task GetMessagesAsync_RespectsOlderAs_PagesFurtherBack()
    {
        using var ctx = new SqliteTestContext();
        SeedTaskforce(ctx, "tf4");
        using (var db = ctx.NewContext())
        {
            db.TaskforceMessages.Add(MakeMessage("tf4", "old", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            db.TaskforceMessages.Add(MakeMessage("tf4", "mid", new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)));
            db.TaskforceMessages.Add(MakeMessage("tf4", "new", new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        // only messages strictly before the cutoff
        var result = await svc.GetMessagesAsync("tf4", ViewerScope.From(Leader()),
            olderAs: new DateTime(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(new[] { "old", "mid" }, result.Select(m => m.Text).ToArray());
    }

    [Fact]
    public async Task GetMessagesAsync_RespectsLimit_KeepingNewest()
    {
        using var ctx = new SqliteTestContext();
        SeedTaskforce(ctx, "tf5");
        using (var db = ctx.NewContext())
        {
            for (var i = 1; i <= 5; i++)
            {
                db.TaskforceMessages.Add(MakeMessage("tf5", $"m{i}", new DateTime(2026, 1, i, 0, 0, 0, DateTimeKind.Utc)));
            }
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        // newest two, returned oldest-first
        var result = await svc.GetMessagesAsync("tf5", ViewerScope.From(Leader()), limit: 2);

        Assert.Equal(new[] { "m4", "m5" }, result.Select(m => m.Text).ToArray());
    }

    // ---------- SendAsync ----------

    [Fact]
    public async Task SendAsync_PersistsTrimmedMessage_AndStampsCodename()
    {
        using var ctx = new SqliteTestContext();
        SeedTaskforce(ctx, "tf6");
        var (svc, _) = Build(ctx);

        var message = await svc.SendAsync("tf6", "  moin  ", Leader());

        Assert.Equal("moin", message.Text);
        Assert.Equal("Falcon", message.AuthorName);
        Assert.Equal("tf6", message.TaskforceId);

        using var check = ctx.NewContext();
        var stored = await check.TaskforceMessages.SingleAsync(m => m.TaskforceId == "tf6");
        Assert.Equal("moin", stored.Text);
        Assert.Equal("Falcon", stored.AuthorName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task SendAsync_Throws_OnEmptyOrWhitespaceText(string? text)
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        // empty-text guard runs before any DB/visibility work
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SendAsync("any", text!, Leader()));
    }

    [Fact]
    public async Task SendAsync_Throws_WhenTaskforceNotVisible()
    {
        using var ctx = new SqliteTestContext();
        SeedTaskforce(ctx, "tf7");
        var (svc, _) = Build(ctx);

        // junior is not assigned to tf7 => not visible
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.SendAsync("tf7", "nope", Junior()));

        using var check = ctx.NewContext();
        Assert.False(await check.TaskforceMessages.AnyAsync(m => m.TaskforceId == "tf7"));
    }

    [Fact]
    public async Task SendAsync_NotifiesMentions()
    {
        using var ctx = new SqliteTestContext();
        SeedTaskforce(ctx, "tf8");
        var (svc, notifications) = Build(ctx);

        await svc.SendAsync("tf8", "Text mit @Erwaehnung", Leader());

        await notifications.Received(1).NotifyMentionedAsync(
            "Text mit @Erwaehnung", Arg.Any<string>(), Arg.Any<string?>(),
            "Taskforce", "tf8", Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    // ---------- DeleteAsync ----------

    [Fact]
    public async Task DeleteAsync_RemovesMessage_WhenAuthor()
    {
        using var ctx = new SqliteTestContext();
        SeedTaskforce(ctx, "tf9");
        var message = MakeMessage("tf9", "mine", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), createdById: "author1");
        using (var db = ctx.NewContext())
        {
            db.TaskforceMessages.Add(message);
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        // author (id "author1") may retract their own message even without leadership
        await svc.DeleteAsync(message.Id, Junior("author1"));

        using var check = ctx.NewContext();
        // no soft-delete interceptor in tests => hard delete, row gone from the filtered set
        Assert.False(await check.TaskforceMessages.AnyAsync(m => m.Id == message.Id));
    }

    [Fact]
    public async Task DeleteAsync_RemovesMessage_WhenLeadership()
    {
        using var ctx = new SqliteTestContext();
        SeedTaskforce(ctx, "tf10");
        var message = MakeMessage("tf10", "theirs", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), createdById: "someone-else");
        using (var db = ctx.NewContext())
        {
            db.TaskforceMessages.Add(message);
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        // leadership may retract any message
        await svc.DeleteAsync(message.Id, Leader());

        using var check = ctx.NewContext();
        Assert.False(await check.TaskforceMessages.AnyAsync(m => m.Id == message.Id));
    }

    [Fact]
    public async Task DeleteAsync_NoOp_WhenMessageMissing()
    {
        using var ctx = new SqliteTestContext();
        SeedTaskforce(ctx, "tf11");
        var message = MakeMessage("tf11", "keep", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), createdById: "x");
        using (var db = ctx.NewContext())
        {
            db.TaskforceMessages.Add(message);
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        // unknown id returns silently, touches nothing
        await svc.DeleteAsync("missing", Leader());

        using var check = ctx.NewContext();
        Assert.Equal(1, await check.TaskforceMessages.CountAsync());
    }

    [Fact]
    public async Task DeleteAsync_Throws_WhenPartner()
    {
        using var ctx = new SqliteTestContext();
        SeedTaskforce(ctx, "tf12");
        var message = MakeMessage("tf12", "posted", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), createdById: "partner1");
        using (var db = ctx.NewContext())
        {
            db.TaskforceMessages.Add(message);
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        // partners may post but never retract; guard runs before any lookup
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.DeleteAsync(message.Id, Partner("partner1")));

        using var check = ctx.NewContext();
        Assert.True(await check.TaskforceMessages.AnyAsync(m => m.Id == message.Id));
    }

    [Fact]
    public async Task DeleteAsync_Throws_WhenNotAuthorNorLeadership()
    {
        using var ctx = new SqliteTestContext();
        SeedTaskforce(ctx, "tf13");
        var message = MakeMessage("tf13", "protected", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), createdById: "other");
        using (var db = ctx.NewContext())
        {
            db.TaskforceMessages.Add(message);
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        // junior "member1" is neither the author ("other") nor leadership
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.DeleteAsync(message.Id, Junior("member1")));

        using var check = ctx.NewContext();
        Assert.True(await check.TaskforceMessages.AnyAsync(m => m.Id == message.Id));
    }
}
