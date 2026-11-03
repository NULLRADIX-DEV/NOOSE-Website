using System.IO;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="LibraryService"/> against in-memory SQLite.</summary>
public sealed class LibraryServiceTests
{
    private static readonly DateTime Ts = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static (LibraryService Svc, ILibraryStorageService Storage) Build(
        SqliteTestContext ctx, long maxBytes = 50L * 1024 * 1024, bool allowType = true)
    {
        var storage = Substitute.For<ILibraryStorageService>();
        storage.MaxBytes.Returns(maxBytes);
        storage.IsAllowedType(Arg.Any<string>()).Returns(allowType);
        storage.SaveAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("saved-file.bin");
        return (new LibraryService(ctx.Factory, storage), storage);
    }

    // Rank >= SpecialAgentInCharge(4) => IsLeadership(): may assign any level, may delete.
    private static ClaimsPrincipal Leader(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).Build();

    // Plain writer: MayWrite() true, IsLeadership() false, no unit flags.
    private static ClaimsPrincipal Writer(string id = "writer")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.SpecialAgent).Build();

    // Non-leadership writer, TRU unit member.
    private static ClaimsPrincipal TruWriter(string id = "tru")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.SpecialAgent).AsTru().Build();

    // Non-leadership junior; not leadership, not read-only.
    private static ClaimsPrincipal Junior(string id = "junior")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).Build();

    // Read-only supervision: TeamLead without admin => RequireWriteAccess rejects.
    private static ClaimsPrincipal OnlyReader(string id = "reader")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).AsTeamLead().Build();

    private static LibraryFile AddFile(SqliteTestContext ctx, string id,
        DocumentClassification classification = DocumentClassification.None, Action<LibraryFile>? configure = null)
    {
        using var db = ctx.NewContext();
        var f = new LibraryFile
        {
            Id = id,
            Title = "File " + id,
            OriginalName = id + ".pdf",
            FileNameSaved = id + "-saved.pdf",
            ContentType = "application/pdf",
            SizeBytes = 1024,
            Classification = classification,
            CreatedAt = Ts,
        };
        configure?.Invoke(f);
        db.LibraryFiles.Add(f);
        db.SaveChanges();
        return f;
    }

    // ---------- GetListAsync ----------

    [Fact]
    public async Task GetListAsync_PlainViewer_SeesOnlyUnclassified()
    {
        using var ctx = new SqliteTestContext();
        AddFile(ctx, "none", DocumentClassification.None);
        AddFile(ctx, "lead", DocumentClassification.Leadership);
        AddFile(ctx, "tru", DocumentClassification.Tru);
        AddFile(ctx, "hrb", DocumentClassification.Hrb);
        var (svc, _) = Build(ctx);

        var ids = (await svc.GetListAsync(DocumentViewerScope.From(Writer()))).Select(f => f.Id).ToHashSet();

        Assert.Equal(new HashSet<string> { "none" }, ids);
    }

    [Fact]
    public async Task GetListAsync_TruViewer_SeesUnclassifiedAndTru()
    {
        using var ctx = new SqliteTestContext();
        AddFile(ctx, "none", DocumentClassification.None);
        AddFile(ctx, "lead", DocumentClassification.Leadership);
        AddFile(ctx, "tru", DocumentClassification.Tru);
        AddFile(ctx, "hrb", DocumentClassification.Hrb);
        var (svc, _) = Build(ctx);

        var ids = (await svc.GetListAsync(DocumentViewerScope.From(TruWriter()))).Select(f => f.Id).ToHashSet();

        Assert.Equal(new HashSet<string> { "none", "tru" }, ids);
    }

    [Fact]
    public async Task GetListAsync_Leadership_SeesEveryLevel()
    {
        using var ctx = new SqliteTestContext();
        AddFile(ctx, "none", DocumentClassification.None);
        AddFile(ctx, "lead", DocumentClassification.Leadership);
        AddFile(ctx, "tru", DocumentClassification.Tru);
        AddFile(ctx, "hrb", DocumentClassification.Hrb);
        var (svc, _) = Build(ctx);

        var ids = (await svc.GetListAsync(DocumentViewerScope.From(Leader()))).Select(f => f.Id).ToHashSet();

        Assert.Equal(new HashSet<string> { "none", "lead", "tru", "hrb" }, ids);
    }

    [Fact]
    public async Task GetListAsync_OrdersByModifiedThenCreated_Descending()
    {
        using var ctx = new SqliteTestContext();
        // a: created earliest but modified latest => must sort first
        AddFile(ctx, "a", configure: f => { f.CreatedAt = Ts; f.ModifiedAt = Ts.AddDays(5); });
        AddFile(ctx, "b", configure: f => { f.CreatedAt = Ts.AddDays(2); f.ModifiedAt = null; });
        AddFile(ctx, "c", configure: f => { f.CreatedAt = Ts.AddDays(3); f.ModifiedAt = null; });
        var (svc, _) = Build(ctx);

        var order = (await svc.GetListAsync(DocumentViewerScope.From(Leader()))).Select(f => f.Id).ToArray();

        Assert.Equal(new[] { "a", "c", "b" }, order);
    }

    // ---------- UploadAsync ----------

    [Fact]
    public async Task UploadAsync_PersistsFile_AndSavesContent_OnHappyPath()
    {
        using var ctx = new SqliteTestContext();
        var (svc, storage) = Build(ctx);
        using var content = new MemoryStream(new byte[] { 1, 2, 3 });

        var file = await svc.UploadAsync("  My Doc  ", "  Reports  ", DocumentClassification.None,
            content, "report.pdf", "application/pdf", 3, Leader());

        Assert.Equal("My Doc", file.Title);
        Assert.Equal("Reports", file.Category);
        Assert.Equal("saved-file.bin", file.FileNameSaved);
        Assert.Equal(DocumentClassification.None, file.Classification);
        await storage.Received(1).SaveAsync(Arg.Any<Stream>(), "report.pdf", Arg.Any<CancellationToken>());

        using var check = ctx.NewContext();
        var row = await check.LibraryFiles.SingleAsync();
        Assert.Equal("My Doc", row.Title);
        Assert.Equal("report.pdf", row.OriginalName);
    }

    [Fact]
    public async Task UploadAsync_TruMember_MayAssignTruLevel()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        using var content = new MemoryStream(new byte[] { 1 });

        var file = await svc.UploadAsync("Secret", null, DocumentClassification.Tru,
            content, "s.pdf", "application/pdf", 1, TruWriter());

        Assert.True(file.IsTRUClassified);
        using var check = ctx.NewContext();
        Assert.True(await check.LibraryFiles.AnyAsync(f => f.IsTRUClassified));
    }

    [Fact]
    public async Task UploadAsync_Throws_Unauthorized_WhenOnlyReader()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        using var content = new MemoryStream(new byte[] { 1 });

        // write guard runs first
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.UploadAsync("t", null, DocumentClassification.None,
                content, "f.pdf", "application/pdf", 1, OnlyReader()));
    }

    [Fact]
    public async Task UploadAsync_Throws_Unauthorized_WhenAssigningLevelBeyondActor()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        using var content = new MemoryStream(new byte[] { 1 });

        // plain writer may write but cannot assign a leadership level
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.UploadAsync("t", null, DocumentClassification.Leadership,
                content, "f.pdf", "application/pdf", 1, Writer()));
    }

    [Fact]
    public async Task UploadAsync_Throws_InvalidOperation_WhenTitleEmpty()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        using var content = new MemoryStream(new byte[] { 1 });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.UploadAsync("   ", null, DocumentClassification.None,
                content, "f.pdf", "application/pdf", 1, Leader()));
    }

    [Fact]
    public async Task UploadAsync_Throws_InvalidOperation_WhenTypeNotAllowed()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx, allowType: false);
        using var content = new MemoryStream(new byte[] { 1 });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.UploadAsync("t", null, DocumentClassification.None,
                content, "f.exe", "application/x-msdownload", 1, Leader()));
    }

    [Fact]
    public async Task UploadAsync_Throws_InvalidOperation_WhenTooLarge()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx, maxBytes: 100);
        using var content = new MemoryStream(new byte[] { 1 });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.UploadAsync("t", null, DocumentClassification.None,
                content, "f.pdf", "application/pdf", 200, Leader()));
    }

    // ---------- RefreshAsync ----------

    [Fact]
    public async Task RefreshAsync_UpdatesTitleAndCategory_OnHappyPath()
    {
        using var ctx = new SqliteTestContext();
        AddFile(ctx, "f1", DocumentClassification.None);
        var (svc, _) = Build(ctx);

        await svc.RefreshAsync("f1", "  New Title  ", "  Cat  ", DocumentClassification.None, Leader());

        using var check = ctx.NewContext();
        var row = await check.LibraryFiles.SingleAsync(f => f.Id == "f1");
        Assert.Equal("New Title", row.Title);
        Assert.Equal("Cat", row.Category);
        Assert.Equal(DocumentClassification.None, row.Classification);
    }

    [Fact]
    public async Task RefreshAsync_Leadership_MayRaiseClassification()
    {
        using var ctx = new SqliteTestContext();
        AddFile(ctx, "f1", DocumentClassification.None);
        var (svc, _) = Build(ctx);

        await svc.RefreshAsync("f1", "T", null, DocumentClassification.Leadership, Leader());

        using var check = ctx.NewContext();
        var row = await check.LibraryFiles.SingleAsync(f => f.Id == "f1");
        Assert.True(row.IsClassified);
    }

    [Fact]
    public async Task RefreshAsync_Throws_Unauthorized_WhenOnlyReader()
    {
        using var ctx = new SqliteTestContext();
        AddFile(ctx, "f1", DocumentClassification.None);
        var (svc, _) = Build(ctx);

        // write guard runs first
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RefreshAsync("f1", "T", null, DocumentClassification.None, OnlyReader()));
    }

    [Fact]
    public async Task RefreshAsync_Throws_InvalidOperation_WhenTitleEmpty()
    {
        using var ctx = new SqliteTestContext();
        AddFile(ctx, "f1", DocumentClassification.None);
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshAsync("f1", "  ", null, DocumentClassification.None, Leader()));
    }

    [Fact]
    public async Task RefreshAsync_Throws_InvalidOperation_WhenFileMissing()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshAsync("ghost", "T", null, DocumentClassification.None, Leader()));
    }

    [Fact]
    public async Task RefreshAsync_Throws_Unauthorized_WhenCannotSeeCurrentLevel()
    {
        using var ctx = new SqliteTestContext();
        AddFile(ctx, "f1", DocumentClassification.Leadership);
        var (svc, _) = Build(ctx);

        // TRU writer may write, but cannot see a leadership-classified file
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RefreshAsync("f1", "T", null, DocumentClassification.Leadership, TruWriter()));
    }

    // ---------- DeleteAsync ----------

    [Fact]
    public async Task DeleteAsync_RemovesRow_OnHappyPath()
    {
        using var ctx = new SqliteTestContext();
        AddFile(ctx, "f1", DocumentClassification.None);
        var (svc, _) = Build(ctx);

        await svc.DeleteAsync("f1", Leader());

        using var check = ctx.NewContext();
        // no soft-delete interceptor in tests => hard delete, row gone from the filtered set
        Assert.False(await check.LibraryFiles.AnyAsync(f => f.Id == "f1"));
    }

    [Fact]
    public async Task DeleteAsync_Throws_Unauthorized_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        AddFile(ctx, "f1", DocumentClassification.None);
        var (svc, _) = Build(ctx);

        // leadership guard runs first
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.DeleteAsync("f1", Junior()));
    }

    [Fact]
    public async Task DeleteAsync_Throws_InvalidOperation_WhenFileMissing()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DeleteAsync("ghost", Leader()));
    }

    // ---------- GetForDownloadAsync ----------

    [Fact]
    public async Task GetForDownloadAsync_ReturnsFile_WhenVisible()
    {
        using var ctx = new SqliteTestContext();
        AddFile(ctx, "f1", DocumentClassification.None);
        var (svc, _) = Build(ctx);

        var file = await svc.GetForDownloadAsync("f1", DocumentViewerScope.From(Writer()));

        Assert.NotNull(file);
        Assert.Equal("f1", file!.Id);
    }

    [Fact]
    public async Task GetForDownloadAsync_ReturnsNull_WhenNotVisible()
    {
        using var ctx = new SqliteTestContext();
        AddFile(ctx, "f1", DocumentClassification.Leadership);
        var (svc, _) = Build(ctx);

        // plain viewer cannot see a leadership-classified file => null, no existence leak
        var file = await svc.GetForDownloadAsync("f1", DocumentViewerScope.From(Writer()));

        Assert.Null(file);
    }

    [Fact]
    public async Task GetForDownloadAsync_ReturnsFile_WhenTruViewerAndTruLevel()
    {
        using var ctx = new SqliteTestContext();
        AddFile(ctx, "f1", DocumentClassification.Tru);
        var (svc, _) = Build(ctx);

        var file = await svc.GetForDownloadAsync("f1", DocumentViewerScope.From(TruWriter()));

        Assert.NotNull(file);
        Assert.Equal("f1", file!.Id);
    }

    [Fact]
    public async Task GetForDownloadAsync_ReturnsNull_WhenFileMissing()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        Assert.Null(await svc.GetForDownloadAsync("ghost", DocumentViewerScope.From(Leader())));
    }
}
