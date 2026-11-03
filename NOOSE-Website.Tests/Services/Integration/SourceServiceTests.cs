using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="SourceService"/> against in-memory SQLite.</summary>
public sealed class SourceServiceTests
{
    private static readonly DateTime Jan = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Feb = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Mar = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    private static (SourceService Svc, ISourcesStorageService Storage) Build(SqliteTestContext ctx)
    {
        var storage = Substitute.For<ISourcesStorageService>();
        return (new SourceService(ctx.Factory, storage, Substitute.For<INotificationService>()), storage);
    }

    // Rank >= SpecialAgentInCharge(4) or admin => IsLeadership() + MayAllTaskforcesSee().
    private static ClaimsPrincipal Leader(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).Build();

    // Junior agent: not leadership, cannot read classified, but may write.
    private static ClaimsPrincipal Junior(string id = "junior")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).Build();

    // Read-only supervision: IsTeamLead && !IsAdmin => IsOnlyReader() (blocked by RequireWriteAccess).
    private static ClaimsPrincipal OnlyReader(string id = "reader")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).AsTeamLead().Build();

    // External partner: read-only, may only add documents.
    private static ClaimsPrincipal Partner(string id = "partner1")
        => ClaimsPrincipalBuilder.Agent(id).AsPartner(PartnerAgency.LSPD, PartnerRank.Member).Build();

    private static Source MakeSource(string entityType, string entityId, SourceType type, string title,
        DateTime createdAt, bool pinned = false, bool internalOnly = false,
        string? url = null, string? targetType = null, string? targetId = null, string? fileNameSaved = null)
        => new()
        {
            EntityType = entityType,
            EntityId = entityId,
            Type = type,
            Title = title,
            CreatedAt = createdAt,
            Pinned = pinned,
            IsInternalOnly = internalOnly,
            Url = url,
            TargetType = targetType,
            TargetId = targetId,
            FileNameSaved = fileNameSaved,
        };

    // ---------- GetForRecordAsync ----------

    [Fact]
    public async Task GetForRecordAsync_ReturnsSources_PinnedFirstThenNewest()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.Sources.Add(MakeSource("Person", "p1", SourceType.FreeText, "pinned-old", Jan, pinned: true));
            db.Sources.Add(MakeSource("Person", "p1", SourceType.FreeText, "unpinned-new", Mar));
            db.Sources.Add(MakeSource("Person", "p1", SourceType.FreeText, "unpinned-old", Feb));
            // a source on a different record must be excluded
            db.Sources.Add(MakeSource("Person", "other", SourceType.FreeText, "elsewhere", Mar));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var result = await svc.GetForRecordAsync("Person", "p1", ViewerScope.From(Junior()));

        Assert.Equal(new[] { "pinned-old", "unpinned-new", "unpinned-old" }, result.Select(s => s.Title).ToArray());
    }

    [Fact]
    public async Task GetForRecordAsync_ReturnsEmpty_WhenRecordNotVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p2", configure: p => p.IsClassified = true));
            db.Sources.Add(MakeSource("Person", "p2", SourceType.FreeText, "secret", Jan));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        // classified record: junior cannot read it -> empty regardless of stored sources
        var result = await svc.GetForRecordAsync("Person", "p2", ViewerScope.From(Junior()));

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetForRecordAsync_Taskforce_Member_SeesInternalOnlySources()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(new Taskforce { Id = "tf1", Name = "Alpha", CaseNumber = "FIB-TF-2026-0001", CreatedAt = Jan });
            db.TaskforceAgents.Add(new TaskforceAgent { TaskforceId = "tf1", AgentId = "junior", CreatedAt = Jan });
            db.Sources.Add(MakeSource("Taskforce", "tf1", SourceType.FreeText, "public", Jan));
            db.Sources.Add(MakeSource("Taskforce", "tf1", SourceType.FreeText, "internal", Feb, internalOnly: true));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        // member sees both internal-only and normal sources
        var result = await svc.GetForRecordAsync("Taskforce", "tf1", ViewerScope.From(Junior("junior")));

        Assert.Equal(2, result.Count);
        Assert.Contains(result, s => s.Title == "internal");
    }

    [Fact]
    public async Task GetForRecordAsync_Taskforce_NonMemberLeadership_HidesInternalOnly()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(new Taskforce { Id = "tf2", Name = "Bravo", CaseNumber = "FIB-TF-2026-0002", CreatedAt = Jan });
            db.Sources.Add(MakeSource("Taskforce", "tf2", SourceType.FreeText, "public", Jan));
            db.Sources.Add(MakeSource("Taskforce", "tf2", SourceType.FreeText, "internal", Feb, internalOnly: true));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        // leadership sees the taskforce (mayAll) but is not a member -> internal-only source hidden
        var result = await svc.GetForRecordAsync("Taskforce", "tf2", ViewerScope.From(Leader()));

        Assert.Single(result);
        Assert.Equal("public", result[0].Title);
    }

    [Fact]
    public async Task GetForRecordAsync_Partner_KeepsReleasedNonInternalSources_HidesInternalCrossRef()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("pp"));
            db.Sources.Add(MakeSource("Person", "pp", SourceType.Link, "web", Jan, url: "https://example.com"));
            db.Sources.Add(MakeSource("Person", "pp", SourceType.Internal, "crossref", Feb, targetType: "Person", targetId: "x"));
            // whole-record release to the agency, children included
            db.PartnerShares.Add(new PartnerShare
            {
                EntityType = "Person",
                EntityId = "pp",
                Agency = PartnerAgency.LSPD,
                PartnerAgentId = null,
                IncludesChildren = true,
            });
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var result = await svc.GetForRecordAsync("Person", "pp", ViewerScope.From(Partner()));

        // link kept via whole-record release; internal cross-ref never partner-visible
        Assert.Single(result);
        Assert.Equal(SourceType.Link, result[0].Type);
    }

    // ---------- CreateAsync ----------

    [Fact]
    public async Task CreateAsync_FreeText_Persists_AndTrimsTitleAndDescription()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("c1"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var input = new SourceInput { Type = SourceType.FreeText, Title = "  Notiz  ", Description = "   " };
        var created = await svc.CreateAsync("Person", "c1", input, Junior());

        Assert.Equal("Notiz", created.Title);
        Assert.Null(created.Description);

        using var check = ctx.NewContext();
        var stored = await check.Sources.SingleAsync(s => s.EntityId == "c1");
        Assert.Equal("Notiz", stored.Title);
        Assert.Equal(SourceType.FreeText, stored.Type);
    }

    [Fact]
    public async Task CreateAsync_Link_Persists_AndTrimsUrl()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("c2"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var input = new SourceInput { Type = SourceType.Link, Title = "Quelle", Url = "  https://example.com/x  " };
        var created = await svc.CreateAsync("Person", "c2", input, Junior());

        Assert.Equal("https://example.com/x", created.Url);
    }

    [Fact]
    public async Task CreateAsync_Link_Throws_OnUnsafeUrl()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("c3"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var input = new SourceInput { Type = SourceType.Link, Title = "Bad", Url = "javascript:alert(1)" };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync("Person", "c3", input, Junior()));
    }

    [Fact]
    public async Task CreateAsync_Link_Throws_WhenUrlMissing()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("c4"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var input = new SourceInput { Type = SourceType.Link, Title = "NoUrl", Url = null };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync("Person", "c4", input, Junior()));
    }

    [Fact]
    public async Task CreateAsync_Internal_Persists()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("c5"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var input = new SourceInput { Type = SourceType.Internal, Title = "Verknüpfung", TargetType = "Faction", TargetId = "f-99" };
        var created = await svc.CreateAsync("Person", "c5", input, Junior());

        Assert.Equal("Faction", created.TargetType);
        Assert.Equal("f-99", created.TargetId);
    }

    [Fact]
    public async Task CreateAsync_Internal_Throws_WhenTargetMissing()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("c6"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var input = new SourceInput { Type = SourceType.Internal, Title = "Kaputt", TargetType = "Faction", TargetId = null };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync("Person", "c6", input, Junior()));
    }

    [Fact]
    public async Task CreateAsync_Upload_Persists_AndCallsStorage()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("c7"));
            db.SaveChanges();
        }
        var (svc, storage) = Build(ctx);
        storage.MaxBytes.Returns(10L * 1024 * 1024);
        storage.IsAllowedType(Arg.Any<string>()).Returns(true);
        storage.SaveAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("saved-file.pdf");

        var input = new SourceInput
        {
            Type = SourceType.Upload,
            Title = "Datei",
            FileContent = new byte[] { 1, 2, 3, 4 },
            OriginalName = "report.pdf",
            ContentType = "application/pdf",
            SizeBytes = 4,
        };
        var created = await svc.CreateAsync("Person", "c7", input, Junior());

        Assert.Equal("saved-file.pdf", created.FileNameSaved);
        Assert.Equal("report.pdf", created.OriginalName);
        await storage.Received(1).SaveAsync(Arg.Any<Stream>(), "report.pdf", Arg.Any<CancellationToken>());

        using var check = ctx.NewContext();
        Assert.True(await check.Sources.AnyAsync(s => s.EntityId == "c7" && s.FileNameSaved == "saved-file.pdf"));
    }

    [Fact]
    public async Task CreateAsync_Upload_Throws_WhenNoFile()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("c8"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var input = new SourceInput { Type = SourceType.Upload, Title = "Leer", FileContent = null };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync("Person", "c8", input, Junior()));
    }

    [Fact]
    public async Task CreateAsync_Upload_Throws_WhenTooLarge()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("c9"));
            db.SaveChanges();
        }
        var (svc, storage) = Build(ctx);
        storage.MaxBytes.Returns(2L);

        var input = new SourceInput { Type = SourceType.Upload, Title = "Groß", FileContent = new byte[] { 1, 2, 3, 4 } };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync("Person", "c9", input, Junior()));
    }

    [Fact]
    public async Task CreateAsync_Upload_Throws_WhenTypeNotAllowed()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("c10"));
            db.SaveChanges();
        }
        var (svc, storage) = Build(ctx);
        storage.MaxBytes.Returns(10L * 1024 * 1024);
        storage.IsAllowedType(Arg.Any<string>()).Returns(false);

        var input = new SourceInput
        {
            Type = SourceType.Upload,
            Title = "Falscher Typ",
            FileContent = new byte[] { 1, 2, 3, 4 },
            ContentType = "application/x-msdownload",
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync("Person", "c10", input, Junior()));
    }

    [Fact]
    public async Task CreateAsync_Document_Persists_WhenDocVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("c11"));
            db.Documents.Add(new Document { Id = "doc1", Title = "Bericht", CreatedAt = Jan });
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var input = new SourceInput { Type = SourceType.Document, Title = "Dok-Quelle", TargetId = "doc1" };
        var created = await svc.CreateAsync("Person", "c11", input, Junior());

        Assert.Equal("Document", created.TargetType);
        Assert.Equal("doc1", created.TargetId);
    }

    [Fact]
    public async Task CreateAsync_Document_Throws_WhenDocMissing()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("c12"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var input = new SourceInput { Type = SourceType.Document, Title = "Fehlt", TargetId = "does-not-exist" };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync("Person", "c12", input, Junior()));
    }

    [Fact]
    public async Task CreateAsync_Document_Throws_WhenClassifiedAndNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("c13"));
            db.Documents.Add(new Document { Id = "doc2", Title = "VS", IsClassified = true, CreatedAt = Jan });
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var input = new SourceInput { Type = SourceType.Document, Title = "Geheim", TargetId = "doc2" };

        // classified doc: junior may not reference it (no existence leak)
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync("Person", "c13", input, Junior()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateAsync_Throws_OnEmptyTitle(string? title)
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var input = new SourceInput { Type = SourceType.FreeText, Title = title! };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync("Person", "any", input, Junior()));
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenRecordNotVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("c14", configure: p => p.IsClassified = true));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var input = new SourceInput { Type = SourceType.FreeText, Title = "nope" };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateAsync("Person", "c14", input, Junior()));

        using var check = ctx.NewContext();
        Assert.False(await check.Sources.AnyAsync(s => s.EntityId == "c14"));
    }

    [Fact]
    public async Task CreateAsync_Partner_Throws_WhenNonDocumentType()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var input = new SourceInput { Type = SourceType.Link, Title = "Link", Url = "https://example.com" };

        // partners may only contribute documents; guard runs before visibility
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateAsync("Person", "any", input, Partner()));
    }

    // ---------- RemoveAsync ----------

    [Fact]
    public async Task RemoveAsync_HardDeletes_WhenVisible()
    {
        using var ctx = new SqliteTestContext();
        var source = MakeSource("Person", "r1", SourceType.FreeText, "weg", Jan);
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("r1"));
            db.Sources.Add(source);
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await svc.RemoveAsync(source.Id, Junior());

        using var check = ctx.NewContext();
        // no soft-delete interceptor in tests => row gone from the filtered set
        Assert.False(await check.Sources.AnyAsync(s => s.Id == source.Id));
    }

    [Fact]
    public async Task RemoveAsync_NoOp_WhenSourceMissing()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("r2"));
            db.Sources.Add(MakeSource("Person", "r2", SourceType.FreeText, "bleibt", Jan));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        // unknown id returns silently, touches nothing
        await svc.RemoveAsync("missing", Junior());

        using var check = ctx.NewContext();
        Assert.Equal(1, await check.Sources.CountAsync());
    }

    [Fact]
    public async Task RemoveAsync_Throws_WhenRecordNotVisible()
    {
        using var ctx = new SqliteTestContext();
        var source = MakeSource("Person", "r3", SourceType.FreeText, "geschützt", Jan);
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("r3", configure: p => p.IsClassified = true));
            db.Sources.Add(source);
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RemoveAsync(source.Id, Junior()));

        using var check = ctx.NewContext();
        Assert.True(await check.Sources.AnyAsync(s => s.Id == source.Id));
    }

    // ---------- PinSetAsync ----------

    [Fact]
    public async Task PinSetAsync_SetsPinned_WhenWriter()
    {
        using var ctx = new SqliteTestContext();
        var source = MakeSource("Person", "pin1", SourceType.FreeText, "anpinnen", Jan);
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("pin1"));
            db.Sources.Add(source);
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await svc.PinSetAsync(source.Id, pinned: true, Junior());

        using var check = ctx.NewContext();
        var stored = await check.Sources.SingleAsync(s => s.Id == source.Id);
        Assert.True(stored.Pinned);
    }

    [Fact]
    public async Task PinSetAsync_Throws_WhenOnlyReader()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        // RequireWriteAccess runs first, before any DB access
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.PinSetAsync("any", pinned: true, OnlyReader()));
    }

    [Fact]
    public async Task PinSetAsync_Throws_WhenSourceMissing()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        // writer passes the guard but the source id is unknown
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.PinSetAsync("missing", pinned: true, Junior()));
    }

    [Fact]
    public async Task PinSetAsync_Throws_WhenRecordNotVisible()
    {
        using var ctx = new SqliteTestContext();
        var source = MakeSource("Person", "pin2", SourceType.FreeText, "vs", Jan);
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("pin2", configure: p => p.IsClassified = true));
            db.Sources.Add(source);
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.PinSetAsync(source.Id, pinned: true, Junior()));
    }

    // ---------- GetForDownloadAsync ----------

    [Fact]
    public async Task GetForDownloadAsync_ReturnsSource_WhenUploadVisible()
    {
        using var ctx = new SqliteTestContext();
        var source = MakeSource("Person", "d1", SourceType.Upload, "datei", Jan, fileNameSaved: "f.bin");
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("d1"));
            db.Sources.Add(source);
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var result = await svc.GetForDownloadAsync(source.Id, ViewerScope.From(Junior()));

        Assert.NotNull(result);
        Assert.Equal(source.Id, result!.Id);
    }

    [Fact]
    public async Task GetForDownloadAsync_ReturnsNull_WhenNotUpload()
    {
        using var ctx = new SqliteTestContext();
        var source = MakeSource("Person", "d2", SourceType.Link, "link", Jan, url: "https://example.com");
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("d2"));
            db.Sources.Add(source);
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var result = await svc.GetForDownloadAsync(source.Id, ViewerScope.From(Junior()));

        Assert.Null(result);
    }

    [Fact]
    public async Task GetForDownloadAsync_ReturnsNull_WhenRecordNotVisible()
    {
        using var ctx = new SqliteTestContext();
        var source = MakeSource("Person", "d3", SourceType.Upload, "datei", Jan, fileNameSaved: "f.bin");
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("d3", configure: p => p.IsClassified = true));
            db.Sources.Add(source);
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var result = await svc.GetForDownloadAsync(source.Id, ViewerScope.From(Junior()));

        Assert.Null(result);
    }

    [Fact]
    public async Task GetForDownloadAsync_ReturnsNull_WhenSourceMissing()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var result = await svc.GetForDownloadAsync("missing", ViewerScope.From(Junior()));

        Assert.Null(result);
    }

    [Fact]
    public async Task GetForDownloadAsync_Partner_ReturnsSource_WhenChildReleasedWhole()
    {
        using var ctx = new SqliteTestContext();
        var source = MakeSource("Person", "d4", SourceType.Upload, "freigegeben", Jan, fileNameSaved: "f.bin");
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("d4"));
            db.Sources.Add(source);
            db.PartnerShares.Add(new PartnerShare
            {
                EntityType = "Person",
                EntityId = "d4",
                Agency = PartnerAgency.LSPD,
                PartnerAgentId = null,
                IncludesChildren = true,
            });
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var result = await svc.GetForDownloadAsync(source.Id, ViewerScope.From(Partner()));

        Assert.NotNull(result);
        Assert.Equal(source.Id, result!.Id);
    }
}
