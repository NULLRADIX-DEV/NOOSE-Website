using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.CurrentUser;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Public;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Uploading a photo in the wanted editor: an ordinary person photo that is then picked as the one source.</summary>
public sealed class WantedPhotoUploadTests
{
    private const string PersonId = "p1";

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).WithCodename("Falcon").Build();

    private static ClaimsPrincipal Senior()
        => ClaimsPrincipalBuilder.Agent("senior").WithRank(Rank.SeniorSpecialAgent).WithCodename("Kite").Build();

    private sealed class FixedUser : ICurrentUserService
    {
        public Task<CurrentUserInfo> GetAsync() => Task.FromResult(Get());

        public CurrentUserInfo Get() => new("lead", "Falcon", true, false, false);
    }

    private sealed record Host(
        PublicWantedService Wanted,
        WantedPhotoUploadService Upload,
        IFileStorageService PeopleFiles,
        IPublicWantedPhotoStorageService PublicFiles);

    private static Host NewHost(SqliteTestContext ctx)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(ctx.Connection)
            .AddInterceptors(new AuditSaveChangesInterceptor(new FixedUser()))
            .Options;
        var factory = new TestDbContextFactory(options);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var modules = new PublicModuleService(factory, cache);

        var caseNumbers = Substitute.For<ICaseNumberService>();
        caseNumbers.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => $"FIB-{ci.ArgAt<string>(1)}-2026-0001");

        var peopleFiles = Substitute.For<IFileStorageService>();
        peopleFiles.IsAllowedType(Arg.Any<string>()).Returns(ci => ci.ArgAt<string>(0).StartsWith("image/", StringComparison.Ordinal));
        peopleFiles.MaxBytes.Returns(10L);
        peopleFiles.SaveAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("saved.jpg");
        peopleFiles.OpenRead(Arg.Any<string>()).Returns(_ => new MemoryStream([1, 2, 3]));
        var publicFiles = Substitute.For<IPublicWantedPhotoStorageService>();
        publicFiles.SaveAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("public.jpg");

        var wanted = new PublicWantedService(factory, modules, caseNumbers, peopleFiles, publicFiles,
            Substitute.For<INotificationService>(), new TipPriorityService(factory),
            Substitute.For<IDiscordWebhookService>(), Substitute.For<IPressReleaseService>(), cache);
        var people = new PersonService(factory, peopleFiles, Substitute.For<IProfileSuggestionService>(), caseNumbers,
            Substitute.For<IThreatScoreService>(), Substitute.For<INotificationService>(), wanted);
        return new Host(wanted, new WantedPhotoUploadService(wanted, people), peopleFiles, publicFiles);
    }

    private static async Task<SqliteTestContext> SeededAsync(Action<Data.Entities.People.Person>? person = null)
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        await PublicModuleSeeder.SeedAsync(db);
        (await db.OeffentlicheModule.SingleAsync(m => m.Key == PublicModules.Wanted)).IsEnabled = true;
        db.People.Add(Seed.Person(PersonId, "Max Mustermann", p =>
        {
            p.CaseNumber = "FIB-P-2026-0001";
            p.WantedReason = "Verdacht auf Waffenhandel";
            person?.Invoke(p);
        }));
        await db.SaveChangesAsync();
        return ctx;
    }

    private static Task<string> DraftAsync(Host host) => host.Wanted.CreateDraftFromPersonAsync(PersonId, Leader());

    private static Stream Image() => new MemoryStream([1, 2, 3]);

    [Fact]
    public async Task AnUpload_CreatesAPersonPhoto_AndCanBePickedAsTheSource()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host);

        var option = await host.Upload.UploadAsync(id, Image(), "mugshot.jpg", "image/jpeg", 3, Leader());

        await using (var db = ctx.NewContext())
        {
            var photo = await db.PersonPhotos.SingleAsync();
            Assert.Equal(option.Id, photo.Id);
            Assert.Equal(PersonId, photo.PersonId);
            Assert.Equal("saved.jpg", photo.FileNameSaved);
            Assert.Equal("lead", photo.CreatedById);
        }
        Assert.Contains(option.Id, (await host.Wanted.GetOptionsAsync(id, Leader())).Photos.Select(p => p.Id));

        await host.Wanted.UpdateSnapshotAsync(
            new PublicWantedInput { Id = id, DisplayName = "Max Mustermann", PhotoSourceId = option.Id }, Leader());

        Assert.Equal(option.Id, (await host.Wanted.GetDraftAsync(id, Leader()))!.PhotoSourceId);
    }

    [Fact]
    public async Task OnALiveNotice_TheUploadedPhotoIsCopiedWithTheNextSave()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host);
        await host.Wanted.PublishAsync(id, null, Leader());

        var option = await host.Upload.UploadAsync(id, Image(), "mugshot.jpg", "image/jpeg", 3, Leader());
        await host.PublicFiles.DidNotReceive().SaveAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        await host.Wanted.UpdateSnapshotAsync(
            new PublicWantedInput { Id = id, DisplayName = "Max Mustermann", PhotoSourceId = option.Id }, Leader());

        await host.PublicFiles.Received(1).SaveAsync(Arg.Any<Stream>(), "image/jpeg", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheTypeAndSizeRulesOfThePersonPhotoApply()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Upload.UploadAsync(id, Image(), "x.exe", "application/octet-stream", 3, Leader()));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Upload.UploadAsync(id, Image(), "big.jpg", "image/jpeg", 11, Leader()));

        await using var db = ctx.NewContext();
        Assert.Empty(await db.PersonPhotos.ToListAsync());
    }

    [Fact]
    public async Task APartner_AReadOnlySupervisor_ACitizenAndTheDemo_CannotUpload()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host);

        var partner = ClaimsPrincipalBuilder.Agent("partner").AsPartner(PartnerAgency.LSPD, PartnerRank.Member).Build();
        var supervisor = ClaimsPrincipalBuilder.Agent("aufsicht").WithRank(Rank.Director).AsTeamLead().Build();
        var citizen = ClaimsPrincipalBuilder.Agent("buerger").WithStatus(AgentStatus.Civilian).Build();
        var demo = ClaimsPrincipalBuilder.Agent("demo").WithRank(Rank.Director).AsDemo().Build();

        foreach (var actor in new[] { partner, supervisor, citizen, demo })
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(
                () => host.Upload.UploadAsync(id, Image(), "mugshot.jpg", "image/jpeg", 3, actor));
        }

        await using var db = ctx.NewContext();
        Assert.Empty(await db.PersonPhotos.ToListAsync());
    }

    [Fact]
    public async Task AClassifiedFile_ReadsAsNotFoundForAnAgentWhoMayNotSeeIt()
    {
        using var ctx = await SeededAsync();
        var host = NewHost(ctx);
        var id = await DraftAsync(host);
        await using (var edit = ctx.NewContext())
        {
            (await edit.People.SingleAsync(p => p.Id == PersonId)).IsClassified = true;
            await edit.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Upload.UploadAsync(id, Image(), "mugshot.jpg", "image/jpeg", 3, Senior()));

        await using var db = ctx.NewContext();
        Assert.Empty(await db.PersonPhotos.ToListAsync());
    }

    [Fact]
    public async Task AnItemNotice_TakesNoUpload()
    {
        using var ctx = await SeededAsync();
        await using (var db = ctx.NewContext())
        {
            db.OeffentlicheFahndungen.Add(new OeffentlicheFahndung
            {
                Id = "item1",
                PersonId = PersonId,
                Kind = PublicWantedKind.Fahrzeug,
                Status = PublicWantedStatus.Entwurf,
                DisplayName = "4XYZ123",
            });
            await db.SaveChangesAsync();
        }
        var host = NewHost(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Upload.UploadAsync("item1", Image(), "mugshot.jpg", "image/jpeg", 3, Leader()));

        await using var check = ctx.NewContext();
        Assert.Empty(await check.PersonPhotos.ToListAsync());
    }

    [Fact]
    public async Task ACapturedNotice_TakesNoUpload()
    {
        using var ctx = await SeededAsync();
        await using (var db = ctx.NewContext())
        {
            db.OeffentlicheFahndungen.Add(new OeffentlicheFahndung
            {
                Id = "cap1",
                PersonId = PersonId,
                Status = PublicWantedStatus.Gefasst,
                DisplayName = "Max Mustermann",
                CapturedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var host = NewHost(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.Upload.UploadAsync("cap1", Image(), "mugshot.jpg", "image/jpeg", 3, Leader()));
    }
}
