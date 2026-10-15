using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using NOOSE_Website.Authorization;
using NOOSE_Website.Infrastructure.Presentations;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Presentations;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Import;
using NOOSE_Website.Tests.Services.Integration;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Import;

/// <summary>Integration tests for <see cref="SlidesImportService"/> against in-memory SQLite.</summary>
public sealed class SlidesImportServiceTests
{
    private const string GoogleId = "1AbCdEfGhIjKlMnOpQrStUvWxYz0123456789";
    private const string Link = $"https://docs.google.com/presentation/d/{GoogleId}/edit#slide=id.p";

    /// <summary>Keeps saved PDFs in memory and records deletions.</summary>
    private sealed class FakeFileStorage : IPresentationFileStorageService
    {
        public Dictionary<string, byte[]> Files { get; } = [];
        public List<string> Deleted { get; } = [];
        public long MaxBytes => 5 * 1024 * 1024;

        public Task<string> SaveAsync(byte[] content, CancellationToken cancellationToken = default)
        {
            var name = $"{Guid.NewGuid():N}.pdf";
            Files[name] = content;
            return Task.FromResult(name);
        }

        public Stream OpenRead(string fileNameSaved) => new MemoryStream(Files[fileNameSaved]);

        public void Delete(string fileNameSaved)
        {
            Files.Remove(fileNameSaved);
            Deleted.Add(fileNameSaved);
        }
    }

    private sealed record Rig(
        SlidesImportService Svc,
        PresentationService Presentations,
        IGoogleExportFetcher Fetcher,
        FakeFileStorage Storage,
        PresentationLiveRegistry Registry,
        PresentationBroadcaster Broadcaster);

    private static Rig Build(SqliteTestContext ctx, int googlePages = 3)
    {
        var fetcher = Substitute.For<IGoogleExportFetcher>();
        fetcher.FetchPresentationPdfAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(TestPdf.Make(googlePages, "Deck aus Google"));
        var storage = new FakeFileStorage();
        var registry = new PresentationLiveRegistry();
        var broadcaster = new PresentationBroadcaster();
        var images = Substitute.For<ITextImageStorageService>();
        return new Rig(
            new SlidesImportService(ctx.Factory, fetcher, storage, new InProcessPdfInspector(), registry, broadcaster,
                NullLogger<SlidesImportService>.Instance),
            new PresentationService(ctx.Factory, images, registry, broadcaster),
            fetcher, storage, registry, broadcaster);
    }

    private static void GoogleAnswers(Rig rig, int pages)
        => rig.Fetcher.FetchPresentationPdfAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(TestPdf.Make(pages, "Deck aus Google"));

    // a unique owner per test keeps the process-wide import quota out of the other tests' way
    private static ClaimsPrincipal Owner(Rank rank = Rank.SpecialAgent)
        => ClaimsPrincipalBuilder.Agent(Guid.NewGuid().ToString()).WithRank(rank).Build();

    private static SlidesImportInput NewDeck(string? link = Link, byte[]? upload = null, string? title = null)
        => new()
        {
            Link = link,
            UploadPdf = upload,
            Settings = new PresentationInput { Title = title ?? string.Empty, Access = PresentationAccess.Public },
        };

    /// <summary>The deck's slides in order: "p{page}" for a Google slide, the layout otherwise.</summary>
    private static string Order(SqliteTestContext ctx, string presentationId)
    {
        using var db = ctx.NewContext();
        return string.Join(',', db.PresentationSlides.Where(s => s.PresentationId == presentationId).OrderBy(s => s.Sorting)
            .AsEnumerable().Select(s => s.Layout == SlideLayout.PdfPage ? $"p{s.PageNumber}" : s.Layout.ToString()));
    }

    // ---------- import ----------

    [Fact]
    public async Task A_link_import_makes_a_deck_of_page_slides_named_like_the_google_deck()
    {
        using var ctx = new SqliteTestContext();
        var rig = Build(ctx);

        var id = await rig.Svc.ImportAsync(NewDeck(), Owner());

        using var db = ctx.NewContext();
        Assert.Equal("Deck aus Google", db.Presentations.Single(p => p.Id == id).Title);
        // the deck brings its own title slide, so none is added in front
        Assert.Equal("p1,p2,p3", Order(ctx, id));
        Assert.Equal([1, 2, 3], db.PresentationSlides.Where(s => s.PresentationId == id).OrderBy(s => s.Sorting).Select(s => s.Sorting));
        var file = db.PresentationFiles.Single(f => f.PresentationId == id);
        Assert.Equal((3, ImportSourceKind.Link, GoogleId, 1), (file.PageCount, file.SourceKind, file.SourceId, file.Revision));
        Assert.Equal("Deck aus Google", file.Title);
        Assert.True(rig.Storage.Files.ContainsKey(file.FileNameSaved));
        Assert.All(db.PresentationSlides.Where(s => s.PresentationId == id), s => Assert.Equal(file.Id, s.PdfFileId));
        await rig.Fetcher.Received(1).FetchPresentationPdfAsync(GoogleId, rig.Storage.MaxBytes, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_upload_appends_its_pages_behind_the_slides_already_there()
    {
        using var ctx = new SqliteTestContext();
        var rig = Build(ctx);
        var owner = Owner();
        var deck = await rig.Presentations.CreateAsync(new PresentationInput { Title = "Eigene", Access = PresentationAccess.Public }, owner);

        await rig.Svc.ImportAsync(new SlidesImportInput
        {
            UploadPdf = TestPdf.Make(2), UploadName = "Briefing Kobra.pdf", PresentationId = deck.Id,
        }, owner);

        Assert.Equal("Title,p1,p2", Order(ctx, deck.Id));
        using var db = ctx.NewContext();
        var file = db.PresentationFiles.Single(f => f.PresentationId == deck.Id);
        // the PDF has no title of its own: the file name stands in
        Assert.Equal("Briefing Kobra", file.Title);
        Assert.Equal((ImportSourceKind.Upload, (string?)null), (file.SourceKind, file.SourceId));
        Assert.Equal("Eigene", db.Presentations.Single(p => p.Id == deck.Id).Title);
        await rig.Fetcher.DidNotReceiveWithAnyArgs().FetchPresentationPdfAsync(default!, default, default);
    }

    [Fact]
    public async Task A_typed_title_wins_and_a_group_of_others_is_refused_before_google_is_asked()
    {
        using var ctx = new SqliteTestContext();
        var rig = Build(ctx);

        var id = await rig.Svc.ImportAsync(NewDeck(title: "Eigener Titel"), Owner());
        var closed = NewDeck();
        closed.Settings!.Access = PresentationAccess.Closed;
        closed.Settings.Audience = AnnouncementAudience.HrbUnit;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => rig.Svc.ImportAsync(closed, Owner()));

        using var db = ctx.NewContext();
        Assert.Equal("Eigener Titel", db.Presentations.Single(p => p.Id == id).Title);
        // the failed import left the dialog's fields as typed
        Assert.Equal(string.Empty, closed.Settings.Title);
        await rig.Fetcher.Received(1).FetchPresentationPdfAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Only_owner_or_leadership_adds_pages_and_partners_supervision_and_demo_never_import()
    {
        using var ctx = new SqliteTestContext();
        var rig = Build(ctx);
        var owner = Owner();
        var deck = await rig.Presentations.CreateAsync(new PresentationInput { Title = "Deck", Access = PresentationAccess.Public }, owner);
        var append = new SlidesImportInput { Link = Link, PresentationId = deck.Id };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => rig.Svc.ImportAsync(append, Owner()));
        ClaimsPrincipal[] refused =
        [
            ClaimsPrincipalBuilder.Agent("p").AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build(),
            ClaimsPrincipalBuilder.Agent("tl").WithRank(Rank.Director).AsTeamLead().Build(),
            ClaimsPrincipalBuilder.Agent("demo").WithRank(Rank.Director).AsHrb().AsDemo().Build(),
        ];
        foreach (var actor in refused)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => rig.Svc.ImportAsync(NewDeck(), actor));
        }
        await rig.Fetcher.DidNotReceiveWithAnyArgs().FetchPresentationPdfAsync(default!, default, default);

        await rig.Svc.ImportAsync(append, Owner(Rank.Director));
        Assert.Equal("Title,p1,p2,p3", Order(ctx, deck.Id));
    }

    [Fact]
    public async Task A_mistyped_link_or_a_file_that_is_no_pdf_costs_no_quota()
    {
        using var ctx = new SqliteTestContext();
        var rig = Build(ctx);
        var actor = Owner();

        for (var i = 0; i <= GoogleImportQuota.PerWindow; i++)
        {
            await Assert.ThrowsAsync<GoogleExportException>(() => rig.Svc.ImportAsync(
                NewDeck(link: "https://docs.google.com/document/d/" + GoogleId), actor));
            await Assert.ThrowsAsync<GoogleExportException>(() => rig.Svc.ImportAsync(
                NewDeck(link: null, upload: "<html>kein PDF</html>"u8.ToArray()), actor));
        }

        await rig.Svc.ImportAsync(NewDeck(link: null, upload: TestPdf.Make(1)), actor);
        Assert.Single(rig.Storage.Files);
    }

    [Fact]
    public async Task Docs_and_slides_imports_share_one_quota()
    {
        using var ctx = new SqliteTestContext();
        var rig = Build(ctx);
        var actor = Owner();

        for (var i = 0; i < GoogleImportQuota.PerWindow; i++)
        {
            await rig.Svc.ImportAsync(NewDeck(link: null, upload: TestPdf.Make(1)), actor);
        }

        var docs = new GoogleImportService(ctx.Factory, rig.Fetcher, Substitute.For<ITextImageStorageService>(),
            NullLogger<GoogleImportService>.Instance);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => docs.ImportDocumentAsync(
            new GoogleDocImportInput { Link = $"https://docs.google.com/document/d/{GoogleId}/edit" }, actor));
        Assert.Contains("Importe", error.Message);
        await rig.Fetcher.DidNotReceiveWithAnyArgs().FetchDocumentZipAsync(default!, default);
    }

    [Fact]
    public async Task A_deck_beyond_the_slide_cap_is_refused_and_nothing_stays_on_disk()
    {
        using var ctx = new SqliteTestContext();
        var rig = Build(ctx, googlePages: PresentationService.MaxSlides + 1);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Svc.ImportAsync(NewDeck(), Owner()));

        Assert.Contains($"{PresentationService.MaxSlides} Folien", error.Message);
        Assert.Empty(rig.Storage.Files);
        using var db = ctx.NewContext();
        Assert.Empty(db.Presentations);
    }

    // ---------- refresh ----------

    [Fact]
    public async Task A_refresh_keeps_place_and_notes_and_puts_new_pages_behind_the_decks_last_slide()
    {
        using var ctx = new SqliteTestContext();
        var rig = Build(ctx);
        var owner = Owner();
        var id = await rig.Svc.ImportAsync(NewDeck(), owner);
        await rig.Presentations.AddSlideAsync(id, SlideLayout.TitleText, owner);
        string fileId, page2, oldName;
        using (var db = ctx.NewContext())
        {
            var file = db.PresentationFiles.Single();
            (fileId, oldName) = (file.Id, file.FileNameSaved);
            page2 = db.PresentationSlides.Single(s => s.PageNumber == 2).Id;
        }
        await rig.Presentations.UpdateSlideAsync(page2, new SlideInput { Layout = SlideLayout.PdfPage, Notes = "Hier Pause" }, owner);
        GoogleAnswers(rig, 5);

        var result = await rig.Svc.RefreshAsync(fileId, null, null, owner);

        Assert.Equal(new SlidesRefreshResult(5, 2, 0), result);
        Assert.Equal("p1,p2,p3,p4,p5,TitleText", Order(ctx, id));
        using (var db = ctx.NewContext())
        {
            Assert.Equal("Hier Pause", db.PresentationSlides.Single(s => s.Id == page2).Notes);
            var file = db.PresentationFiles.Single();
            Assert.Equal((5, 2), (file.PageCount, file.Revision));
            Assert.NotEqual(oldName, file.FileNameSaved);
            Assert.Contains(oldName, rig.Storage.Deleted);
            Assert.True(rig.Storage.Files.ContainsKey(file.FileNameSaved));
        }
    }

    [Fact]
    public async Task Lost_pages_drop_their_slides_and_a_page_the_author_deleted_stays_deleted()
    {
        using var ctx = new SqliteTestContext();
        var rig = Build(ctx, googlePages: 4);
        var owner = Owner();
        var id = await rig.Svc.ImportAsync(NewDeck(), owner);
        string fileId, page2;
        using (var db = ctx.NewContext())
        {
            fileId = db.PresentationFiles.Single().Id;
            page2 = db.PresentationSlides.Single(s => s.PageNumber == 2).Id;
        }
        await rig.Presentations.DeleteSlideAsync(page2, owner);
        GoogleAnswers(rig, 3);

        var result = await rig.Svc.RefreshAsync(fileId, null, null, owner);

        Assert.Equal(new SlidesRefreshResult(3, 0, 1), result);
        Assert.Equal("p1,p3", Order(ctx, id));
        using var check = ctx.NewContext();
        Assert.Equal([1, 2], check.PresentationSlides.Where(s => s.PresentationId == id).OrderBy(s => s.Sorting).Select(s => s.Sorting));
    }

    [Fact]
    public async Task A_file_import_refreshes_only_with_a_new_file_and_a_link_import_may_switch_to_one()
    {
        using var ctx = new SqliteTestContext();
        var rig = Build(ctx);
        var owner = Owner();
        var uploaded = await rig.Svc.ImportAsync(NewDeck(link: null, upload: TestPdf.Make(2)), owner);
        var linked = await rig.Svc.ImportAsync(NewDeck(), owner);
        string uploadFile, linkFile;
        using (var db = ctx.NewContext())
        {
            uploadFile = db.PresentationFiles.Single(f => f.PresentationId == uploaded).Id;
            linkFile = db.PresentationFiles.Single(f => f.PresentationId == linked).Id;
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Svc.RefreshAsync(uploadFile, null, null, owner));
        await rig.Svc.RefreshAsync(uploadFile, TestPdf.Make(3), "Neu.pdf", owner);
        await rig.Svc.RefreshAsync(linkFile, TestPdf.Make(1), "Lokal.pdf", owner);

        Assert.Equal("p1,p2,p3", Order(ctx, uploaded));
        Assert.Equal("p1", Order(ctx, linked));
        using var check = ctx.NewContext();
        var switched = check.PresentationFiles.Single(f => f.Id == linkFile);
        Assert.Equal((ImportSourceKind.Upload, (string?)null), (switched.SourceKind, switched.SourceId));
        await rig.Fetcher.Received(1).FetchPresentationPdfAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Only_the_owner_or_leadership_refreshes_or_removes_an_import()
    {
        using var ctx = new SqliteTestContext();
        var rig = Build(ctx);
        var id = await rig.Svc.ImportAsync(NewDeck(), Owner());
        string fileId;
        using (var db = ctx.NewContext())
        {
            fileId = db.PresentationFiles.Single().Id;
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => rig.Svc.RefreshAsync(fileId, null, null, Owner()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => rig.Svc.RemoveAsync(fileId, Owner()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => rig.Svc.RemoveAsync(fileId,
            ClaimsPrincipalBuilder.Agent("tl").WithRank(Rank.Director).AsTeamLead().Build()));
        Assert.Equal("p1,p2,p3", Order(ctx, id));
    }

    [Fact]
    public async Task Removing_an_import_takes_its_slides_and_its_file()
    {
        using var ctx = new SqliteTestContext();
        var rig = Build(ctx);
        var owner = Owner();
        var deck = await rig.Presentations.CreateAsync(new PresentationInput { Title = "Deck", Access = PresentationAccess.Public }, owner);
        await rig.Svc.ImportAsync(new SlidesImportInput { Link = Link, PresentationId = deck.Id }, owner);
        string fileId, stored;
        using (var db = ctx.NewContext())
        {
            var file = db.PresentationFiles.Single();
            (fileId, stored) = (file.Id, file.FileNameSaved);
        }

        await rig.Svc.RemoveAsync(fileId, owner);

        Assert.Equal("Title", Order(ctx, deck.Id));
        using var check = ctx.NewContext();
        Assert.Empty(check.PresentationFiles);
        Assert.Contains(stored, rig.Storage.Deleted);
    }

    [Fact]
    public async Task A_running_deck_tells_its_viewers_when_imported_pages_change()
    {
        using var ctx = new SqliteTestContext();
        var rig = Build(ctx);
        var owner = Owner();
        var deck = await rig.Presentations.CreateAsync(new PresentationInput { Title = "Deck", Access = PresentationAccess.Public }, owner);
        var events = new List<PresentationLiveEvent>();
        rig.Broadcaster.Changed += (_, kind) => events.Add(kind);
        rig.Registry.Start(deck.Id, "Deck", owner.GetAgentId()!, "Vortrag", DateTime.UtcNow);

        await rig.Svc.ImportAsync(new SlidesImportInput { Link = Link, PresentationId = deck.Id }, owner);
        string fileId;
        using (var db = ctx.NewContext())
        {
            fileId = db.PresentationFiles.Single().Id;
        }
        await rig.Svc.RefreshAsync(fileId, null, null, owner);
        await rig.Svc.RemoveAsync(fileId, owner);

        Assert.Equal(
            [PresentationLiveEvent.SlidesChanged, PresentationLiveEvent.SlidesChanged, PresentationLiveEvent.SlidesChanged],
            events);
    }

    // ---------- the file and the deck view ----------

    [Theory]
    [InlineData("own", true)]
    [InlineData("inv", true)]
    [InlineData("lead", true)]
    [InlineData("sup", true)]
    [InlineData("jr", false)]
    [InlineData("tru", false)]
    public async Task The_pdf_reaches_exactly_those_who_see_the_deck(string viewer, bool sees)
    {
        using var ctx = new SqliteTestContext();
        PresentationServiceTests.SeedAgents(ctx);
        PresentationServiceTests.SeedDecks(ctx);
        var rig = Build(ctx);
        // leadership may add to any deck; a fresh one keeps the shared quota of "own" untouched
        await rig.Svc.ImportAsync(new SlidesImportInput { UploadPdf = TestPdf.Make(1), PresentationId = "priv" },
            Owner(Rank.Director));
        string fileId, stored;
        using (var db = ctx.NewContext())
        {
            var file = db.PresentationFiles.Single();
            (fileId, stored) = (file.Id, file.FileNameSaved);
        }

        var access = await rig.Presentations.GetPdfAccessAsync(fileId, PresentationServiceTests.Viewers[viewer]());

        Assert.Equal(sees ? stored : null, access?.FileNameSaved);
    }

    [Fact]
    public async Task A_refresh_that_lost_the_race_leaves_no_doubled_slides_and_no_file_behind()
    {
        using var ctx = new SqliteTestContext();
        var rig = Build(ctx);
        var owner = Owner();
        var id = await rig.Svc.ImportAsync(NewDeck(), owner);
        string fileId;
        using (var db = ctx.NewContext())
        {
            fileId = db.PresentationFiles.Single().Id;
        }
        // while Google renders, someone else's refresh commits
        rig.Fetcher.FetchPresentationPdfAsync(Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                using var other = ctx.NewContext();
                other.PresentationFiles.Single().Revision = 2;
                other.SaveChanges();
                return TestPdf.Make(5);
            });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Svc.RefreshAsync(fileId, null, null, owner));

        Assert.Contains("gerade von jemand anderem geändert", error.Message);
        Assert.Equal("p1,p2,p3", Order(ctx, id));
        Assert.Single(rig.Storage.Files);
    }

    [Fact]
    public async Task The_file_carries_its_revision_so_an_old_url_is_told_apart()
    {
        using var ctx = new SqliteTestContext();
        var rig = Build(ctx);
        var owner = Owner();
        await rig.Svc.ImportAsync(NewDeck(), owner);
        string fileId;
        using (var db = ctx.NewContext())
        {
            fileId = db.PresentationFiles.Single().Id;
        }

        var before = await rig.Presentations.GetPdfAccessAsync(fileId, owner);
        await rig.Svc.RefreshAsync(fileId, null, null, owner);
        var after = await rig.Presentations.GetPdfAccessAsync(fileId, owner);

        Assert.Equal((1, 2), (before!.Revision, after!.Revision));
        Assert.NotEqual(before.FileNameSaved, after.FileNameSaved);
    }

    [Fact]
    public async Task The_deck_view_lists_its_imports_and_a_copied_google_slide_shows_the_same_page()
    {
        using var ctx = new SqliteTestContext();
        var rig = Build(ctx);
        var owner = Owner();
        var id = await rig.Svc.ImportAsync(NewDeck(), owner);
        string page3;
        using (var db = ctx.NewContext())
        {
            page3 = db.PresentationSlides.Single(s => s.PageNumber == 3).Id;
        }

        var copy = await rig.Presentations.DuplicateSlideAsync(page3, owner);
        var view = await rig.Presentations.GetAsync(id, owner);

        Assert.Equal((SlideLayout.PdfPage, 3), (copy.Layout, copy.PageNumber));
        Assert.Equal("p1,p2,p3,p3", Order(ctx, id));
        var import = Assert.Single(view!.Files);
        Assert.Equal(("Deck aus Google", 3, 4, ImportSourceKind.Link), (import.Title, import.PageCount, import.SlideCount, import.SourceKind));
        Assert.All(view.Slides, s => Assert.Equal(1, s.PdfFile!.Revision));
    }
}
