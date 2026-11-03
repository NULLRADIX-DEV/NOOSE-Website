using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Requests;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>End date of a partner release: the read gate, the sweep and the write-side validation.</summary>
public sealed class PartnerShareExpiryTests : IDisposable
{
    private const PartnerAgency Agency = PartnerAgency.LSPD;
    private readonly SqliteTestContext _ctx = new();

    public void Dispose() => _ctx.Dispose();

    private static DateTime Past => DateTime.UtcNow.AddHours(-2);
    private static DateTime Future => DateTime.UtcNow.AddDays(3);

    private PartnerShareService Service() => new(_ctx.Factory, Substitute.For<INotificationService>());

    private static ClaimsPrincipalBuilder Leader()
        => ClaimsPrincipalBuilder.Agent("leader").AsAdmin().WithRank(Rank.Director).WithCodename("Warden");

    private static ClaimsPrincipalBuilder Writer()
        => ClaimsPrincipalBuilder.Agent("writer").WithRank(Rank.SpecialAgent).WithCodename("Fox");

    private PartnerShare Share(string entityId, DateTime? validUntil, string type = nameof(Person), string? agentId = null,
        bool includesChildren = false, PartnerAgency agency = Agency)
    {
        var share = new PartnerShare
        {
            EntityType = type, EntityId = entityId, Agency = agency, PartnerAgentId = agentId,
            IncludesChildren = includesChildren, ValidUntil = validUntil,
            CreatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        using var db = _ctx.NewContext();
        db.PartnerShares.Add(share);
        db.SaveChanges();
        return share;
    }

    private void SeedPeople(params string[] ids)
    {
        using var db = _ctx.NewContext();
        foreach (var id in ids)
        {
            db.People.Add(Seed.Person(id));
        }
        db.SaveChanges();
    }

    private List<AuditLog> Audit(string entityType, string entityId)
    {
        using var db = _ctx.NewContext();
        return db.AuditLogs.Where(a => a.EntityType == entityType && a.EntityId == entityId).OrderBy(a => a.Id).ToList();
    }

    // ---- read gate ----

    [Fact]
    public async Task Gate_IgnoresAnExpiredShare_EvenBeforeTheSweepRan()
    {
        SeedPeople("expired", "live", "forever");
        Share("expired", Past);
        Share("live", Future);
        Share("forever", null);
        await using var db = _ctx.NewContext();

        Assert.False(await PartnerVisibility.IsRecordVisibleToPartnerAsync(db, nameof(Person), "expired", Agency, "partner"));
        Assert.True(await PartnerVisibility.IsRecordVisibleToPartnerAsync(db, nameof(Person), "live", Agency, "partner"));
        Assert.True(await PartnerVisibility.IsRecordVisibleToPartnerAsync(db, nameof(Person), "forever", Agency, "partner"));

        var listed = await db.People.OnlyPartnerVisible(db, Agency, "partner").Select(p => p.Id).OrderBy(i => i).ToListAsync();
        Assert.Equal(new[] { "forever", "live" }, listed);
        Assert.False(await PartnerVisibility.HasShareAsync(db, nameof(Person), "expired", Agency, "partner"));
        Assert.Empty(await PartnerVisibility.ReleasedParentIdsAsync(db, nameof(Person), new[] { "expired" }, Agency, "partner"));
    }

    [Fact]
    public async Task Gate_ExpiredIndividualShare_GrantsNothing()
    {
        SeedPeople("p1");
        Share("p1", Past, agentId: "partner");
        await using var db = _ctx.NewContext();

        Assert.False(await PartnerVisibility.IsRecordVisibleToPartnerAsync(db, nameof(Person), "p1", Agency, "partner"));
    }

    [Fact]
    public async Task Gate_ExpiredWholeRecordShare_NoLongerCoversChildren()
    {
        SeedPeople("p1");
        Share("p1", Past, includesChildren: true);
        Share("doc-1", Future, type: "PersonDoc");
        Share("doc-2", Past, type: "PersonDoc");
        await using var db = _ctx.NewContext();

        Assert.False(await PartnerVisibility.ParentIncludesChildrenAsync(db, nameof(Person), "p1", Agency, "partner"));
        var released = await PartnerVisibility.ReleasedChildIdsAsync(db, "PersonDoc", new[] { "doc-1", "doc-2" }, Agency, "partner");
        Assert.Equal(new[] { "doc-1" }, released.ToArray());
    }

    [Fact]
    public async Task Gate_ShareEndingLater_StillGrantsAccessNow()
    {
        SeedPeople("p1");
        Share("p1", DateTime.UtcNow.AddMinutes(5), includesChildren: true);
        await using var db = _ctx.NewContext();

        Assert.True(await PartnerVisibility.IsRecordVisibleToPartnerAsync(db, nameof(Person), "p1", Agency, "partner"));
        Assert.True(await PartnerVisibility.ParentIncludesChildrenAsync(db, nameof(Person), "p1", Agency, "partner"));
    }

    // ---- sweep ----

    [Fact]
    public async Task Sweep_RevokesExpiredShares_AndKeepsTheRest()
    {
        SeedPeople("expired", "live", "forever");
        Share("expired", Past);
        Share("live", Future);
        Share("forever", null);

        var count = await new PartnerShareExpiryService(_ctx.Factory).ExpireDueAsync();

        Assert.Equal(1, count);
        using var db = _ctx.NewContext();
        Assert.Equal(new[] { "forever", "live" }, db.PartnerShares.Select(s => s.EntityId).OrderBy(i => i).ToArray());
    }

    [Fact]
    public async Task Sweep_WritesASystemAuditRowOnTheRecord_ThatSaysExpired()
    {
        SeedPeople("p1");
        Share("p1", Past);

        await new PartnerShareExpiryService(_ctx.Factory).ExpireDueAsync();

        var row = Assert.Single(Audit(nameof(Person), "p1"));
        Assert.Equal("System", row.AgentName);
        Assert.Null(row.AgentId);
        Assert.Equal(AuditAction.Modified, row.Action);
        Assert.Contains("Partner-Freigabe LSPD", row.ChangesJson);
        Assert.Contains("abgelaufen", row.ChangesJson);
        Assert.DoesNotContain("entzogen", row.ChangesJson);
    }

    [Fact]
    public async Task ManualRevoke_IsLoggedAsRevoked_NotAsExpired()
    {
        SeedPeople("p1");
        Share("p1", Future);

        await Service().SetParentAsync(nameof(Person), "p1", Agency, released: false, includesChildren: false, Leader());

        var row = Assert.Single(Audit(nameof(Person), "p1"));
        Assert.Equal("Warden", row.AgentName);
        Assert.Contains("entzogen", row.ChangesJson);
        Assert.DoesNotContain("abgelaufen", row.ChangesJson);
    }

    [Fact]
    public async Task Sweep_IsIdempotent_AndLogsOnce()
    {
        SeedPeople("p1");
        Share("p1", Past);
        var sweeper = new PartnerShareExpiryService(_ctx.Factory);

        Assert.Equal(1, await sweeper.ExpireDueAsync());
        Assert.Equal(0, await sweeper.ExpireDueAsync());
        Assert.Single(Audit(nameof(Person), "p1"));
    }

    [Fact]
    public async Task Sweep_LabelsAnIndividualShareWithTheCodename()
    {
        SeedPeople("p1");
        using (var db = _ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("partner-1", configure: a =>
            {
                a.Codename = "Nightowl";
                a.PartnerAgency = PartnerAgency.LSPD;
            }));
            db.SaveChanges();
        }
        Share("p1", Past, agentId: "partner-1");

        await new PartnerShareExpiryService(_ctx.Factory).ExpireDueAsync();

        Assert.Contains("Nightowl", Assert.Single(Audit(nameof(Person), "p1")).ChangesJson);
    }

    // ---- write side ----

    [Fact]
    public async Task SetParent_WithAPastEndDate_IsRejected_AndCreatesNothing()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service().SetParentAsync(nameof(Person), "p1", Agency, true, false, Past, Leader()));

        using var db = _ctx.NewContext();
        Assert.Empty(db.PartnerShares.ToList());
    }

    [Fact]
    public async Task SetParent_WithAnEndDate_StoresIt_AndLogsOnTheRecord()
    {
        var end = Future;

        await Service().SetParentAsync(nameof(Person), "p1", Agency, true, true, end, Leader());

        using var db = _ctx.NewContext();
        var share = Assert.Single(db.PartnerShares.ToList());
        Assert.Equal(end, share.ValidUntil!.Value, TimeSpan.FromSeconds(1));
        Assert.Contains("freigegeben, bis", Assert.Single(Audit(nameof(Person), "p1")).ChangesJson);
    }

    [Fact]
    public async Task SetIndividualParent_WithAPastEndDate_IsRejected()
    {
        using (var db = _ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("partner-1", configure: a => a.PartnerAgency = PartnerAgency.LSPD));
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service().SetIndividualParentAsync(nameof(Person), "p1", "partner-1", true, false, Past, Leader()));
    }

    [Fact]
    public async Task SetParent_WithoutAnEndDate_KeepsTheExistingOne()
    {
        var end = Future;
        Share("p1", end);

        await Service().SetParentAsync(nameof(Person), "p1", Agency, true, true, Leader());

        using var db = _ctx.NewContext();
        Assert.Equal(end, db.PartnerShares.Single().ValidUntil!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task SetValidUntil_ExtendsShortensAndClears()
    {
        Share("p1", Future);
        var service = Service();

        var later = DateTime.UtcNow.AddDays(20);
        await service.SetValidUntilAsync(nameof(Person), "p1", Agency, null, later, Leader());
        using (var db = _ctx.NewContext())
        {
            Assert.Equal(later, db.PartnerShares.Single().ValidUntil!.Value, TimeSpan.FromSeconds(1));
        }

        var sooner = DateTime.UtcNow.AddHours(5);
        await service.SetValidUntilAsync(nameof(Person), "p1", Agency, null, sooner, Leader());
        using (var db = _ctx.NewContext())
        {
            Assert.Equal(sooner, db.PartnerShares.Single().ValidUntil!.Value, TimeSpan.FromSeconds(1));
        }

        await service.SetValidUntilAsync(nameof(Person), "p1", Agency, null, null, Leader());
        using (var db = _ctx.NewContext())
        {
            Assert.Null(db.PartnerShares.Single().ValidUntil);
        }
        Assert.Equal(3, Audit(nameof(Person), "p1").Count);
    }

    [Fact]
    public async Task SetValidUntil_RejectsAPastDate_AndKeepsTheShare()
    {
        var end = Future;
        Share("p1", end);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service().SetValidUntilAsync(nameof(Person), "p1", Agency, null, Past, Leader()));

        using var db = _ctx.NewContext();
        Assert.Equal(end, db.PartnerShares.Single().ValidUntil!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task SetValidUntil_Throws_WithoutAnActiveShare_OrWithoutLeadership()
    {
        Share("expired", Past);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service().SetValidUntilAsync(nameof(Person), "expired", Agency, null, Future, Leader()));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service().SetValidUntilAsync(nameof(Person), "none", Agency, null, Future, Leader()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Service().SetValidUntilAsync(nameof(Person), "expired", Agency, null, Future, Writer()));
    }

    [Fact]
    public async Task ReleasingAgain_BringsBackAnExpiredRowThatWasNotSweptYet_Unlimited()
    {
        Share("p1", Past);

        await Service().SetParentAsync(nameof(Person), "p1", Agency, true, false, Leader());

        using var db = _ctx.NewContext();
        var share = Assert.Single(db.PartnerShares.ToList());
        Assert.Null(share.ValidUntil);
    }

    [Fact]
    public async Task States_HideAnExpiredShare_AndCarryTheEndDateOfALiveOne()
    {
        Share("p1", Past, agency: PartnerAgency.LSPD);
        var end = Future;
        Share("p1", end, agency: PartnerAgency.DoJ);

        var states = await Service().GetForRecordAsync(nameof(Person), "p1");

        Assert.False(states.Single(s => s.Agency == PartnerAgency.LSPD).Released);
        var doj = states.Single(s => s.Agency == PartnerAgency.DoJ);
        Assert.True(doj.Released);
        Assert.Equal(end, doj.ValidUntil!.Value, TimeSpan.FromSeconds(1));
    }

    // ---- request flow ----

    [Fact]
    public async Task Request_WithAPastEndDate_IsRejected()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service().RequestPartnerShareAsync(Writer(), nameof(Person), "p1", Agency, null, false, "Grund", Past));
    }

    [Fact]
    public async Task Request_AnExpiredShare_DoesNotBlockANewRequest()
    {
        Share("p1", Past);

        await Service().RequestPartnerShareAsync(Writer(), nameof(Person), "p1", Agency, null, false, "Grund", Future);

        using var db = _ctx.NewContext();
        Assert.NotNull(Assert.Single(db.Requests.ToList()).FreigabeValidUntil);
    }

    [Fact]
    public async Task Approve_AppliesTheRequestedEndDateToTheShare()
    {
        var end = Future;
        var request = SeedRequest(end);

        await Service().ApprovePartnerShareRequestAsync(Leader(), request.Id, null);

        using var db = _ctx.NewContext();
        Assert.Equal(end, db.PartnerShares.Single().ValidUntil!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Approve_RefusesARequestWhoseWishedEndHasPassed_InsteadOfGrantingItUnlimited()
    {
        var request = SeedRequest(Past);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service().ApprovePartnerShareRequestAsync(Leader(), request.Id, null));

        using var db = _ctx.NewContext();
        Assert.Empty(db.PartnerShares.ToList());
        Assert.Equal(RequestStatus.Requested, db.Requests.Single().Status);
    }

    private Request SeedRequest(DateTime? validUntil)
    {
        var request = new Request
        {
            Type = RequestType.PartnerFreigabe, Status = RequestStatus.Requested,
            TargetType = nameof(Person), TargetId = "p1", TargetDesignation = "Ziel",
            FreigabeAgency = Agency, Justification = "Grund", FreigabeValidUntil = validUntil,
            CreatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        using var db = _ctx.NewContext();
        db.Requests.Add(request);
        db.SaveChanges();
        return request;
    }

    // ---- helper rules ----

    [Fact]
    public void Expiry_Require_AcceptsUnlimitedAndFuture_RejectsPastAndNow()
    {
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

        Assert.Null(PartnerShareExpiry.Require(null, now));
        Assert.Equal(now.AddMinutes(1), PartnerShareExpiry.Require(now.AddMinutes(1), now));
        Assert.Throws<InvalidOperationException>(() => PartnerShareExpiry.Require(now, now));
        Assert.Throws<InvalidOperationException>(() => PartnerShareExpiry.Require(now.AddSeconds(-1), now));
    }

    [Fact]
    public void Expiry_IsActive_EndsExactlyAtTheInstant()
    {
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(PartnerShareExpiry.IsActive(null, now));
        Assert.True(PartnerShareExpiry.IsActive(now.AddTicks(1), now));
        Assert.False(PartnerShareExpiry.IsActive(now, now));
    }

    [Fact]
    public void Expiry_Presets_AddTheirDays_AndUnlimitedHasNoEnd()
    {
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal(now.AddDays(1), PartnerShareExpiry.FromPreset(PartnerShareDuration.OneDay, now));
        Assert.Equal(now.AddDays(7), PartnerShareExpiry.FromPreset(PartnerShareDuration.SevenDays, now));
        Assert.Equal(now.AddDays(30), PartnerShareExpiry.FromPreset(PartnerShareDuration.ThirtyDays, now));
        Assert.Null(PartnerShareExpiry.FromPreset(PartnerShareDuration.Unlimited, now));
    }

    [Fact]
    public void Expiry_EndOfDay_IsTheLastMinuteOfThatLocalDay()
    {
        var utc = PartnerShareExpiry.EndOfDay(new DateOnly(2026, 11, 20));

        var local = MeetingTime.Local(utc);
        Assert.Equal(new DateTime(2026, 11, 20, 23, 59, 0), local);
    }

    [Fact]
    public void Expiry_Describe_ReadsUnlimitedRemainingAndExpired()
    {
        var now = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal("unbegrenzt", PartnerShareExpiry.Describe(null, now));
        Assert.Equal("abgelaufen", PartnerShareExpiry.Describe(now.AddMinutes(-1), now));
        Assert.EndsWith("(noch 6 Tage)", PartnerShareExpiry.Describe(now.AddDays(6).AddHours(1), now));
        Assert.EndsWith("(noch 5 Std.)", PartnerShareExpiry.Describe(now.AddHours(5).AddMinutes(10), now));
        Assert.EndsWith("(noch 12 Min.)", PartnerShareExpiry.Describe(now.AddMinutes(12).AddSeconds(10), now));
    }
}
