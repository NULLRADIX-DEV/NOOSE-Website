using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Presentations;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.Presentations;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Llm;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;
using NOOSE_Website.Services.Llm.Tools;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>A presentation opened by NOOSEI: the record follows <see cref="PresentationVisibility"/>, and the speaker
/// notes follow <see cref="PresentationVisibility.MayManage(ViewerScope, Presentation)"/>.</summary>
/// <remarks>The viewers come from <see cref="PresentationServiceTests"/>: "own" owns every deck, "inv" is invited to
/// the private one, "tfm" sits in taskforce Kobra.</remarks>
public sealed class NooseiPresentationTests
{
    private static readonly DateTime Ts = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static ClaimsPrincipal Viewer(string name) => PresentationServiceTests.Viewers[name]();

    private static void SeedDecks(SqliteTestContext ctx)
    {
        PresentationServiceTests.SeedAgents(ctx);
        using var db = ctx.NewContext();
        Presentation Deck(string id, string title, PresentationAccess access, AnnouncementAudience? audience = null,
            string? targetId = null)
            => new()
            {
                Id = id, Title = title, Access = access, Audience = audience, TargetId = targetId, OwnerId = "own",
                CreatedAt = Ts,
                Slides = [new PresentationSlide
                {
                    Id = $"{id}-s1", Layout = SlideLayout.TitleText, Title = $"Folie {id}", BodyHtml = $"<p>Text {id}</p>",
                    Notes = $"Notiz {id}", Sorting = 1, CreatedAt = Ts,
                }],
            };

        var pub = Deck("pub", "Lagebild Hafen", PresentationAccess.Public);
        pub.Description = "Wochenbriefing";
        // inserted out of order: the dossier follows the sorting, not the rows
        pub.Slides =
        [
            new PresentationSlide
            {
                Id = "pub-s3", Layout = SlideLayout.Image, Title = "Karte", BodyHtml = "<p>Alter Text</p>",
                ImageId = "img1", Sorting = 3, CreatedAt = Ts,
            },
            new PresentationSlide
            {
                Id = "pub-s1", Layout = SlideLayout.TitleText, Title = "Zugriffsplan",
                BodyHtml = "<p>Zugriff um <b>22 Uhr</b></p><ul><li>Team A</li></ul>",
                Notes = "Geheimwort\nBlau", Sorting = 1, CreatedAt = Ts,
            },
            new PresentationSlide
            {
                Id = "pub-s2", Layout = SlideLayout.PdfPage, Title = "Google-Titel", BodyHtml = "<p>Google-Text</p>",
                PdfFileId = "file1", PageNumber = 3, Notes = "Notiz zur Google-Folie", Sorting = 2, CreatedAt = Ts,
            },
        ];
        db.Presentations.AddRange(
            pub,
            Deck("priv", "Lagebild Geheim", PresentationAccess.Private),
            Deck("tru", "Lagebild TRU", PresentationAccess.Closed, AnnouncementAudience.TruUnit),
            Deck("tf", "Lagebild Kobra", PresentationAccess.Closed, AnnouncementAudience.Taskforce, "tf1"),
            Deck("rank", "Lagebild Rang", PresentationAccess.Closed, AnnouncementAudience.FromRank));
        db.Presentations.Local.Single(x => x.Id == "rank").MinRank = Rank.SpecialAgent;
        db.PresentationFiles.Add(new PresentationFile
        {
            Id = "file1", PresentationId = "pub", FileNameSaved = "deck.pdf", Title = "Hafen-Deck", PageCount = 5,
            ImportedAt = Ts, CreatedAt = Ts,
        });
        db.PresentationInvites.Add(new PresentationInvite { PresentationId = "priv", AgentId = "inv", CreatedAt = Ts });
        db.People.Add(Seed.Person("p1", "Max"));
        db.Links.Add(new Link { SourceType = nameof(Presentation), SourceId = "priv", TargetType = nameof(Person), TargetId = "p1" });
        db.SaveChanges();
    }

    private static Task<NooseiToolResult> ReadAsync(SqliteTestContext ctx, string id, string viewer)
        => new ReadRecordTool(ctx.Factory, Substitute.For<IAccessLogService>()).InvokeAsync(
            Args($$"""{"typ":"Präsentation","id":"{{id}}"}"""), NooseiToolContext.From(Viewer(viewer)));

    // ---------- lies_akte ----------

    [Theory]
    [InlineData("jr", "pub", true)]
    [InlineData("jr", "priv", false)]
    [InlineData("inv", "priv", true)]
    [InlineData("own", "priv", true)]
    [InlineData("lead", "priv", true)]
    [InlineData("jr", "tru", false)]
    [InlineData("tru", "tru", true)]
    [InlineData("jr", "tf", false)]
    [InlineData("tfm", "tf", true)]
    public async Task Lies_akte_opens_a_presentation_only_for_who_may_see_it(string viewer, string deck, bool sees)
    {
        using var ctx = new SqliteTestContext();
        SeedDecks(ctx);

        var result = await ReadAsync(ctx, deck, viewer);

        if (sees)
        {
            Assert.False(result.IsError);
            Assert.Contains("Titel: Lagebild ", result.Text);
            Assert.Equal((nameof(Presentation), deck), (result.Refs![0].Kind, result.Refs[0].Id));
        }
        else
        {
            // word for word the answer for a record that does not exist
            Assert.Equal(NooseiToolResult.NotFound().Text, result.Text);
            Assert.True(result.IsError);
        }
    }

    [Fact]
    public async Task A_partner_cannot_open_even_a_public_presentation()
    {
        using var ctx = new SqliteTestContext();
        SeedDecks(ctx);
        var partner = ClaimsPrincipalBuilder.Agent("partner").AsPartner(PartnerAgency.LSPD, PartnerRank.Special).Build();

        var result = await new ReadRecordTool(ctx.Factory, Substitute.For<IAccessLogService>()).InvokeAsync(
            Args("""{"typ":"Präsentation","id":"pub"}"""), NooseiToolContext.From(partner));

        Assert.Equal(NooseiToolResult.NotFound().Text, result.Text);
    }

    [Fact]
    public async Task The_dossier_reads_the_slides_in_order_as_plain_text()
    {
        using var ctx = new SqliteTestContext();
        SeedDecks(ctx);

        var text = (await ReadAsync(ctx, "pub", "jr")).Text;

        Assert.Contains("Titel: Lagebild Hafen", text);
        Assert.Contains("Beschreibung: Wochenbriefing", text);
        Assert.Contains("Sichtbarkeit: Öffentlich", text);
        Assert.Contains("Erstellt von: Codename-own", text);
        Assert.Contains("— Folien (3) —", text);
        var first = text.IndexOf("1. Zugriffsplan — Zugriff um 22 Uhr Team A", StringComparison.Ordinal);
        var second = text.IndexOf("2. Google-Folie, Seite 3", StringComparison.Ordinal);
        var third = text.IndexOf("3. Karte [Bild]", StringComparison.Ordinal);
        Assert.True(first >= 0 && second > first && third > second, text);
        Assert.DoesNotContain("<", text);
    }

    [Fact]
    public async Task A_google_slide_is_named_by_its_page_only()
    {
        using var ctx = new SqliteTestContext();
        SeedDecks(ctx);

        var text = (await ReadAsync(ctx, "pub", "jr")).Text;

        // what the stage shows is the PDF page, so nothing typed into the row goes out
        Assert.DoesNotContain("Google-Titel", text);
        Assert.DoesNotContain("Google-Text", text);
        // an image slide shows no body either, even when an earlier layout left one behind
        Assert.DoesNotContain("Alter Text", text);
    }

    [Fact]
    public async Task The_dossier_names_the_audience_like_the_page()
    {
        using var ctx = new SqliteTestContext();
        SeedDecks(ctx);
        var expected = new Dictionary<string, string>
        {
            ["tru"] = "Nur TRU",
            ["tf"] = "Taskforce Kobra",
            ["priv"] = "Privat",
            ["rank"] = $"Ab {RankDisplay.Name(Rank.SpecialAgent)}",
        };

        foreach (var (deck, label) in expected)
        {
            Assert.Contains($"Sichtbarkeit: {label}", (await ReadAsync(ctx, deck, "own")).Text);
        }
    }

    [Fact]
    public async Task A_long_deck_lists_whole_slides_says_what_is_left_out_and_keeps_its_links()
    {
        using var ctx = new SqliteTestContext();
        SeedDecks(ctx);
        using (var db = ctx.NewContext())
        {
            db.Presentations.Add(new Presentation
            {
                Id = "long", Title = "Langes Briefing", Access = PresentationAccess.Public, OwnerId = "own", CreatedAt = Ts,
                Slides = Enumerable.Range(1, 60).Select(i => new PresentationSlide
                {
                    Id = $"long-s{i}", Layout = SlideLayout.TitleText, Title = $"Abschnitt {i}",
                    BodyHtml = $"<p>{new string('x', 350)} Ende{i}.</p>", Sorting = i, CreatedAt = Ts,
                }).ToList(),
            });
            db.Links.Add(new Link { SourceType = nameof(Presentation), SourceId = "long", TargetType = nameof(Person), TargetId = "p1" });
            db.SaveChanges();
        }

        var text = (await ReadAsync(ctx, "long", "jr")).Text;

        Assert.Contains("— Folien (60) —", text);
        var omitted = Regex.Match(text, @"Folien (\d+) bis 60 nicht aufgeführt");
        Assert.True(omitted.Success, text);
        var firstLeftOut = int.Parse(omitted.Groups[1].Value);
        // whole slides only: the last one listed is complete, the next one is absent
        Assert.Contains($"Ende{firstLeftOut - 1}.", text);
        Assert.DoesNotContain($"Ende{firstLeftOut}.", text);
        // the slides leave room for what hangs behind them
        Assert.Contains("Max", text);
        Assert.DoesNotContain("(gekürzt)", text);
    }

    // ---------- speaker notes ----------

    [Theory]
    [InlineData("own", true)]
    [InlineData("lead", true)]
    [InlineData("jr", false)]
    [InlineData("tru", false)]
    public async Task Speaker_notes_go_only_to_the_owner_and_leadership(string viewer, bool notes)
    {
        using var ctx = new SqliteTestContext();
        SeedDecks(ctx);

        var text = (await ReadAsync(ctx, "pub", viewer)).Text;

        Assert.Contains("Zugriffsplan", text);
        Assert.Equal(notes, text.Contains("Notizen: Geheimwort Blau", StringComparison.Ordinal));
        // a Google slide keeps its notes, and the same rule covers them
        Assert.Equal(notes, text.Contains("Notiz zur Google-Folie", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_invited_agent_reads_the_private_slides_but_not_their_notes()
    {
        using var ctx = new SqliteTestContext();
        SeedDecks(ctx);

        var invited = (await ReadAsync(ctx, "priv", "inv")).Text;
        var owner = (await ReadAsync(ctx, "priv", "own")).Text;

        Assert.Contains("Folie priv — Text priv", invited);
        Assert.DoesNotContain("Notiz priv", invited);
        Assert.Contains("Notizen: Notiz priv", owner);
    }

    [Fact]
    public async Task Notes_never_cost_the_owner_slide_text()
    {
        using var ctx = new SqliteTestContext();
        SeedDecks(ctx);
        using (var db = ctx.NewContext())
        {
            db.Presentations.Add(new Presentation
            {
                Id = "notes", Title = "Notizenreich", Access = PresentationAccess.Public, OwnerId = "own", CreatedAt = Ts,
                Slides = Enumerable.Range(1, 6).Select(i => new PresentationSlide
                {
                    Id = $"notes-s{i}", Layout = SlideLayout.TitleText, Title = $"Teil {i}",
                    BodyHtml = $"<p>{new string('x', 370)} Ende{i}.</p>", Notes = $"{new string('n', 370)} Notiz{i}.",
                    Sorting = i, CreatedAt = Ts,
                }).ToList(),
            });
            db.SaveChanges();
        }

        var owner = (await ReadAsync(ctx, "notes", "own")).Text;
        var junior = (await ReadAsync(ctx, "notes", "jr")).Text;

        // the audience's view of the slides is the same for both
        foreach (var text in new[] { owner, junior })
        {
            Assert.Contains("Ende6.", text);
            Assert.DoesNotContain("nicht aufgeführt; den vollen Inhalt", text);
        }
        Assert.Contains("Notiz1.", owner);
        Assert.DoesNotContain("Notiz6.", owner);
        Assert.Contains("Weitere Sprechernotizen nicht aufgeführt.", owner);
        Assert.DoesNotContain("Notizen:", junior);
    }

    [Fact]
    public async Task The_notes_rule_is_the_page_rule_for_everyone_noosei_serves()
    {
        using var ctx = new SqliteTestContext();
        SeedDecks(ctx);
        var pages = new PresentationService(ctx.Factory, Substitute.For<ITextImageStorageService>(),
            new PresentationLiveRegistry(), new PresentationBroadcaster());

        foreach (var name in new[] { "own", "lead", "jr", "tru", "hrb", "tfm", "inv" })
        {
            var user = Viewer(name);
            var page = (await pages.GetAsync("pub", user))!;
            Assert.Equal(page.MayManage, PresentationVisibility.MayManage(ViewerScope.From(user), page.Presentation));
        }
        // the page also asks for write access, which only read-only supervision lacks here, and NOOSEI never serves it
        Assert.Throws<UnauthorizedAccessException>(() => Permission.RequireLlmUse(Viewer("sup")));
        // an ownerless deck belongs to nobody, not to every viewer without an id
        Assert.False(PresentationVisibility.MayManage(ViewerScope.From(Viewer("jr")) with { MeId = "" }, new Presentation()));
    }

    // ---------- cached brief ----------

    [Theory]
    [InlineData("pub", false)]
    [InlineData("priv", true)]
    [InlineData("tru", true)]
    public async Task The_cached_brief_carries_no_notes_and_is_classified_unless_public(string deck, bool classified)
    {
        using var ctx = new SqliteTestContext();
        SeedDecks(ctx);
        await using var db = ctx.NewContext();

        // built without a viewer, exactly as the cache builds it
        var brief = (await DossierContextBuilder.BuildAsync(db, nameof(Presentation), deck, null))!.Value;

        Assert.DoesNotContain("Notiz", brief.Text);
        Assert.DoesNotContain("Geheimwort", brief.Text);
        Assert.Equal(classified, brief.IsClassified);
    }

    private static DossierSummaryService Briefs(SqliteTestContext ctx, INooseiGateway gateway, bool allowClassifiedEgress)
        => new(ctx.Factory, gateway, NooseiProviderStub.Returning(), Options.Create(new LlmOptions
        {
            Enabled = true,
            ApiKey = "k",
            Model = "test-model",
            AllowClassifiedEgress = allowClassifiedEgress,
        }));

    private static INooseiGateway Gateway(List<NooseiCall> calls)
    {
        var gateway = Substitute.For<INooseiGateway>();
        gateway.IsConfigured.Returns(true);
        gateway.AskAsync(Arg.Do<NooseiCall>(calls.Add), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(new NooseiAnswer("""{"tldr":"Kurz.","kernpunkte":["Punkt"],"risiko":{"stufe":"niedrig"}}""",
                LlmUsage.Empty, new LlmQuotaCharge(0, 0m, LlmQuotaStatus.Empty, null, true), 1, [], false));
        return gateway;
    }

    [Fact]
    public async Task A_brief_generated_by_the_owner_still_sends_no_notes()
    {
        using var ctx = new SqliteTestContext();
        SeedDecks(ctx);
        var calls = new List<NooseiCall>();

        await Briefs(ctx, Gateway(calls), allowClassifiedEgress: false)
            .GenerateAsync(nameof(Presentation), "pub", Viewer("own"), force: true);

        var prompt = string.Join("\n", Assert.Single(calls).Messages.Select(m => m.Content));
        Assert.Contains("Zugriffsplan", prompt);
        Assert.DoesNotContain("Geheimwort", prompt);
        Assert.DoesNotContain("Notiz zur Google-Folie", prompt);
    }

    [Fact]
    public async Task A_private_presentation_stays_behind_the_egress_switch()
    {
        using var ctx = new SqliteTestContext();
        SeedDecks(ctx);
        var calls = new List<NooseiCall>();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => Briefs(ctx, Gateway(calls), allowClassifiedEgress: false)
            .GenerateAsync(nameof(Presentation), "priv", Viewer("own"), force: true));

        Assert.Contains("Verschlusssache", refused.Message);

        Assert.Empty(calls);
    }

    [Theory]
    [InlineData("inv", false)]
    [InlineData("jr", true)]
    public async Task Hole_kurzbrief_answers_an_outsider_like_a_missing_record(string viewer, bool notFound)
    {
        using var ctx = new SqliteTestContext();
        SeedDecks(ctx);
        var briefs = Briefs(ctx, Gateway([]), allowClassifiedEgress: true);

        var result = await new GetBriefTool(briefs).InvokeAsync(
            Args("""{"typ":"Präsentation","id":"priv"}"""), NooseiToolContext.From(Viewer(viewer)));

        Assert.Equal(notFound, result.Text == NooseiToolResult.NotFound().Text);
        Assert.Equal(!notFound, result.Text.Contains("noch keinen Kurzbrief", StringComparison.Ordinal));
    }

    // ---------- lies_zeitstrahl, zeige_verbindungen, finde_verbindungsweg ----------

    [Fact]
    public async Task Lies_zeitstrahl_reads_like_an_empty_one_for_an_outsider()
    {
        using var ctx = new SqliteTestContext();
        SeedDecks(ctx);
        using (var db = ctx.NewContext())
        {
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(Presentation), EntityId = "priv", Action = AuditAction.Created, Timestamp = Ts,
                AgentId = "own", AgentName = "Codename-own",
            });
            db.SaveChanges();
        }
        var tool = new ReadTimelineTool(new TimelineService(ctx.Factory));
        Task<NooseiToolResult> Run(string deck, string viewer)
            => tool.InvokeAsync(Args($$"""{"typ":"Präsentation","id":"{{deck}}"}"""), NooseiToolContext.From(Viewer(viewer)));

        var invited = await Run("priv", "inv");
        var outsider = await Run("priv", "jr");
        var empty = await Run("pub", "jr");

        Assert.Contains("Zeitstrahl (", invited.Text);
        Assert.Equal(empty.Text, outsider.Text);
    }

    [Theory]
    [InlineData("inv", true)]
    [InlineData("jr", false)]
    public async Task Zeige_verbindungen_lists_what_a_presentation_is_linked_to(string viewer, bool sees)
    {
        using var ctx = new SqliteTestContext();
        SeedDecks(ctx);

        var result = await new ListRelatedTool(ctx.Factory).InvokeAsync(
            Args("""{"typ":"Präsentation","id":"priv"}"""), NooseiToolContext.From(Viewer(viewer)));

        if (sees)
        {
            Assert.Contains("Max", result.Text);
        }
        else
        {
            Assert.Equal(NooseiToolResult.NotFound().Text, result.Text);
        }
    }

    private static ReadRecordContentTool ContentTool(SqliteTestContext ctx)
        => new(
            ctx.Factory,
            Substitute.For<ICommentService>(),
            Substitute.For<ISourceService>(),
            Substitute.For<IFollowupService>(),
            new LinkService(ctx.Factory, Substitute.For<IThreatScoreService>()),
            Substitute.For<ICustomFieldValueService>(),
            Substitute.For<ITagService>(),
            Substitute.For<IPersonDocService>(),
            Substitute.For<IObservationService>(),
            Substitute.For<ITaskforceChatService>(),
            Substitute.For<IMeetingService>(),
            Substitute.For<IBewerbungService>(),
            Substitute.For<IPersonnelFileService>(),
            Substitute.For<IInformantService>(),
            Substitute.For<IPublicWantedService>(),
            Substitute.For<IPublicFactionProfileService>(),
            Substitute.For<IObjectionService>(),
            Substitute.For<ITipService>(),
            Substitute.For<IAuditLogQueryService>());

    [Fact]
    public async Task Lies_akteninhalt_offers_a_presentation_its_links_under_its_own_title()
    {
        using var ctx = new SqliteTestContext();
        SeedDecks(ctx);
        var tool = ContentTool(ctx);
        Task<NooseiToolResult> Run(string args, string viewer)
            => tool.InvokeAsync(Args(args), NooseiToolContext.From(Viewer(viewer)));

        var everything = await Run("""{"typ":"Präsentation","id":"priv"}""", "inv");
        var comments = await Run("""{"typ":"Präsentation","id":"priv","inhalt":"kommentare"}""", "inv");
        var outsider = await Run("""{"typ":"Präsentation","id":"priv"}""", "jr");

        Assert.StartsWith("Präsentation: Lagebild Geheim", everything.Text);
        Assert.Contains("Max", everything.Text);
        // the deck page has no comments, so asking for them is a wrong question, not an empty answer
        Assert.Contains("gibt es bei einer Akte vom Typ Präsentation nicht", comments.Text);
        Assert.Equal(NooseiToolResult.NotFound().Text, outsider.Text);
    }

    [Theory]
    [InlineData("pub")]
    [InlineData("priv")]
    public async Task Finde_verbindungsweg_says_presentations_are_not_in_the_graph(string deck)
    {
        using var ctx = new SqliteTestContext();
        SeedDecks(ctx);
        var graph = Substitute.For<IGraphService>();

        var result = await new FindPathTool(graph).InvokeAsync(
            Args($$"""{"von_typ":"Präsentation","von_id":"{{deck}}","nach_typ":"Person","nach_id":"p1"}"""),
            NooseiToolContext.From(Viewer("jr")));

        // a statement about the kind, the same for a deck the agent sees and one they do not
        Assert.Equal("Präsentationen sind nicht Teil des Beziehungsgraphen. Ihre direkten Verbindungen liefert "
            + "zeige_verbindungen.", result.Text);
        Assert.Empty(graph.ReceivedCalls());
    }
}
