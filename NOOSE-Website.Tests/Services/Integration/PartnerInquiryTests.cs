using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>A partner asks for records in free text; leadership answers by releasing one record.</summary>
public sealed class PartnerInquiryTests : IDisposable
{
    private const string PartnerId = "partner";
    private readonly SqliteTestContext _ctx = new();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();

    public void Dispose() => _ctx.Dispose();

    private PartnerShareService NewService() => new(_ctx.Factory, _notifications);

    private static ClaimsPrincipal Parlament() => ClaimsPrincipalBuilder.Agent(PartnerId).AsPartner(PartnerAgency.Parlament, PartnerRank.Member).Build();
    private static ClaimsPrincipal Leader() => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    private void Grant(PartnerFeature features)
    {
        using var db = _ctx.NewContext();
        db.PartnerAgencyProfiles.Add(new PartnerAgencyProfile { Agency = PartnerAgency.Parlament, Features = features });
        db.SaveChanges();
    }

    private async Task<string> SubmitAsync(string target = "John Doe")
    {
        await NewService().SubmitPartnerInquiryAsync(Parlament(), target, "Untersuchungsausschuss");
        await using var db = _ctx.NewContext();
        // the audit interceptor is not attached here; stamp the author like it would
        var row = await db.Requests.SingleAsync(r => r.TargetDesignation == target);
        row.CreatedById = PartnerId;
        await db.SaveChangesAsync();
        return row.Id;
    }

    [Fact]
    public async Task Submit_WithoutTheFunction_IsRefused()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            NewService().SubmitPartnerInquiryAsync(Parlament(), "John Doe", "Grund"));
    }

    [Fact]
    public async Task Submit_ByAnInternalAgent_IsRefused()
    {
        Grant(PartnerFeature.ShareRequests);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            NewService().SubmitPartnerInquiryAsync(Leader(), "John Doe", "Grund"));
    }

    [Fact]
    public async Task Submit_StoresAnOpenInquiry_AndNotifiesLeadership()
    {
        Grant(PartnerFeature.ShareRequests);
        using (var db = _ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("lead", Rank.Director));
            db.Users.Add(Seed.Agent("junior", Rank.JuniorAgent));
            db.SaveChanges();
        }

        await NewService().SubmitPartnerInquiryAsync(Parlament(), "John Doe", "Untersuchungsausschuss");

        await using var check = _ctx.NewContext();
        var row = await check.Requests.SingleAsync();
        Assert.Equal(RequestType.PartnerAnfrage, row.Type);
        Assert.Equal(PartnerShareService.InquiryTarget, row.TargetType);
        Assert.Equal(PartnerAgency.Parlament, row.FreigabeAgency);
        Assert.Equal(PartnerId, row.FreigabePartnerAgentId);
        await _notifications.Received(1).NotifyManyAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.Contains("lead") && !ids.Contains("junior")),
            NotificationType.PartnerInquiry, Arg.Any<string>(), "/admin/freigaben", PartnerId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Submit_TheSameOpenInquiryTwice_IsRefused()
    {
        Grant(PartnerFeature.ShareRequests);
        await SubmitAsync("John Doe");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService().SubmitPartnerInquiryAsync(Parlament(), "  john doe ", "nochmal"));
    }

    [Fact]
    public async Task Pending_IsLeadershipOnly()
    {
        Grant(PartnerFeature.ShareRequests);
        await SubmitAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => NewService().GetPendingPartnerInquiriesAsync(Parlament()));
        Assert.Single(await NewService().GetPendingPartnerInquiriesAsync(Leader()));
    }

    [Fact]
    public async Task Approve_ReleasesTheChosenRecordToTheAskingAccount_AndNotifiesIt()
    {
        Grant(PartnerFeature.ShareRequests);
        using (var db = _ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "John Doe"));
            db.SaveChanges();
        }
        var id = await SubmitAsync();

        await NewService().ApprovePartnerInquiryAsync(Leader(), id, nameof(Person), "p1", toAccountOnly: true, includesChildren: false, "gern");

        await using var check = _ctx.NewContext();
        var share = await check.PartnerShares.SingleAsync();
        Assert.Equal(("Person", "p1", PartnerAgency.Parlament, PartnerId), (share.EntityType, share.EntityId, share.Agency, share.PartnerAgentId));
        var row = await check.Requests.SingleAsync();
        Assert.Equal(RequestStatus.Approved, row.Status);
        Assert.Equal(("Person", "p1"), (row.TargetType, row.TargetId));
        await _notifications.Received(1).NotifyAsync(PartnerId, NotificationType.RequestDecided, Arg.Any<string>(),
            "/personen/p1", Arg.Any<CancellationToken>());
        Assert.True(await PartnerVisibility.IsRecordVisibleToPartnerAsync(check, nameof(Person), "p1", PartnerAgency.Parlament, PartnerId));
    }

    [Fact]
    public async Task Approve_ByANonLeader_OrForAMissingRecord_IsRefused()
    {
        Grant(PartnerFeature.ShareRequests);
        var id = await SubmitAsync();
        var junior = ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.JuniorAgent).Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            NewService().ApprovePartnerInquiryAsync(junior, id, nameof(Person), "p1", false, false, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService().ApprovePartnerInquiryAsync(Leader(), id, nameof(Person), "missing", false, false, null));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService().ApprovePartnerInquiryAsync(Leader(), id, nameof(PersonDoc), "d1", false, false, null));
    }

    [Fact]
    public async Task Approve_NeverNarrowsAnExistingWholeRecordRelease()
    {
        Grant(PartnerFeature.ShareRequests);
        using (var db = _ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "John Doe"));
            db.PartnerShares.Add(new PartnerShare { EntityType = "Person", EntityId = "p1", Agency = PartnerAgency.Parlament, IncludesChildren = true });
            db.SaveChanges();
        }
        var id = await SubmitAsync();

        await NewService().ApprovePartnerInquiryAsync(Leader(), id, nameof(Person), "p1", toAccountOnly: false, includesChildren: false, null);

        await using var check = _ctx.NewContext();
        Assert.True((await check.PartnerShares.SingleAsync()).IncludesChildren);
    }

    [Fact]
    public async Task Approve_Twice_IsRefused()
    {
        Grant(PartnerFeature.ShareRequests);
        using (var db = _ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "John Doe"));
            db.SaveChanges();
        }
        var id = await SubmitAsync();
        await NewService().ApprovePartnerInquiryAsync(Leader(), id, nameof(Person), "p1", false, false, null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => NewService().RejectPartnerInquiryAsync(Leader(), id, null));
    }

    [Fact]
    public async Task Submit_BeyondTheOpenLimit_IsRefused()
    {
        Grant(PartnerFeature.ShareRequests);
        for (var i = 0; i < PartnerShareService.MaxOpenInquiries; i++)
        {
            await SubmitAsync($"Person {i}");
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService().SubmitPartnerInquiryAsync(Parlament(), "Noch eine", "Grund"));
    }

    [Fact]
    public async Task ALongSubject_NeverBreaksTheDecision()
    {
        Grant(PartnerFeature.ShareRequests);
        var id = await SubmitAsync(new string('x', 256));
        _notifications.NotifyAsync(Arg.Any<string?>(), Arg.Any<NotificationType>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Data too long")));

        await NewService().RejectPartnerInquiryAsync(Leader(), id, null);

        await _notifications.Received(1).NotifyAsync(PartnerId, NotificationType.RequestDecided,
            Arg.Is<string>(t => t.Length < 300), "/anfragen", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reject_ClosesTheInquiry_AndNotifiesTheAsker()
    {
        Grant(PartnerFeature.ShareRequests);
        var id = await SubmitAsync();

        await NewService().RejectPartnerInquiryAsync(Leader(), id, "nicht zuständig");

        await using var check = _ctx.NewContext();
        Assert.Equal(RequestStatus.Rejected, (await check.Requests.SingleAsync()).Status);
        Assert.Empty(await check.PartnerShares.ToListAsync());
        await _notifications.Received(1).NotifyAsync(PartnerId, NotificationType.RequestDecided, Arg.Any<string>(),
            "/anfragen", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnOpenInquiry_IsVisibleToLeadership_NotToOtherAgents()
    {
        Grant(PartnerFeature.ShareRequests);
        var id = await SubmitAsync();

        // the asker follows it on /anfragen, not through record visibility
        await using var db = _ctx.NewContext();
        Assert.True(await Visibility.IsRecordVisibleAsync(db, "Request", id, ViewerScope.From(Leader())));
        Assert.False(await Visibility.IsRecordVisibleAsync(db, "Request", id,
            ViewerScope.From(ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.JuniorAgent).Build())));
    }
}
