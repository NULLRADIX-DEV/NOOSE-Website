using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Models.Cases;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="CaseService"/> against in-memory SQLite.</summary>
public sealed class CaseServiceTests
{
    // ---------- construction / helpers ----------

    private static CaseService Build(SqliteTestContext ctx, INotificationService? notifications = null)
    {
        var caseNo = Substitute.For<ICaseNumberService>();
        caseNo.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("FIB-V-2026-0001");
        var suggestion = Substitute.For<IProfileSuggestionService>();
        return new CaseService(ctx.Factory, caseNo, suggestion,
            notifications ?? Substitute.For<INotificationService>());
    }

    // Director => IsLeadership() and MayHighestClassification().
    private static ClaimsPrincipal Director(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).Build();

    // Trainee => neither leadership nor highest-classification.
    private static ClaimsPrincipal Junior(string id = "junior")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).Build();

    private static CaseInput Input(
        string title = "Ermittlung",
        CaseStatus status = CaseStatus.Open,
        Classification classification = Classification.Unknown,
        DocumentClassification secrecy = DocumentClassification.None,
        string? type = null,
        string? justification = null)
        => new()
        {
            Title = title,
            Status = status,
            Classification = classification,
            SecrecyLevel = secrecy,
            Type = type,
            ClassificationJustification = justification,
        };

    private static Case NewCase(
        string id,
        string title = "Ermittlung",
        DocumentClassification secrecy = DocumentClassification.None,
        Classification classification = Classification.Unknown,
        CaseStatus status = CaseStatus.Open,
        Action<Case>? configure = null)
        => Seed.Case(id, title, c =>
        {
            c.CaseNumber = $"FIB-V-2026-{id}";
            c.SecrecyLevel = secrecy;
            c.Classification = classification;
            c.Status = status;
            configure?.Invoke(c);
        });

    // ---------- CreateAsync ----------

    [Fact]
    public async Task CreateAsync_PersistsCase_AndAutoAssignsCreatorAsCaseLead()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("lead"));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var created = await svc.CreateAsync(
            Input(title: "  Fall X  ", type: "Betrug"), Director("lead"));

        Assert.Equal("FIB-V-2026-0001", created.CaseNumber);
        Assert.Equal("Fall X", created.Title);
        Assert.Equal("Betrug", created.Type);

        using var check = ctx.NewContext();
        var stored = await check.Cases.SingleAsync(c => c.Id == created.Id);
        Assert.Equal("Fall X", stored.Title);
        // creator auto-assigned as case lead.
        var lead = await check.CaseAgents.SingleAsync(a => a.CaseId == created.Id);
        Assert.Equal("lead", lead.AgentId);
        Assert.True(lead.IsCaseLead);
        // Unknown classification => no history entry.
        Assert.False(await check.ClassificationHistory.AnyAsync(e => e.EntityId == created.Id));
    }

    [Fact]
    public async Task CreateAsync_WithClassification_WritesHistory()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("lead"));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var created = await svc.CreateAsync(
            Input(classification: Classification.ReviewCase, justification: "  Grund  "), Director("lead"));

        Assert.Equal(Classification.ReviewCase, created.Classification);
        using var check = ctx.NewContext();
        var entry = await check.ClassificationHistory
            .SingleAsync(e => e.EntityType == nameof(Case) && e.EntityId == created.Id);
        Assert.Equal(Classification.ReviewCase, entry.Value);
        Assert.Equal("Grund", entry.Justification);
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenAssigningSecrecyWithoutPermission()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        // Junior may not assign the Leadership secrecy level.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateAsync(Input(secrecy: DocumentClassification.Leadership), Junior()));
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenSettingHighestClassificationBelowRank()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        // SpecialAgent (rank 2) lacks MayHighestClassification => rank gate rejects the highest level.
        var actor = ClaimsPrincipalBuilder.Agent("sa").WithRank(Rank.SpecialAgent).Build();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync(Input(classification: Classification.SecuredStateThreatening), actor));
    }

    // ---------- RefreshAsync ----------

    [Fact]
    public async Task RefreshAsync_NotifiesMentionDelta_WithOldAndNewBodyText()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Cases.Add(NewCase("c9", configure: c => c.Description = "Sachverhalt alt"));
            db.SaveChanges();
        }
        var notifications = Substitute.For<INotificationService>();
        var svc = Build(ctx, notifications);

        var input = Input(title: "Neu");
        input.Description = "Sachverhalt neu";
        await svc.RefreshAsync("c9", input, Director());

        // old text carries the previous body so already-pinged agents are not pinged again
        await notifications.Received(1).NotifyMentionedDeltaAsync(
            Arg.Is<string?>(s => s != null && s.Contains("Sachverhalt alt")),
            Arg.Is<string?>(s => s != null && s.Contains("Sachverhalt neu")),
            Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Is<string>(t => t == "Case"), Arg.Is<string>(i => i == "c9"),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshAsync_UpdatesFields_AndSetsCompletedAt()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Cases.Add(NewCase("c1", "Alt", status: CaseStatus.Open));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        await svc.RefreshAsync("c1", Input(title: "Neu", status: CaseStatus.Completed), Director());

        using var check = ctx.NewContext();
        var stored = await check.Cases.SingleAsync(c => c.Id == "c1");
        Assert.Equal("Neu", stored.Title);
        Assert.Equal(CaseStatus.Completed, stored.Status);
        Assert.NotNull(stored.CompletedAt);
    }

    [Fact]
    public async Task RefreshAsync_ClearsCompletedAt_WhenReopened()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Cases.Add(NewCase("c1", status: CaseStatus.Completed,
                configure: c => c.CompletedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        await svc.RefreshAsync("c1", Input(status: CaseStatus.Open), Director());

        using var check = ctx.NewContext();
        var stored = await check.Cases.SingleAsync(c => c.Id == "c1");
        Assert.Null(stored.CompletedAt);
    }

    [Fact]
    public async Task RefreshAsync_Throws_OnUnknownCase()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshAsync("missing", Input(), Director()));
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenActorCannotSeeClassifiedCase()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Cases.Add(NewCase("c1", secrecy: DocumentClassification.Leadership));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RefreshAsync("c1", Input(), Junior()));
    }

    // ---------- DeleteAsync ----------

    [Fact]
    public async Task DeleteAsync_RemovesCase_WhenLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Cases.Add(NewCase("c1"));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        await svc.DeleteAsync("c1", Director());

        // No soft-delete interceptor in the test context => the row is hard-deleted.
        using var check = ctx.NewContext();
        Assert.False(await check.Cases.IgnoreQueryFilters().AnyAsync(c => c.Id == "c1"));
    }

    [Fact]
    public async Task DeleteAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.DeleteAsync("any", Junior()));
    }

    [Fact]
    public async Task DeleteAsync_Throws_OnUnknownCase()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DeleteAsync("missing", Director()));
    }

    // ---------- RestoreAsync ----------

    [Fact]
    public async Task RestoreAsync_ClearsSoftDeleteFlags()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Cases.Add(NewCase("c1", configure: c =>
            {
                c.IsDeleted = true;
                c.DeletedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
                c.DeletedById = "someone";
            }));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        await svc.RestoreAsync("c1", Director());

        using var check = ctx.NewContext();
        // Restored row reappears in the filtered set.
        var stored = await check.Cases.SingleAsync(c => c.Id == "c1");
        Assert.False(stored.IsDeleted);
        Assert.Null(stored.DeletedAt);
        Assert.Null(stored.DeletedById);
    }

    [Fact]
    public async Task RestoreAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RestoreAsync("any", Junior()));
    }

    // ---------- ClassificationSetAsync ----------

    [Fact]
    public async Task ClassificationSetAsync_UpdatesAndWritesHistory()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Cases.Add(NewCase("c1"));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        await svc.ClassificationSetAsync("c1", Classification.SuspicionCase, "weil", Director());

        using var check = ctx.NewContext();
        var stored = await check.Cases.SingleAsync(c => c.Id == "c1");
        Assert.Equal(Classification.SuspicionCase, stored.Classification);
        var entry = await check.ClassificationHistory
            .SingleAsync(e => e.EntityType == nameof(Case) && e.EntityId == "c1");
        Assert.Equal(Classification.SuspicionCase, entry.Value);
        Assert.Equal("weil", entry.Justification);
    }

    [Fact]
    public async Task ClassificationSetAsync_Throws_WhenHighestClassificationBelowRank()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Cases.Add(NewCase("c1"));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        // Rank gate runs before the record is even loaded.
        var actor = ClaimsPrincipalBuilder.Agent("sa").WithRank(Rank.SpecialAgent).Build();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ClassificationSetAsync("c1", Classification.SecuredStateThreatening, null, actor));
    }

    [Fact]
    public async Task ClassificationSetAsync_Throws_WhenActorCannotSeeClassifiedCase()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Cases.Add(NewCase("c1", secrecy: DocumentClassification.Leadership));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        // ReviewCase passes the rank gate, but the leadership-only case is not visible to a junior.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.ClassificationSetAsync("c1", Classification.ReviewCase, null, Junior()));
    }

    // ---------- GetListAsync ----------

    [Fact]
    public async Task GetListAsync_FiltersClassified_ForNonPrivilegedViewer()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Cases.Add(NewCase("open", "Offen"));
            db.Cases.Add(NewCase("secret", "Geheim", secrecy: DocumentClassification.Leadership));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetListAsync(ViewerScope.From(Junior()));

        Assert.Single(result);
        Assert.Equal("open", result[0].Id);
    }

    [Fact]
    public async Task GetListAsync_ReturnsAll_ForLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Cases.Add(NewCase("open", "Offen"));
            db.Cases.Add(NewCase("secret", "Geheim", secrecy: DocumentClassification.Leadership));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetListAsync(ViewerScope.From(Director()));

        Assert.Equal(2, result.Count);
    }

    // ---------- GetDetailAsync ----------

    [Fact]
    public async Task GetDetailAsync_ReturnsCase_WhenVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Cases.Add(NewCase("c1", "Sichtbar"));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetDetailAsync("c1", ViewerScope.From(Junior()));

        Assert.NotNull(result);
        Assert.Equal("Sichtbar", result!.Title);
    }

    [Fact]
    public async Task GetDetailAsync_ReturnsNull_WhenClassifiedAndNotVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Cases.Add(NewCase("c1", secrecy: DocumentClassification.Leadership));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetDetailAsync("c1", ViewerScope.From(Junior()));

        Assert.Null(result);
    }

    [Fact]
    public async Task GetDetailAsync_ReturnsNull_WhenMissing()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        Assert.Null(await svc.GetDetailAsync("missing", ViewerScope.From(Director())));
    }

    // ---------- GetTrashAsync ----------

    [Fact]
    public async Task GetTrashAsync_ReturnsSoftDeletedOnly()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Cases.Add(NewCase("live", "Aktiv"));
            db.Cases.Add(NewCase("dead", "Papierkorb", configure: c =>
            {
                c.IsDeleted = true;
                c.DeletedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
            }));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetTrashAsync();

        Assert.Single(result);
        Assert.Equal("dead", result[0].Id);
    }

    // ---------- SearchAsync ----------

    [Fact]
    public async Task SearchAsync_ScopeOverload_FiltersByTextAndClassification()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Cases.Add(NewCase("a", "Alpha Fall"));
            db.Cases.Add(NewCase("b", "Beta Fall", secrecy: DocumentClassification.Leadership));
            db.Cases.Add(NewCase("g", "Gamma"));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.SearchAsync("Fall", ViewerScope.From(Junior()));

        // "Beta Fall" is classified (hidden), "Gamma" lacks the term => only "Alpha Fall".
        Assert.Single(result);
        Assert.Equal("Alpha Fall", result[0].Title);
    }

    [Fact]
    public async Task SearchAsync_BoolOverload_LeadershipSeesClassified()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Cases.Add(NewCase("s", "Geheim", secrecy: DocumentClassification.Leadership));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.SearchAsync("Geheim", isLeadership: true);

        Assert.Single(result);
        Assert.Equal("s", result[0].Id);
    }

    [Fact]
    public async Task SearchAsync_BoolOverload_NonLeadershipHidesClassified()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Cases.Add(NewCase("s", "Geheim", secrecy: DocumentClassification.Leadership));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.SearchAsync("Geheim", isLeadership: false);

        Assert.Empty(result);
    }

    [Fact]
    public async Task SearchAsync_TruViewer_SeesTruCase_ButNotLeadershipCase()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Cases.Add(NewCase("tru", "TRU Sache", secrecy: DocumentClassification.Tru));
            db.Cases.Add(NewCase("ldr", "Chef Sache", secrecy: DocumentClassification.Leadership));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var truScope = ViewerScope.From(
            ClaimsPrincipalBuilder.Agent("t").WithRank(Rank.Trainee).AsTru().Build());
        var result = await svc.SearchAsync("Sache", truScope);

        Assert.Single(result);
        Assert.Equal("tru", result[0].Id);
    }

    // ---------- GetClassificationHistoryAsync ----------

    [Fact]
    public async Task GetClassificationHistoryAsync_ReturnsHistory_WhenVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Cases.Add(NewCase("c1"));
            db.ClassificationHistory.Add(new ClassificationHistory
            {
                EntityType = nameof(Case),
                EntityId = "c1",
                Value = Classification.ReviewCase,
                Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.ClassificationHistory.Add(new ClassificationHistory
            {
                EntityType = nameof(Case),
                EntityId = "c1",
                Value = Classification.SuspicionCase,
                Timestamp = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetClassificationHistoryAsync("c1", ViewerScope.From(Junior()));

        Assert.Equal(2, result.Count);
        // Newest first.
        Assert.Equal(Classification.SuspicionCase, result[0].Value);
    }

    [Fact]
    public async Task GetClassificationHistoryAsync_ReturnsEmpty_WhenNotVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Cases.Add(NewCase("c1", secrecy: DocumentClassification.Leadership));
            db.ClassificationHistory.Add(new ClassificationHistory
            {
                EntityType = nameof(Case),
                EntityId = "c1",
                Value = Classification.ReviewCase,
                Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetClassificationHistoryAsync("c1", ViewerScope.From(Junior()));

        Assert.Empty(result);
    }

    // ---------- GetAgentsAsync / GetCaseLeadAsync ----------

    [Fact]
    public async Task GetAgentsAsync_ReturnsAgents_CaseLeadFirst()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", configure: a => a.Codename = "Zulu"));
            db.Users.Add(Seed.Agent("a2", configure: a => a.Codename = "Alpha"));
            db.Cases.Add(NewCase("c1"));
            db.CaseAgents.Add(new CaseAgent { CaseId = "c1", AgentId = "a1", IsCaseLead = true });
            db.CaseAgents.Add(new CaseAgent { CaseId = "c1", AgentId = "a2", IsCaseLead = false });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetAgentsAsync("c1");

        // Case lead first, then remaining by codename.
        Assert.Equal(new[] { "a1", "a2" }, result.Select(a => a.AgentId).ToArray());
        Assert.NotNull(result[0].Agent);
    }

    [Fact]
    public async Task GetCaseLeadAsync_ReturnsOnlyLeads()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", configure: a => a.Codename = "Zulu"));
            db.Users.Add(Seed.Agent("a2", configure: a => a.Codename = "Alpha"));
            db.Cases.Add(NewCase("c1"));
            db.CaseAgents.Add(new CaseAgent { CaseId = "c1", AgentId = "a1", IsCaseLead = true });
            db.CaseAgents.Add(new CaseAgent { CaseId = "c1", AgentId = "a2", IsCaseLead = false });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetCaseLeadAsync("c1");

        Assert.Single(result);
        Assert.Equal("a1", result[0].AgentId);
    }

    // ---------- AgentAllocateAsync ----------

    [Fact]
    public async Task AgentAllocateAsync_AddsAssignment_WhenLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a2"));
            db.Cases.Add(NewCase("c1"));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        await svc.AgentAllocateAsync("c1", "a2", asCaseLead: false, Director());

        using var check = ctx.NewContext();
        var alloc = await check.CaseAgents.SingleAsync(a => a.CaseId == "c1" && a.AgentId == "a2");
        Assert.False(alloc.IsCaseLead);
    }

    [Fact]
    public async Task AgentAllocateAsync_AllowsCaseLead_ToAssignNonLead()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("ff"));
            db.Users.Add(Seed.Agent("a2"));
            db.Cases.Add(NewCase("c1"));
            // "ff" is a case lead of this case (a junior, not leadership).
            db.CaseAgents.Add(new CaseAgent { CaseId = "c1", AgentId = "ff", IsCaseLead = true });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        await svc.AgentAllocateAsync("c1", "a2", asCaseLead: false, Junior("ff"));

        using var check = ctx.NewContext();
        Assert.True(await check.CaseAgents.AnyAsync(a => a.CaseId == "c1" && a.AgentId == "a2"));
    }

    [Fact]
    public async Task AgentAllocateAsync_Throws_WhenCaseLeadTriesToGrantLead()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("ff"));
            db.Users.Add(Seed.Agent("a2"));
            db.Cases.Add(NewCase("c1"));
            db.CaseAgents.Add(new CaseAgent { CaseId = "c1", AgentId = "ff", IsCaseLead = true });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        // A case lead may assign, but only leadership may grant the case-lead flag.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.AgentAllocateAsync("c1", "a2", asCaseLead: true, Junior("ff")));
    }

    [Fact]
    public async Task AgentAllocateAsync_Throws_WhenNotLeadershipOrCaseLead()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a2"));
            db.Cases.Add(NewCase("c1"));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.AgentAllocateAsync("c1", "a2", asCaseLead: false, Junior()));
    }

    [Fact]
    public async Task AgentAllocateAsync_Throws_WhenActorCannotSeeClassifiedCase()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a2"));
            db.Cases.Add(NewCase("c1", secrecy: DocumentClassification.Leadership));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.AgentAllocateAsync("c1", "a2", asCaseLead: false, Junior()));
    }

    [Fact]
    public async Task AgentAllocateAsync_Throws_WhenCaseMissing()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AgentAllocateAsync("missing", "a2", asCaseLead: false, Director()));
    }

    [Fact]
    public async Task AgentAllocateAsync_Throws_WhenAgentMissing()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Cases.Add(NewCase("c1"));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AgentAllocateAsync("c1", "ghost", asCaseLead: false, Director()));
    }

    [Theory]
    [InlineData("teamlead")]
    [InlineData("teamlead-admin")]
    [InlineData("partner")]
    [InlineData("terminated")]
    public async Task AgentAllocateAsync_Throws_WhenAgentNotSelectable(string id)
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(NotSelectable(id));
            db.Cases.Add(NewCase("c1"));
            db.SaveChanges();
        }
        var svc = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AgentAllocateAsync("c1", id, asCaseLead: false, Director()));
    }

    /// <summary>An agent shape no picker offers; the service must reject it too.</summary>
    private static NOOSE_Website.Data.Entities.Agent NotSelectable(string id) => id switch
    {
        "teamlead" => Seed.Agent(id, configure: a => a.IsTeamLead = true),
        "teamlead-admin" => Seed.Agent(id, configure: a => { a.IsTeamLead = true; a.IsAdmin = true; }),
        "partner" => Seed.Agent(id, configure: a => a.PartnerAgency = PartnerAgency.LSPD),
        _ => Seed.Agent(id, status: AgentStatus.Terminated),
    };

    [Fact]
    public async Task AgentAllocateAsync_Throws_WhenAlreadyAssigned()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a2"));
            db.Cases.Add(NewCase("c1"));
            db.CaseAgents.Add(new CaseAgent { CaseId = "c1", AgentId = "a2", IsCaseLead = false });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AgentAllocateAsync("c1", "a2", asCaseLead: false, Director()));
    }

    // ---------- AgentRemoveAsync ----------

    [Fact]
    public async Task AgentRemoveAsync_RemovesAssignment_WhenLeadership()
    {
        using var ctx = new SqliteTestContext();
        var allocId = Guid.NewGuid().ToString();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a2"));
            db.Cases.Add(NewCase("c1"));
            db.CaseAgents.Add(new CaseAgent { Id = allocId, CaseId = "c1", AgentId = "a2" });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        await svc.AgentRemoveAsync(allocId, Director());

        using var check = ctx.NewContext();
        Assert.False(await check.CaseAgents.AnyAsync(a => a.Id == allocId));
    }

    [Fact]
    public async Task AgentRemoveAsync_NoOp_WhenAllocationMissing()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        // Returns without throwing when the allocation does not exist.
        await svc.AgentRemoveAsync("missing", Director());

        using var check = ctx.NewContext();
        Assert.Equal(0, await check.CaseAgents.CountAsync());
    }

    [Fact]
    public async Task AgentRemoveAsync_Throws_WhenNotLeadershipOrCaseLead()
    {
        using var ctx = new SqliteTestContext();
        var allocId = Guid.NewGuid().ToString();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a2"));
            db.Cases.Add(NewCase("c1"));
            db.CaseAgents.Add(new CaseAgent { Id = allocId, CaseId = "c1", AgentId = "a2" });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.AgentRemoveAsync(allocId, Junior()));
    }

    // ---------- CaseLeadSetAsync ----------

    [Fact]
    public async Task CaseLeadSetAsync_SetsFlag_WhenLeadership()
    {
        using var ctx = new SqliteTestContext();
        var allocId = Guid.NewGuid().ToString();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a2"));
            db.Cases.Add(NewCase("c1"));
            db.CaseAgents.Add(new CaseAgent { Id = allocId, CaseId = "c1", AgentId = "a2", IsCaseLead = false });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        await svc.CaseLeadSetAsync(allocId, true, Director());

        using var check = ctx.NewContext();
        var alloc = await check.CaseAgents.SingleAsync(a => a.Id == allocId);
        Assert.True(alloc.IsCaseLead);
    }

    [Fact]
    public async Task CaseLeadSetAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CaseLeadSetAsync("any", true, Junior()));
    }

    [Fact]
    public async Task CaseLeadSetAsync_Throws_WhenAllocationMissing()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CaseLeadSetAsync("missing", true, Director()));
    }

    // ---------- GetHistoryAsync ----------

    [Fact]
    public async Task GetHistoryAsync_ReturnsAuditEntries_WhenVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Cases.Add(NewCase("c1"));
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(Case),
                EntityId = "c1",
                Timestamp = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc),
            });
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(Case),
                EntityId = "c1",
                Timestamp = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
            });
            // Unrelated entry for a different entity type must be excluded.
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(Person),
                EntityId = "c1",
                Timestamp = new DateTime(2026, 1, 4, 0, 0, 0, DateTimeKind.Utc),
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetHistoryAsync("c1", isLeadership: false);

        Assert.Equal(2, result.Count);
        Assert.All(result, a => Assert.Equal(nameof(Case), a.EntityType));
        // Newest first.
        Assert.Equal(new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc), result[0].Timestamp);
    }

    [Fact]
    public async Task GetHistoryAsync_ReturnsEmpty_WhenClassifiedAndNotVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Cases.Add(NewCase("c1", secrecy: DocumentClassification.Leadership));
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(Case),
                EntityId = "c1",
                Timestamp = new DateTime(2026, 1, 5, 0, 0, 0, DateTimeKind.Utc),
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetHistoryAsync("c1", isLeadership: false);

        Assert.Empty(result);
    }
}
