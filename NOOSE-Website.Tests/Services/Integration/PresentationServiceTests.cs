using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Presentations;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Infrastructure.Presentations;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Presentations;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="PresentationService"/> and <see cref="PresentationVisibility"/>.</summary>
public sealed class PresentationServiceTests
{
    private static readonly DateTime Ts = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>Keeps saved pictures in memory and records deletions.</summary>
    private sealed class FakeImageStorage : ITextImageStorageService
    {
        public List<string> Saved { get; } = [];
        public List<string> Deleted { get; } = [];
        public long MaxBytes => 1024;
        public bool IsAllowedType(string contentType) => contentType is "image/png" or "image/jpeg";

        public Task<string> SaveAsync(Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            var name = $"{Guid.NewGuid()}.png";
            Saved.Add(name);
            return Task.FromResult(name);
        }

        public Stream OpenRead(string fileNameSaved) => new MemoryStream([1, 2, 3]);
        public void Delete(string fileNameSaved) => Deleted.Add(fileNameSaved);
    }

    private static (PresentationService Svc, FakeImageStorage Storage, PresentationLiveRegistry Registry) Build(SqliteTestContext ctx,
        PresentationBroadcaster? broadcaster = null)
    {
        var storage = new FakeImageStorage();
        var registry = new PresentationLiveRegistry();
        return (new PresentationService(ctx.Factory, storage, registry, broadcaster ?? new PresentationBroadcaster()), storage, registry);
    }

    internal static ClaimsPrincipal Agent(string id, Rank rank = Rank.SpecialAgent)
        => ClaimsPrincipalBuilder.Agent(id).WithRank(rank).WithCodename($"Codename-{id}").Build();

    internal static readonly Dictionary<string, Func<ClaimsPrincipal>> Viewers = new()
    {
        ["own"] = () => Agent("own"),
        ["tru"] = () => ClaimsPrincipalBuilder.Agent("tru").WithRank(Rank.SpecialAgent).WithCodename("Codename-tru").AsTru().Build(),
        ["hrb"] = () => ClaimsPrincipalBuilder.Agent("hrb").WithRank(Rank.SpecialAgent).WithCodename("Codename-hrb").AsHrb().Build(),
        ["jr"] = () => Agent("jr", Rank.JuniorAgent),
        ["tfm"] = () => Agent("tfm"),
        ["inv"] = () => Agent("inv", Rank.JuniorAgent),
        ["lead"] = () => Agent("lead", Rank.Director),
        // read-only supervision reads like leadership, but writes nothing
        ["sup"] = () => ClaimsPrincipalBuilder.Agent("sup").WithRank(Rank.Director).AsTeamLead().Build(),
    };

    internal static void SeedAgents(SqliteTestContext ctx)
    {
        using var db = ctx.NewContext();
        db.Users.AddRange(
            Seed.Agent("own", Rank.SpecialAgent),
            Seed.Agent("tru", Rank.SpecialAgent, configure: a => a.IsTRU = true),
            Seed.Agent("hrb", Rank.SpecialAgent, configure: a => a.IsHRB = true),
            Seed.Agent("jr", Rank.JuniorAgent),
            Seed.Agent("tfm", Rank.SpecialAgent),
            Seed.Agent("inv", Rank.JuniorAgent),
            Seed.Agent("lead", Rank.Director),
            Seed.Agent("sup", Rank.Director, configure: a => a.IsTeamLead = true),
            Seed.Agent("gone", Rank.SpecialAgent, AgentStatus.Blocked));
        db.Taskforces.Add(new Taskforce { Id = "tf1", Name = "Kobra", CaseNumber = "NOOSE-TF-2026-0001", CreatedAt = Ts });
        db.TaskforceAgents.Add(new TaskforceAgent { TaskforceId = "tf1", AgentId = "tfm" });
        db.SaveChanges();
    }

    /// <summary>One deck per kind of access, all owned by "own".</summary>
    internal static void SeedDecks(SqliteTestContext ctx)
    {
        using var db = ctx.NewContext();
        Presentation Deck(string id, PresentationAccess access, AnnouncementAudience? audience = null, string? targetId = null,
            Rank? minRank = null)
            => new()
            {
                Id = id, Title = id, Access = access, Audience = audience, TargetId = targetId, MinRank = minRank,
                OwnerId = "own", CreatedAt = Ts,
                Slides = [new PresentationSlide { Id = $"{id}-s1", Layout = SlideLayout.Title, Title = id, Sorting = 1, CreatedAt = Ts }],
            };
        db.Presentations.AddRange(
            Deck("pub", PresentationAccess.Public),
            Deck("tru", PresentationAccess.Closed, AnnouncementAudience.TruUnit),
            Deck("hrb", PresentationAccess.Closed, AnnouncementAudience.HrbUnit),
            Deck("rank", PresentationAccess.Closed, AnnouncementAudience.FromRank, minRank: Rank.SpecialAgent),
            Deck("tf", PresentationAccess.Closed, AnnouncementAudience.Taskforce, targetId: "tf1"),
            Deck("priv", PresentationAccess.Private));
        db.PresentationInvites.Add(new PresentationInvite { PresentationId = "priv", AgentId = "inv", CreatedAt = Ts });
        db.SaveChanges();
    }

    // ---------- visibility ----------

    [Theory]
    [InlineData("own", "hrb,priv,pub,rank,tf,tru")]
    [InlineData("tru", "pub,rank,tru")]
    [InlineData("hrb", "hrb,pub,rank")]
    [InlineData("jr", "pub")]
    [InlineData("tfm", "pub,rank,tf")]
    [InlineData("inv", "priv,pub")]
    [InlineData("lead", "hrb,priv,pub,rank,tf,tru")]
    [InlineData("sup", "hrb,priv,pub,rank,tf,tru")]
    public async Task Each_viewer_sees_public_their_groups_and_their_invites(string viewer, string expected)
    {
        using var ctx = new SqliteTestContext();
        SeedAgents(ctx);
        SeedDecks(ctx);
        var (svc, _, _) = Build(ctx);

        var list = await svc.GetListAsync(Viewers[viewer]());

        Assert.Equal(expected, string.Join(',', list.Select(p => p.Id).Order(StringComparer.Ordinal)));
        foreach (var id in new[] { "pub", "tru", "hrb", "rank", "tf", "priv" })
        {
            Assert.Equal(expected.Split(',').Contains(id), await svc.GetAsync(id, Viewers[viewer]()) is not null);
        }
    }

    [Fact]
    public async Task Partners_and_citizens_see_nothing()
    {
        using var ctx = new SqliteTestContext();
        SeedAgents(ctx);
        SeedDecks(ctx);
        var (svc, _, _) = Build(ctx);
        var partner = ClaimsPrincipalBuilder.Agent("p1").AsPartner(PartnerAgency.LSPD, PartnerRank.Special).Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.GetListAsync(partner));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.GetAsync("pub", partner));
        await using var db = ctx.NewContext();
        Assert.False(await PresentationVisibility.IsVisibleAsync(db, "pub", ViewerScope.From(partner)));
    }

    [Fact]
    public async Task Only_owner_and_leadership_may_manage_and_the_view_says_so()
    {
        using var ctx = new SqliteTestContext();
        SeedAgents(ctx);
        SeedDecks(ctx);
        var (svc, _, _) = Build(ctx);

        Assert.True((await svc.GetAsync("pub", Viewers["own"]()))!.MayManage);
        Assert.True((await svc.GetAsync("pub", Viewers["lead"]()))!.MayManage);
        Assert.False((await svc.GetAsync("pub", Viewers["tru"]()))!.MayManage);
        // supervision reads it but writes nothing
        Assert.False((await svc.GetAsync("pub", Viewers["sup"]()))!.MayManage);

        var input = new PresentationInput { Title = "Neu", Access = PresentationAccess.Public };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.UpdateAsync("pub", input, Viewers["tru"]()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.DeleteAsync("pub", Viewers["tru"]()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.AddSlideAsync("pub", SlideLayout.TitleText, Viewers["tru"]()));
        await svc.UpdateAsync("pub", input, Viewers["lead"]());
    }

    // ---------- creating ----------

    [Fact]
    public async Task Create_needs_write_access_as_an_internal_agent()
    {
        using var ctx = new SqliteTestContext();
        SeedAgents(ctx);
        var (svc, _, _) = Build(ctx);
        var input = new PresentationInput { Title = "Lage", Access = PresentationAccess.Public };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateAsync(input, Viewers["sup"]()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateAsync(input, ClaimsPrincipalBuilder.Agent("d").WithRank(Rank.Director).AsDemo().Build()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateAsync(input, ClaimsPrincipalBuilder.Agent("p1").AsPartner(PartnerAgency.LSPD, PartnerRank.Special).Build()));

        var created = await svc.CreateAsync(input, Viewers["jr"]());

        await using var db = ctx.NewContext();
        var stored = await db.Presentations.Include(p => p.Slides).SingleAsync(p => p.Id == created.Id);
        Assert.Equal("jr", stored.OwnerId);
        // it starts with its title slide
        var slide = Assert.Single(stored.Slides);
        Assert.Equal(SlideLayout.Title, slide.Layout);
        Assert.Equal("Lage", slide.Title);
    }

    [Theory]
    [InlineData("tru", AnnouncementAudience.TruUnit, null, null, true)]
    [InlineData("hrb", AnnouncementAudience.TruUnit, null, null, false)]
    [InlineData("hrb", AnnouncementAudience.HrbUnit, null, null, true)]
    [InlineData("tru", AnnouncementAudience.HrbUnit, null, null, false)]
    [InlineData("tfm", AnnouncementAudience.Taskforce, "tf1", null, true)]
    [InlineData("tru", AnnouncementAudience.Taskforce, "tf1", null, false)]
    [InlineData("tru", AnnouncementAudience.FromRank, null, Rank.SpecialAgent, true)]
    [InlineData("jr", AnnouncementAudience.FromRank, null, Rank.SpecialAgent, false)]
    [InlineData("lead", AnnouncementAudience.TruUnit, null, null, true)]
    [InlineData("lead", AnnouncementAudience.Taskforce, "tf1", null, true)]
    public async Task A_closed_presentation_may_only_address_a_group_its_author_is_in(string author,
        AnnouncementAudience audience, string? targetId, Rank? minRank, bool allowed)
    {
        using var ctx = new SqliteTestContext();
        SeedAgents(ctx);
        var (svc, _, _) = Build(ctx);
        var input = new PresentationInput
        {
            Title = "Briefing", Access = PresentationAccess.Closed, Audience = audience, TargetId = targetId, MinRank = minRank,
        };

        if (allowed)
        {
            var created = await svc.CreateAsync(input, Viewers[author]());
            await using var db = ctx.NewContext();
            var stored = await db.Presentations.SingleAsync(p => p.Id == created.Id);
            Assert.Equal(audience, stored.Audience);
        }
        else
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateAsync(input, Viewers[author]()));
        }
    }

    [Fact]
    public async Task A_closed_presentation_needs_a_valid_group()
    {
        using var ctx = new SqliteTestContext();
        SeedAgents(ctx);
        var (svc, _, _) = Build(ctx);

        Task Create(AnnouncementAudience? audience, string? targetId = null, Rank? minRank = null)
            => svc.CreateAsync(new PresentationInput
            {
                Title = "X", Access = PresentationAccess.Closed, Audience = audience, TargetId = targetId, MinRank = minRank,
            }, Viewers["lead"]());

        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(null));
        // "all active" is the public access, not a group
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(AnnouncementAudience.AllActive));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(AnnouncementAudience.Taskforce, "nope"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Create(AnnouncementAudience.FromRank));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync(new PresentationInput { Title = "  " }, Viewers["lead"]()));
    }

    [Fact]
    public async Task Invites_keep_selectable_agents_only_and_go_when_the_deck_is_no_longer_private()
    {
        using var ctx = new SqliteTestContext();
        SeedAgents(ctx);
        var (svc, _, _) = Build(ctx);

        var created = await svc.CreateAsync(new PresentationInput
        {
            Title = "Geheim", Access = PresentationAccess.Private, InviteIds = ["inv", "inv", "gone", "sup", "unknown", "jr"],
        }, Viewers["own"]());

        await using (var db = ctx.NewContext())
        {
            Assert.Equal(["inv", "jr"], db.PresentationInvites.Where(i => i.PresentationId == created.Id)
                .Select(i => i.AgentId).OrderBy(x => x).ToList());
        }
        Assert.NotNull(await svc.GetAsync(created.Id, Viewers["inv"]()));
        Assert.Null(await svc.GetAsync(created.Id, Viewers["tru"]()));

        await svc.UpdateAsync(created.Id, new PresentationInput
        {
            Title = "Geheim", Access = PresentationAccess.Private, InviteIds = ["jr"],
        }, Viewers["own"]());
        Assert.Null(await svc.GetAsync(created.Id, Viewers["inv"]()));

        await svc.UpdateAsync(created.Id, new PresentationInput
        {
            Title = "Offen", Access = PresentationAccess.Public, InviteIds = ["jr"],
        }, Viewers["own"]());
        await using (var db = ctx.NewContext())
        {
            Assert.Empty(db.PresentationInvites.Where(i => i.PresentationId == created.Id));
        }
    }

    // ---------- slides ----------

    [Fact]
    public async Task Slides_are_added_moved_duplicated_and_deleted_in_order()
    {
        using var ctx = new SqliteTestContext();
        SeedAgents(ctx);
        SeedDecks(ctx);
        var (svc, _, _) = Build(ctx);
        var own = Viewers["own"]();

        var second = await svc.AddSlideAsync("pub", SlideLayout.TitleText, own);
        var third = await svc.AddSlideAsync("pub", SlideLayout.Section, own);
        await svc.MoveSlideAsync(third.Id, 0, own);
        var copy = await svc.DuplicateSlideAsync(second.Id, own);

        async Task<List<string>> Order()
            => (await svc.GetAsync("pub", own))!.Slides.Select(s => s.Id).ToList();

        Assert.Equal([third.Id, "pub-s1", second.Id, copy.Id], await Order());

        await svc.DeleteSlideAsync("pub-s1", own);
        Assert.Equal([third.Id, second.Id, copy.Id], await Order());
        await using var db = ctx.NewContext();
        Assert.Equal([1, 2, 3], db.PresentationSlides.Where(s => s.PresentationId == "pub")
            .OrderBy(s => s.Sorting).Select(s => s.Sorting).ToList());
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AddSlideAsync("pub", SlideLayout.PdfPage, own));
    }

    [Fact]
    public async Task Update_cleans_the_text_and_drops_it_for_a_layout_without_text()
    {
        using var ctx = new SqliteTestContext();
        SeedAgents(ctx);
        SeedDecks(ctx);
        var (svc, _, _) = Build(ctx);
        var own = Viewers["own"]();

        await svc.UpdateSlideAsync("pub-s1", new SlideInput
        {
            Layout = SlideLayout.TitleText, Title = " Lage ", BodyHtml = "<p>Text<script>alert(1)</script></p>", Notes = "Langsam",
        }, own);
        await using (var db = ctx.NewContext())
        {
            var slide = await db.PresentationSlides.SingleAsync(s => s.Id == "pub-s1");
            Assert.Equal("Lage", slide.Title);
            Assert.DoesNotContain("script", slide.BodyHtml);
            Assert.Contains("Text", slide.BodyHtml);
            Assert.Equal("Langsam", slide.Notes);
        }

        await svc.UpdateSlideAsync("pub-s1", new SlideInput { Layout = SlideLayout.Image, BodyHtml = "<p>weg</p>" }, own);
        await using (var db = ctx.NewContext())
        {
            Assert.Null((await db.PresentationSlides.SingleAsync(s => s.Id == "pub-s1")).BodyHtml);
        }
    }

    [Fact]
    public async Task A_slide_picture_is_replaced_copied_on_duplicate_and_removed_with_its_slide()
    {
        using var ctx = new SqliteTestContext();
        SeedAgents(ctx);
        SeedDecks(ctx);
        var (svc, storage, _) = Build(ctx);
        var own = Viewers["own"]();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SetSlideImageAsync("pub-s1", new MemoryStream([1]), "image/svg+xml", own));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SetSlideImageAsync("pub-s1", new MemoryStream(new byte[2048]), "image/png", own));

        await svc.SetSlideImageAsync("pub-s1", new MemoryStream([1, 2]), "image/png", own);
        await svc.SetSlideImageAsync("pub-s1", new MemoryStream([3, 4]), "image/png", own);
        Assert.Equal(2, storage.Saved.Count);
        Assert.Equal([storage.Saved[0]], storage.Deleted);

        var copy = await svc.DuplicateSlideAsync("pub-s1", own);
        await using (var db = ctx.NewContext())
        {
            var original = await db.PresentationSlides.SingleAsync(s => s.Id == "pub-s1");
            var duplicate = await db.PresentationSlides.SingleAsync(s => s.Id == copy.Id);
            Assert.NotNull(duplicate.ImageId);
            Assert.NotEqual(original.ImageId, duplicate.ImageId);
            var image = await db.TextImages.SingleAsync(t => t.Id == duplicate.ImageId);
            Assert.Equal((nameof(PresentationSlide), copy.Id), (image.EntityType, image.EntityId));
        }

        await svc.DeleteSlideAsync(copy.Id, own);
        await svc.RemoveSlideImageAsync("pub-s1", own);
        Assert.Equal(3, storage.Deleted.Count);
        await using (var db = ctx.NewContext())
        {
            Assert.Empty(db.TextImages.IgnoreQueryFilters().Where(t => t.EntityType == nameof(PresentationSlide)));
        }
    }

    [Fact]
    public async Task A_duplicate_gets_its_own_copy_of_the_pictures_in_its_text_and_a_deleted_slide_takes_its_own_along()
    {
        using var ctx = new SqliteTestContext();
        SeedAgents(ctx);
        SeedDecks(ctx);
        var (svc, storage, _) = Build(ctx);
        var own = Viewers["own"]();
        const string pictureId = "0b5c3a4e-1d2f-4a6b-9c8d-7e6f5a4b3c2d";
        await using (var db = ctx.NewContext())
        {
            db.TextImages.Add(new TextImage
            {
                Id = pictureId, EntityType = nameof(PresentationSlide), EntityId = "pub-s1", FileNameSaved = "inline.png",
                ContentType = "image/png", SizeBytes = 3, CreatedAt = Ts,
            });
            var slide = await db.PresentationSlides.SingleAsync(s => s.Id == "pub-s1");
            slide.BodyHtml = $"<p><img src=\"/dateien/textbilder/{pictureId}\"></p>";
            await db.SaveChangesAsync();
        }

        var copy = await svc.DuplicateSlideAsync("pub-s1", own);

        string copiedId;
        await using (var db = ctx.NewContext())
        {
            var duplicate = await db.PresentationSlides.SingleAsync(s => s.Id == copy.Id);
            Assert.DoesNotContain(pictureId, duplicate.BodyHtml);
            var row = await db.TextImages.SingleAsync(t => t.EntityType == nameof(PresentationSlide) && t.EntityId == copy.Id);
            copiedId = row.Id;
            Assert.Contains($"/dateien/textbilder/{copiedId}", duplicate.BodyHtml);
        }

        // deleting the original leaves the copy's picture alone
        await svc.DeleteSlideAsync("pub-s1", own);
        Assert.Equal(["inline.png"], storage.Deleted);
        await using (var db = ctx.NewContext())
        {
            Assert.False(await db.TextImages.IgnoreQueryFilters().AnyAsync(t => t.Id == pictureId));
            Assert.True(await db.TextImages.AnyAsync(t => t.Id == copiedId));
        }
    }

    [Fact]
    public async Task A_running_deck_follows_renames_and_slide_edits()
    {
        using var ctx = new SqliteTestContext();
        SeedAgents(ctx);
        SeedDecks(ctx);
        var broadcaster = new PresentationBroadcaster();
        var events = new List<PresentationLiveEvent>();
        broadcaster.Changed += (_, kind) => events.Add(kind);
        var (svc, _, registry) = Build(ctx, broadcaster);
        var own = Viewers["own"]();

        // not live: nobody needs to hear of slide edits
        await svc.AddSlideAsync("pub", SlideLayout.TitleText, own);
        Assert.Empty(events);

        registry.Start("pub", "pub", "own", "Codename-own", Ts);
        await svc.UpdateAsync("pub", new PresentationInput { Title = "Neuer Titel", Access = PresentationAccess.Public }, own);
        await svc.AddSlideAsync("pub", SlideLayout.Section, own);

        Assert.Equal("Neuer Titel", registry.Get("pub")!.Title);
        Assert.Equal([PresentationLiveEvent.Started, PresentationLiveEvent.SlidesChanged], events);
    }

    [Fact]
    public async Task A_slide_picture_is_visible_exactly_where_its_presentation_is()
    {
        using var ctx = new SqliteTestContext();
        SeedAgents(ctx);
        SeedDecks(ctx);
        var (svc, _, _) = Build(ctx);
        await svc.SetSlideImageAsync("tru-s1", new MemoryStream([1]), "image/png", Viewers["own"]());

        await using var db = ctx.NewContext();
        Assert.True(await Visibility.IsRecordVisibleAsync(db, nameof(PresentationSlide), "tru-s1", ViewerScope.From(Viewers["tru"]())));
        Assert.False(await Visibility.IsRecordVisibleAsync(db, nameof(PresentationSlide), "tru-s1", ViewerScope.From(Viewers["hrb"]())));
    }

    // ---------- trash ----------

    [Fact]
    public async Task Delete_ends_a_live_session()
    {
        using var ctx = new SqliteTestContext();
        SeedAgents(ctx);
        SeedDecks(ctx);
        var (svc, _, registry) = Build(ctx);
        registry.Start("pub", "pub", "own", "Codename-own", Ts);

        await svc.DeleteAsync("pub", Viewers["own"]());

        Assert.Null(registry.Get("pub"));
        Assert.Null(await svc.GetAsync("pub", Viewers["own"]()));
    }

    [Fact]
    public async Task The_trash_lists_deleted_decks_and_only_leadership_restores()
    {
        using var ctx = new SqliteTestContext();
        SeedAgents(ctx);
        SeedDecks(ctx);
        // the soft-delete interceptor is not part of the test context; mark it the way it would
        await using (var db = ctx.NewContext())
        {
            var deck = await db.Presentations.SingleAsync(p => p.Id == "pub");
            deck.IsDeleted = true;
            deck.DeletedAt = Ts;
            await db.SaveChangesAsync();
        }
        var (svc, _, _) = Build(ctx);

        Assert.Equal(["pub"], (await svc.GetTrashAsync()).Select(p => p.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RestoreAsync("pub", Viewers["own"]()));

        await svc.RestoreAsync("pub", Viewers["lead"]());
        Assert.NotNull(await svc.GetAsync("pub", Viewers["own"]()));
        Assert.Empty(await svc.GetTrashAsync());
    }
}
