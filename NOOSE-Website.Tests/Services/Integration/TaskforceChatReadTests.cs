using System.Security.Claims;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Infrastructure.Chat;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Taskforces;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Unread counter and read mark of the taskforce chat.</summary>
public sealed class TaskforceChatReadTests
{
    // relative to the clock, because MarkReadAsync stamps the real time
    private static readonly DateTime Now = DateTime.UtcNow;

    private static ClaimsPrincipal Me()
        => ClaimsPrincipalBuilder.Agent("me").WithRank(Rank.JuniorAgent).WithCodename("Rookie").Build();

    private static TaskforceChatService Chat(SqliteTestContext ctx)
        => new(ctx.Factory, new TaskforceChatBroadcaster(), Substitute.For<INotificationService>());

    private static void SeedTaskforce(AppDbContext db, string id, DateTime joined, DateTime? readAt = null,
        bool archived = false, bool deleted = false, string member = "me")
    {
        db.Taskforces.Add(new Taskforce
        {
            Id = id,
            Name = "Taskforce " + id,
            CaseNumber = "NOOSE-TF-2026-" + id,
            IsArchived = archived,
            IsDeleted = deleted,
            CreatedAt = Now.AddDays(-30),
        });
        db.TaskforceAgents.Add(new TaskforceAgent { TaskforceId = id, AgentId = member, CreatedAt = joined, LastReadAt = readAt });
    }

    private static void Agents(AppDbContext db)
    {
        db.Users.Add(Seed.Agent("me", Rank.JuniorAgent));
        db.Users.Add(Seed.Agent("other", Rank.JuniorAgent));
    }

    private static TaskforceMessage Message(string taskforceId, DateTime at, string? by = "other", bool deleted = false)
        => new() { TaskforceId = taskforceId, Text = "…", AuthorName = "Wolf", CreatedAt = at, CreatedById = by, IsDeleted = deleted };

    private static DateTime? MarkOf(SqliteTestContext ctx, string taskforceId, string agentId = "me")
    {
        using var db = ctx.NewContext();
        return db.TaskforceAgents.Single(ta => ta.TaskforceId == taskforceId && ta.AgentId == agentId).LastReadAt;
    }

    [Fact]
    public async Task Without_a_mark_a_member_counts_from_joining()
    {
        using var ctx = new SqliteTestContext();
        var joined = Now.AddDays(-2);
        using (var db = ctx.NewContext())
        {
            Agents(db);
            SeedTaskforce(db, "tf", joined);
            // history from before joining is not news to the newcomer
            db.TaskforceMessages.AddRange(Message("tf", Now.AddDays(-3)), Message("tf", Now.AddDays(-1)));
            db.SaveChanges();
        }

        var state = await Chat(ctx).GetReadStateAsync("tf", Me());

        Assert.Equal(new TaskforceReadState(joined, 1), state);
        Assert.Equal(1, Assert.Single(await Chat(ctx).GetUnreadAsync(Me())).Count);
    }

    [Fact]
    public async Task Marking_read_clears_the_counter()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            Agents(db);
            SeedTaskforce(db, "tf", Now.AddDays(-5), readAt: Now.AddDays(-1));
            db.TaskforceMessages.AddRange(Message("tf", Now.AddHours(-2)), Message("tf", Now.AddMinutes(-5)));
            db.SaveChanges();
        }
        var chat = Chat(ctx);
        Assert.Equal(2, (await chat.GetReadStateAsync("tf", Me())).Unread);

        await chat.MarkReadAsync("tf", Me());

        Assert.Empty(await chat.GetUnreadAsync(Me()));
        Assert.Equal(0, (await chat.GetReadStateAsync("tf", Me())).Unread);
        Assert.True(MarkOf(ctx, "tf") >= Now);
    }

    [Fact]
    public async Task The_mark_only_moves_forward()
    {
        using var ctx = new SqliteTestContext();
        // a later look from a second tab got there first
        var ahead = Now.AddHours(1);
        using (var db = ctx.NewContext())
        {
            Agents(db);
            SeedTaskforce(db, "tf", Now.AddDays(-5), readAt: ahead);
            db.SaveChanges();
        }

        await Chat(ctx).MarkReadAsync("tf", Me());

        Assert.Equal(ahead, MarkOf(ctx, "tf"));
    }

    [Fact]
    public async Task Marking_read_touches_only_the_own_row_and_leaves_no_modified_stamp()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            Agents(db);
            SeedTaskforce(db, "tf", Now.AddDays(-5));
            db.TaskforceAgents.Add(new TaskforceAgent { TaskforceId = "tf", AgentId = "other", CreatedAt = Now.AddDays(-5) });
            db.SaveChanges();
        }

        await Chat(ctx).MarkReadAsync("tf", Me());

        Assert.NotNull(MarkOf(ctx, "tf"));
        Assert.Null(MarkOf(ctx, "tf", "other"));
        using var check = ctx.NewContext();
        Assert.Null(check.TaskforceAgents.Single(ta => ta.AgentId == "me").ModifiedAt);
    }

    public static TheoryData<string> Markless => ["supervision", "demo", "partner"];

    [Theory]
    [MemberData(nameof(Markless))]
    public async Task Accounts_that_cannot_write_set_no_mark_and_get_no_counter(string kind)
    {
        var actor = kind switch
        {
            "supervision" => ClaimsPrincipalBuilder.Agent("me").WithRank(Rank.Director).AsTeamLead().Build(),
            "demo" => ClaimsPrincipalBuilder.Agent("me").WithRank(Rank.Director).AsDemo().Build(),
            _ => ClaimsPrincipalBuilder.Agent("me").AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build(),
        };
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            Agents(db);
            SeedTaskforce(db, "tf", Now.AddDays(-5));
            db.TaskforceMessages.Add(Message("tf", Now.AddHours(-1)));
            db.SaveChanges();
        }
        var chat = Chat(ctx);

        await chat.MarkReadAsync("tf", actor);

        // a counter nobody could clear would only be noise
        Assert.Null(MarkOf(ctx, "tf"));
        Assert.Empty(await chat.GetUnreadAsync(actor));
        Assert.Equal(TaskforceReadState.None, await chat.GetReadStateAsync("tf", actor));
    }

    [Fact]
    public async Task A_non_member_keeps_no_mark()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            Agents(db);
            // leadership reads every taskforce, but counts only where it is assigned
            SeedTaskforce(db, "tf", Now.AddDays(-5), member: "other");
            db.TaskforceMessages.Add(Message("tf", Now.AddHours(-1), by: "other"));
            db.SaveChanges();
        }
        var leader = ClaimsPrincipalBuilder.Agent("me").WithRank(Rank.Director).Build();
        var chat = Chat(ctx);

        await chat.MarkReadAsync("tf", leader);

        Assert.Equal(TaskforceReadState.None, await chat.GetReadStateAsync("tf", leader));
        Assert.Empty(await chat.GetUnreadAsync(leader));
    }

    [Fact]
    public async Task Archived_taskforces_count_on_request_and_trashed_ones_never()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            Agents(db);
            SeedTaskforce(db, "live", Now.AddDays(-5));
            SeedTaskforce(db, "archived", Now.AddDays(-5), archived: true);
            SeedTaskforce(db, "trashed", Now.AddDays(-5), deleted: true);
            db.TaskforceMessages.AddRange(
                Message("live", Now.AddHours(-1)), Message("archived", Now.AddHours(-1)), Message("trashed", Now.AddHours(-1)));
            db.SaveChanges();
        }
        var chat = Chat(ctx);

        var live = await chat.GetUnreadAsync(Me());
        var all = await chat.GetUnreadAsync(Me(), includeArchived: true);

        Assert.Equal(["live"], live.Select(a => a.TaskforceId).ToArray());
        Assert.Equal(["archived", "live"], all.Select(a => a.TaskforceId).Order().ToArray());
    }

    [Fact]
    public async Task List_counter_and_chat_state_agree()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            Agents(db);
            SeedTaskforce(db, "a", Now.AddDays(-5), readAt: Now.AddDays(-1));
            SeedTaskforce(db, "b", Now.AddDays(-2));
            SeedTaskforce(db, "c", Now.AddDays(-5), readAt: Now.AddHours(-1));
            db.TaskforceMessages.AddRange(
                Message("a", Now.AddHours(-3)),
                // own, retracted and already read: none of them count
                Message("a", Now.AddHours(-2), by: "me"),
                Message("a", Now.AddHours(-2), deleted: true),
                Message("a", Now.AddDays(-2)),
                // a line without an author still came from someone else
                Message("b", Now.AddDays(-1), by: null),
                Message("b", Now.AddDays(-3)),
                Message("c", Now.AddHours(-2)));
            db.SaveChanges();
        }
        var chat = Chat(ctx);

        var counts = (await chat.GetUnreadAsync(Me())).ToDictionary(a => a.TaskforceId, a => a.Count);

        Assert.Equal(new Dictionary<string, int> { ["a"] = 1, ["b"] = 1 }, counts);
        foreach (var id in new[] { "a", "b", "c" })
        {
            Assert.Equal(counts.GetValueOrDefault(id), (await chat.GetReadStateAsync(id, Me())).Unread);
        }
    }
}
