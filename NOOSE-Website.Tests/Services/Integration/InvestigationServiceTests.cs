using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Investigations;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Data.Entities.Warrants;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.CurrentUser;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Investigations;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Write rules of investigations that the access resolution alone does not cover.</summary>
public sealed class InvestigationServiceTests : IDisposable
{
    private const string Tf = "tf-1";
    private const string Inv = "inv-1";

    private readonly SqliteTestContext _ctx = new();
    private readonly InvestigationService _svc;
    private readonly IInvestigationPhotoStorageService _photos = Substitute.For<IInvestigationPhotoStorageService>();

    public InvestigationServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_ctx.Connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.AmbientTransactionWarning))
            .AddInterceptors(new AuditSaveChangesInterceptor(new CurrentUser()))
            .Options;
        _svc = new InvestigationService(new TestDbContextFactory(options), Substitute.For<ICaseNumberService>(),
            Substitute.For<INotificationService>(), _photos);
        _photos.IsAllowedType(Arg.Any<string>()).Returns(true);
        _photos.MaxBytes.Returns(10 * 1024 * 1024);
        _photos.SaveAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("bild.png");

        using var db = _ctx.NewContext();
        db.Users.AddRange(
            Seed.Agent("lead", Rank.SpecialAgent),
            Seed.Agent("manager", Rank.SpecialAgent),
            Seed.Agent("other", Rank.SpecialAgent),
            Seed.Agent("sid", configure: a =>
            {
                a.PartnerAgency = PartnerAgency.DoJ;
                a.PartnerUnitId = PartnerUnits.DojSid;
            }));
        db.Taskforces.Add(new Taskforce { Id = Tf, Name = "TF", CaseNumber = "FIB-TF-2026-0001", Scope = TaskforceScope.CrossAgency });
        db.TaskforcePartnerAgencies.Add(new TaskforcePartnerAgency { TaskforceId = Tf, Agency = PartnerAgency.DoJ });
        db.Investigations.Add(new Investigation { Id = Inv, TaskforceId = Tf, Title = "Akte", CaseNumber = "FIB-ER-2026-0001" });
        db.InvestigationParticipants.AddRange(
            new InvestigationParticipant { Id = "row-lead", InvestigationId = Inv, AgentId = "lead", Role = InvestigationRole.Lead },
            // may manage participants, but is no lead
            new InvestigationParticipant { Id = "row-manager", InvestigationId = Inv, AgentId = "manager", Role = InvestigationRole.Investigator,
                TabAccessJson = """{"beteiligte":2}""" },
            new InvestigationParticipant { Id = "row-other", InvestigationId = Inv, AgentId = "other", Role = InvestigationRole.Investigator },
            new InvestigationParticipant { Id = "row-sid", InvestigationId = Inv, AgentId = "sid", Role = InvestigationRole.Investigator,
                TabAccessJson = """{"tatvorwurf":1,"tathergang":1,"personen":1,"beweismittel":1,"vernehmungen":1,"aufgaben":1,"notizen":1,"anordnungen":1,"beteiligte":1,"verlauf":1}""" });
        db.SaveChanges();
    }

    public void Dispose() => _ctx.Dispose();

    /// <summary>Acting agent for the audit stamp.</summary>
    private sealed class CurrentUser : ICurrentUserService
    {
        public Task<CurrentUserInfo> GetAsync() => Task.FromResult(Get());
        public CurrentUserInfo Get() => new("lead", "lead", false, false, false);
    }

    private static ClaimsPrincipal Internal(string id) => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.SpecialAgent).Build();

    private static InvestigationParticipantInput Row(string id, string agentId, InvestigationRole role)
        => new(id, agentId, null, role, new Dictionary<string, InvestigationAccessLevel>());

    [Fact]
    public async Task A_manager_cannot_make_themselves_lead()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _svc.SaveParticipantAsync(Inv, Row("row-manager", "manager", InvestigationRole.Lead), Internal("manager")));
    }

    [Fact]
    public async Task A_manager_cannot_change_their_own_rights()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _svc.SaveParticipantAsync(Inv, Row("row-manager", "manager", InvestigationRole.Observer), Internal("manager")));
    }

    [Fact]
    public async Task A_manager_cannot_demote_or_remove_the_lead()
    {
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _svc.SaveParticipantAsync(Inv, Row("row-lead", "lead", InvestigationRole.Investigator), Internal("manager")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _svc.RemoveParticipantAsync(Inv, "row-lead", Internal("manager")));
    }

    [Fact]
    public async Task A_manager_still_changes_others()
    {
        await _svc.SaveParticipantAsync(Inv, Row("row-other", "other", InvestigationRole.Observer), Internal("manager"));

        await using var db = _ctx.NewContext();
        Assert.Equal(InvestigationRole.Observer, db.InvestigationParticipants.Find("row-other")!.Role);
    }

    [Fact]
    public async Task The_last_lead_cannot_step_down()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _svc.SaveParticipantAsync(Inv, Row("row-lead", "lead", InvestigationRole.Investigator), Internal("lead")));
    }

    [Fact]
    public async Task The_history_hides_entries_limited_to_others()
    {
        var chargeId = await _svc.SaveChargeAsync(Inv,
            new InvestigationChargeInput(null, null, "§ 99", "StGB", "Geheim", InvestigationChargeCheck.Open), Internal("lead"));
        await _svc.SetRestrictionAsync(Inv, nameof(InvestigationCharge), chargeId,
            [new InvestigationTarget("other", null, "other")], Internal("lead"));
        var sid = ClaimsPrincipalBuilder.Agent("sid").AsPartnerUnit(PartnerAgency.DoJ, PartnerUnits.DojSid).Build();

        Assert.DoesNotContain(await _svc.GetHistoryAsync(Inv, sid), h => h.What.Contains("§ 99"));
        Assert.Contains(await _svc.GetHistoryAsync(Inv, Internal("lead")), h => h.What.Contains("§ 99"));

        // still hidden once the entry is deleted
        await _svc.DeleteEntryAsync(Inv, nameof(InvestigationCharge), chargeId, Internal("lead"));
        Assert.DoesNotContain(await _svc.GetHistoryAsync(Inv, sid), h => h.What.Contains("§ 99"));
    }

    [Fact]
    public async Task A_partner_who_writes_evidence_gets_the_officer_names_without_the_participants_tab()
    {
        var sid = ClaimsPrincipalBuilder.Agent("sid").AsPartnerUnit(PartnerAgency.DoJ, PartnerUnits.DojSid).Build();
        // reading only: no picker, so no names
        var reader = await _svc.GetAsync(Inv, sid);
        Assert.Empty(reader!.Officers);

        await using (var db = _ctx.NewContext())
        {
            db.InvestigationParticipants.Find("row-sid")!.TabAccessJson = """{"beweismittel":2,"beteiligte":0}""";
            await db.SaveChangesAsync();
        }
        string sidName;
        await using (var db = _ctx.NewContext())
        {
            sidName = db.Users.Find("sid")!.Codename;
        }

        var writer = await _svc.GetAsync(Inv, sid);

        Assert.Empty(writer!.Participants);
        Assert.Equal(["lead", "manager", "other", "sid"], writer.Officers.Select(o => o.AgentId).Order());
        Assert.Equal(sidName, writer.MyName);
    }

    private async Task<(string EvidenceId, string PhotoId)> EvidenceWithPhotoAsync()
    {
        var evidenceId = await _svc.SaveEvidenceAsync(Inv,
            new InvestigationEvidenceInput(null, "Messer", null, null, null, InvestigationEvidenceStatus.Secured, null), Internal("lead"));
        var photoId = await _svc.AddEvidencePhotoAsync(Inv, evidenceId, new MemoryStream([1, 2, 3]), "messer.png", "image/png", 3, Internal("lead"));
        return (evidenceId, photoId);
    }

    [Fact]
    public async Task An_evidence_picture_follows_the_limit_of_its_piece()
    {
        var (evidenceId, photoId) = await EvidenceWithPhotoAsync();
        var sid = ClaimsPrincipalBuilder.Agent("sid").AsPartnerUnit(PartnerAgency.DoJ, PartnerUnits.DojSid).Build();
        Assert.NotNull(await _svc.GetEvidencePhotoAsync(photoId, sid));

        await _svc.SetRestrictionAsync(Inv, nameof(InvestigationEvidence), evidenceId,
            [new InvestigationTarget("other", null, "other")], Internal("lead"));

        Assert.Null(await _svc.GetEvidencePhotoAsync(photoId, sid));
        Assert.NotNull(await _svc.GetEvidencePhotoAsync(photoId, Internal("other")));
        Assert.DoesNotContain(await _svc.GetHistoryAsync(Inv, sid), h => h.What.Contains("messer.png"));
        Assert.Empty((await _svc.GetAsync(Inv, sid))!.Evidence);
    }

    [Fact]
    public async Task Only_edit_on_the_evidence_tab_attaches_or_removes_pictures()
    {
        var (evidenceId, photoId) = await EvidenceWithPhotoAsync();
        // the SID row reads the evidence tab only
        var sid = ClaimsPrincipalBuilder.Agent("sid").AsPartnerUnit(PartnerAgency.DoJ, PartnerUnits.DojSid).Build();
        _photos.ClearReceivedCalls();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _svc.AddEvidencePhotoAsync(Inv, evidenceId, new MemoryStream([1]), "x.png", "image/png", 1, sid));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _svc.RemoveEvidencePhotoAsync(Inv, photoId, sid));
        await _photos.DidNotReceive().SaveAsync(Arg.Any<Stream>(), "image/png", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_picture_goes_with_its_deleted_piece()
    {
        var (evidenceId, photoId) = await EvidenceWithPhotoAsync();

        await _svc.DeleteEntryAsync(Inv, nameof(InvestigationEvidence), evidenceId, Internal("lead"));

        Assert.Null(await _svc.GetEvidencePhotoAsync(photoId, Internal("lead")));
    }

    [Theory]
    [InlineData("Lena Carter ", "p-lena")]        // trailing blank
    [InlineData("Julius Hummerman", "p-julius")]  // one letter short
    [InlineData("Robnin Schneider", "p-robin")]   // typo, the other Schneider is far off
    [InlineData("Timotheus Porter", "p-timo")]    // two letters off
    [InlineData("Unbekannte Person", null)]
    public void An_imported_interrogation_finds_its_person_despite_a_typo(string name, string? expected)
    {
        var people = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Lena Carter"] = "p-lena", ["Julius Hummermann"] = "p-julius", ["Robin Schneider"] = "p-robin",
            ["Johann Schneider"] = "p-johann", ["Timotheus Porta"] = "p-timo",
        };

        Assert.Equal(expected, InvestigationService.MatchPerson(name, people));
    }

    [Fact]
    public void Two_close_names_link_to_neither()
    {
        var people = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Max Muster"] = "a", ["Max Mustar"] = "b" };

        Assert.Null(InvestigationService.MatchPerson("Max Mustor", people));
    }

    private static string EvidenceToken() => MentionParser.Token(nameof(InvestigationEvidence), Guid.NewGuid().ToString());

    [Fact]
    public async Task The_five_reference_fields_store_the_bare_token()
    {
        var lead = Internal("lead");
        // a token for a piece that does not even exist: writing validates nothing, so it is no oracle either
        var text = $"siehe {EvidenceToken()} dazu";

        await _svc.UpdateAsync(Inv, new InvestigationInput("Akte", Tf, null, null, InvestigationStatus.Investigating, DateTime.UtcNow, text), lead);
        var noteId = await _svc.AddNoteAsync(Inv, text, lead);
        var summaryId = await _svc.SaveInterrogationAsync(Inv, new InvestigationInterrogationInput(null, null, "Max", "Zeuge", null, null,
            null, null, text, null, true, []), lead);
        var evidenceId = await _svc.SaveEvidenceAsync(Inv,
            new InvestigationEvidenceInput(null, "Messer", text, null, null, InvestigationEvidenceStatus.Secured, null), lead);
        var personId = await _svc.SavePersonAsync(Inv,
            new InvestigationPersonInput(null, "Max", null, null, "Zeuge", InvestigationPersonStatus.Listed, text), lead);

        await using var db = _ctx.NewContext();
        Assert.Equal(text, db.Investigations.Find(Inv)!.Indictment);
        Assert.Equal(text, db.InvestigationNotes.Find(noteId)!.Text);
        Assert.Equal(text, db.InvestigationInterrogations.Find(summaryId)!.Summary);
        Assert.Equal(text, db.InvestigationEvidence.Find(evidenceId)!.Description);
        Assert.Equal(text, db.InvestigationPeople.Find(personId)!.Note);
    }

    [Fact]
    public async Task A_reference_field_never_keeps_half_a_token()
    {
        var token = EvidenceToken();
        // the token straddles the limit: a plain cut would leave "@{InvestigationEvi" as literal text
        var text = new string('a', InvestigationService.NoteMaxLength - 10) + " " + token;

        var noteId = await _svc.AddNoteAsync(Inv, text, Internal("lead"));

        await using var db = _ctx.NewContext();
        var stored = db.InvestigationNotes.Find(noteId)!.Text;
        Assert.True(stored.Length <= InvestigationService.NoteMaxLength);
        Assert.DoesNotContain("@{", stored);
        Assert.Equal(new string('a', InvestigationService.NoteMaxLength - 10), stored);
    }

    [Fact]
    public async Task A_token_in_a_task_stays_out_of_the_history_label()
    {
        var token = EvidenceToken();
        await _svc.SaveTaskAsync(Inv, new InvestigationTaskInput(null, $"Prüfen {token} sofort", null, null), Internal("lead"));

        var history = await _svc.GetHistoryAsync(Inv, Internal("lead"));

        var entry = Assert.Single(history, h => h.EntityType == nameof(InvestigationTask));
        Assert.DoesNotContain("@{", entry.What);
        Assert.Contains("Prüfen sofort", entry.What);
    }

    [Fact]
    public async Task An_investigation_with_an_open_warrant_cannot_be_deleted()
    {
        await using (var db = _ctx.NewContext())
        {
            db.People.Add(Seed.Person("p-1"));
            db.Warrants.Add(new Warrant
            {
                Id = "w-1", InvestigationId = Inv, PersonId = "p-1", Status = WarrantStatus.Requested,
                CaseNumber = "FIB-AN-2026-0001", Subject = "Haus", Justification = "Grund",
            });
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => _svc.DeleteAsync(Inv, Internal("lead")));
    }
}
