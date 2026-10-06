using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Watchlist;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="FollowupService"/> against in-memory SQLite.</summary>
public sealed class FollowupServiceTests
{
    private static FollowupService Build(SqliteTestContext ctx, INotificationService? notifications = null)
        => new(ctx.Factory, notifications ?? Substitute.For<INotificationService>());

    // Rank >= SupervisorySpecialAgent(4) or admin => IsLeadership() + MayClassifiedRead().
    private static ClaimsPrincipal Leader(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).Build();

    // Junior agent: not leadership, may write, cannot read classified records.
    private static ClaimsPrincipal Junior(string id = "me")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.JuniorAgent).Build();

    private static readonly DateTime Past = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Future = new(2999, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Followup MakeFollowup(string entityType, string entityId, Action<Followup>? configure = null)
    {
        var f = new Followup
        {
            EntityType = entityType,
            EntityId = entityId,
            DueAt = Past,
        };
        configure?.Invoke(f);
        return f;
    }

    // ---------- GetForRecordAsync ----------

    [Fact]
    public async Task GetForRecordAsync_ReturnsFollowups_OpenFirstThenByDue()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.Followups.Add(MakeFollowup("Person", "p1", f => { f.Note = "feb"; f.DueAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc); }));
            db.Followups.Add(MakeFollowup("Person", "p1", f => { f.Note = "jan"; f.DueAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc); }));
            db.Followups.Add(MakeFollowup("Person", "p1", f => { f.Note = "done"; f.Done = true; f.DueAt = new DateTime(2025, 12, 1, 0, 0, 0, DateTimeKind.Utc); }));
            // a followup on a different record must be excluded
            db.Followups.Add(MakeFollowup("Person", "other", f => f.Note = "elsewhere"));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        // non-classified record is visible even to a junior; open sort before done, then by due date
        var result = await svc.GetForRecordAsync("Person", "p1", Junior());

        Assert.Equal(new[] { "jan", "feb", "done" }, result.Select(r => r.Note).ToArray());
    }

    [Fact]
    public async Task GetForRecordAsync_ReturnsEmpty_WhenRecordNotVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p2", configure: p => p.IsClassified = true));
            db.Followups.Add(MakeFollowup("Person", "p2", f => f.Note = "secret"));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        // classified record: junior cannot read it -> empty
        var result = await svc.GetForRecordAsync("Person", "p2", Junior());

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetForRecordAsync_SetsOverdue_AndResolvesCodename()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p3"));
            db.Users.Add(Seed.Agent("resp"));
            db.Followups.Add(MakeFollowup("Person", "p3", f => { f.ResponsibleAgentId = "resp"; f.DueAt = Past; }));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetForRecordAsync("Person", "p3", Leader());

        var item = Assert.Single(result);
        Assert.True(item.Overdue); // open + due in the past
        Assert.Equal("Codename-resp", item.ResponsibleCodename);
        Assert.True(item.MayEdit); // leadership may edit any
    }

    [Fact]
    public async Task GetForRecordAsync_MayEdit_FalseForUnrelatedNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p4"));
            db.Followups.Add(MakeFollowup("Person", "p4", f => { f.CreatedById = "other"; f.ResponsibleAgentId = "other"; }));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        // visible junior who is neither creator nor responsible nor leadership
        var result = await svc.GetForRecordAsync("Person", "p4", Junior("me"));

        var item = Assert.Single(result);
        Assert.False(item.MayEdit);
    }

    // ---------- CreateAsync ----------

    [Fact]
    public async Task CreateAsync_Persists_WithCreatorAsDefaultResponsible_AndTrimmedNote()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p5"));
            db.SaveChanges();
        }
        var svc = Build(ctx);
        var due = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        await svc.CreateAsync("Person", "p5", new FollowupInput(due, "  Rueckruf  ", null), Junior("me"));

        using var check = ctx.NewContext();
        var stored = await check.Followups.SingleAsync(w => w.EntityId == "p5");
        Assert.Equal("Rueckruf", stored.Note);
        Assert.Equal("me", stored.ResponsibleAgentId); // null selection defaults to the creator
        Assert.Equal(due, stored.DueAt);
        Assert.False(stored.Done);
    }

    [Fact]
    public async Task CreateAsync_NotifiesMentions_GatedOnTheHostRecord()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p9"));
            db.SaveChanges();
        }
        var notifications = Substitute.For<INotificationService>();
        var svc = Build(ctx, notifications);
        var due = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

        await svc.CreateAsync("Person", "p9", new FollowupInput(due, "Notiz", null), Junior("me"));

        // host record, never the Followup itself: Visibility treats an unknown type as visible
        await notifications.Received(1).NotifyMentionedDeltaAsync(
            Arg.Is<string?>(s => s == null), Arg.Is<string?>(s => s == "Notiz"),
            Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Is<string>(t => t == "Person"), Arg.Is<string>(i => i == "p9"),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>(), "Followup");
    }

    [Fact]
    public async Task RefreshAsync_NotifiesOnlyTheDelta()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p10"));
            db.Followups.Add(MakeFollowup("Person", "p10", f =>
            {
                f.Id = "w10";
                f.Note = "alt";
                f.CreatedById = "me";
            }));
            db.SaveChanges();
        }
        var notifications = Substitute.For<INotificationService>();
        var svc = Build(ctx, notifications);

        await svc.RefreshAsync("w10", new FollowupInput(Future, "neu", null), Junior("me"));

        await notifications.Received(1).NotifyMentionedDeltaAsync(
            Arg.Is<string?>(s => s == "alt"), Arg.Is<string?>(s => s == "neu"),
            Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Is<string>(t => t == "Person"), Arg.Is<string>(i => i == "p10"),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>(), "Followup");
    }

    [Fact]
    public async Task CreateAsync_UsesProvidedResponsible_WhenActiveAgent()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p6"));
            db.Users.Add(Seed.Agent("resp", status: AgentStatus.Active));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        await svc.CreateAsync("Person", "p6", new FollowupInput(Future, null, "resp"), Junior("me"));

        using var check = ctx.NewContext();
        var stored = await check.Followups.SingleAsync(w => w.EntityId == "p6");
        Assert.Equal("resp", stored.ResponsibleAgentId);
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenResponsibleNotActive()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p7"));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        // "ghost" is not an active agent
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync("Person", "p7", new FollowupInput(Future, null, "ghost"), Junior("me")));

        using var check = ctx.NewContext();
        Assert.False(await check.Followups.AnyAsync(w => w.EntityId == "p7"));
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenRecordNotVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p8", configure: p => p.IsClassified = true));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        // classified record: junior cannot access it
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateAsync("Person", "p8", new FollowupInput(Future, "nope", null), Junior("me")));

        using var check = ctx.NewContext();
        Assert.False(await check.Followups.AnyAsync(w => w.EntityId == "p8"));
    }

    [Fact]
    public async Task CreateAsync_TrimsWhitespaceNoteToNull()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p9"));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        await svc.CreateAsync("Person", "p9", new FollowupInput(Future, "   ", null), Junior("me"));

        using var check = ctx.NewContext();
        var stored = await check.Followups.SingleAsync(w => w.EntityId == "p9");
        Assert.Null(stored.Note);
    }

    // ---------- RefreshAsync ----------

    [Fact]
    public async Task RefreshAsync_UpdatesFields_AndClearsNotifiedOnDateChange()
    {
        using var ctx = new SqliteTestContext();
        var f = MakeFollowup("Person", "p10", x =>
        {
            x.CreatedById = "me";
            x.DueAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            x.Note = "old";
            x.NotifiedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        });
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p10"));
            db.Followups.Add(f);
            db.SaveChanges();
        }
        var svc = Build(ctx);
        var newDue = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

        await svc.RefreshAsync(f.Id, new FollowupInput(newDue, "new", null), Junior("me"));

        using var check = ctx.NewContext();
        var stored = await check.Followups.SingleAsync(w => w.Id == f.Id);
        Assert.Equal(newDue, stored.DueAt);
        Assert.Equal("new", stored.Note);
        Assert.Null(stored.NotifiedAt); // moved due date re-arms notification
        Assert.Equal("me", stored.ResponsibleAgentId); // null selection defaults to the actor
    }

    [Fact]
    public async Task RefreshAsync_KeepsResponsible_WhenUnchangedAndAgentTerminated()
    {
        using var ctx = new SqliteTestContext();
        var f = MakeFollowup("Person", "p11", x =>
        {
            x.CreatedById = "me";
            x.DueAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            x.ResponsibleAgentId = "gone";
        });
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p11"));
            db.Users.Add(Seed.Agent("gone", status: AgentStatus.Terminated));
            db.Followups.Add(f);
            db.SaveChanges();
        }
        var svc = Build(ctx);
        var newDue = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);

        // re-saving a followup whose responsible has since left must not fail
        await svc.RefreshAsync(f.Id, new FollowupInput(newDue, null, "gone"), Junior("me"));

        using var check = ctx.NewContext();
        var stored = await check.Followups.SingleAsync(w => w.Id == f.Id);
        Assert.Equal(newDue, stored.DueAt);
        Assert.Equal("gone", stored.ResponsibleAgentId);
    }

    [Fact]
    public async Task RefreshAsync_NullResponsible_FallsBackToActor_WhichIsWhyTheDialogMustResendTheStoredId()
    {
        using var ctx = new SqliteTestContext();
        var f = MakeFollowup("Person", "p14", x =>
        {
            x.CreatedById = "me";
            x.ResponsibleAgentId = "gone";
        });
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p14"));
            db.Users.Add(Seed.Agent("gone", status: AgentStatus.Terminated));
            db.Followups.Add(f);
            db.SaveChanges();
        }
        var svc = Build(ctx);

        // Pins the contract FollowupDialog depends on: a null pick means "me", so a dialog that fails to
        // resolve an unselectable stored responsible would silently steal the assignment. The dialog
        // therefore falls back to FindAsync instead of sending null.
        await svc.RefreshAsync(f.Id, new FollowupInput(Future, null, null), Junior("me"));

        using var check = ctx.NewContext();
        Assert.Equal("me", (await check.Followups.SingleAsync(w => w.Id == f.Id)).ResponsibleAgentId);
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenNewResponsibleIsPartner()
    {
        using var ctx = new SqliteTestContext();
        var f = MakeFollowup("Person", "p12", x =>
        {
            x.CreatedById = "me";
            x.ResponsibleAgentId = "me";
        });
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p12"));
            db.Users.Add(Seed.Agent("partner", configure: a => a.PartnerAgency = PartnerAgency.LSPD));
            db.Followups.Add(f);
            db.SaveChanges();
        }
        var svc = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshAsync(f.Id, new FollowupInput(Future, null, "partner"), Junior("me")));
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenResponsibleIsTeamLead()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p13"));
            db.Users.Add(Seed.Agent("tl", configure: a => a.IsTeamLead = true));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync("Person", "p13", new FollowupInput(Future, null, "tl"), Junior("me")));
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenNotFound()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshAsync("missing", new FollowupInput(Future, null, null), Leader()));
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenNotAuthorized()
    {
        using var ctx = new SqliteTestContext();
        var f = MakeFollowup("Person", "p11", x => { x.CreatedById = "other"; x.ResponsibleAgentId = "other"; });
        using (var db = ctx.NewContext())
        {
            db.Followups.Add(f);
            db.SaveChanges();
        }
        var svc = Build(ctx);

        // junior "me" is neither creator, responsible, nor leadership
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RefreshAsync(f.Id, new FollowupInput(Future, "x", null), Junior("me")));
    }

    // ---------- CompleteAsync ----------

    [Fact]
    public async Task CompleteAsync_MarksDone_AndStampsDoneBy()
    {
        using var ctx = new SqliteTestContext();
        var f = MakeFollowup("Person", "p12", x => x.CreatedById = "me");
        using (var db = ctx.NewContext())
        {
            db.Followups.Add(f);
            db.SaveChanges();
        }
        var svc = Build(ctx);

        // creator may complete their own followup
        await svc.CompleteAsync(f.Id, Junior("me"));

        using var check = ctx.NewContext();
        var stored = await check.Followups.SingleAsync(w => w.Id == f.Id);
        Assert.True(stored.Done);
        Assert.NotNull(stored.DoneAt);
        Assert.Equal("me", stored.DoneById);
    }

    [Fact]
    public async Task CompleteAsync_Throws_WhenNotAuthorized()
    {
        using var ctx = new SqliteTestContext();
        var f = MakeFollowup("Person", "p13", x => x.CreatedById = "other");
        using (var db = ctx.NewContext())
        {
            db.Followups.Add(f);
            db.SaveChanges();
        }
        var svc = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CompleteAsync(f.Id, Junior("me")));

        using var check = ctx.NewContext();
        Assert.False((await check.Followups.SingleAsync(w => w.Id == f.Id)).Done);
    }

    // ---------- ReopenAsync ----------

    [Fact]
    public async Task ReopenAsync_ClearsDone_AndReArmsNotification()
    {
        using var ctx = new SqliteTestContext();
        var f = MakeFollowup("Person", "p14", x =>
        {
            x.CreatedById = "me";
            x.Done = true;
            x.DoneAt = Past;
            x.DoneById = "someone";
            x.NotifiedAt = Past;
        });
        using (var db = ctx.NewContext())
        {
            db.Followups.Add(f);
            db.SaveChanges();
        }
        var svc = Build(ctx);

        await svc.ReopenAsync(f.Id, Junior("me"));

        using var check = ctx.NewContext();
        var stored = await check.Followups.SingleAsync(w => w.Id == f.Id);
        Assert.False(stored.Done);
        Assert.Null(stored.DoneAt);
        Assert.Null(stored.DoneById);
        Assert.Null(stored.NotifiedAt);
    }

    // ---------- DeleteAsync ----------

    [Fact]
    public async Task DeleteAsync_RemovesRow_WhenCreator()
    {
        using var ctx = new SqliteTestContext();
        var f = MakeFollowup("Person", "p15", x => x.CreatedById = "me");
        using (var db = ctx.NewContext())
        {
            db.Followups.Add(f);
            db.SaveChanges();
        }
        var svc = Build(ctx);

        await svc.DeleteAsync(f.Id, Junior("me"));

        using var check = ctx.NewContext();
        // no soft-delete interceptor in tests => hard delete, row gone from the filtered set
        Assert.False(await check.Followups.AnyAsync(w => w.Id == f.Id));
    }

    [Fact]
    public async Task DeleteAsync_Throws_WhenResponsibleButNotCreator()
    {
        using var ctx = new SqliteTestContext();
        // responsible may edit, but delete is reserved to creator or leadership
        var f = MakeFollowup("Person", "p16", x => { x.CreatedById = "other"; x.ResponsibleAgentId = "me"; });
        using (var db = ctx.NewContext())
        {
            db.Followups.Add(f);
            db.SaveChanges();
        }
        var svc = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.DeleteAsync(f.Id, Junior("me")));

        using var check = ctx.NewContext();
        Assert.True(await check.Followups.AnyAsync(w => w.Id == f.Id));
    }

    [Fact]
    public async Task DeleteAsync_Throws_WhenNotFound()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DeleteAsync("missing", Leader()));
    }

    // ---------- GetMyDueAsync ----------

    [Fact]
    public async Task GetMyDueAsync_ReturnsDue_WhereResponsible()
    {
        using var ctx = new SqliteTestContext();
        var person = Seed.Person("p17", "Max Mustermann");
        using (var db = ctx.NewContext())
        {
            db.People.Add(person);
            db.Followups.Add(MakeFollowup("Person", "p17", f => { f.ResponsibleAgentId = "me"; f.DueAt = Past; f.Note = "ruf an"; }));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetMyDueAsync(Junior("me"));

        var item = Assert.Single(result);
        Assert.Equal($"{person.Name} ({person.CaseNumber})", item.Display);
        Assert.Equal("ruf an", item.Note);
        Assert.NotNull(item.Href);
    }

    [Fact]
    public async Task GetMyDueAsync_ReturnsDue_WhereWatching()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p18"));
            // responsible is someone else, but I follow the record
            db.Followups.Add(MakeFollowup("Person", "p18", f => { f.ResponsibleAgentId = "other"; f.DueAt = Past; }));
            db.Watchlists.Add(new WatchlistEntry { AgentId = "me", EntityType = "Person", EntityId = "p18" });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetMyDueAsync(Junior("me"));

        Assert.Single(result);
    }

    [Fact]
    public async Task GetMyDueAsync_ExcludesDone_Future_AndUnrelated()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p19"));
            db.Followups.Add(MakeFollowup("Person", "p19", f => { f.ResponsibleAgentId = "me"; f.DueAt = Past; f.Note = "due"; }));
            db.Followups.Add(MakeFollowup("Person", "p19", f => { f.ResponsibleAgentId = "me"; f.DueAt = Past; f.Done = true; f.Note = "done"; }));
            db.Followups.Add(MakeFollowup("Person", "p19", f => { f.ResponsibleAgentId = "me"; f.DueAt = Future; f.Note = "future"; }));
            db.Followups.Add(MakeFollowup("Person", "p19", f => { f.ResponsibleAgentId = "other"; f.DueAt = Past; f.Note = "other"; }));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetMyDueAsync(Junior("me"));

        var item = Assert.Single(result);
        Assert.Equal("due", item.Note);
    }

    [Fact]
    public async Task GetMyDueAsync_HidesClassified_FromNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p20", configure: p => p.IsClassified = true));
            db.Followups.Add(MakeFollowup("Person", "p20", f => { f.ResponsibleAgentId = "me"; f.DueAt = Past; }));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        // record resolves as classified; junior cannot read classified -> hidden
        var result = await svc.GetMyDueAsync(Junior("me"));

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetMyDueAsync_ShowsClassified_ToLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p21", configure: p => p.IsClassified = true));
            db.Followups.Add(MakeFollowup("Person", "p21", f => { f.ResponsibleAgentId = "lead"; f.DueAt = Past; }));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetMyDueAsync(Leader("lead"));

        Assert.Single(result);
    }

    [Fact]
    public async Task GetMyDueAsync_ReturnsEmpty_WhenNoAgentId()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        var result = await svc.GetMyDueAsync(ClaimsPrincipalBuilder.Anonymous());

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetMyDueAsync_ExcludesTrashedOrUnknownRecord()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            // followup points at a person that does not exist -> unresolved -> skipped
            db.Followups.Add(MakeFollowup("Person", "ghost", f => { f.ResponsibleAgentId = "me"; f.DueAt = Past; }));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetMyDueAsync(Junior("me"));

        Assert.Empty(result);
    }

    // ---------- tickets as a followup target ----------

    private static Ticket MakeTicket(string id = "t1") => new()
    {
        Id = id,
        CaseNumber = "NOOSE-T-2026-0001",
        Subject = "Anliegen einer Bürgerin",
        Kind = TicketArt.Intern,
        Status = TicketStatus.Offen,
        LastActivityAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public async Task GetForRecordAsync_ShowsATicketFollowupToTheDesk()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Tickets.Add(MakeTicket());
            db.Followups.Add(MakeFollowup(nameof(Ticket), "t1", f => f.Note = "nachfassen"));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetForRecordAsync(nameof(Ticket), "t1", Leader());

        Assert.Equal("nachfassen", Assert.Single(result).Note);
    }

    [Fact]
    public async Task GetForRecordAsync_HidesATicketFollowupFromAnUninvolvedAgent()
    {
        // the ticket gate is the desk or the one attached agent; a bystander must not learn the row exists
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Tickets.Add(MakeTicket());
            db.Followups.Add(MakeFollowup(nameof(Ticket), "t1", f => f.Note = "nachfassen"));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        Assert.Empty(await svc.GetForRecordAsync(nameof(Ticket), "t1", Junior()));
    }

    [Fact]
    public async Task GetForRecordAsync_ShowsATicketFollowupToAnAttachedAgent()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Tickets.Add(MakeTicket());
            db.TicketBeteiligte.Add(new TicketParticipant { TicketId = "t1", AgentId = "me" });
            db.Followups.Add(MakeFollowup(nameof(Ticket), "t1", f => f.Note = "nachfassen"));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        Assert.Single(await svc.GetForRecordAsync(nameof(Ticket), "t1", Junior("me")));
    }

    [Fact]
    public async Task CreateAsync_RefusesAFollowupOnATicketTheActorMayNotOpen()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Tickets.Add(MakeTicket());
            db.SaveChanges();
        }
        var svc = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateAsync(nameof(Ticket), "t1",
            new FollowupInput(Future, "nachfassen", null), Junior()));
    }
}
