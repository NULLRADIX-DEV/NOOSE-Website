using System.Security.Claims;
using NSubstitute;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Navigation;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Changelog;
using NOOSE_Website.Services.Handbook;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Tests.Services;

/// <summary>The registry that fans the global recycle bin out over the record services.</summary>
public class TrashServiceTests
{
    private readonly IPersonService _people = Substitute.For<IPersonService>();
    private readonly ICaseService _cases = Substitute.For<ICaseService>();
    private readonly IChangelogService _changelog = Substitute.For<IChangelogService>();
    private readonly IHandbookService _handbook = Substitute.For<IHandbookService>();

    private TrashService Build() => new(
        _people,
        Substitute.For<IFactionService>(),
        Substitute.For<IPersonGroupService>(),
        Substitute.For<IPartyService>(),
        _cases,
        Substitute.For<IOperationService>(),
        Substitute.For<ITaskforceService>(),
        Substitute.For<IJobService>(),
        Substitute.For<IAnnouncementService>(),
        Substitute.For<IAppointmentService>(),
        Substitute.For<IMeetingService>(),
        Substitute.For<IAgentActivityService>(),
        Substitute.For<IAbsenceService>(),
        Substitute.For<IAbductionService>(),
        Substitute.For<IInformantService>(),
        Substitute.For<IRadioService>(),
        Substitute.For<IEvidenceService>(),
        Substitute.For<IKassenService>(),
        Substitute.For<IFinancingService>(),
        Substitute.For<IFeedbackService>(),
        Substitute.For<IPublicPageService>(),
        Substitute.For<IPublicWantedService>(),
        Substitute.For<IPublicFactionProfileService>(),
        Substitute.For<ITipService>(),
        Substitute.For<ITicketService>(),
        Substitute.For<IObjectionService>(),
        Substitute.For<IPressReleaseService>(),
        Substitute.For<IPublicWarningService>(),
        Substitute.For<IPublicReportService>(),
        _changelog,
        _handbook,
        Substitute.For<ILawService>());

    [Fact]
    public void Kind_keys_are_unique()
    {
        var keys = Build().Kinds.Select(k => k.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Kind_keys_match_the_trash_page_sections()
        => Assert.Equal(MergedPageSections.Trash, Build().Kinds.Select(k => k.Key).ToArray());

    [Fact]
    public void Every_kind_carries_a_label_icon_and_list_route()
    {
        foreach (var kind in Build().Kinds)
        {
            Assert.False(string.IsNullOrWhiteSpace(kind.Label));
            Assert.False(string.IsNullOrWhiteSpace(kind.Icon));
            Assert.StartsWith("/", kind.ListRoute);
        }
    }

    [Fact]
    public async Task Unknown_kind_is_rejected_rather_than_silently_empty()
    {
        var service = Build();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.GetAsync("gibtsnicht"));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => service.RestoreAsync("gibtsnicht", "id", new ClaimsPrincipal()));
    }

    [Fact]
    public async Task Get_projects_the_owning_service_rows_newest_deletion_first()
    {
        _people.GetTrashAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new Person { Id = "old", Name = "Alt", CaseNumber = "A-1", DeletedAt = new DateTime(2026, 1, 1) },
            new Person { Id = "new", Name = "Neu", CaseNumber = "A-2", DeletedAt = new DateTime(2026, 5, 1) },
        ]);

        var rows = await Build().GetAsync("personen");

        Assert.Equal(["new", "old"], rows.Select(r => r.Id));
        Assert.All(rows, r => Assert.Equal("personen", r.Kind));
        Assert.Equal("Neu", rows[0].Title);
    }

    [Fact]
    public async Task Kind_lookup_is_case_insensitive()
    {
        _cases.GetTrashAsync(Arg.Any<CancellationToken>()).Returns([]);
        var rows = await Build().GetAsync("VORGAENGE");
        Assert.Empty(rows);
    }

    /// <summary>The two handbook sources sit next to each other with the same shape - a classic swap.</summary>
    [Theory]
    [InlineData("neuerungen")]
    [InlineData("handbuch-kapitel")]
    [InlineData("handbuch-artikel")]
    public async Task Restore_of_a_new_source_reaches_its_own_method(string kind)
    {
        var actor = new ClaimsPrincipal();

        await Build().RestoreAsync(kind, "zeile-1", actor);

        switch (kind)
        {
            case "neuerungen":
                await _changelog.Received(1).RestoreAsync("zeile-1", actor, Arg.Any<CancellationToken>());
                break;
            case "handbuch-kapitel":
                await _handbook.Received(1).RestoreChapterAsync("zeile-1", actor, Arg.Any<CancellationToken>());
                await _handbook.DidNotReceive().RestoreArticleAsync(
                    Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
                break;
            default:
                await _handbook.Received(1).RestoreArticleAsync("zeile-1", actor, Arg.Any<CancellationToken>());
                await _handbook.DidNotReceive().RestoreChapterAsync(
                    Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
                break;
        }
    }

    [Fact]
    public async Task Restore_delegates_to_the_owning_service_so_guards_and_audit_run()
    {
        var actor = new ClaimsPrincipal();
        await Build().RestoreAsync("vorgaenge", "case-1", actor);

        await _cases.Received(1).RestoreAsync("case-1", actor, Arg.Any<CancellationToken>());
        await _people.DidNotReceive().RestoreAsync(Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }
}
