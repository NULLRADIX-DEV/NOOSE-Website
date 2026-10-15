using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Presentations;
using NOOSE_Website.Infrastructure.Presentations;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;
using static NOOSE_Website.Tests.Services.Integration.PresentationServiceTests;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>An invitation to a live session: the stage, the banner, the bell and the slide pictures, nothing else of
/// the deck, and only until the session ends.</summary>
public sealed class PresentationLiveInviteTests
{
    private static readonly DateTime Ts = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private sealed record Rig(
        PresentationLiveService Live,
        PresentationService Decks,
        TextImageService Images,
        PresentationLiveRegistry Registry,
        INotificationService Notifications,
        List<(string Id, PresentationLiveEvent Kind)> Events);

    private static Rig Build(SqliteTestContext ctx)
    {
        var registry = new PresentationLiveRegistry();
        var broadcaster = new PresentationBroadcaster();
        var events = new List<(string, PresentationLiveEvent)>();
        broadcaster.Changed += (id, kind) => events.Add((id, kind));
        var notifications = Substitute.For<INotificationService>();
        var storage = Substitute.For<ITextImageStorageService>();
        return new Rig(
            new PresentationLiveService(ctx.Factory, registry, broadcaster, notifications, NullLogger<PresentationLiveService>.Instance),
            new PresentationService(ctx.Factory, storage, registry, broadcaster),
            new TextImageService(ctx.Factory, storage, registry),
            registry, notifications, events);
    }

    /// <summary>The HRB deck becomes a Google import with notes and a picture; "jr" may not see it.</summary>
    private static SqliteTestContext Seeded()
    {
        var ctx = new SqliteTestContext();
        SeedAgents(ctx);
        SeedDecks(ctx);
        using var db = ctx.NewContext();
        db.Users.Add(Seed.Agent("p1", Rank.SpecialAgent, configure: a => a.PartnerAgency = PartnerAgency.LSPD));
        db.PresentationFiles.Add(new PresentationFile
        {
            Id = "f1", PresentationId = "hrb", FileNameSaved = "deck.pdf", Title = "Deck", PageCount = 1, ImportedAt = Ts,
            CreatedAt = Ts,
        });
        var slide = db.PresentationSlides.Single(s => s.Id == "hrb-s1");
        slide.Layout = SlideLayout.PdfPage;
        slide.PdfFileId = "f1";
        slide.PageNumber = 1;
        slide.Notes = "nur für den Vortragenden";
        db.TextImages.Add(new TextImage
        {
            Id = "img1", EntityType = nameof(PresentationSlide), EntityId = "hrb-s1", FileNameSaved = "bild.png",
            ContentType = "image/png", SizeBytes = 3,
        });
        db.SaveChanges();
        return ctx;
    }

    /// <summary>Deck, Google PDF and slide picture, each asked on its own so one open path cannot hide behind another.</summary>
    private static async Task<(bool Deck, bool Pdf, bool Picture)> StageAsync(Rig rig, string viewer)
        => (await rig.Decks.GetForLiveAsync("hrb", Viewers[viewer]()) is not null,
            await rig.Decks.GetPdfAccessAsync("f1", Viewers[viewer]()) is not null,
            await rig.Images.GetAccessAsync("img1", Viewers[viewer]()) is not null);

    private static async Task<bool> SeesStageAsync(Rig rig, string viewer)
    {
        var stage = await StageAsync(rig, viewer);
        // all three open together or not at all
        Assert.True(stage == (true, true, true) || stage == (false, false, false), $"{viewer}: {stage}");
        return stage.Deck;
    }

    [Fact]
    public async Task Only_the_presenter_invites_and_only_selectable_agents_are_taken()
    {
        using var ctx = Seeded();
        var rig = Build(ctx);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Live.InviteAsync("hrb", ["jr"], Viewers["own"]()));
        await rig.Live.StartAsync("hrb", Viewers["own"]());

        // the presenter's own id without write access: only the write guard stands in the way
        var ownReadOnly = ClaimsPrincipalBuilder.Agent("own").WithRank(Rank.SpecialAgent).AsTeamLead().Build();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => rig.Live.InviteAsync("hrb", ["jr"], ownReadOnly));
        Assert.Throws<UnauthorizedAccessException>(() => rig.Live.Uninvite("hrb", "jr", ownReadOnly));
        Assert.Empty(rig.Registry.Get("hrb")!.InvitedIds);
        // supervision writes nothing, leadership did not start it, the HRB member may not present it
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => rig.Live.InviteAsync("hrb", ["jr"], Viewers["sup"]()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => rig.Live.InviteAsync("hrb", ["jr"], Viewers["lead"]()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => rig.Live.InviteAsync("hrb", ["jr"], Viewers["hrb"]()));

        // blocked, supervision, partner, oneself and unknown ids fall away
        var added = await rig.Live.InviteAsync("hrb", ["jr", "gone", "sup", "p1", "own", "nobody", "jr"], Viewers["own"]());

        Assert.Equal(1, added);
        Assert.Equal(["jr"], rig.Registry.Get("hrb")!.InvitedIds);
        Assert.Contains(("hrb", PresentationLiveEvent.InvitesChanged), rig.Events);
        await rig.Notifications.Received(1).NotifyManyInAppAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { "jr" })),
            NotificationType.PresentationLive, Arg.Is<string>(t => t.Contains("lädt dich")), "/praesentationen/hrb/live", "own",
            Arg.Any<CancellationToken>());

        // inviting again rings no second time
        Assert.Equal(0, await rig.Live.InviteAsync("hrb", ["jr"], Viewers["own"]()));
        await rig.Notifications.Received(1).NotifyManyInAppAsync(Arg.Any<IReadOnlyCollection<string>>(),
            NotificationType.PresentationLive, Arg.Is<string>(t => t.Contains("lädt dich")), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_invited_agent_watches_the_running_session_and_nothing_else_of_the_deck()
    {
        using var ctx = Seeded();
        var rig = Build(ctx);
        await rig.Live.StartAsync("hrb", Viewers["own"]());
        Assert.False(await SeesStageAsync(rig, "jr"));
        await rig.Live.InviteAsync("hrb", ["jr"], Viewers["own"]());

        Assert.NotNull(await rig.Live.JoinAsync("hrb", "k1", Viewers["jr"]()));
        Assert.Equal(["Codename-jr"], rig.Registry.Get("hrb")!.ViewerCodenames);
        Assert.Contains("hrb", (await rig.Live.GetVisibleLiveAsync(Viewers["jr"]())).Select(s => s.PresentationId));
        Assert.True(await SeesStageAsync(rig, "jr"));

        var deck = (await rig.Decks.GetForLiveAsync("hrb", Viewers["jr"]()))!;
        Assert.True(deck.LiveInviteOnly);
        Assert.False(deck.MayManage);
        Assert.Equal(["hrb-s1"], deck.Slides.Select(s => s.Id));
        Assert.All(deck.Slides, s => Assert.Null(s.Notes));
        Assert.Empty(deck.InviteIds);
        Assert.Empty(deck.Files);

        // detail page, print, editor, list and search read the deck's own visibility, which did not change
        Assert.Null(await rig.Decks.GetAsync("hrb", Viewers["jr"]()));
        Assert.DoesNotContain("hrb", (await rig.Decks.GetListAsync(Viewers["jr"]())).Select(p => p.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => rig.Decks.AddSlideAsync("hrb", SlideLayout.Title, Viewers["jr"]()));

        // another agent the presenter did not invite sees nothing
        Assert.False(await SeesStageAsync(rig, "tru"));
        Assert.Null(await rig.Live.JoinAsync("hrb", "k2", Viewers["tru"]()));
    }

    [Fact]
    public async Task The_invitation_ends_with_the_session_and_does_not_carry_over_to_the_next()
    {
        using var ctx = Seeded();
        var rig = Build(ctx);
        await rig.Live.StartAsync("hrb", Viewers["own"]());
        await rig.Live.InviteAsync("hrb", ["jr"], Viewers["own"]());
        Assert.True(await SeesStageAsync(rig, "jr"));

        rig.Live.End("hrb", Viewers["own"]());

        Assert.False(await SeesStageAsync(rig, "jr"));
        Assert.Empty(await rig.Live.GetVisibleLiveAsync(Viewers["jr"]()));
        await rig.Live.StartAsync("hrb", Viewers["own"]());
        Assert.Empty(rig.Registry.Get("hrb")!.InvitedIds);
        Assert.False(await SeesStageAsync(rig, "jr"));
        Assert.Null(await rig.Live.JoinAsync("hrb", "k1", Viewers["jr"]()));
    }

    [Fact]
    public async Task Only_the_presenter_takes_an_invite_back_and_the_seat_goes_with_it()
    {
        using var ctx = Seeded();
        var rig = Build(ctx);
        await rig.Live.StartAsync("hrb", Viewers["own"]());
        await rig.Live.InviteAsync("hrb", ["jr"], Viewers["own"]());
        Assert.NotNull(await rig.Live.JoinAsync("hrb", "k1", Viewers["jr"]()));

        Assert.Throws<UnauthorizedAccessException>(() => rig.Live.Uninvite("hrb", "jr", Viewers["lead"]()));
        Assert.Throws<UnauthorizedAccessException>(() => rig.Live.Uninvite("hrb", "jr", Viewers["sup"]()));
        rig.Live.Uninvite("hrb", "jr", Viewers["own"]());

        Assert.Empty(rig.Registry.Get("hrb")!.InvitedIds);
        Assert.Equal(2, rig.Events.Count(e => e == ("hrb", PresentationLiveEvent.InvitesChanged)));
        // the page checks again on that event and gives the seat back
        Assert.Null(await rig.Live.JoinAsync("hrb", "k1", Viewers["jr"]()));
        Assert.Empty(rig.Registry.Get("hrb")!.ViewerCodenames);
        Assert.False(await SeesStageAsync(rig, "jr"));
    }

    [Fact]
    public async Task An_invited_agent_who_is_no_longer_selectable_sees_nothing()
    {
        using var ctx = Seeded();
        var rig = Build(ctx);
        await rig.Live.StartAsync("hrb", Viewers["own"]());
        await rig.Live.InviteAsync("hrb", ["jr"], Viewers["own"]());
        await using (var db = ctx.NewContext())
        {
            (await db.Users.SingleAsync(u => u.Id == "jr")).Status = AgentStatus.Blocked;
            await db.SaveChangesAsync();
        }

        Assert.False(await SeesStageAsync(rig, "jr"));
        Assert.Null(await rig.Live.JoinAsync("hrb", "k1", Viewers["jr"]()));
        Assert.Empty(await rig.Live.GetVisibleLiveAsync(Viewers["jr"]()));
    }

    [Fact]
    public async Task A_partner_never_watches_even_when_the_registry_lists_him()
    {
        using var ctx = Seeded();
        var rig = Build(ctx);
        await rig.Live.StartAsync("hrb", Viewers["own"]());
        // past the service, which would have dropped the partner
        rig.Registry.Invite("hrb", "own", ["p1"]);
        var partner = ClaimsPrincipalBuilder.Agent("p1").AsPartner(PartnerAgency.LSPD, PartnerRank.Special).Build();

        await using var db = ctx.NewContext();
        Assert.False(await PresentationLiveAccess.IsInvitedViewerAsync(db, rig.Registry, "hrb", partner));
        Assert.Empty(await rig.Live.GetVisibleLiveAsync(partner));
        Assert.Null(await rig.Images.GetAccessAsync("img1", partner));
    }

    [Fact]
    public async Task Starting_with_invites_rings_the_group_once_and_the_invited_outside_it_with_the_invitation()
    {
        using var ctx = Seeded();
        var rig = Build(ctx);

        await rig.Live.StartAsync("hrb", Viewers["own"](), ["jr", "hrb", "p1"]);

        Assert.Equal(["hrb", "jr"], rig.Registry.Get("hrb")!.InvitedIds);
        await rig.Notifications.Received(1).NotifyManyInAppAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { "hrb" })),
            NotificationType.PresentationLive, Arg.Is<string>(t => t.Contains("präsentiert jetzt")), "/praesentationen/hrb/live",
            "own", Arg.Any<CancellationToken>());
        // the HRB member already heard of the start; the bell of the invitation goes to the outsider only
        await rig.Notifications.Received(1).NotifyManyInAppAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { "jr" })),
            NotificationType.PresentationLive, Arg.Is<string>(t => t.Contains("lädt dich")), "/praesentationen/hrb/live", "own",
            Arg.Any<CancellationToken>());
        await rig.Notifications.Received(1).NotifyManyInAppAsync(Arg.Any<IReadOnlyCollection<string>>(),
            NotificationType.PresentationLive, Arg.Is<string>(t => t.Contains("lädt dich")), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        Assert.True(await SeesStageAsync(rig, "jr"));
    }
}
