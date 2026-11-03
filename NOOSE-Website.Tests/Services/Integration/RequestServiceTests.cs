using NSubstitute;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Requests;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="RequestService"/> against in-memory SQLite.</summary>
public sealed class RequestServiceTests
{
    private static RequestService CreateService(SqliteTestContext ctx, INotificationService? notifications = null)
        => new(ctx.Factory, notifications ?? Substitute.For<INotificationService>());

    private static Request SeedRequest(SqliteTestContext ctx, RequestType type, RequestStatus status,
        string targetType, string targetId, string? createdById = null, DateTime? createdAt = null,
        Action<Request>? configure = null)
    {
        var request = new Request
        {
            Type = type,
            Status = status,
            TargetType = targetType,
            TargetId = targetId,
            TargetDesignation = "Ziel",
            TargetClassification = Classification.SecuredStateThreatening,
            Justification = "Grund",
            CreatedById = createdById,
            CreatedAt = createdAt ?? new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        configure?.Invoke(request);
        using var db = ctx.NewContext();
        db.Requests.Add(request);
        db.SaveChanges();
        return request;
    }

    // ---- HasOpenRequestAsync ----

    [Fact]
    public async Task HasOpenRequestAsync_ReturnsTrue_WhenOpenRequestExists()
    {
        using var ctx = new SqliteTestContext();
        SeedRequest(ctx, RequestType.Upgrade, RequestStatus.Requested, nameof(Person), "p1");
        var service = CreateService(ctx);

        var result = await service.HasOpenRequestAsync(nameof(Person), "p1");

        Assert.True(result);
    }

    [Fact]
    public async Task HasOpenRequestAsync_ReturnsFalse_WhenOnlyDecidedRequestExists()
    {
        using var ctx = new SqliteTestContext();
        SeedRequest(ctx, RequestType.Upgrade, RequestStatus.Approved, nameof(Person), "p1");
        var service = CreateService(ctx);

        var result = await service.HasOpenRequestAsync(nameof(Person), "p1");

        Assert.False(result);
    }

    // ---- UpgradeRequestAsync ----

    [Fact]
    public async Task UpgradeRequestAsync_CreatesRequest_ForVisibleTarget()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1"));
            db.SaveChanges();
        }
        var service = CreateService(ctx);
        var actor = ClaimsPrincipalBuilder.Agent("a1").WithCodename("Falcon").Build();

        await service.UpgradeRequestAsync(nameof(Person), "p1", "Max Mustermann",
            Classification.SecuredStateThreatening, "  Gefahr im Verzug  ", actor);

        using var check = ctx.NewContext();
        var row = Assert.Single(check.Requests.ToList());
        Assert.Equal(RequestType.Upgrade, row.Type);
        Assert.Equal(RequestStatus.Requested, row.Status);
        Assert.Equal(nameof(Person), row.TargetType);
        Assert.Equal("p1", row.TargetId);
        Assert.Equal(Classification.SecuredStateThreatening, row.TargetClassification);
        Assert.Equal("Gefahr im Verzug", row.Justification);
        Assert.Equal("Falcon", row.RequesterName);
    }

    [Fact]
    public async Task UpgradeRequestAsync_Throws_WhenTargetNotSecuredStateThreatening()
    {
        using var ctx = new SqliteTestContext();
        var service = CreateService(ctx);
        var actor = ClaimsPrincipalBuilder.Agent("a1").Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpgradeRequestAsync(nameof(Person), "p1", "Max",
                Classification.SuspicionCase, "Grund", actor));
    }

    [Fact]
    public async Task UpgradeRequestAsync_Throws_WhenJustificationBlank()
    {
        using var ctx = new SqliteTestContext();
        var service = CreateService(ctx);
        var actor = ClaimsPrincipalBuilder.Agent("a1").Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpgradeRequestAsync(nameof(Person), "p1", "Max",
                Classification.SecuredStateThreatening, "   ", actor));
    }

    [Fact]
    public async Task UpgradeRequestAsync_Throws_WhenTargetNotVisible()
    {
        using var ctx = new SqliteTestContext();
        // No Person seeded -> record not found -> not visible.
        var service = CreateService(ctx);
        var actor = ClaimsPrincipalBuilder.Agent("a1").Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpgradeRequestAsync(nameof(Person), "missing", "Max",
                Classification.SecuredStateThreatening, "Grund", actor));
    }

    [Fact]
    public async Task UpgradeRequestAsync_Throws_WhenOpenRequestAlreadyExists()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1"));
            db.SaveChanges();
        }
        SeedRequest(ctx, RequestType.Upgrade, RequestStatus.Requested, nameof(Person), "p1");
        var service = CreateService(ctx);
        var actor = ClaimsPrincipalBuilder.Agent("a1").Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.UpgradeRequestAsync(nameof(Person), "p1", "Max",
                Classification.SecuredStateThreatening, "Grund", actor));
    }

    // ---- GetOpenAsync ----

    [Fact]
    public async Task GetOpenAsync_ReturnsVisibleUpgradeRequests_OrderedByCreatedAt_ExcludingOtherTypes()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1"));
            db.People.Add(Seed.Person(id: "p2", name: "Erika Beispiel"));
            db.SaveChanges();
        }
        SeedRequest(ctx, RequestType.Upgrade, RequestStatus.Requested, nameof(Person), "p2",
            createdAt: new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc));
        SeedRequest(ctx, RequestType.Upgrade, RequestStatus.Requested, nameof(Person), "p1",
            createdAt: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        // Excluded: PartnerFreigabe type and a decided upgrade.
        SeedRequest(ctx, RequestType.PartnerFreigabe, RequestStatus.Requested, nameof(Person), "p1");
        SeedRequest(ctx, RequestType.Upgrade, RequestStatus.Approved, nameof(Person), "p1");
        var service = CreateService(ctx);

        var open = await service.GetOpenAsync(isLeadership: true);

        Assert.Equal(2, open.Count);
        Assert.Equal("p1", open[0].TargetId); // earlier CreatedAt first
        Assert.Equal("p2", open[1].TargetId);
    }

    [Fact]
    public async Task GetOpenAsync_HidesClassifiedTarget_FromNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "plain"));
            db.People.Add(Seed.Person(id: "secret", configure: p => p.IsClassified = true));
            db.SaveChanges();
        }
        SeedRequest(ctx, RequestType.Upgrade, RequestStatus.Requested, nameof(Person), "plain");
        SeedRequest(ctx, RequestType.Upgrade, RequestStatus.Requested, nameof(Person), "secret");
        var service = CreateService(ctx);

        var forViewer = await service.GetOpenAsync(isLeadership: false);
        var forLeadership = await service.GetOpenAsync(isLeadership: true);

        Assert.Equal("plain", Assert.Single(forViewer).TargetId);
        Assert.Equal(2, forLeadership.Count);
    }

    // ---- GetOpenCountAsync ----

    [Fact]
    public async Task GetOpenCountAsync_NonLeadership_CountsOnlyVisibleUpgrades()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1"));
            db.SaveChanges();
        }
        SeedRequest(ctx, RequestType.Upgrade, RequestStatus.Requested, nameof(Person), "p1");
        SeedRequest(ctx, RequestType.PartnerFreigabe, RequestStatus.Requested, nameof(Person), "p1");
        var service = CreateService(ctx);

        var count = await service.GetOpenCountAsync(isLeadership: false);

        Assert.Equal(1, count); // partner requests not counted for non-leadership
    }

    [Fact]
    public async Task GetOpenCountAsync_Leadership_AddsPartnerFreigabeRequests()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1"));
            db.SaveChanges();
        }
        SeedRequest(ctx, RequestType.Upgrade, RequestStatus.Requested, nameof(Person), "p1");
        SeedRequest(ctx, RequestType.PartnerFreigabe, RequestStatus.Requested, nameof(Person), "p1");
        var service = CreateService(ctx);

        var count = await service.GetOpenCountAsync(isLeadership: true);

        Assert.Equal(2, count); // 1 upgrade + 1 partner
    }

    // ---- GetMyAsync ----

    [Fact]
    public async Task GetMyAsync_ReturnsOwnRequests_OrderedByCreatedAtDescending()
    {
        using var ctx = new SqliteTestContext();
        SeedRequest(ctx, RequestType.Upgrade, RequestStatus.Requested, nameof(Person), "p1",
            createdById: "me", createdAt: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        SeedRequest(ctx, RequestType.Upgrade, RequestStatus.Approved, nameof(Person), "p2",
            createdById: "me", createdAt: new DateTime(2026, 2, 3, 0, 0, 0, DateTimeKind.Utc));
        SeedRequest(ctx, RequestType.Upgrade, RequestStatus.Requested, nameof(Person), "p3",
            createdById: "someone-else");
        var service = CreateService(ctx);

        var mine = await service.GetMyAsync("me");

        Assert.Equal(2, mine.Count);
        Assert.Equal("p2", mine[0].TargetId); // newest first
        Assert.Equal("p1", mine[1].TargetId);
    }

    // ---- DecideAsync ----

    [Fact]
    public async Task DecideAsync_Throws_WhenActorLacksHighestClassification()
    {
        using var ctx = new SqliteTestContext();
        var service = CreateService(ctx);
        var actor = ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.DecideAsync("any", approved: true, note: null, actor));
    }

    [Fact]
    public async Task DecideAsync_Approve_SetsClassification_LogsHistory_AndNotifies()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1", configure: p => p.Classification = Classification.ReviewCase));
            db.SaveChanges();
        }
        var request = SeedRequest(ctx, RequestType.Upgrade, RequestStatus.Requested, nameof(Person), "p1",
            createdById: "requester-1");
        var notifications = Substitute.For<INotificationService>();
        var service = CreateService(ctx, notifications);
        var actor = ClaimsPrincipalBuilder.Agent("decider").AsAdmin().WithCodename("Warden").Build();

        await service.DecideAsync(request.Id, approved: true, note: "  bestätigt  ", actor);

        using var check = ctx.NewContext();
        Assert.Equal(Classification.SecuredStateThreatening,
            check.People.Single(p => p.Id == "p1").Classification);

        var decided = check.Requests.Single(r => r.Id == request.Id);
        Assert.Equal(RequestStatus.Approved, decided.Status);
        Assert.Equal("Warden", decided.DeciderName);
        Assert.NotNull(decided.DecidedAt);
        Assert.Equal("bestätigt", decided.DecisionNote);

        var history = Assert.Single(check.ClassificationHistory.ToList());
        Assert.Equal(nameof(Person), history.EntityType);
        Assert.Equal("p1", history.EntityId);
        Assert.Equal(Classification.SecuredStateThreatening, history.Value);
        Assert.Equal(request.Id, history.RequestId);

        await notifications.Received(1).NotifyAsync("requester-1", NotificationType.RequestDecided,
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DecideAsync_Reject_LeavesTargetClassificationUnchanged()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1", configure: p => p.Classification = Classification.ReviewCase));
            db.SaveChanges();
        }
        var request = SeedRequest(ctx, RequestType.Upgrade, RequestStatus.Requested, nameof(Person), "p1",
            createdById: "requester-1");
        var service = CreateService(ctx);
        var actor = ClaimsPrincipalBuilder.Agent("decider").AsAdmin().WithCodename("Warden").Build();

        await service.DecideAsync(request.Id, approved: false, note: null, actor);

        using var check = ctx.NewContext();
        Assert.Equal(Classification.ReviewCase, check.People.Single(p => p.Id == "p1").Classification);
        Assert.Equal(RequestStatus.Rejected, check.Requests.Single(r => r.Id == request.Id).Status);
        Assert.Empty(check.ClassificationHistory.ToList());
    }

    [Fact]
    public async Task DecideAsync_Throws_WhenRequestNotFound()
    {
        using var ctx = new SqliteTestContext();
        var service = CreateService(ctx);
        var actor = ClaimsPrincipalBuilder.Agent("decider").AsAdmin().WithCodename("Warden").Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DecideAsync("does-not-exist", approved: true, note: null, actor));
    }

    [Fact]
    public async Task DecideAsync_Throws_WhenRequestAlreadyDecided()
    {
        using var ctx = new SqliteTestContext();
        var request = SeedRequest(ctx, RequestType.Upgrade, RequestStatus.Approved, nameof(Person), "p1",
            createdById: "requester-1");
        var service = CreateService(ctx);
        var actor = ClaimsPrincipalBuilder.Agent("decider").AsAdmin().WithCodename("Warden").Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DecideAsync(request.Id, approved: true, note: null, actor));
    }

    // ---- type scoping: three request types share one table, one decision path ----

    [Theory]
    [InlineData(RequestType.PartnerFreigabe)]
    [InlineData(RequestType.Veroeffentlichung)]
    public async Task DecideAsync_ANonUpgradeRequest_Throws(RequestType type)
    {
        // approval here means "set the classification on the target"; any other type would run that with an unset
        // classification and silently downgrade the record
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", configure: p => p.Classification = Classification.SuspicionCase));
            db.SaveChanges();
        }
        var request = SeedRequest(ctx, type, RequestStatus.Requested, nameof(Person), "p1",
            configure: r => r.TargetClassification = Classification.Unknown);
        var service = CreateService(ctx);
        var actor = ClaimsPrincipalBuilder.Agent("decider").AsAdmin().WithCodename("Warden").Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DecideAsync(request.Id, approved: true, note: null, actor));

        using var read = ctx.NewContext();
        Assert.Equal(Classification.SuspicionCase, read.People.Single().Classification);
        Assert.Equal(RequestStatus.Requested, read.Requests.Single().Status);
    }

    [Fact]
    public async Task HasOpenRequestAsync_IgnoresAPublicationRequest()
    {
        // it drives the classification panel; a publication request must not read as "an upgrade is running"
        using var ctx = new SqliteTestContext();
        SeedRequest(ctx, RequestType.Veroeffentlichung, RequestStatus.Requested, nameof(Person), "p1");
        var service = CreateService(ctx);

        Assert.False(await service.HasOpenRequestAsync(nameof(Person), "p1"));
    }

    [Fact]
    public async Task UpgradeRequestAsync_IsNotBlockedByAnOpenPublicationRequest()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.SaveChanges();
        }
        SeedRequest(ctx, RequestType.Veroeffentlichung, RequestStatus.Requested, nameof(Person), "p1");
        var service = CreateService(ctx);
        var actor = ClaimsPrincipalBuilder.Agent("agent").WithRank(Rank.Director).WithCodename("Falcon").Build();

        await service.UpgradeRequestAsync(nameof(Person), "p1", "Ziel",
            Classification.SecuredStateThreatening, "Begründung", actor);

        using var read = ctx.NewContext();
        Assert.Single(read.Requests, r => r.Type == RequestType.Upgrade);
    }
}
