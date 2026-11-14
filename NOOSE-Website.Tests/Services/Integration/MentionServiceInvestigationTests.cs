using System.Security.Claims;
using System.Text.Json;
using NOOSE_Website.Data.Entities.Investigations;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Llm.Tools;
using NSubstitute;
using W = NOOSE_Website.Tests.Services.Integration.InvestigationReferenceWorld;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Investigation references through the mention pipeline: resolved per viewer, otherwise indistinguishable from a missing target.</summary>
public sealed class MentionServiceInvestigationTests : IDisposable
{
    private readonly W _world = new();

    public void Dispose() => _world.Dispose();

    private MentionService Service(List<QuickHit>? quickHits = null)
    {
        var search = Substitute.For<ISearchService>();
        search.QuickSearchAsync(Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(quickHits ?? new List<QuickHit>());
        return new MentionService(_world.Ctx.Factory, search);
    }

    private const string Missing = "(nicht verfügbar)";

    private static string Token(string type, string id) => MentionParser.Token(type, id);

    /// <summary>Everything a reader could observe for one token, with the id itself taken out.</summary>
    private static async Task<string> Observe(MentionService svc, string type, string id, ViewerScope scope)
    {
        var text = $"Siehe {Token(type, id)} dazu";
        var html = $"<p>Siehe {Token(type, id)} dazu</p>";
        var segments = await svc.ResolveAsync(text, scope.MayClassifiedRead, scope.MeId, partnerAgency: scope.PartnerAgency);
        var many = await svc.ResolveManyAsync([text], scope.MayClassifiedRead, scope.MeId, partnerAgency: scope.PartnerAgency);
        var rich = await svc.ResolveHtmlAsync(html, scope.MayClassifiedRead, scope.MeId, partnerAgency: scope.PartnerAgency);
        var plain = await svc.ResolveHtmlAsync(html, scope.MayClassifiedRead, scope.MeId, partnerAgency: scope.PartnerAgency, plain: true);
        var labels = await svc.HtmlLabelsAsync(html, scope.MayClassifiedRead, scope.MeId, partnerAgency: scope.PartnerAgency);
        return JsonSerializer.Serialize(new { segments, many, rich, plain, labels = labels.Values }).Replace(id, "ID");
    }

    [Theory]
    [InlineData("outsider")]
    [InlineData("member")]
    [InlineData("dojchief")]
    [InlineData("noevid")]
    public async Task An_invisible_reference_reads_exactly_like_a_random_guid(string viewer)
    {
        var svc = Service();
        var scope = W.Scope(viewer);

        var invisible = await Observe(svc, nameof(InvestigationEvidence), W.Evidence, scope);
        var random = await Observe(svc, nameof(InvestigationEvidence), W.Missing, scope);

        Assert.Equal(random, invisible);
        Assert.DoesNotContain("Verschlusssache", invisible);
        Assert.DoesNotContain("Messer", invisible);
        Assert.DoesNotContain("ermittlungen", invisible);
        var seg = (await svc.ResolveAsync(Token(nameof(InvestigationEvidence), W.Evidence), scope.MayClassifiedRead, scope.MeId,
            partnerAgency: scope.PartnerAgency)).Single();
        Assert.Equal(Missing, seg.Text);
        Assert.Null(seg.Type);
        Assert.Null(seg.Href);
        Assert.False(seg.Hidden);
    }

    [Fact]
    public async Task A_participant_gets_label_link_and_type()
    {
        var scope = W.Scope("inv");
        var seg = (await Service().ResolveAsync(Token(nameof(InvestigationEvidence), W.Evidence), scope.MayClassifiedRead, scope.MeId)).Single();

        Assert.Equal($"{W.CaseNumber} · Beweismittel #3 Messer", seg.Text);
        Assert.Equal($"/ermittlungen/{W.Inv}?tab=beweismittel&eintrag={W.Evidence}", seg.Href);
        Assert.Equal(nameof(InvestigationEvidence), seg.Type);
    }

    [Fact]
    public async Task A_partner_resolves_through_their_unit_row_and_tab_rights()
    {
        var svc = Service();
        var scope = W.Scope("sta");
        var segments = await svc.ResolveAsync(
            $"{Token(nameof(InvestigationEvidence), W.Evidence)} {Token(nameof(InvestigationNote), W.Note)}",
            scope.MayClassifiedRead, scope.MeId, partnerAgency: scope.PartnerAgency);
        var refs = segments.Where(s => s.IsReference).ToList();

        Assert.Equal($"{W.CaseNumber} · Beweismittel #3 Messer", refs[0].Text);
        Assert.Equal(Missing, refs[1].Text);
    }

    [Fact]
    public async Task Other_records_resolve_as_before_and_stay_dropped_for_partners()
    {
        var svc = Service();
        var text = $"{Token("Person", W.FilePerson)} {Token(nameof(Investigation), W.Inv)}";

        var internalRefs = (await svc.ResolveAsync(text, false, "inv")).Where(s => s.IsReference).ToList();
        Assert.StartsWith("Otto Offen", internalRefs[0].Text);
        Assert.Equal("Person", internalRefs[0].Type);
        Assert.Equal($"{W.CaseNumber} · Akte Messer", internalRefs[1].Text);

        var partnerRefs = (await svc.ResolveAsync(text, false, "sid", partnerAgency: PartnerAgency.DoJ)).Where(s => s.IsReference).ToList();
        Assert.Equal(Missing, partnerRefs[0].Text);
        Assert.Equal("Person", partnerRefs[0].Type);
        Assert.Equal($"{W.CaseNumber} · Akte Messer", partnerRefs[1].Text);
    }

    [Fact]
    public async Task Investigations_false_withholds_them_even_from_leadership()
    {
        var seg = (await Service().ResolveAsync(Token(nameof(Investigation), W.Inv), true, "chief", investigations: false)).Single();

        Assert.Equal(Missing, seg.Text);
        Assert.Null(seg.Href);
        Assert.Null(seg.Type);
    }

    [Fact]
    public async Task Editor_labels_name_what_resolves_and_neutralize_the_rest()
    {
        var html = $"<p>{Token(nameof(InvestigationEvidence), W.Evidence)} {Token(nameof(InvestigationEvidence), W.Restricted)}</p>";
        var labels = await Service().HtmlLabelsAsync(html, false, "inv");

        Assert.Equal($"{W.CaseNumber} · Beweismittel #3 Messer", labels[$"{nameof(InvestigationEvidence)}:{W.Evidence}"]);
        Assert.Equal(Missing, labels[$"{nameof(InvestigationEvidence)}:{W.Restricted}"]);
    }

    // ---- picker ----

    [Fact]
    public async Task A_stranger_gets_no_investigation_hits()
    {
        var hits = await Service().CandidatesAsync("Messer", W.Viewer("member"));
        Assert.DoesNotContain(hits, h => InvestigationReferences.IsType(h.Type));
    }

    [Fact]
    public async Task Without_the_evidence_tab_the_file_is_offered_but_not_the_evidence()
    {
        var hits = await Service().CandidatesAsync("Messer", W.Viewer("noevid"));

        Assert.Contains(hits, h => h.Type == nameof(Investigation) && h.Id == W.Inv);
        Assert.DoesNotContain(hits, h => h.Type == nameof(InvestigationEvidence));
    }

    [Fact]
    public async Task A_restricted_entry_is_not_offered_and_hash_number_finds_evidence()
    {
        var svc = Service();
        Assert.DoesNotContain(await svc.CandidatesAsync("Pistole", W.Viewer("inv")), h => h.Id == W.Restricted);
        Assert.Contains(await svc.CandidatesAsync("#3", W.Viewer("inv")), h => h.Id == W.Evidence);
    }

    [Fact]
    public async Task A_partner_gets_only_investigation_hits_whatever_the_source()
    {
        var svc = Service([new QuickHit("Person", W.FilePerson, "Messer Otto", "FIB-P-2026-0001")]);

        var hits = await svc.CandidatesAsync("Messer", W.Viewer("sid"));

        Assert.NotEmpty(hits);
        Assert.All(hits, h => Assert.True(InvestigationReferences.IsType(h.Type), h.Type));
        Assert.Empty(await svc.CandidatesAsync("Messer", W.Viewer("dojchief")));
        Assert.Empty(await svc.CandidatesAsync("Messer", W.Viewer("sid"), MentionSource.Assistant));
    }

    [Fact]
    public async Task The_assistant_gets_no_investigations_and_an_investigation_text_no_agents()
    {
        var svc = Service();

        var chat = await svc.CandidatesAsync("Messer", W.Viewer("inv"), MentionSource.Assistant);
        Assert.Contains(chat, h => h.Type == "Agent");
        Assert.DoesNotContain(chat, h => InvestigationReferences.IsType(h.Type));

        var field = await svc.CandidatesAsync("Messer", W.Viewer("inv"), MentionSource.InvestigationField, W.Inv);
        Assert.DoesNotContain(field, h => h.Type == "Agent");
        Assert.Equal(nameof(Investigation), field[0].Type);

        var plain = await svc.CandidatesAsync("Messer", W.Viewer("inv"));
        Assert.Contains(plain, h => h.Type == "Agent");
        Assert.Contains(plain, h => h.Type == nameof(InvestigationEvidence));
    }

    [Fact]
    public async Task Every_offered_investigation_token_resolves_for_its_writer()
    {
        var svc = Service();
        foreach (var viewer in new[] { "inv", "noevid", "sta", "sid", "obs" })
        {
            var user = W.Viewer(viewer);
            var scope = W.Scope(viewer);
            foreach (var text in new[] { "Akte", "Messer", "Pistole", "#3", "Max", "Erika", "Prüfen", "§" })
            {
                foreach (var hit in (await svc.CandidatesAsync(text, user, MentionSource.InvestigationField)).Where(h => InvestigationReferences.IsType(h.Type)))
                {
                    var seg = (await svc.ResolveAsync(Token(hit.Type, hit.Id), scope.MayClassifiedRead, scope.MeId,
                        partnerAgency: scope.PartnerAgency)).Single();
                    Assert.Equal(hit.Display, seg.Text);
                    Assert.NotNull(seg.Href);
                }
            }
        }
    }

    // ---- FIBI ----

    [Fact]
    public async Task ResolveMentionTool_never_hands_an_investigation_to_the_model()
    {
        var tool = new ResolveMentionTool(Service());
        var text = $"Was ist mit {Token(nameof(InvestigationEvidence), W.Evidence)} und {Token(nameof(Investigation), W.Inv)}?";

        var result = await tool.InvokeAsync(JsonDocument.Parse(JsonSerializer.Serialize(new { text })).RootElement.Clone(),
            NooseiToolContext.From(W.Viewer("chief")));

        Assert.Contains(Missing, result.Text);
        Assert.DoesNotContain("Messer", result.Text);
        Assert.DoesNotContain(W.CaseNumber, result.Text);
        Assert.Empty(result.Refs ?? []);
    }
}
