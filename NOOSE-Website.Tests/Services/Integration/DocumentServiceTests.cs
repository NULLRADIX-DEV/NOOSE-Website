using System.Linq;
using System.Security.Claims;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="DocumentService"/> against in-memory SQLite.</summary>
public sealed class DocumentServiceTests
{
    private static DocumentService NewService(SqliteTestContext ctx) => new(ctx.Factory);

    // ---- principals --------------------------------------------------------

    // Director => IsLeadership, MayClassifiedRead => may assign any classification.
    private static ClaimsPrincipal Leader(string id = "dir")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).Build();

    // Trainee: not leadership, not admin, not partner (a plain writer).
    private static ClaimsPrincipal Junior(string id = "jr")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).Build();

    // TeamLead with leadership rank but no admin => read-only supervisor.
    private static ClaimsPrincipal ReadOnlySupervisor(string id = "ro")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).AsTeamLead().Build();

    private static ClaimsPrincipal Partner(string id, PartnerAgency agency = PartnerAgency.DoJ)
        => ClaimsPrincipalBuilder.Agent(id).AsPartner(agency, PartnerRank.Chief).Build();

    // ---- scopes ------------------------------------------------------------

    private static DocumentViewerScope PrivilegedScope(string? meId = "me")
        => new(MayClassified: true, IsTru: false, IsHrb: false, IsLeadership: true, IsAdmin: false, MeId: meId);

    private static DocumentViewerScope PlainScope(string? meId = "me")
        => new(MayClassified: false, IsTru: false, IsHrb: false, IsLeadership: false, IsAdmin: false, MeId: meId);

    private static DocumentViewerScope TruScope(string? meId = "me")
        => new(MayClassified: false, IsTru: true, IsHrb: false, IsLeadership: false, IsAdmin: false, MeId: meId);

    // ---- entity factory ----------------------------------------------------

    private static Document Doc(string id, string title = "Titel", Action<Document>? configure = null)
    {
        var d = new Document
        {
            Id = id,
            Title = title,
            ContentHtml = "<p>x</p>",
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        configure?.Invoke(d);
        return d;
    }

    private static DocumentInput ValidInput(string title = "Neuer Titel") => new()
    {
        Title = title,
        Category = "Kat",
        ContentHtml = "<p>Inhalt</p>",
        Classification = DocumentClassification.None,
    };

    // ---- GetListAsync ------------------------------------------------------

    [Fact]
    public async Task GetListAsync_ReturnsVisibleDocs_OrderedByPinnedThenRefreshed()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("a", configure: d => d.CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            db.Documents.Add(Doc("b", configure: d => d.CreatedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)));
            db.Documents.Add(Doc("c", configure: d => { d.CreatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc); d.Pinned = true; }));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetListAsync(PrivilegedScope());

        Assert.Equal(new[] { "c", "b", "a" }, result.Select(d => d.Id).ToArray());
    }

    [Fact]
    public async Task GetListAsync_PlainScope_HidesClassifiedDocs()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("none"));
            db.Documents.Add(Doc("lead", configure: d => d.IsClassified = true));
            db.Documents.Add(Doc("tru", configure: d => d.IsTRUClassified = true));
            db.Documents.Add(Doc("hrb", configure: d => d.IsHRBClassified = true));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetListAsync(PlainScope());

        Assert.Equal(new[] { "none" }, result.Select(d => d.Id).ToArray());
    }

    [Fact]
    public async Task GetListAsync_TruScope_SeesTruDocs()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("none"));
            db.Documents.Add(Doc("tru", configure: d => d.IsTRUClassified = true));
            db.Documents.Add(Doc("lead", configure: d => d.IsClassified = true));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetListAsync(TruScope());

        Assert.Equal(new[] { "none", "tru" }, result.Select(d => d.Id).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task GetListAsync_TaskforceInternal_HiddenFromNonMember()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("public"));
            db.Documents.Add(Doc("tf", configure: d => d.OwnerTaskforceId = "tf1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetListAsync(PlainScope("me"));

        Assert.Equal(new[] { "public" }, result.Select(d => d.Id).ToArray());
    }

    [Fact]
    public async Task GetListAsync_TaskforceInternal_VisibleToMember()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("tf", configure: d => d.OwnerTaskforceId = "tf1"));
            db.TaskforceAgents.Add(new TaskforceAgent { Id = "ta1", TaskforceId = "tf1", AgentId = "me" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetListAsync(PlainScope("me"));

        Assert.Single(result);
        Assert.Equal("tf", result[0].Id);
    }

    [Fact]
    public async Task GetListAsync_AccessExclusion_HidesDocForExcludedAgent()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("open"));
            db.Documents.Add(Doc("revoked"));
            db.DocumentAccessExclusions.Add(new DocumentAccessExclusion { Id = "x1", DocumentId = "revoked", AgentId = "me" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetListAsync(PlainScope("me"));

        Assert.Equal(new[] { "open" }, result.Select(d => d.Id).ToArray());
    }

    [Fact]
    public async Task GetListAsync_Partner_ReturnsOnlySharedNonClassified()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("shared"));
            db.Documents.Add(Doc("unshared"));
            db.Documents.Add(Doc("sharedButClassified", configure: d => d.IsClassified = true));
            db.PartnerShares.Add(new PartnerShare { Id = "s1", EntityType = nameof(Document), EntityId = "shared", Agency = PartnerAgency.DoJ });
            db.PartnerShares.Add(new PartnerShare { Id = "s2", EntityType = nameof(Document), EntityId = "sharedButClassified", Agency = PartnerAgency.DoJ });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetListAsync(PlainScope(null), partnerAgency: PartnerAgency.DoJ, partnerAgentId: "p1");

        Assert.Equal(new[] { "shared" }, result.Select(d => d.Id).ToArray());
    }

    // ---- SearchAsync -------------------------------------------------------

    [Fact]
    public async Task SearchAsync_MatchesTitleSubstring()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("a", "Alpha Report"));
            db.Documents.Add(Doc("b", "Beta Memo"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.SearchAsync("Alpha", PrivilegedScope());

        Assert.Single(result);
        Assert.Equal("a", result[0].Id);
    }

    [Fact]
    public async Task SearchAsync_EmptyText_ReturnsAll_RespectsMax()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("a", configure: d => d.CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            db.Documents.Add(Doc("b", configure: d => d.CreatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)));
            db.Documents.Add(Doc("c", configure: d => d.CreatedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.SearchAsync(null, PrivilegedScope(), max: 2);

        Assert.Equal(2, result.Count);
        Assert.Equal(new[] { "c", "b" }, result.Select(d => d.Id).ToArray());
    }

    [Fact]
    public async Task SearchAsync_PlainScope_ExcludesClassified()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("pub", "Public"));
            db.Documents.Add(Doc("sec", "Secret", configure: d => d.IsClassified = true));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.SearchAsync(null, PlainScope());

        Assert.Equal(new[] { "pub" }, result.Select(d => d.Id).ToArray());
    }

    // ---- GetAsync ----------------------------------------------------------

    [Fact]
    public async Task GetAsync_ReturnsDocument_WhenVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("d1", "Sichtbar"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetAsync("d1", PrivilegedScope());

        Assert.NotNull(result);
        Assert.Equal("Sichtbar", result!.Title);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenClassifiedBeyondScope()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("d1", configure: d => d.IsClassified = true));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetAsync("d1", PlainScope());

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenTaskforceInternalAndNotMember()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("d1", configure: d => d.OwnerTaskforceId = "tf1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetAsync("d1", PlainScope("me"));

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenAccessExcluded()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("d1"));
            db.DocumentAccessExclusions.Add(new DocumentAccessExclusion { Id = "x1", DocumentId = "d1", AgentId = "me" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetAsync("d1", PlainScope("me"));

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAsync_ReturnsNull_WhenMissing()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        var result = await svc.GetAsync("does-not-exist", PrivilegedScope());

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAsync_Partner_ReturnsDoc_WhenShared()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("d1", "Freigegeben"));
            db.PartnerShares.Add(new PartnerShare { Id = "s1", EntityType = nameof(Document), EntityId = "d1", Agency = PartnerAgency.DoJ });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetAsync("d1", PlainScope(null), partnerAgency: PartnerAgency.DoJ, partnerAgentId: "p1");

        Assert.NotNull(result);
        Assert.Equal("Freigegeben", result!.Title);
    }

    [Fact]
    public async Task GetAsync_Partner_ReturnsNull_WhenNotShared()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("d1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetAsync("d1", PlainScope(null), partnerAgency: PartnerAgency.DoJ, partnerAgentId: "p1");

        Assert.Null(result);
    }

    // ---- CreateAsync -------------------------------------------------------

    [Fact]
    public async Task CreateAsync_PersistsDocument()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var input = new DocumentInput
        {
            Title = "  Titel  ",
            Category = "  Kat  ",
            ContentHtml = "<p>Hallo</p>",
            Classification = DocumentClassification.None,
        };

        var created = await svc.CreateAsync(input, Junior());

        using var db = ctx.NewContext();
        var stored = db.Documents.FirstOrDefault(d => d.Id == created.Id);
        Assert.NotNull(stored);
        Assert.Equal("Titel", stored!.Title);
        Assert.Equal("Kat", stored.Category);
        Assert.Equal(DocumentClassification.None, stored.Classification);
        Assert.Contains("Hallo", stored.ContentHtml);
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenTitleEmpty()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var input = ValidInput("   ");

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateAsync(input, Junior()));
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenClassificationNotAssignable()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var input = new DocumentInput
        {
            Title = "Geheim",
            ContentHtml = "<p>x</p>",
            Classification = DocumentClassification.Leadership,
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateAsync(input, Junior()));
    }

    // ---- RefreshAsync ------------------------------------------------------

    [Fact]
    public async Task RefreshAsync_UpdatesDocument()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("d1", "Alt"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = new DocumentInput
        {
            Title = "Neu",
            Category = "NeueKat",
            ContentHtml = "<p>Neuer Inhalt</p>",
            Classification = DocumentClassification.Tru,
        };

        await svc.RefreshAsync("d1", input, Leader());

        using var db2 = ctx.NewContext();
        var stored = db2.Documents.First(d => d.Id == "d1");
        Assert.Equal("Neu", stored.Title);
        Assert.Equal("NeueKat", stored.Category);
        Assert.Equal(DocumentClassification.Tru, stored.Classification);
        Assert.Contains("Neuer Inhalt", stored.ContentHtml);
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenTitleEmpty()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("d1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RefreshAsync("d1", ValidInput("  "), Leader()));
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenMissing()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RefreshAsync("nope", ValidInput(), Leader()));
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenClassifiedBeyondScope()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("d1", configure: d => d.IsClassified = true));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RefreshAsync("d1", ValidInput(), Junior()));
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenPartnerEditsForeignDoc()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("d1", configure: d => d.CreatedById = "someone-else"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RefreshAsync("d1", ValidInput(), Partner("p1")));
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenClassificationNotAssignable()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("d1")); // currently None => Junior may see it
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = new DocumentInput
        {
            Title = "Neu",
            ContentHtml = "<p>x</p>",
            Classification = DocumentClassification.Leadership,
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RefreshAsync("d1", input, Junior()));
    }

    // ---- PinSetAsync -------------------------------------------------------

    [Fact]
    public async Task PinSetAsync_SetsPinned_ForLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("d1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.PinSetAsync("d1", true, Leader());

        using var db2 = ctx.NewContext();
        Assert.True(db2.Documents.First(d => d.Id == "d1").Pinned);
    }

    [Fact]
    public async Task PinSetAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("d1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.PinSetAsync("d1", true, Junior()));
    }

    [Fact]
    public async Task PinSetAsync_Throws_WhenReadOnlySupervisor()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("d1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        // read-only supervisor passes RequireLeadership but fails RequireWriteAccess
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.PinSetAsync("d1", true, ReadOnlySupervisor()));
    }

    [Fact]
    public async Task PinSetAsync_Throws_WhenMissing()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.PinSetAsync("nope", true, Leader()));
    }

    // ---- DeleteAsync -------------------------------------------------------

    [Fact]
    public async Task DeleteAsync_Creator_RemovesRow()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("d1", configure: d => d.CreatedById = "creator"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        // creator is a plain junior (not leadership) but owns the document
        await svc.DeleteAsync("d1", Junior("creator"));

        using var db2 = ctx.NewContext();
        Assert.False(db2.Documents.Any(d => d.Id == "d1"));
    }

    [Fact]
    public async Task DeleteAsync_Leadership_RemovesRow()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("d1", configure: d => d.CreatedById = "someone-else"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.DeleteAsync("d1", Leader());

        using var db2 = ctx.NewContext();
        Assert.False(db2.Documents.Any(d => d.Id == "d1"));
    }

    [Fact]
    public async Task DeleteAsync_Throws_WhenNotCreatorNorLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("d1", configure: d => d.CreatedById = "owner"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.DeleteAsync("d1", Junior("intruder")));

        using var db2 = ctx.NewContext();
        Assert.True(db2.Documents.Any(d => d.Id == "d1"));
    }

    [Fact]
    public async Task DeleteAsync_Missing_NoThrow()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        // returns silently when the document does not exist
        await svc.DeleteAsync("nope", Leader());
    }

    // ---- GetAttachmentsAsync -----------------------------------------------

    [Fact]
    public async Task GetAttachmentsAsync_ReturnsResolvedAttachments()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("d1"));
            db.People.Add(Seed.Person("p1", "Max Mustermann"));
            db.Sources.Add(new Source
            {
                Id = "src1",
                Type = SourceType.Document,
                TargetType = nameof(Document),
                TargetId = "d1",
                EntityType = nameof(NOOSE_Website.Data.Entities.People.Person),
                EntityId = "p1",
                Title = "Verweis",
            });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetAttachmentsAsync("d1", isLeadership: true, meId: null);

        Assert.Single(result);
        Assert.Equal("p1", result[0].EntityId);
        Assert.Equal(nameof(NOOSE_Website.Data.Entities.People.Person), result[0].EntityType);
        Assert.Contains("Max Mustermann", result[0].Display);
    }

    [Fact]
    public async Task GetAttachmentsAsync_ReturnsEmpty_WhenNoSources()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("d1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetAttachmentsAsync("d1", isLeadership: true, meId: null);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAttachmentsAsync_HidesClassifiedFromNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Documents.Add(Doc("d1"));
            db.People.Add(Seed.Person("p1", "Geheim", configure: p => p.IsClassified = true));
            db.Sources.Add(new Source
            {
                Id = "src1",
                Type = SourceType.Document,
                TargetType = nameof(Document),
                TargetId = "d1",
                EntityType = nameof(NOOSE_Website.Data.Entities.People.Person),
                EntityId = "p1",
            });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetAttachmentsAsync("d1", isLeadership: false, meId: null);

        Assert.Empty(result);
    }
}
