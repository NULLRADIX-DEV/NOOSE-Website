using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Requests;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="PartnerShareService"/> against in-memory SQLite.</summary>
public sealed class PartnerShareServiceTests
{
    private static PartnerShareService CreateService(SqliteTestContext ctx)
        => new(ctx.Factory, Substitute.For<INotificationService>());

    private static PartnerShare SeedShare(SqliteTestContext ctx, string entityType, string entityId,
        PartnerAgency agency, string? partnerAgentId = null, bool includesChildren = false)
    {
        var share = new PartnerShare
        {
            EntityType = entityType,
            EntityId = entityId,
            Agency = agency,
            PartnerAgentId = partnerAgentId,
            IncludesChildren = includesChildren,
            CreatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        using var db = ctx.NewContext();
        db.PartnerShares.Add(share);
        db.SaveChanges();
        return share;
    }

    private static Request SeedRequest(SqliteTestContext ctx, RequestType type, RequestStatus status,
        string targetType, string targetId, PartnerAgency? agency = null, string? partnerAgentId = null,
        bool includesChildren = false, DateTime? createdAt = null)
    {
        var request = new Request
        {
            Type = type,
            Status = status,
            TargetType = targetType,
            TargetId = targetId,
            TargetDesignation = "Ziel",
            FreigabeAgency = agency,
            FreigabePartnerAgentId = partnerAgentId,
            FreigabeIncludesChildren = includesChildren,
            Justification = "Grund",
            CreatedAt = createdAt ?? new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        using var db = ctx.NewContext();
        db.Requests.Add(request);
        db.SaveChanges();
        return request;
    }

    private static ClaimsPrincipalBuilder Leader()
        => ClaimsPrincipalBuilder.Agent("leader").AsAdmin().WithRank(Rank.Director).WithCodename("Warden");

    private static ClaimsPrincipalBuilder NonLeader()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.JuniorAgent);

    // ---- GetForRecordAsync ----

    [Fact]
    public async Task GetForRecordAsync_ReturnsPerAgencyState_ForAgencyWideShare()
    {
        using var ctx = new SqliteTestContext();
        SeedShare(ctx, nameof(Person), "p1", PartnerAgency.LSPD, includesChildren: true);
        var service = CreateService(ctx);

        var states = await service.GetForRecordAsync(nameof(Person), "p1");

        Assert.Equal(PartnerAgencyDisplay.All.Count, states.Count); // one entry per agency
        var lspd = states.Single(s => s.Agency == PartnerAgency.LSPD);
        Assert.True(lspd.Released);
        Assert.True(lspd.IncludesChildren);
        Assert.False(states.Single(s => s.Agency == PartnerAgency.DoJ).Released);
        Assert.False(states.Single(s => s.Agency == PartnerAgency.LSMD).Released);
    }

    [Fact]
    public async Task GetForRecordAsync_ExcludesIndividualAccountShares()
    {
        using var ctx = new SqliteTestContext();
        // individual share (PartnerAgentId set) must not count as an agency-wide release
        SeedShare(ctx, nameof(Person), "p1", PartnerAgency.LSPD, partnerAgentId: "acct-1");
        var service = CreateService(ctx);

        var states = await service.GetForRecordAsync(nameof(Person), "p1");

        Assert.All(states, s => Assert.False(s.Released));
    }

    // ---- GetForChildAsync ----

    [Fact]
    public async Task GetForChildAsync_ReturnsPerAgencyState_ForAgencyWideShare()
    {
        using var ctx = new SqliteTestContext();
        SeedShare(ctx, "PersonDoc", "doc-1", PartnerAgency.DoJ);
        var service = CreateService(ctx);

        var states = await service.GetForChildAsync("PersonDoc", "doc-1");

        Assert.Equal(PartnerAgencyDisplay.All.Count, states.Count);
        Assert.True(states.Single(s => s.Agency == PartnerAgency.DoJ).Released);
        Assert.False(states.Single(s => s.Agency == PartnerAgency.LSPD).Released);
    }

    // ---- GetIndividualSharesForRecordAsync ----

    [Fact]
    public async Task GetIndividualSharesForRecordAsync_ReturnsAccountShares_WithAgentDetails()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("partner-1", configure: a =>
            {
                a.Codename = "Nightowl";
                a.PartnerAgency = PartnerAgency.LSPD;
                a.PartnerRank = PartnerRank.Chief;
            }));
            db.SaveChanges();
        }
        SeedShare(ctx, nameof(Person), "p1", PartnerAgency.LSPD, partnerAgentId: "partner-1", includesChildren: true);
        // an agency-wide share for the same record must be excluded from the individual list
        SeedShare(ctx, nameof(Person), "p1", PartnerAgency.DoJ);
        var service = CreateService(ctx);

        var rows = await service.GetIndividualSharesForRecordAsync(nameof(Person), "p1");

        var row = Assert.Single(rows);
        Assert.Equal("partner-1", row.AgentId);
        Assert.Equal("Nightowl", row.Codename);
        Assert.Equal(PartnerAgency.LSPD, row.Agency);
        Assert.Equal(PartnerRank.Chief, row.Rank);
        Assert.True(row.IncludesChildren);
    }

    // ---- GetSelectablePartnersAsync ----

    [Fact]
    public async Task GetSelectablePartnersAsync_ReturnsActivePartnersOnly_OrderedByAgencyThenCodename()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("p-lspd", configure: a =>
            {
                a.Codename = "Bravo";
                a.PartnerAgency = PartnerAgency.LSPD;
                a.PartnerRank = PartnerRank.Member;
            }));
            db.Users.Add(Seed.Agent("p-doj", configure: a =>
            {
                a.Codename = "Alpha";
                a.PartnerAgency = PartnerAgency.DoJ;
                a.PartnerRank = PartnerRank.Special;
            }));
            // internal agent (no partner agency) -> excluded
            db.Users.Add(Seed.Agent("internal", configure: a => a.Codename = "Zulu"));
            // pending partner -> excluded
            db.Users.Add(Seed.Agent("p-pending", status: AgentStatus.Pending, configure: a =>
            {
                a.Codename = "Charlie";
                a.PartnerAgency = PartnerAgency.LSMD;
            }));
            db.SaveChanges();
        }
        var service = CreateService(ctx);

        var options = await service.GetSelectablePartnersAsync();

        Assert.Equal(2, options.Count);
        Assert.Equal("p-doj", options[0].AgentId);   // DoJ(1) before LSPD(2)
        Assert.Equal("p-lspd", options[1].AgentId);
        Assert.Equal(PartnerAgency.DoJ, options[0].Agency);
        Assert.Equal(PartnerRank.Special, options[0].Rank);
    }

    // ---- SetParentAsync ----

    [Fact]
    public async Task SetParentAsync_CreatesShare_WhenReleased()
    {
        using var ctx = new SqliteTestContext();
        var service = CreateService(ctx);

        await service.SetParentAsync(nameof(Person), "p1", PartnerAgency.LSPD,
            released: true, includesChildren: true, Leader());

        using var check = ctx.NewContext();
        var row = Assert.Single(check.PartnerShares.ToList());
        Assert.Equal(nameof(Person), row.EntityType);
        Assert.Equal("p1", row.EntityId);
        Assert.Equal(PartnerAgency.LSPD, row.Agency);
        Assert.Null(row.PartnerAgentId);
        Assert.True(row.IncludesChildren);
    }

    [Fact]
    public async Task SetParentAsync_RemovesShare_WhenNotReleased()
    {
        using var ctx = new SqliteTestContext();
        SeedShare(ctx, nameof(Person), "p1", PartnerAgency.LSPD, includesChildren: true);
        var service = CreateService(ctx);

        await service.SetParentAsync(nameof(Person), "p1", PartnerAgency.LSPD,
            released: false, includesChildren: false, Leader());

        using var check = ctx.NewContext();
        Assert.Empty(check.PartnerShares.ToList()); // hard-deleted (no soft-delete interceptor in tests)
    }

    [Fact]
    public async Task SetParentAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var service = CreateService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.SetParentAsync(nameof(Person), "p1", PartnerAgency.LSPD, true, true, NonLeader()));
    }

    // ---- SetChildAsync ----

    [Fact]
    public async Task SetChildAsync_CreatesShellShare_WhenReleased()
    {
        using var ctx = new SqliteTestContext();
        var service = CreateService(ctx);

        await service.SetChildAsync("PersonDoc", "doc-1", PartnerAgency.DoJ, released: true, Leader());

        using var check = ctx.NewContext();
        var row = Assert.Single(check.PartnerShares.ToList());
        Assert.Equal("PersonDoc", row.EntityType);
        Assert.Equal("doc-1", row.EntityId);
        Assert.Equal(PartnerAgency.DoJ, row.Agency);
        Assert.False(row.IncludesChildren); // child shares are always shell-only
    }

    [Fact]
    public async Task SetChildAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var service = CreateService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.SetChildAsync("PersonDoc", "doc-1", PartnerAgency.DoJ, true, NonLeader()));
    }

    // ---- SetIndividualParentAsync ----

    [Fact]
    public async Task SetIndividualParentAsync_CreatesShare_DerivingAgencyFromAccount()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("partner-1", configure: a => a.PartnerAgency = PartnerAgency.LSMD));
            db.SaveChanges();
        }
        var service = CreateService(ctx);

        await service.SetIndividualParentAsync(nameof(Person), "p1", "partner-1",
            released: true, includesChildren: true, Leader());

        using var check = ctx.NewContext();
        var row = Assert.Single(check.PartnerShares.ToList());
        Assert.Equal("partner-1", row.PartnerAgentId);
        Assert.Equal(PartnerAgency.LSMD, row.Agency); // taken from the account, not passed in
        Assert.True(row.IncludesChildren);
    }

    [Fact]
    public async Task SetIndividualParentAsync_Throws_WhenAccountIsNotPartner()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("internal-1")); // no PartnerAgency
            db.SaveChanges();
        }
        var service = CreateService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SetIndividualParentAsync(nameof(Person), "p1", "internal-1", true, false, Leader()));
    }

    [Fact]
    public async Task SetIndividualParentAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var service = CreateService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.SetIndividualParentAsync(nameof(Person), "p1", "partner-1", true, false, NonLeader()));
    }

    // ---- GetTypeSummariesAsync ----

    [Fact]
    public async Task GetTypeSummariesAsync_ReportsTotalsAndAgencyWideSharedCounts()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1"));
            db.People.Add(Seed.Person(id: "p2", name: "Erika Beispiel"));
            db.Factions.Add(Seed.Faction(id: "f1"));
            db.SaveChanges();
        }
        SeedShare(ctx, nameof(Person), "p1", PartnerAgency.LSPD);
        // individual share does not count toward agency-wide shared totals
        SeedShare(ctx, nameof(Person), "p2", PartnerAgency.LSPD, partnerAgentId: "acct-1");
        // a share for a different agency must not be counted
        SeedShare(ctx, nameof(Faction), "f1", PartnerAgency.DoJ);
        var service = CreateService(ctx);

        var summaries = await service.GetTypeSummariesAsync(PartnerAgency.LSPD);

        var person = summaries.Single(s => s.TypeKey == nameof(Person));
        Assert.Equal(2, person.TotalRecords);
        Assert.Equal(1, person.SharedRecords); // only the agency-wide LSPD share

        var faction = summaries.Single(s => s.TypeKey == nameof(Faction));
        Assert.Equal(1, faction.TotalRecords);
        Assert.Equal(0, faction.SharedRecords); // its share is for DoJ, not LSPD
    }

    // ---- SetTypeAsync ----

    [Fact]
    public async Task SetTypeAsync_Release_CreatesShareForEveryRecord_ReturnsAddedCount()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1"));
            db.People.Add(Seed.Person(id: "p2", name: "Erika Beispiel"));
            db.SaveChanges();
        }
        var service = CreateService(ctx);

        var added = await service.SetTypeAsync(nameof(Person), PartnerAgency.LSPD,
            released: true, includesChildren: true, Leader());

        Assert.Equal(2, added);
        using var check = ctx.NewContext();
        var rows = check.PartnerShares.Where(s => s.EntityType == nameof(Person) && s.Agency == PartnerAgency.LSPD).ToList();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(r.IncludesChildren));
        Assert.All(rows, r => Assert.Null(r.PartnerAgentId));
    }

    [Fact]
    public async Task SetTypeAsync_Withdraw_RemovesAgencyWideShares_KeepsIndividualShares()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1"));
            db.People.Add(Seed.Person(id: "p2", name: "Erika Beispiel"));
            db.SaveChanges();
        }
        SeedShare(ctx, nameof(Person), "p1", PartnerAgency.LSPD);
        SeedShare(ctx, nameof(Person), "p2", PartnerAgency.LSPD);
        // individual account share must survive a type-wide withdrawal
        SeedShare(ctx, nameof(Person), "p1", PartnerAgency.LSPD, partnerAgentId: "acct-1");
        var service = CreateService(ctx);

        var removed = await service.SetTypeAsync(nameof(Person), PartnerAgency.LSPD,
            released: false, includesChildren: false, Leader());

        Assert.Equal(2, removed);
        using var check = ctx.NewContext();
        var remaining = Assert.Single(check.PartnerShares.ToList());
        Assert.Equal("acct-1", remaining.PartnerAgentId);
    }

    [Fact]
    public async Task SetTypeAsync_Throws_ForNonReleasableType()
    {
        using var ctx = new SqliteTestContext();
        var service = CreateService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SetTypeAsync("NotAType", PartnerAgency.LSPD, true, false, Leader()));
    }

    [Fact]
    public async Task SetTypeAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var service = CreateService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.SetTypeAsync(nameof(Person), PartnerAgency.LSPD, true, false, NonLeader()));
    }

    // ---- RequestPartnerShareAsync ----

    [Fact]
    public async Task RequestPartnerShareAsync_CreatesRequest_WithDesignationAndTrimmedJustification()
    {
        using var ctx = new SqliteTestContext();
        var person = Seed.Person(id: "p1");
        using (var db = ctx.NewContext())
        {
            db.People.Add(person);
            db.SaveChanges();
        }
        var service = CreateService(ctx);
        var actor = ClaimsPrincipalBuilder.Agent("a1").WithCodename("Falcon").Build();

        await service.RequestPartnerShareAsync(actor, nameof(Person), "p1", PartnerAgency.LSPD,
            partnerAgentId: null, includesChildren: true, justification: "  Amtshilfe  ");

        using var check = ctx.NewContext();
        var row = Assert.Single(check.Requests.ToList());
        Assert.Equal(RequestType.PartnerFreigabe, row.Type);
        Assert.Equal(RequestStatus.Requested, row.Status);
        Assert.Equal(nameof(Person), row.TargetType);
        Assert.Equal("p1", row.TargetId);
        Assert.Equal($"{person.Name} ({person.CaseNumber})", row.TargetDesignation);
        Assert.Equal(PartnerAgency.LSPD, row.FreigabeAgency);
        Assert.Null(row.FreigabePartnerAgentId);
        Assert.True(row.FreigabeIncludesChildren);
        Assert.Equal("Amtshilfe", row.Justification);
        Assert.Equal("Falcon", row.RequesterName);
    }

    [Fact]
    public async Task RequestPartnerShareAsync_Throws_WhenJustificationBlank()
    {
        using var ctx = new SqliteTestContext();
        var service = CreateService(ctx);
        var actor = ClaimsPrincipalBuilder.Agent("a1").Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RequestPartnerShareAsync(actor, nameof(Person), "p1", PartnerAgency.LSPD, null, false, "   "));
    }

    [Fact]
    public async Task RequestPartnerShareAsync_Throws_WhenAlreadyReleasedAgencyWide()
    {
        using var ctx = new SqliteTestContext();
        SeedShare(ctx, nameof(Person), "p1", PartnerAgency.LSPD);
        var service = CreateService(ctx);
        var actor = ClaimsPrincipalBuilder.Agent("a1").Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RequestPartnerShareAsync(actor, nameof(Person), "p1", PartnerAgency.LSPD, null, false, "Grund"));
    }

    [Fact]
    public async Task RequestPartnerShareAsync_Throws_WhenPendingRequestExists()
    {
        using var ctx = new SqliteTestContext();
        SeedRequest(ctx, RequestType.PartnerFreigabe, RequestStatus.Requested, nameof(Person), "p1",
            agency: PartnerAgency.LSPD, partnerAgentId: null);
        var service = CreateService(ctx);
        var actor = ClaimsPrincipalBuilder.Agent("a1").Build();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RequestPartnerShareAsync(actor, nameof(Person), "p1", PartnerAgency.LSPD, null, false, "Grund"));
    }

    [Fact]
    public async Task RequestPartnerShareAsync_Throws_WhenActorLacksWriteAccess()
    {
        using var ctx = new SqliteTestContext();
        var service = CreateService(ctx);
        // team lead without admin = read-only supervisor -> no write access
        var reader = ClaimsPrincipalBuilder.Agent("tl").AsTeamLead().Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.RequestPartnerShareAsync(reader, nameof(Person), "p1", PartnerAgency.LSPD, null, false, "Grund"));
    }

    // ---- GetPendingPartnerShareRequestsAsync ----

    [Fact]
    public async Task GetPendingPartnerShareRequestsAsync_ReturnsRequestedPartnerFreigabe_OrderedByCreatedAt()
    {
        using var ctx = new SqliteTestContext();
        SeedRequest(ctx, RequestType.PartnerFreigabe, RequestStatus.Requested, nameof(Person), "later",
            agency: PartnerAgency.LSPD, createdAt: new DateTime(2026, 2, 2, 0, 0, 0, DateTimeKind.Utc));
        SeedRequest(ctx, RequestType.PartnerFreigabe, RequestStatus.Requested, nameof(Person), "earlier",
            agency: PartnerAgency.LSPD, createdAt: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        // excluded: wrong type and already-decided
        SeedRequest(ctx, RequestType.Upgrade, RequestStatus.Requested, nameof(Person), "upgrade");
        SeedRequest(ctx, RequestType.PartnerFreigabe, RequestStatus.Approved, nameof(Person), "decided",
            agency: PartnerAgency.LSPD);
        var service = CreateService(ctx);

        var pending = await service.GetPendingPartnerShareRequestsAsync();

        Assert.Equal(2, pending.Count);
        Assert.Equal("earlier", pending[0].TargetId);
        Assert.Equal("later", pending[1].TargetId);
    }

    // ---- ApprovePartnerShareRequestAsync ----

    [Fact]
    public async Task ApprovePartnerShareRequestAsync_CreatesShare_AndMarksApproved()
    {
        using var ctx = new SqliteTestContext();
        var request = SeedRequest(ctx, RequestType.PartnerFreigabe, RequestStatus.Requested, nameof(Person), "p1",
            agency: PartnerAgency.LSPD, partnerAgentId: null, includesChildren: true);
        var service = CreateService(ctx);

        await service.ApprovePartnerShareRequestAsync(Leader(), request.Id, "  ok  ");

        using var check = ctx.NewContext();
        var share = Assert.Single(check.PartnerShares.ToList());
        Assert.Equal(nameof(Person), share.EntityType);
        Assert.Equal("p1", share.EntityId);
        Assert.Equal(PartnerAgency.LSPD, share.Agency);
        Assert.Null(share.PartnerAgentId);
        Assert.True(share.IncludesChildren);

        var decided = check.Requests.Single(r => r.Id == request.Id);
        Assert.Equal(RequestStatus.Approved, decided.Status);
        Assert.Equal("Warden", decided.DeciderName);
        Assert.NotNull(decided.DecidedAt);
        Assert.Equal("ok", decided.DecisionNote);
    }

    [Fact]
    public async Task ApprovePartnerShareRequestAsync_Throws_WhenNotFound()
    {
        using var ctx = new SqliteTestContext();
        var service = CreateService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApprovePartnerShareRequestAsync(Leader(), "missing", null));
    }

    [Fact]
    public async Task ApprovePartnerShareRequestAsync_Throws_WhenWrongType()
    {
        using var ctx = new SqliteTestContext();
        var request = SeedRequest(ctx, RequestType.Upgrade, RequestStatus.Requested, nameof(Person), "p1");
        var service = CreateService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApprovePartnerShareRequestAsync(Leader(), request.Id, null));
    }

    [Fact]
    public async Task ApprovePartnerShareRequestAsync_Throws_WhenAlreadyDecided()
    {
        using var ctx = new SqliteTestContext();
        var request = SeedRequest(ctx, RequestType.PartnerFreigabe, RequestStatus.Approved, nameof(Person), "p1",
            agency: PartnerAgency.LSPD);
        var service = CreateService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApprovePartnerShareRequestAsync(Leader(), request.Id, null));
    }

    [Fact]
    public async Task ApprovePartnerShareRequestAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var service = CreateService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ApprovePartnerShareRequestAsync(NonLeader(), "any", null));
    }

    // ---- RejectPartnerShareRequestAsync ----

    [Fact]
    public async Task RejectPartnerShareRequestAsync_MarksRejected_WithoutCreatingShare()
    {
        using var ctx = new SqliteTestContext();
        var request = SeedRequest(ctx, RequestType.PartnerFreigabe, RequestStatus.Requested, nameof(Person), "p1",
            agency: PartnerAgency.LSPD);
        var service = CreateService(ctx);

        await service.RejectPartnerShareRequestAsync(Leader(), request.Id, "  abgelehnt  ");

        using var check = ctx.NewContext();
        var decided = check.Requests.Single(r => r.Id == request.Id);
        Assert.Equal(RequestStatus.Rejected, decided.Status);
        Assert.Equal("Warden", decided.DeciderName);
        Assert.NotNull(decided.DecidedAt);
        Assert.Equal("abgelehnt", decided.DecisionNote);
        Assert.Empty(check.PartnerShares.ToList()); // rejection creates no share
    }

    [Fact]
    public async Task RejectPartnerShareRequestAsync_Throws_WhenAlreadyDecided()
    {
        using var ctx = new SqliteTestContext();
        var request = SeedRequest(ctx, RequestType.PartnerFreigabe, RequestStatus.Rejected, nameof(Person), "p1",
            agency: PartnerAgency.LSPD);
        var service = CreateService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.RejectPartnerShareRequestAsync(Leader(), request.Id, null));
    }

    [Fact]
    public async Task RejectPartnerShareRequestAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var service = CreateService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.RejectPartnerShareRequestAsync(NonLeader(), "any", null));
    }
}
