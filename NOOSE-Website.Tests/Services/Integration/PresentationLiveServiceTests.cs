using Microsoft.Extensions.Logging.Abstractions;
using NOOSE_Website.Infrastructure.Presentations;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;
using static NOOSE_Website.Tests.Services.Integration.PresentationServiceTests;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="PresentationLiveService"/> on top of the in-memory registry.</summary>
public sealed class PresentationLiveServiceTests
{
    private sealed record Rig(PresentationLiveService Svc, PresentationLiveRegistry Registry, INotificationService Notifications,
        List<(string Id, PresentationLiveEvent Kind)> Events);

    private static Rig Build(SqliteTestContext ctx)
    {
        var registry = new PresentationLiveRegistry();
        var broadcaster = new PresentationBroadcaster();
        var events = new List<(string, PresentationLiveEvent)>();
        broadcaster.Changed += (id, kind) => events.Add((id, kind));
        var notifications = Substitute.For<INotificationService>();
        return new Rig(new PresentationLiveService(ctx.Factory, registry, broadcaster, notifications,
            NullLogger<PresentationLiveService>.Instance), registry, notifications, events);
    }

    private static SqliteTestContext Seeded()
    {
        var ctx = new SqliteTestContext();
        SeedAgents(ctx);
        SeedDecks(ctx);
        return ctx;
    }

    [Fact]
    public async Task Only_owner_or_leadership_may_start_and_the_group_hears_of_it_in_the_bell()
    {
        using var ctx = Seeded();
        var rig = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => rig.Svc.StartAsync("tru", Viewers["tru"]()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => rig.Svc.StartAsync("tru", Viewers["sup"]()));

        var session = await rig.Svc.StartAsync("tru", Viewers["own"]());

        Assert.Equal(("own", 0, PresentationFollowMode.Strict), (session.PresenterId, session.SlideIndex, session.Mode));
        Assert.Contains(("tru", PresentationLiveEvent.Started), rig.Events);
        // selectable TRU agents only; Discord never hears of a closed group
        await rig.Notifications.Received(1).NotifyManyInAppAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { "tru" })),
            NotificationType.PresentationLive, Arg.Any<string>(), "/praesentationen/tru/live", "own",
            Arg.Any<CancellationToken>());
        await rig.Notifications.DidNotReceiveWithAnyArgs().NotifyManyAsync(default!, default, default!, default, default, default);
    }

    [Fact]
    public async Task Starting_again_returns_the_running_session_without_a_second_notice_and_nobody_else_takes_it()
    {
        using var ctx = Seeded();
        var rig = Build(ctx);

        await rig.Svc.StartAsync("priv", Viewers["own"]());
        await rig.Svc.StartAsync("priv", Viewers["own"]());
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Svc.StartAsync("priv", Viewers["lead"]()));

        await rig.Notifications.Received(1).NotifyManyInAppAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { "inv" })),
            NotificationType.PresentationLive, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Only_who_may_see_it_joins_and_counts()
    {
        using var ctx = Seeded();
        var rig = Build(ctx);
        Assert.Null(await rig.Svc.JoinAsync("hrb", "k0", Viewers["hrb"]()));
        await rig.Svc.StartAsync("hrb", Viewers["own"]());

        Assert.Null(await rig.Svc.JoinAsync("hrb", "k1", Viewers["tru"]()));
        var joined = await rig.Svc.JoinAsync("hrb", "k2", Viewers["hrb"]());
        await rig.Svc.JoinAsync("hrb", "k3", Viewers["hrb"]());

        Assert.NotNull(joined);
        // two tabs of one agent: one name, both counted until each closes
        Assert.Equal(["Codename-hrb"], rig.Registry.Get("hrb")!.ViewerCodenames);
        rig.Svc.Leave("hrb", "k2");
        Assert.Equal(["Codename-hrb"], rig.Registry.Get("hrb")!.ViewerCodenames);
        rig.Svc.Leave("hrb", "k3");
        Assert.Empty(rig.Registry.Get("hrb")!.ViewerCodenames);
    }

    [Fact]
    public async Task A_viewer_who_lost_the_right_loses_the_seat_on_rejoin()
    {
        using var ctx = Seeded();
        var rig = Build(ctx);
        await rig.Svc.StartAsync("priv", Viewers["own"]());
        Assert.NotNull(await rig.Svc.JoinAsync("priv", "k1", Viewers["inv"]()));
        await using (var db = ctx.NewContext())
        {
            db.PresentationInvites.RemoveRange(db.PresentationInvites.Where(i => i.PresentationId == "priv"));
            await db.SaveChangesAsync();
        }

        Assert.Null(await rig.Svc.JoinAsync("priv", "k1", Viewers["inv"]()));
        Assert.Empty(rig.Registry.Get("priv")!.ViewerCodenames);
    }

    [Fact]
    public async Task Only_the_presenter_pages_and_switches_the_mode()
    {
        using var ctx = Seeded();
        var rig = Build(ctx);
        await rig.Svc.StartAsync("pub", Viewers["own"]());

        Assert.Throws<UnauthorizedAccessException>(() => rig.Svc.GoTo("pub", 3, Viewers["lead"]()));
        Assert.Throws<UnauthorizedAccessException>(() => rig.Svc.SetMode("pub", PresentationFollowMode.Free, Viewers["tru"]()));

        rig.Svc.GoTo("pub", 2, Viewers["own"]());
        rig.Svc.SetMode("pub", PresentationFollowMode.Free, Viewers["own"]());

        var session = rig.Registry.Get("pub")!;
        Assert.Equal((2, PresentationFollowMode.Free), (session.SlideIndex, session.Mode));
        Assert.Contains(("pub", PresentationLiveEvent.SlideChanged), rig.Events);
        Assert.Contains(("pub", PresentationLiveEvent.ModeChanged), rig.Events);
    }

    [Fact]
    public async Task The_presenter_or_leadership_ends_it_nobody_else()
    {
        using var ctx = Seeded();
        var rig = Build(ctx);
        await rig.Svc.StartAsync("pub", Viewers["own"]());

        Assert.Throws<UnauthorizedAccessException>(() => rig.Svc.End("pub", Viewers["tru"]()));
        rig.Svc.End("pub", Viewers["lead"]());

        Assert.Null(rig.Registry.Get("pub"));
        Assert.Contains(("pub", PresentationLiveEvent.Ended), rig.Events);
    }

    [Fact]
    public async Task The_banner_lists_only_sessions_the_viewer_may_see()
    {
        using var ctx = Seeded();
        var rig = Build(ctx);
        await rig.Svc.StartAsync("pub", Viewers["own"]());
        await rig.Svc.StartAsync("tru", Viewers["own"]());
        await rig.Svc.StartAsync("priv", Viewers["own"]());

        Assert.Equal(["pub", "tru"], (await rig.Svc.GetVisibleLiveAsync(Viewers["tru"]())).Select(s => s.PresentationId).Order());
        Assert.Equal(["priv", "pub"], (await rig.Svc.GetVisibleLiveAsync(Viewers["inv"]())).Select(s => s.PresentationId).Order());
        var partner = ClaimsPrincipalBuilder.Agent("p1").AsPartner(PartnerAgency.LSPD, PartnerRank.Special).Build();
        Assert.Empty(await rig.Svc.GetVisibleLiveAsync(partner));
    }

    [Fact]
    public async Task A_session_ends_when_the_presenter_page_stays_closed_past_the_grace()
    {
        using var ctx = Seeded();
        var rig = Build(ctx);
        await rig.Svc.StartAsync("pub", Viewers["own"]());
        Assert.False(rig.Svc.PresenterConnected("pub", "x", Viewers["lead"]()));
        Assert.True(rig.Svc.PresenterConnected("pub", "tab1", Viewers["own"]()));
        Assert.True(rig.Svc.PresenterConnected("pub", "tab2", Viewers["own"]()));

        // one of two tabs closing keeps it alive
        rig.Svc.PresenterDisconnected("pub", "tab1");
        Assert.Empty(rig.Registry.ExpireAbandoned(DateTime.UtcNow.AddHours(1)));

        rig.Svc.PresenterDisconnected("pub", "tab2");
        Assert.Empty(rig.Registry.ExpireAbandoned(DateTime.UtcNow.AddSeconds(30)));
        Assert.Equal(["pub"], rig.Registry.ExpireAbandoned(DateTime.UtcNow + PresentationLiveRegistry.PresenterGrace + TimeSpan.FromSeconds(5)));
        Assert.Null(rig.Registry.Get("pub"));
    }

    [Fact]
    public async Task A_session_whose_presenter_never_opened_the_page_expires_too()
    {
        using var ctx = Seeded();
        var rig = Build(ctx);
        var started = DateTime.UtcNow;
        await rig.Svc.StartAsync("pub", Viewers["own"]());

        Assert.Equal(["pub"], rig.Registry.ExpireAbandoned(started + PresentationLiveRegistry.PresenterGrace + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task Starting_needs_a_slide()
    {
        using var ctx = Seeded();
        var rig = Build(ctx);
        await using (var db = ctx.NewContext())
        {
            db.PresentationSlides.RemoveRange(db.PresentationSlides.Where(s => s.PresentationId == "pub"));
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Svc.StartAsync("pub", Viewers["own"]()));
        Assert.Null(rig.Registry.Get("pub"));
    }
}
