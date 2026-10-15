using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Import;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Import;

/// <summary>Integration tests for <see cref="GoogleImportService"/> against in-memory SQLite.</summary>
public sealed class GoogleImportServiceTests
{
    private const string Link = "https://docs.google.com/document/d/1AbCdEfGhIjKlMnOpQrStUvWxYz0123456789/edit";
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2, 3];

    /// <summary>Keeps saved pictures in memory and records deletions.</summary>
    private sealed class FakeImageStorage : ITextImageStorageService
    {
        public List<string> Saved { get; } = [];
        public List<string> Deleted { get; } = [];
        public long MaxBytes => 8 * 1024 * 1024;
        public bool IsAllowedType(string contentType) => contentType is "image/png" or "image/jpeg";

        public Task<string> SaveAsync(Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            var name = $"{Guid.NewGuid()}.png";
            Saved.Add(name);
            return Task.FromResult(name);
        }

        public Stream OpenRead(string fileNameSaved) => new MemoryStream();
        public void Delete(string fileNameSaved) => Deleted.Add(fileNameSaved);
    }

    private static byte[] Export(string text, bool withImage = true)
    {
        var body = $"<p><span style=\"font-weight:700\">{text}</span></p>"
            + (withImage ? "<p><img src=\"images/image1.png\" style=\"width:10px\"></p>" : string.Empty);
        var html = $"<html><head><title>Titel aus Google</title><style>p{{font-family:\"Arial\"}}</style></head><body>{body}</body></html>";
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var stream = archive.CreateEntry("doc.html").Open())
            {
                stream.Write(Encoding.UTF8.GetBytes(html));
            }
            if (withImage)
            {
                using var stream = archive.CreateEntry("images/image1.png").Open();
                stream.Write(Png);
            }
        }
        return buffer.ToArray();
    }

    private static (GoogleImportService Svc, IGoogleExportFetcher Fetcher, FakeImageStorage Storage) Build(SqliteTestContext ctx)
    {
        var fetcher = Substitute.For<IGoogleExportFetcher>();
        fetcher.FetchDocumentZipAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Export("Erste Fassung"));
        var storage = new FakeImageStorage();
        return (new GoogleImportService(ctx.Factory, fetcher, storage, NullLogger<GoogleImportService>.Instance), fetcher, storage);
    }

    // a unique agent per test keeps the process-wide quota out of the other tests' way
    private static ClaimsPrincipal Agent(string? id = null, Rank rank = Rank.SpecialAgent)
        => ClaimsPrincipalBuilder.Agent(id ?? Guid.NewGuid().ToString()).WithRank(rank).Build();

    [Fact]
    public async Task An_original_import_keeps_both_copies_and_files_the_picture_under_the_document()
    {
        using var ctx = new SqliteTestContext();
        var (svc, fetcher, storage) = Build(ctx);
        var actor = Agent();

        var document = await svc.ImportDocumentAsync(new GoogleDocImportInput { Link = Link, Category = "SOP" }, actor);

        using var db = ctx.NewContext();
        var row = db.Documents.Single(d => d.Id == document.Id);
        Assert.Equal("Titel aus Google", row.Title);
        Assert.Equal("SOP", row.Category);
        Assert.NotNull(row.ImportedHtml);
        Assert.Matches(@"font-weight:\s*(700|bold)", row.ImportedHtml);
        Assert.Contains("<strong>Erste Fassung</strong>", row.ContentHtml);
        Assert.Equal(ImportSourceKind.Link, row.ImportSourceKind);
        Assert.Equal("1AbCdEfGhIjKlMnOpQrStUvWxYz0123456789", row.ImportSourceId);
        var image = db.TextImages.Single();
        Assert.Equal((nameof(Document), document.Id), (image.EntityType, image.EntityId));
        Assert.Contains($"/dateien/textbilder/{image.Id}", row.ImportedHtml);
        Assert.Contains($"/dateien/textbilder/{image.Id}", row.ContentHtml);
        Assert.Single(storage.Saved);
        await fetcher.Received(1).FetchDocumentZipAsync("1AbCdEfGhIjKlMnOpQrStUvWxYz0123456789", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_editable_import_is_an_ordinary_document_and_an_upload_needs_no_link()
    {
        using var ctx = new SqliteTestContext();
        var (svc, fetcher, _) = Build(ctx);

        var document = await svc.ImportDocumentAsync(new GoogleDocImportInput
        {
            UploadZip = Export("Hochgeladen", withImage: false), Mode = GoogleImportMode.Editable, Title = "Eigener Titel",
        }, Agent());

        using var db = ctx.NewContext();
        var row = db.Documents.Single(d => d.Id == document.Id);
        Assert.Null(row.ImportedHtml);
        Assert.Equal("Eigener Titel", row.Title);
        Assert.Equal(ImportSourceKind.Upload, row.ImportSourceKind);
        Assert.Null(row.ImportSourceId);
        await fetcher.DidNotReceiveWithAnyArgs().FetchDocumentZipAsync(default!, default);
    }

    [Fact]
    public async Task Partners_supervision_and_demo_cannot_import_and_no_one_sets_a_level_above_their_own()
    {
        using var ctx = new SqliteTestContext();
        var (svc, fetcher, _) = Build(ctx);
        var input = new GoogleDocImportInput { Link = Link };

        ClaimsPrincipal[] refused =
        [
            ClaimsPrincipalBuilder.Agent("p").AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build(),
            ClaimsPrincipalBuilder.Agent("tl").WithRank(Rank.Director).AsTeamLead().Build(),
            ClaimsPrincipalBuilder.Agent("demo").WithRank(Rank.Director).AsHrb().AsDemo().Build(),
        ];
        foreach (var actor in refused)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.ImportDocumentAsync(input, actor));
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.ImportDocumentAsync(
            new GoogleDocImportInput { Link = Link, Classification = DocumentClassification.Leadership }, Agent()));
        await fetcher.DidNotReceiveWithAnyArgs().FetchDocumentZipAsync(default!, default);
    }

    [Fact]
    public async Task A_link_that_is_not_a_google_document_is_refused_before_any_fetch()
    {
        using var ctx = new SqliteTestContext();
        var (svc, fetcher, _) = Build(ctx);

        await Assert.ThrowsAsync<GoogleExportException>(() => svc.ImportDocumentAsync(
            new GoogleDocImportInput { Link = "https://evil.example/document/d/1AbCdEfGhIjKlMnOpQrStUvWxYz0123456789" }, Agent()));
        await fetcher.DidNotReceiveWithAnyArgs().FetchDocumentZipAsync(default!, default);
    }

    [Fact]
    public async Task Saving_in_the_editor_leaves_an_imported_original_untouched()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);
        var actor = Agent();
        var document = await svc.ImportDocumentAsync(new GoogleDocImportInput { Link = Link }, actor);
        var documents = new DocumentService(ctx.Factory);

        await documents.RefreshAsync(document.Id, new DocumentInput { Title = "Neuer Titel", ContentHtml = "<p>überschrieben</p>" }, actor);

        using var db = ctx.NewContext();
        var row = db.Documents.Single(d => d.Id == document.Id);
        Assert.Equal("Neuer Titel", row.Title);
        Assert.Contains("Erste Fassung", row.ContentHtml);
        Assert.DoesNotContain("überschrieben", row.ContentHtml);
    }

    [Fact]
    public async Task Refreshing_fetches_again_and_removes_the_old_picture()
    {
        using var ctx = new SqliteTestContext();
        var (svc, fetcher, storage) = Build(ctx);
        var actor = Agent();
        var document = await svc.ImportDocumentAsync(new GoogleDocImportInput { Link = Link }, actor);
        fetcher.FetchDocumentZipAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Export("Zweite Fassung"));

        await svc.ReimportDocumentAsync(document.Id, null, actor);

        using var db = ctx.NewContext();
        var row = db.Documents.Single(d => d.Id == document.Id);
        Assert.Contains("Zweite Fassung", row.ImportedHtml);
        Assert.Contains("Zweite Fassung", row.ContentHtml);
        var image = Assert.Single(db.TextImages);
        Assert.Contains(image.Id, row.ImportedHtml);
        Assert.Equal(2, storage.Saved.Count);
        Assert.Equal([storage.Saved[0]], storage.Deleted);
    }

    [Fact]
    public async Task Converting_drops_the_original_and_the_editor_takes_over()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);
        var actor = Agent();
        var document = await svc.ImportDocumentAsync(new GoogleDocImportInput { Link = Link }, actor);

        await svc.ConvertToEditableAsync(document.Id, actor);
        await new DocumentService(ctx.Factory).RefreshAsync(document.Id,
            new DocumentInput { Title = "x", ContentHtml = "<p>jetzt bearbeitbar</p>" }, actor);

        using var db = ctx.NewContext();
        var row = db.Documents.Single(d => d.Id == document.Id);
        Assert.Null(row.ImportedHtml);
        Assert.Contains("jetzt bearbeitbar", row.ContentHtml);
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ReimportDocumentAsync(document.Id, null, actor));
    }

    [Fact]
    public async Task A_classified_document_cannot_be_refreshed_by_someone_who_cannot_see_it()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);
        var leader = Agent(rank: Rank.Director);
        var document = await svc.ImportDocumentAsync(
            new GoogleDocImportInput { Link = Link, Classification = DocumentClassification.Leadership }, leader);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ReimportDocumentAsync(document.Id, null, Agent()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ConvertToEditableAsync(document.Id, Agent()));
    }

    [Fact]
    public async Task The_quota_stops_the_eleventh_import_within_the_window()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);
        var actor = Agent();

        for (var i = 0; i < GoogleImportService.QuotaPerWindow; i++)
        {
            await svc.ImportDocumentAsync(new GoogleDocImportInput { UploadZip = Export($"Nr {i}", withImage: false) }, actor);
        }

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ImportDocumentAsync(new GoogleDocImportInput { UploadZip = Export("zu viel", withImage: false) }, actor));
        Assert.Contains("Importe", error.Message);
    }
}
