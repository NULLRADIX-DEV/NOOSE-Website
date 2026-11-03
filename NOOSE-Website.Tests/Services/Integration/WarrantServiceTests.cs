using System.Security.Claims;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Warrants;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.CurrentUser;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Warrants;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;
using NOOSE_Website.Services.Search;
using NOOSE_Website.Services.Search.Providers;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="WarrantService"/>: lifecycle, rights and visibility over in-memory SQLite.</summary>
/// <remarks>Runs with the audit interceptor attached, because "own request" and the soft delete both come from it.</remarks>
public sealed class WarrantServiceTests : IDisposable
{
    private readonly SqliteTestContext _ctx = new();
    private readonly CurrentUser _currentUser = new();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();
    private readonly ICaseNumberService _caseNumbers = Substitute.For<ICaseNumberService>();
    private readonly TestDbContextFactory _factory;
    private readonly WarrantService _svc;

    public WarrantServiceTests()
    {
        var next = 0;
        _caseNumbers.NextAsync(Arg.Any<AppDbContext>(), WarrantRules.CaseNumberPrefix, Arg.Any<CancellationToken>())
            .Returns(_ => $"FIB-AN-2026-{++next:0000}");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_ctx.Connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.AmbientTransactionWarning))
            .AddInterceptors(new AuditSaveChangesInterceptor(_currentUser))
            .Options;
        _factory = new TestDbContextFactory(options);
        _svc = new WarrantService(_factory, _caseNumbers, _notifications);

        using var db = _ctx.NewContext();
        db.People.Add(Seed.Person("p-open", "Otto Offen"));
        db.People.Add(Seed.Person("p-vs", "Vera Verschluss", p => p.IsClassified = true));
        db.People.Add(Seed.Person("p-hrt", "Theo Hrt", p => { p.IsClassified = true; p.IsTRUClassified = true; }));
        db.Cases.Add(Seed.Case("c-open", "Offener Vorgang"));
        db.Cases.Add(Seed.Case("c-vs", "Geheimer Vorgang", c => c.IsClassified = true));
        db.Laws.Add(new Law { Id = "l1", LawBook = "StGB", Paragraph = "§ 12", Title = "Diebstahl", Text = "<p>a</p>" });
        db.Laws.Add(new Law { Id = "l2", LawBook = "BtMG", Paragraph = "§ 5", Title = "Besitz", Text = "<p>b</p>" });
        db.SaveChanges();
    }

    public void Dispose() => _ctx.Dispose();

    /// <summary>Acting agent for the audit stamp; the services read the principal, the interceptor reads this.</summary>
    private sealed class CurrentUser : ICurrentUserService
    {
        public string? Id { get; set; }
        public Task<CurrentUserInfo> GetAsync() => Task.FromResult(Get());
        public CurrentUserInfo Get() => new(Id, Id, false, false, false);
    }

    private ClaimsPrincipal As(ClaimsPrincipal principal)
    {
        _currentUser.Id = principal.GetAgentId();
        return principal;
    }

    private static ClaimsPrincipal Agent(string id = "agent")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.SpecialAgent).WithCodename($"Hawk-{id}").Build();

    private static ClaimsPrincipal Leader(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.SpecialAgentInCharge).WithCodename($"Falcon-{id}").Build();

    private static ClaimsPrincipal Reader()
        => ClaimsPrincipalBuilder.Agent("tl").WithRank(Rank.Director).AsTeamLead().Build();

    private static ClaimsPrincipal TeamLeadAdmin()
        => ClaimsPrincipalBuilder.Agent("tla").WithRank(Rank.Director).AsTeamLead().AsAdmin().Build();

    private static ClaimsPrincipal Demo()
        => ClaimsPrincipalBuilder.Agent("demo").WithRank(Rank.Director).AsHrb().AsDemo().Build();

    private static ClaimsPrincipal Partner()
        => ClaimsPrincipalBuilder.Agent("partner").AsPartner(PartnerAgency.DoJ, PartnerRank.Chief).Build();

    private static ClaimsPrincipal Hrt()
        => ClaimsPrincipalBuilder.Agent("hrt").WithRank(Rank.SpecialAgent).AsTru().Build();

    private static ClaimsPrincipal Applicant()
        => ClaimsPrincipalBuilder.Agent("app").WithStatus(AgentStatus.Applicant).Build();

    private static WarrantInput Input(string person = "p-open", string? caseId = null, params string[] laws) => new()
    {
        Type = WarrantType.Search,
        PersonId = person,
        CaseId = caseId,
        Subject = "Wohnung Grove Street 12",
        Justification = "Hinweis auf ein Waffenlager",
        LawIds = laws.Length == 0 ? ["l1"] : laws.ToList(),
        ValidityDays = 3,
    };

    private Warrant Raw(string id)
    {
        using var db = _ctx.NewContext();
        return db.Warrants.IgnoreQueryFilters().AsNoTracking().Single(w => w.Id == id);
    }

    private void Update(string id, Action<Warrant> change)
    {
        using var db = _ctx.NewContext();
        var row = db.Warrants.IgnoreQueryFilters().Single(w => w.Id == id);
        change(row);
        db.SaveChanges();
    }

    private async Task<Warrant> SignedAsync(string person = "p-open", string? caseId = null)
    {
        var w = await _svc.CreateAsync(Input(person, caseId), As(Agent()));
        await _svc.SignAsync(w.Id, 3, null, As(Leader()));
        return w;
    }

    // ---- requesting ----

    [Fact]
    public async Task An_agent_requests_and_gets_a_case_number_and_a_legal_basis_snapshot()
    {
        var w = await _svc.CreateAsync(Input("p-open", "c-open", "l2", "l1"), As(Agent()));

        var row = Raw(w.Id);
        Assert.Equal("FIB-AN-2026-0001", row.CaseNumber);
        Assert.Equal(WarrantStatus.Requested, row.Status);
        Assert.Equal("agent", row.CreatedById);
        Assert.Equal("Hawk-agent", row.RequesterName);
        Assert.Equal("c-open", row.CaseId);
        // citation order is the requester's order
        Assert.Equal(["§ 5 BtMG – Besitz", "§ 12 StGB – Diebstahl"], WarrantRules.LegalBasisLines(row));
        Assert.Equal(["l2", "l1"], WarrantRules.LawIdsOf(row));
        Assert.Null(row.ValidFrom);
        Assert.Null(row.ValidUntil);
        await _caseNumbers.Received(1).NextAsync(Arg.Any<AppDbContext>(), "AN", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_law_edited_after_the_request_does_not_move_the_paper()
    {
        var w = await _svc.CreateAsync(Input(), As(Agent()));
        using (var db = _ctx.NewContext())
        {
            db.Laws.Single(l => l.Id == "l1").Title = "Schwerer Diebstahl";
            db.SaveChanges();
        }

        Assert.Equal(["§ 12 StGB – Diebstahl"], WarrantRules.LegalBasisLines(Raw(w.Id)));
        // the link follows the law book, the snapshot does not
        var view = await _svc.GetAsync(w.Id, Agent());
        Assert.Equal("§ 12 StGB – Schwerer Diebstahl", Assert.Single(view!.Laws!).Label);
    }

    public static TheoryData<string> NonRequesters => ["reader", "teamleadadmin", "demo", "partner", "applicant"];

    private static ClaimsPrincipal Named(string who) => who switch
    {
        "reader" => Reader(),
        "teamleadadmin" => TeamLeadAdmin(),
        "demo" => Demo(),
        "partner" => Partner(),
        "applicant" => Applicant(),
        _ => throw new ArgumentOutOfRangeException(nameof(who)),
    };

    [Theory]
    [MemberData(nameof(NonRequesters))]
    public async Task Only_internal_writers_request(string who)
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _svc.CreateAsync(Input(), As(Named(who))));

        using var db = _ctx.NewContext();
        Assert.Empty(db.Warrants.IgnoreQueryFilters());
        await _caseNumbers.DidNotReceiveWithAnyArgs().NextAsync(default!, default!, default);
    }

    [Fact]
    public async Task A_person_the_requester_cannot_see_reads_as_missing()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.CreateAsync(Input("p-vs"), As(Agent())));
        Assert.Equal("Die Person wurde nicht gefunden.", ex.Message);

        // the same text as for an id that does not exist
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.CreateAsync(Input("nope"), As(Agent())));
        Assert.Equal(ex.Message, missing.Message);
    }

    [Fact]
    public async Task A_case_the_requester_cannot_see_is_refused()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.CreateAsync(Input("p-open", "c-vs"), As(Agent())));
        Assert.Equal("Der Vorgang wurde nicht gefunden.", ex.Message);
    }

    [Fact]
    public async Task A_hrt_agent_may_request_against_a_tru_person()
    {
        var w = await _svc.CreateAsync(Input("p-hrt"), As(Hrt()));

        Assert.Equal("p-hrt", Raw(w.Id).PersonId);
    }

    [Fact]
    public async Task The_request_is_validated()
    {
        var agent = As(Agent());
        var noSubject = Input();
        noSubject.Subject = "   ";
        await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.CreateAsync(noSubject, agent));

        var noReason = Input();
        noReason.Justification = string.Empty;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.CreateAsync(noReason, agent));

        var noLaw = Input();
        noLaw.LawIds = [];
        await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.CreateAsync(noLaw, agent));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.CreateAsync(Input("p-open", null, "l1", "gone"), agent));

        var tooMany = Input();
        tooMany.LawIds = Enumerable.Range(0, WarrantRules.MaxLaws + 1).Select(i => $"l{i}").ToList();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.CreateAsync(tooMany, agent));

        var oddDays = Input();
        oddDays.ValidityDays = 5;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.CreateAsync(oddDays, agent));

        var unknownType = Input();
        unknownType.Type = (WarrantType)99;
        await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.CreateAsync(unknownType, agent));

        var tooLong = Input();
        tooLong.Subject = new string('x', WarrantRules.SubjectMaxLength + 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.CreateAsync(tooLong, agent));

        using var db = _ctx.NewContext();
        Assert.Empty(db.Warrants.IgnoreQueryFilters());
    }

    [Fact]
    public async Task A_cited_paragraph_counts_once()
    {
        var w = await _svc.CreateAsync(Input("p-open", null, "l1", "l1", " l1 "), As(Agent()));

        Assert.Equal(["l1"], WarrantRules.LawIdsOf(Raw(w.Id)));
    }

    // ---- signing ----

    [Fact]
    public async Task Leadership_signs_and_the_validity_starts_now()
    {
        var w = await _svc.CreateAsync(Input(), As(Agent()));
        var before = DateTime.UtcNow;

        await _svc.SignAsync(w.Id, 7, "  passt  ", As(Leader()));

        var row = Raw(w.Id);
        Assert.Equal(WarrantStatus.Signed, row.Status);
        Assert.Equal(7, row.ValidityDays);
        Assert.NotNull(row.ValidFrom);
        Assert.True(row.ValidFrom >= before.AddSeconds(-1));
        Assert.Equal(row.ValidFrom!.Value.AddDays(7), row.ValidUntil);
        Assert.Equal("lead", row.DeciderId);
        Assert.Equal("Falcon-lead", row.DeciderName);
        Assert.Equal(Rank.SpecialAgentInCharge, row.DeciderRank);
        Assert.Equal("passt", row.DecisionNote);
        Assert.Equal(WarrantState.Valid, (await _svc.GetAsync(w.Id, Agent()))!.State);
        await _notifications.Received(1).NotifyAsync("agent", NotificationType.RequestDecided,
            Arg.Is<string>(t => t.Contains(row.CaseNumber) && !t.Contains("Otto")), $"/anordnungen/{w.Id}",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_admin_without_a_leadership_rank_counts_as_leadership()
    {
        var w = await _svc.CreateAsync(Input(), As(Agent()));
        var admin = ClaimsPrincipalBuilder.Agent("adm").WithRank(Rank.Trainee).AsAdmin().Build();

        await _svc.SignAsync(w.Id, 3, null, As(admin));

        Assert.Equal(WarrantStatus.Signed, Raw(w.Id).Status);
    }

    [Fact]
    public async Task Nobody_signs_their_own_request()
    {
        var w = await _svc.CreateAsync(Input(), As(Leader()));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.SignAsync(w.Id, 3, null, As(Leader())));
        Assert.Contains("Vier-Augen", ex.Message);
        Assert.Equal(WarrantStatus.Requested, Raw(w.Id).Status);

        await _svc.SignAsync(w.Id, 3, null, As(Leader("lead2")));
        Assert.Equal(WarrantStatus.Signed, Raw(w.Id).Status);
        // a self-decided step rings no bell for the actor, but the requester here is someone else
        await _notifications.Received(1).NotifyAsync("lead", NotificationType.RequestDecided, Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    public static TheoryData<string> NonSigners => ["agent", "reader", "teamleadadmin", "demo", "partner"];

    [Theory]
    [MemberData(nameof(NonSigners))]
    public async Task Only_leadership_with_write_access_signs(string who)
    {
        var w = await _svc.CreateAsync(Input(), As(Agent("requester")));
        var principal = who == "agent" ? Agent() : Named(who);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _svc.SignAsync(w.Id, 3, null, As(principal)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _svc.RejectAsync(w.Id, null, As(principal)));

        Assert.Equal(WarrantStatus.Requested, Raw(w.Id).Status);
    }

    [Fact]
    public async Task A_decided_request_cannot_be_decided_again()
    {
        var w = await SignedAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.SignAsync(w.Id, 3, null, As(Leader("lead2"))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.RejectAsync(w.Id, null, As(Leader("lead2"))));
    }

    [Fact]
    public async Task A_signer_chooses_only_an_offered_validity()
    {
        var w = await _svc.CreateAsync(Input(), As(Agent()));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.SignAsync(w.Id, 30, null, As(Leader())));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.SignAsync(w.Id, 0, null, As(Leader())));
        Assert.Equal(WarrantStatus.Requested, Raw(w.Id).Status);
    }

    [Fact]
    public async Task A_leader_cannot_sign_what_they_cannot_see()
    {
        var w = await _svc.CreateAsync(Input("p-hrt"), As(Hrt()));
        // an HRT-only record is not hidden from leadership, so hide the warrant through a trashed person instead
        using (var db = _ctx.NewContext())
        {
            db.People.Single(p => p.Id == "p-hrt").IsDeleted = true;
            db.SaveChanges();
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.SignAsync(w.Id, 3, null, As(Leader())));
        Assert.Equal("Die Anordnung wurde nicht gefunden.", ex.Message);
    }

    [Fact]
    public async Task A_rejection_is_final()
    {
        var w = await _svc.CreateAsync(Input(), As(Agent()));

        await _svc.RejectAsync(w.Id, "zu dünn", As(Leader()));

        var row = Raw(w.Id);
        Assert.Equal(WarrantStatus.Rejected, row.Status);
        Assert.Null(row.ValidUntil);
        Assert.Equal("zu dünn", row.DecisionNote);
        Assert.Equal(WarrantState.Rejected, (await _svc.GetAsync(w.Id, Agent()))!.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.SignAsync(w.Id, 3, null, As(Leader("lead2"))));
    }

    [Fact]
    public void The_status_is_a_concurrency_token()
    {
        using var db = _ctx.NewContext();
        var status = db.Model.FindEntityType(typeof(Warrant))!.FindProperty(nameof(Warrant.Status))!;

        Assert.True(status.IsConcurrencyToken);
    }

    [Fact]
    public async Task A_decision_on_a_stale_copy_is_refused()
    {
        var w = await _svc.CreateAsync(Input(), As(Agent()));
        await using var stale = _factory.CreateDbContext();
        var copy = await stale.Warrants.SingleAsync(x => x.Id == w.Id);

        await _svc.RejectAsync(w.Id, null, As(Leader()));
        copy.Status = WarrantStatus.Signed;

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
        Assert.Equal(WarrantStatus.Rejected, Raw(w.Id).Status);
    }

    // ---- expiry and cancellation ----

    [Fact]
    public async Task Validity_runs_out_on_read_without_any_worker()
    {
        var w = await SignedAsync();
        Update(w.Id, x => x.ValidUntil = DateTime.UtcNow.AddMinutes(-1));

        Assert.Equal(WarrantState.Expired, (await _svc.GetAsync(w.Id, Agent()))!.State);
        Assert.Equal(WarrantStatus.Signed, Raw(w.Id).Status);
        Assert.Single(await _svc.GetListAsync(WarrantState.Expired, Agent()));
        Assert.Empty(await _svc.GetListAsync(WarrantState.Valid, Agent()));
    }

    [Fact]
    public async Task Only_a_valid_warrant_can_be_cancelled()
    {
        var open = await _svc.CreateAsync(Input(), As(Agent()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.CancelAsync(open.Id, "Grund", As(Leader())));

        var expired = await SignedAsync();
        Update(expired.Id, x => x.ValidUntil = DateTime.UtcNow.AddMinutes(-1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.CancelAsync(expired.Id, "Grund", As(Leader())));

        var valid = await SignedAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.CancelAsync(valid.Id, "  ", As(Leader())));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _svc.CancelAsync(valid.Id, "Grund", As(Agent())));

        await _svc.CancelAsync(valid.Id, "Beschuldigter gestellt", As(Leader("lead2")));

        var row = Raw(valid.Id);
        Assert.Equal(WarrantStatus.Cancelled, row.Status);
        Assert.Equal("Falcon-lead2", row.CancelledByName);
        Assert.Equal("Beschuldigter gestellt", row.CancellationReason);
        Assert.NotNull(row.CancelledAt);
        Assert.Equal(WarrantState.Cancelled, (await _svc.GetAsync(valid.Id, Agent()))!.State);
        Assert.True(WarrantRules.WasSigned(row));
    }

    // ---- visibility ----

    [Fact]
    public async Task A_warrant_is_never_more_visible_than_its_person()
    {
        var open = await _svc.CreateAsync(Input("p-open"), As(Agent()));
        var secret = await _svc.CreateAsync(Input("p-vs"), As(Leader()));

        Assert.NotNull(await _svc.GetAsync(open.Id, Agent("other")));
        Assert.Null(await _svc.GetAsync(secret.Id, Agent("other")));
        Assert.Equal([open.Id], (await _svc.GetListAsync(null, Agent("other"))).Select(v => v.Warrant.Id));
        Assert.Empty(await _svc.GetForPersonAsync("p-vs", Agent("other")));

        Assert.NotNull(await _svc.GetAsync(secret.Id, Leader("lead2")));
        Assert.NotNull(await _svc.GetAsync(secret.Id, Reader()));
    }

    [Fact]
    public async Task A_hrt_person_is_seen_by_tru_but_not_by_a_plain_agent()
    {
        var w = await _svc.CreateAsync(Input("p-hrt"), As(Hrt()));

        Assert.NotNull(await _svc.GetAsync(w.Id, Hrt()));
        Assert.Null(await _svc.GetAsync(w.Id, Agent()));
    }

    [Fact]
    public async Task A_classified_case_hides_the_warrant_even_when_the_person_is_open()
    {
        var w = await _svc.CreateAsync(Input("p-open", "c-vs"), As(Leader()));

        Assert.Null(await _svc.GetAsync(w.Id, Agent()));
        Assert.Empty(await _svc.GetForPersonAsync("p-open", Agent()));
        Assert.NotNull(await _svc.GetAsync(w.Id, Leader("lead2")));
        Assert.Single(await _svc.GetForCaseAsync("c-vs", Leader("lead2")));
    }

    [Fact]
    public async Task The_requester_loses_sight_when_the_person_is_classified_later()
    {
        var w = await _svc.CreateAsync(Input("p-open"), As(Agent()));
        using (var db = _ctx.NewContext())
        {
            db.People.Single(p => p.Id == "p-open").IsClassified = true;
            db.SaveChanges();
        }

        Assert.Null(await _svc.GetAsync(w.Id, Agent()));
        Assert.Empty(await _svc.GetListAsync(null, Agent()));
    }

    [Theory]
    [InlineData("partner")]
    [InlineData("applicant")]
    public async Task Outsiders_see_no_warrant(string who)
    {
        var w = await SignedAsync();
        var outsider = Named(who);

        Assert.Null(await _svc.GetAsync(w.Id, outsider));
        Assert.Empty(await _svc.GetListAsync(null, outsider));
        Assert.Empty(await _svc.GetForPersonAsync("p-open", outsider));
        Assert.Empty(await _svc.GetPendingAsync(outsider));
        Assert.Null(await _svc.GetTargetAsync("Person", "p-open", outsider));

        await using var db = _ctx.NewContext();
        Assert.False(await Visibility.IsRecordVisibleAsync(db, nameof(Warrant), w.Id, ViewerScope.From(outsider)));
    }

    [Fact]
    public async Task The_central_visibility_check_names_warrants_explicitly()
    {
        var open = await SignedAsync("p-open");
        var secret = await _svc.CreateAsync(Input("p-vs"), As(Leader()));
        await using var db = _ctx.NewContext();

        Assert.True(await Visibility.IsRecordVisibleAsync(db, nameof(Warrant), open.Id, ViewerScope.From(Agent())));
        Assert.False(await Visibility.IsRecordVisibleAsync(db, nameof(Warrant), secret.Id, ViewerScope.From(Agent())));
        Assert.True(await Visibility.IsRecordVisibleAsync(db, nameof(Warrant), secret.Id, ViewerScope.From(Leader())));
        // an unknown id is no longer answered "visible" by the tail of the method
        Assert.False(await Visibility.IsRecordVisibleAsync(db, nameof(Warrant), "nope", ViewerScope.From(Leader())));
    }

    [Fact]
    public async Task A_target_resolves_only_under_the_readers_rights()
    {
        Assert.Equal("Otto Offen", (await _svc.GetTargetAsync("Person", "p-open", Agent()))?.Name);
        Assert.Null(await _svc.GetTargetAsync("Person", "p-vs", Agent()));
        Assert.Null(await _svc.GetTargetAsync("Case", "c-vs", Agent()));
        Assert.Equal("Geheimer Vorgang", (await _svc.GetTargetAsync("Case", "c-vs", Leader()))?.Name);
        Assert.Null(await _svc.GetTargetAsync("Faction", "p-open", Leader()));
    }

    // ---- inbox ----

    [Fact]
    public async Task The_badge_counts_what_the_viewer_could_sign()
    {
        await _svc.CreateAsync(Input("p-open"), As(Agent()));
        await _svc.CreateAsync(Input("p-open"), As(Leader()));
        await SignedAsync();

        Assert.Equal(1, await _svc.GetOpenCountAsync(Leader()));
        Assert.Equal(2, await _svc.GetOpenCountAsync(Leader("lead2")));
        Assert.Equal(0, await _svc.GetOpenCountAsync(Agent()));
        Assert.Equal(0, await _svc.GetOpenCountAsync(Reader()));
        Assert.Equal(2, (await _svc.GetPendingAsync(Leader())).Count);
    }

    [Fact]
    public async Task The_inbox_leaves_out_requests_the_viewer_cannot_see()
    {
        await _svc.CreateAsync(Input("p-hrt"), As(Hrt()));

        // HRT but no leadership: may not sign, and the count says so
        Assert.Equal(0, await _svc.GetOpenCountAsync(Hrt()));
        Assert.Empty(await _svc.GetPendingAsync(Agent()));
        Assert.Single(await _svc.GetPendingAsync(Leader()));
    }

    // ---- delete and trash ----

    [Fact]
    public async Task The_requester_withdraws_an_open_request_into_the_trash()
    {
        var w = await _svc.CreateAsync(Input(), As(Agent()));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _svc.DeleteAsync(w.Id, As(Agent("other"))));
        await _svc.DeleteAsync(w.Id, As(Agent()));

        var row = Raw(w.Id);
        Assert.True(row.IsDeleted);
        Assert.Null(await _svc.GetAsync(w.Id, Leader()));
        var trashed = Assert.Single(await _svc.GetTrashAsync());
        Assert.Equal("Otto Offen", trashed.PersonName);
    }

    [Fact]
    public async Task A_signed_warrant_is_deleted_only_by_leadership()
    {
        var w = await SignedAsync();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _svc.DeleteAsync(w.Id, As(Agent())));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _svc.DeleteAsync(w.Id, As(Reader())));

        await _svc.DeleteAsync(w.Id, As(Leader("lead2")));
        Assert.True(Raw(w.Id).IsDeleted);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _svc.RestoreAsync(w.Id, As(Agent())));
        await _svc.RestoreAsync(w.Id, As(Leader()));
        Assert.False(Raw(w.Id).IsDeleted);
        Assert.Equal(WarrantState.Valid, (await _svc.GetAsync(w.Id, Agent()))!.State);
    }

    [Fact]
    public async Task Deleting_an_invisible_warrant_is_a_silent_no_op()
    {
        var secret = await _svc.CreateAsync(Input("p-vs"), As(Leader()));

        await _svc.DeleteAsync(secret.Id, As(Agent()));

        Assert.False(Raw(secret.Id).IsDeleted);
    }

    [Fact]
    public async Task A_trashed_person_takes_its_warrants_out_of_sight()
    {
        var w = await SignedAsync();
        using (var db = _ctx.NewContext())
        {
            db.People.Single(p => p.Id == "p-open").IsDeleted = true;
            db.SaveChanges();
        }

        Assert.Null(await _svc.GetAsync(w.Id, Leader()));
        Assert.Empty(await _svc.GetListAsync(null, Leader()));
    }

    // ---- surroundings ----

    [Fact]
    public async Task A_person_merge_moves_the_warrants_including_the_trashed_ones()
    {
        using (var db = _ctx.NewContext())
        {
            db.People.Add(Seed.Person("src", "Quelle"));
            db.People.Add(Seed.Person("dst", "Ziel"));
            db.SaveChanges();
        }
        var live = await _svc.CreateAsync(Input("src"), As(Agent()));
        var trashed = await _svc.CreateAsync(Input("src"), As(Agent()));
        await _svc.DeleteAsync(trashed.Id, As(Agent()));

        var merge = new PersonMergeService(_factory, Substitute.For<IPublicWantedService>());
        await merge.MergeAsync("src", "dst", As(ClaimsPrincipalBuilder.Agent("dir").WithRank(Rank.Director).Build()));

        Assert.Equal("dst", Raw(live.Id).PersonId);
        Assert.Equal("dst", Raw(trashed.Id).PersonId);
    }

    [Fact]
    public async Task The_search_finds_a_warrant_by_case_number_only_where_its_person_is_visible()
    {
        var open = await SignedAsync("p-open");
        var secret = await _svc.CreateAsync(Input("p-vs"), As(Leader()));
        var provider = new WarrantSearchProvider(_factory);

        async Task<IReadOnlyList<SearchHit>> SearchAs(ClaimsPrincipal user, string text)
            => await provider.SearchAsync(new SearchQuery
            {
                Text = text,
                Viewer = new SearchViewer(user, ViewerScope.From(user), null, DateTime.UtcNow),
            });

        var agentHits = await SearchAs(Agent(), "FIB-AN");
        Assert.Equal([open.Id], agentHits.Select(h => h.TargetId));
        Assert.Contains("Gültig", agentHits[0].Title);
        Assert.Equal(2, (await SearchAs(Leader(), "FIB-AN")).Count);
        // the internal reasoning is not searchable
        Assert.Empty(await SearchAs(Leader(), "Waffenlager"));
        Assert.Single(await SearchAs(Agent(), "Grove Street"));

        var partner = Partner();
        Assert.False(provider.AppliesTo(new SearchViewer(partner, ViewerScope.From(partner), null, DateTime.UtcNow)));
        Assert.Equal(PartnerAccess.Never, provider.Partner);
        Assert.Contains(secret.Id, (await SearchAs(Leader(), secret.CaseNumber)).Select(h => h.TargetId));
    }
}
