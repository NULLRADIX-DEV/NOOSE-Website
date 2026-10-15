using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services;

/// <summary>A pasted image is exactly as secret as the record it hangs on; the token grants nothing on its own.</summary>
public class TextImageServiceTests
{
    private sealed class FakeStorage : ITextImageStorageService
    {
        public List<string> Saved { get; } = new();
        public long MaxBytes => 1024;

        public bool IsAllowedType(string contentType) => contentType == "image/png";

        public Task<string> SaveAsync(Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            var name = $"file-{Saved.Count}.png";
            Saved.Add(name);
            return Task.FromResult(name);
        }

        public Stream OpenRead(string fileNameSaved) => new MemoryStream();

        public void Delete(string fileNameSaved) => Saved.Remove(fileNameSaved);
    }

    private static (TextImageService Service, FakeStorage Storage) Build(SqliteTestContext ctx)
    {
        var storage = new FakeStorage();
        return (new TextImageService(ctx.Factory, storage, new NOOSE_Website.Infrastructure.Presentations.PresentationLiveRegistry()), storage);
    }

    private static async Task<string> RowAsync(SqliteTestContext ctx, string entityType, string entityId)
    {
        await using var db = ctx.NewContext();
        var row = new TextImage
        {
            EntityType = entityType,
            EntityId = entityId,
            FileNameSaved = "bild.png",
            ContentType = "image/png",
            SizeBytes = 12,
        };
        db.TextImages.Add(row);
        await db.SaveChangesAsync();
        return row.Id;
    }

    [Fact]
    public async Task SaveAsync_StoresTheFileAndReturnsTheMentionToken()
    {
        using var ctx = new SqliteTestContext();
        var person = Seed.Person();
        await using (var db = ctx.NewContext())
        {
            db.People.Add(person);
            await db.SaveChangesAsync();
        }
        var (service, storage) = Build(ctx);

        var token = await service.SaveAsync(nameof(Person), person.Id, [1, 2, 3], "image/png",
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.SpecialAgent));

        Assert.NotNull(token);
        var parsed = Assert.Single(MentionParser.Parse(token));
        Assert.Equal(nameof(TextImage), parsed.Type);
        Assert.Single(storage.Saved);
    }

    [Fact]
    public async Task SaveAsync_RejectsANonImageAndAnOversizedPasteWithoutTouchingTheDisk()
    {
        using var ctx = new SqliteTestContext();
        var person = Seed.Person();
        await using (var db = ctx.NewContext())
        {
            db.People.Add(person);
            await db.SaveChangesAsync();
        }
        var (service, storage) = Build(ctx);
        var actor = ClaimsPrincipalBuilder.Agent().WithRank(Rank.SpecialAgent).Build();

        Assert.Null(await service.SaveAsync(nameof(Person), person.Id, [1], "application/pdf", actor));
        Assert.Null(await service.SaveAsync(nameof(Person), person.Id, new byte[2048], "image/png", actor));
        Assert.Empty(storage.Saved);
    }

    [Fact]
    public async Task SaveAsync_RefusesARecordThePastingAgentMayNotSee()
    {
        // otherwise a picture could be parked under a foreign case number and read back from there
        using var ctx = new SqliteTestContext();
        var person = Seed.Person(configure: p => p.IsClassified = true);
        await using (var db = ctx.NewContext())
        {
            db.People.Add(person);
            await db.SaveChangesAsync();
        }
        var (service, storage) = Build(ctx);

        var token = await service.SaveAsync(nameof(Person), person.Id, [1, 2, 3], "image/png",
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.SpecialAgent));

        Assert.Null(token);
        Assert.Empty(storage.Saved);
    }

    [Fact]
    public async Task SaveAsync_VetoesTheReadOnlySupervisionBeforeAnythingIsWritten()
    {
        using var ctx = new SqliteTestContext();
        var (service, storage) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveAsync(
            nameof(Person), "irgendwas", [1, 2, 3], "image/png",
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director).AsTeamLead()));
        Assert.Empty(storage.Saved);
    }

    [Fact]
    public async Task GetAccessAsync_AnswersTheSameMissForAnUnknownIdAndAClassifiedCarrier()
    {
        using var ctx = new SqliteTestContext();
        var person = Seed.Person(configure: p => p.IsClassified = true);
        await using (var db = ctx.NewContext())
        {
            db.People.Add(person);
            await db.SaveChangesAsync();
        }
        var id = await RowAsync(ctx, nameof(Person), person.Id);
        var (service, _) = Build(ctx);

        Assert.Null(await service.GetAccessAsync(Guid.NewGuid().ToString(),
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.SupervisorySpecialAgent)));
        Assert.Null(await service.GetAccessAsync(id, ClaimsPrincipalBuilder.Agent().WithRank(Rank.SpecialAgent)));
    }

    [Fact]
    public async Task GetAccessAsync_HandsOutTheFileToAViewerOfTheCarryingRecord()
    {
        using var ctx = new SqliteTestContext();
        var person = Seed.Person(configure: p => p.IsClassified = true);
        await using (var db = ctx.NewContext())
        {
            db.People.Add(person);
            await db.SaveChangesAsync();
        }
        var id = await RowAsync(ctx, nameof(Person), person.Id);
        var (service, _) = Build(ctx);

        var access = await service.GetAccessAsync(id, ClaimsPrincipalBuilder.Agent().WithRank(Rank.SupervisorySpecialAgent));

        Assert.NotNull(access);
        Assert.Equal("bild.png", access.FileNameSaved);
        Assert.Equal("image/png", access.ContentType);
    }

    [Fact]
    public async Task GetAccessAsync_TreatsASoftDeletedCarrierAsGone()
    {
        // the record is out of every list, so its pictures must not stay readable through a kept token
        using var ctx = new SqliteTestContext();
        var person = Seed.Person(configure: p => p.IsDeleted = true);
        await using (var db = ctx.NewContext())
        {
            db.People.Add(person);
            await db.SaveChangesAsync();
        }
        var id = await RowAsync(ctx, nameof(Person), person.Id);
        var (service, _) = Build(ctx);

        Assert.Null(await service.GetAccessAsync(id, ClaimsPrincipalBuilder.Agent().WithRank(Rank.SupervisorySpecialAgent)));
    }

    [Fact]
    public async Task GetAccessAsync_KeepsAPersonalFileImageWithLeadership()
    {
        // a personal file carries no classification flag although only leadership may read it
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("agent-9"));
            await db.SaveChangesAsync();
        }
        var id = await RowAsync(ctx, nameof(Agent), "agent-9");
        var (service, _) = Build(ctx);

        Assert.Null(await service.GetAccessAsync(id, ClaimsPrincipalBuilder.Agent().WithRank(Rank.SpecialAgent)));
        Assert.NotNull(await service.GetAccessAsync(id,
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.SupervisorySpecialAgent)));
    }

    [Fact]
    public async Task GetAccessAsync_AnswersARichTextCarrierThroughTheRecordGate()
    {
        // the interceptor files rich-text pictures under their own record instead of a mention token
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("agent-9"));
            db.AgentNotes.Add(new AgentNote
            {
                Id = "n1", AgentId = "agent-9", EntryDate = DateTime.UtcNow, Text = "<p>Vermerk</p>",
            });
            await db.SaveChangesAsync();
        }
        var id = await RowAsync(ctx, nameof(AgentNote), "n1");
        var (service, _) = Build(ctx);

        Assert.Null(await service.GetAccessAsync(id, ClaimsPrincipalBuilder.Agent().WithRank(Rank.SpecialAgent)));
        Assert.NotNull(await service.GetAccessAsync(id,
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.SupervisorySpecialAgent)));
    }

    [Fact]
    public async Task GetAccessAsync_RefusesACarrierTypeNobodyRegistered()
    {
        // an unregistered type must not fall through to a "visible" answer
        using var ctx = new SqliteTestContext();
        var id = await RowAsync(ctx, "GibtEsNicht", "x");
        var (service, _) = Build(ctx);

        Assert.Null(await service.GetAccessAsync(id,
            ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director)));
    }
}
