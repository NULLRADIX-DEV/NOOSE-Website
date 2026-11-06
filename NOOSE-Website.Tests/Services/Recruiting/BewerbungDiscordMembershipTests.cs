using System.Security.Claims;
using Microsoft.Extensions.Logging;
using NOOSE_Website.Data;
using NOOSE_Website.Infrastructure.Chat;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Recruiting;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Recruiting;

/// <summary>An application is only taken from someone on the Discord server; a Discord outage never stands in the way.</summary>
public sealed class BewerbungDiscordMembershipTests
{
    private const string Invite = "https://discord.gg/9TeYjJyYxf";

    private sealed class Rig
    {
        public IDiscordMembershipCheck Membership { get; } = Substitute.For<IDiscordMembershipCheck>();

        public IDiscordDmService Direct { get; } = Substitute.For<IDiscordDmService>();

        public ISourcesStorageService Storage { get; } = Substitute.For<ISourcesStorageService>();

        public IBewerbungssperreService Sperren { get; } = Substitute.For<IBewerbungssperreService>();

        public INotificationService Notifications { get; } = Substitute.For<INotificationService>();

        public IApplicantDmNotifier Applicant { get; } = Substitute.For<IApplicantDmNotifier>();

        public BewerbungService Service { get; }

        public Rig(SqliteTestContext ctx, DiscordMembership membership, string invite = Invite)
        {
            Membership.CheckAsync("u1", Arg.Any<CancellationToken>()).Returns(membership);
            Direct.GetSettingsAsync(Arg.Any<CancellationToken>())
                .Returns(new DiscordDmSettings(false, false, true, invite));
            Storage.IsAllowedType(Arg.Any<string>()).Returns(true);
            Storage.MaxBytes.Returns(1024 * 1024);
            Storage.SaveAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("gespeichert.pdf");
            var caseNo = Substitute.For<ICaseNumberService>();
            caseNo.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns("FIB-B-2026-0001");
            Service = new BewerbungService(ctx.Factory, caseNo, Storage, new BewerbungBroadcaster(), Sperren,
                Notifications, Applicant, Membership, Direct, Substitute.For<IApplicationCaseService>(),
                Substitute.For<ILogger<BewerbungService>>());
        }
    }

    private static ClaimsPrincipal Applicant(string id = "u1")
        => ClaimsPrincipalBuilder.Agent(id).WithStatus(AgentStatus.Applicant).Build();

    private static BewerbungSubmitModel Model() => new() { StreamConsent = true, Name = "Max Mustermann" };

    private static async Task<int> CountAsync(SqliteTestContext ctx)
    {
        await using var db = ctx.NewContext();
        return await db.Bewerbungen.CountAsync();
    }

    [Fact]
    public async Task Someone_who_is_not_on_the_server_cannot_apply_and_gets_the_invite()
    {
        using var ctx = new SqliteTestContext();
        var rig = new Rig(ctx, DiscordMembership.NotMember);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Service.SubmitAsync(Model(), null, null, null, Applicant()));

        Assert.Equal("Bewerbungen nehmen wir nur von Mitgliedern unseres Discord-Servers an. "
            + $"Tritt ihm bei und sende die Bewerbung dann erneut ab: {Invite}", ex.Message);
        Assert.Equal(0, await CountAsync(ctx));
    }

    [Fact]
    public async Task The_invite_in_the_refusal_is_the_one_an_admin_set()
    {
        using var ctx = new SqliteTestContext();
        var rig = new Rig(ctx, DiscordMembership.NotMember, "https://discord.com/invite/neuer-code");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Service.SubmitAsync(Model(), null, null, null, Applicant()));

        Assert.EndsWith(": https://discord.com/invite/neuer-code", ex.Message);
    }

    [Fact]
    public async Task A_refused_application_saves_nothing_uploads_nothing_and_tells_nobody()
    {
        using var ctx = new SqliteTestContext();
        var rig = new Rig(ctx, DiscordMembership.NotMember);
        await using var file = new MemoryStream([1, 2, 3]);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Service.SubmitAsync(Model(), file, "lebenslauf.pdf", "application/pdf", Applicant(), file.Length));

        Assert.Equal(0, await CountAsync(ctx));
        await rig.Storage.DidNotReceiveWithAnyArgs().SaveAsync(default!, default!, default);
        await rig.Notifications.DidNotReceiveWithAnyArgs()
            .NotifyManyAsync(default!, default, default!, default, default, default);
        await rig.Applicant.DidNotReceiveWithAnyArgs().NotifyAsync(default!, default, default);
    }

    [Theory]
    [InlineData(DiscordMembership.Member)]
    [InlineData(DiscordMembership.Unknown)]
    public async Task A_member_and_an_unknown_answer_both_may_apply(DiscordMembership membership)
    {
        using var ctx = new SqliteTestContext();
        var rig = new Rig(ctx, membership);

        var created = await rig.Service.SubmitAsync(Model(), null, null, null, Applicant());

        Assert.Equal("u1", created.ApplicantUserId);
        Assert.Equal(1, await CountAsync(ctx));
        await rig.Membership.Received(1).CheckAsync("u1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_member_keeps_the_attachment()
    {
        using var ctx = new SqliteTestContext();
        var rig = new Rig(ctx, DiscordMembership.Member);
        await using var file = new MemoryStream([1, 2, 3]);

        var created = await rig.Service.SubmitAsync(Model(), file, "lebenslauf.pdf", "application/pdf", Applicant(), file.Length);

        Assert.Equal("gespeichert.pdf", created.AttachmentFileNameSaved);
        await rig.Storage.Received(1).SaveAsync(file, "lebenslauf.pdf", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_invite_is_only_read_for_someone_who_is_refused()
    {
        using var ctx = new SqliteTestContext();
        var rig = new Rig(ctx, DiscordMembership.Member);

        await rig.Service.SubmitAsync(Model(), null, null, null, Applicant());

        await rig.Direct.DidNotReceiveWithAnyArgs().GetSettingsAsync(default);
    }

    [Fact]
    public async Task The_check_comes_after_the_ban_and_the_existing_application_checks()
    {
        using var ctx = new SqliteTestContext();
        var rig = new Rig(ctx, DiscordMembership.NotMember);
        rig.Sperren.GetActiveAsync("u1", Arg.Any<CancellationToken>())
            .Returns(new BewerbungssperreInfo("s1", "u1", null, "Max", null, true, null, null, DateTime.UtcNow, null));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Service.SubmitAsync(Model(), null, null, null, Applicant()));

        Assert.DoesNotContain("Discord", ex.Message);
        await rig.Membership.DidNotReceiveWithAnyArgs().CheckAsync(default!, default);
    }

    [Fact]
    public async Task A_missing_name_is_reported_before_Discord_is_asked()
    {
        using var ctx = new SqliteTestContext();
        var rig = new Rig(ctx, DiscordMembership.NotMember);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => rig.Service.SubmitAsync(new BewerbungSubmitModel { StreamConsent = true, Name = " " }, null, null, null, Applicant()));

        Assert.Contains("Namen", ex.Message);
        await rig.Membership.DidNotReceiveWithAnyArgs().CheckAsync(default!, default);
    }
}
