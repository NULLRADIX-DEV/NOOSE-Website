using System.Security.Claims;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Llm;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Llm;
using NOOSE_Website.Services.Public;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>NOOSEI drafting what the agency tells a citizen: the gates, the prompt, and what a draft is scrubbed of.</summary>
public sealed class CitizenAssistServiceTests
{
    private const string TicketId = "t1";
    private const string TipId = "h1";
    private const string ObjectionId = "e1";
    private const string WantedId = "f1";

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    private static ClaimsPrincipal Junior()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.SpecialAgent).Build();

    private static ClaimsPrincipal Supervision()
        => ClaimsPrincipalBuilder.Agent("aufsicht").WithRank(Rank.Director).AsTeamLead().Build();

    private static ClaimsPrincipal Demo()
        => ClaimsPrincipalBuilder.Agent("demo").WithRank(Rank.Director).AsDemo().Build();

    private static NooseiAnswer Answer(string text)
        => new(text, LlmUsage.Empty, new LlmQuotaCharge(120, 0.001m, LlmQuotaStatus.Empty, null, true), 1, [], false);

    private static CitizenAssistService Build(SqliteTestContext ctx, string answer,
        Action<NooseiCall>? inspect = null, bool configured = true)
        => Build(ctx, answer, out _, inspect, configured);

    private static CitizenAssistService Build(SqliteTestContext ctx, string answer, out INooseiGateway gateway,
        Action<NooseiCall>? inspect = null, bool configured = true)
    {
        gateway = Substitute.For<INooseiGateway>();
        gateway.IsConfigured.Returns(configured);
        gateway.AskAsync(Arg.Any<NooseiCall>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                inspect?.Invoke(call.Arg<NooseiCall>());
                return Answer(answer);
            });

        var settings = Substitute.For<INooseiSettingsService>();
        settings.GetAddendumAsync(Arg.Any<CancellationToken>()).Returns((string?)null);
        return new CitizenAssistService(ctx.Factory, gateway, settings);
    }

    // ---- ticket ----

    /// <summary>A citizen ticket with one line from each side.</summary>
    private static async Task<SqliteTestContext> SeededAsync(TicketArt kind = TicketArt.Fuehrungsebene,
        bool withMessages = true)
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        db.Tickets.Add(new Ticket
        {
            Id = TicketId,
            CaseNumber = "NOOSE-T-2026-0001",
            Subject = "Beschädigtes Fahrzeug",
            Kind = kind,
            Category = TicketKategorie.Anzeige,
            Status = TicketStatus.Offen,
            LastActivityAt = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc),
        });
        if (withMessages)
        {
            db.TicketNachrichten.Add(new TicketNachricht
            {
                Id = "m1",
                TicketId = TicketId,
                Audience = TicketMessageAudience.Buerger,
                Text = "Mein Fahrzeug wurde beschädigt, was kann ich tun?",
                AuthorIsCitizen = true,
                CreatedAt = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc),
            });
            db.TicketNachrichten.Add(new TicketNachricht
            {
                Id = "m2",
                TicketId = TicketId,
                Audience = TicketMessageAudience.Intern,
                Text = "Interne Einschätzung: Zuständigkeit prüfen.",
                AuthorAgentId = "lead",
                CreatedAt = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc),
            });
        }
        await db.SaveChangesAsync();
        return ctx;
    }

    [Fact]
    public async Task TheDraftComesBackWithItsCost()
    {
        using var ctx = await SeededAsync();
        var svc = Build(ctx, "Guten Tag, wir haben Ihr Anliegen aufgenommen und melden uns.");

        var result = await svc.SuggestTicketReplyAsync(TicketId, Leader());

        Assert.StartsWith("Guten Tag", result.Text);
        Assert.Equal(120L, result.QuotaTokens);
    }

    [Fact]
    public async Task ThePromptCarriesTheCitizenThreadAndNotTheInternalOne()
    {
        using var ctx = await SeededAsync();
        NooseiCall? seen = null;
        var svc = Build(ctx, "Guten Tag, wir melden uns.", call => seen = call);

        await svc.SuggestTicketReplyAsync(TicketId, Leader());

        var prompt = seen!.LoggedPrompt!;
        Assert.Contains("Mein Fahrzeug wurde beschädigt", prompt);
        // the internal thread is not the agency's answer to anybody and never reaches the model here
        Assert.DoesNotContain("Interne Einschätzung", prompt);
        // the call points at the ticket, so the NOOSEI log can be traced back to it
        Assert.Equal(nameof(Ticket), seen.EntityType);
        Assert.Equal(TicketId, seen.EntityId);
    }

    [Fact]
    public async Task AMentionTokenIsScrubbedOutOfTheDraft()
    {
        // the citizen thread refuses tokens at the service, so a draft carrying one could never be sent
        using var ctx = await SeededAsync();
        var token = MentionParser.Token("Agent", "11111111-1111-1111-1111-111111111111");
        var svc = Build(ctx, $"Guten Tag, {token} kümmert sich darum.");

        var result = await svc.SuggestTicketReplyAsync(TicketId, Leader());

        Assert.DoesNotContain("@{", result.Text);
    }

    [Fact]
    public async Task AnOverlongDraftIsClippedToOneMessage()
    {
        using var ctx = await SeededAsync();
        var svc = Build(ctx, new string('x', TicketRules.MaxMessageLength + 500));

        var result = await svc.SuggestTicketReplyAsync(TicketId, Leader());

        Assert.True(result.Text.Length <= TicketRules.MaxMessageLength);
    }

    [Fact]
    public async Task AnEmptyAnswerIsRefusedRatherThanClearingTheComposer()
    {
        using var ctx = await SeededAsync();
        var svc = Build(ctx, "   ");

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SuggestTicketReplyAsync(TicketId, Leader()));
    }

    [Fact]
    public async Task OnlyTheDeskMayDraft()
    {
        using var ctx = await SeededAsync();
        var svc = Build(ctx, "Guten Tag.");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.SuggestTicketReplyAsync(TicketId, Junior()));
        // the read-only supervision fails on the write guard, which runs before the rank check
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.SuggestTicketReplyAsync(TicketId, Supervision()));
    }

    [Fact]
    public async Task AnInternalTicketHasNoCitizenAnswerToDraft()
    {
        using var ctx = await SeededAsync(TicketArt.Intern);
        var svc = Build(ctx, "Guten Tag.");

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SuggestTicketReplyAsync(TicketId, Leader()));
    }

    [Fact]
    public async Task AnEmptyThreadAndAnUnconfiguredModelBothRefuse()
    {
        using var empty = await SeededAsync(withMessages: false);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(empty, "Guten Tag.").SuggestTicketReplyAsync(TicketId, Leader()));

        using var ctx = await SeededAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(ctx, "Guten Tag.", configured: false).SuggestTicketReplyAsync(TicketId, Leader()));
    }

    // ---- tip and objection ----

    /// <summary>A citizen named Max Mustermann and a published notice whose public name is someone else.</summary>
    private static void SeedCitizenAndNotice(AppDbContext db)
    {
        // distinct DiscordId: the column defaults to "" and carries a unique index
        db.Users.Add(new Agent
        {
            Id = "buerger", UserName = "buerger", DiscordId = "1001", Status = AgentStatus.Civilian,
        });
        db.BuergerProfile.Add(new BuergerProfil
        {
            Id = "profil", UserId = "buerger", FirstName = "Max", LastName = "Mustermann",
        });
        db.OeffentlicheFahndungen.Add(new OeffentlicheFahndung
        {
            Id = WantedId,
            CaseNumber = "NOOSE-F-2026-0001",
            DisplayName = "Tony Cipriani",
            Status = PublicWantedStatus.Veroeffentlicht,
        });
    }

    /// <summary>A tip about the notice, with one line from the tipster, one agency line and one internal note.</summary>
    private static async Task<SqliteTestContext> TipSeededAsync(bool anonymous = false,
        TipStatus status = TipStatus.Rueckfrage)
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        SeedCitizenAndNotice(db);
        db.Hinweise.Add(new Hinweis
        {
            Id = TipId,
            CaseNumber = "NOOSE-H-2026-0001",
            CitizenProfileId = "profil",
            WantsAnonymity = anonymous,
            WantedId = WantedId,
            Text = "Ich habe den Gesuchten gestern Abend an der Tankstelle in Sandy Shores gesehen.",
            Status = status,
        });
        db.HinweisNachrichten.Add(new HinweisNachricht
        {
            Id = "hm1",
            HinweisId = TipId,
            Audience = TipMessageAudience.Buerger,
            Text = "Um welche Uhrzeit war das ungefähr?",
            CreatedAt = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc),
        });
        db.HinweisNachrichten.Add(new HinweisNachricht
        {
            Id = "hm2",
            HinweisId = TipId,
            Audience = TipMessageAudience.Buerger,
            Text = "Gegen 22 Uhr, er fuhr einen roten Sportwagen.",
            AuthorIsCitizen = true,
            CreatedAt = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc),
        });
        db.HinweisNachrichten.Add(new HinweisNachricht
        {
            Id = "hm3",
            HinweisId = TipId,
            Audience = TipMessageAudience.Intern,
            Text = "Interne Einschätzung: Halter des Wagens prüfen.",
            AuthorAgentId = "lead",
            CreatedAt = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();
        return ctx;
    }

    [Fact]
    public async Task ATipDraftReadsTheTipAndTheCitizenThreadOnly()
    {
        using var ctx = await TipSeededAsync();
        NooseiCall? seen = null;
        var svc = Build(ctx, "Guten Tag, danke für Ihre Angaben.", call => seen = call);

        var result = await svc.SuggestTipReplyAsync(TipId, Junior());

        Assert.StartsWith("Guten Tag", result.Text);
        var prompt = seen!.LoggedPrompt!;
        // the submission is a column of the tip, not a thread row, and has to be in there by itself
        Assert.Contains("Tankstelle in Sandy Shores", prompt);
        Assert.Contains("roten Sportwagen", prompt);
        Assert.Contains("Tony Cipriani", prompt);
        Assert.DoesNotContain("Interne Einschätzung", prompt);
        Assert.Equal(nameof(Hinweis), seen.EntityType);
        Assert.Equal(TipId, seen.EntityId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ATipDraftNeverNamesTheTipster(bool anonymous)
    {
        // the projection reads no citizen field at all, so the promise holds without a branch on it
        using var ctx = await TipSeededAsync(anonymous);
        NooseiCall? seen = null;
        var svc = Build(ctx, "Guten Tag.", call => seen = call);

        await svc.SuggestTipReplyAsync(TipId, Junior());

        Assert.DoesNotContain("Mustermann", seen!.LoggedPrompt!);
        Assert.DoesNotContain(seen.Messages, m => m.Content?.Contains("Mustermann") == true);
    }

    [Fact]
    public async Task AClosedTipTakesNoDraft()
    {
        using var ctx = await TipSeededAsync(status: TipStatus.Verworfen);
        var svc = Build(ctx, "Guten Tag.", out var gateway);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SuggestTipReplyAsync(TipId, Junior()));
        await gateway.DidNotReceiveWithAnyArgs().AskAsync(default!, default!, default);
    }

    [Fact]
    public async Task ATipDraftNeedsTheRightToWriteToTheTipster()
    {
        using var ctx = await TipSeededAsync();
        var svc = Build(ctx, "Guten Tag.");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.SuggestTipReplyAsync(TipId, Supervision()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.SuggestTipReplyAsync(TipId, Demo()));
    }

    [Fact]
    public async Task ATipDraftIsScrubbedAndClippedToOneMessage()
    {
        using var ctx = await TipSeededAsync();
        var token = MentionParser.Token("Agent", "11111111-1111-1111-1111-111111111111");
        var svc = Build(ctx, $"Guten Tag, {token} " + new string('x', TipRules.MaxMessageLength + 500));

        var result = await svc.SuggestTipReplyAsync(TipId, Junior());

        Assert.DoesNotContain("@{", result.Text);
        Assert.True(result.Text.Length <= TipRules.MaxMessageLength);
    }

    /// <summary>An objection by Max Mustermann against the notice.</summary>
    private static async Task<SqliteTestContext> ObjectionSeededAsync(
        ObjectionStatus status = ObjectionStatus.InPruefung,
        PublicWantedStatus noticeStatus = PublicWantedStatus.Zurueckgezogen)
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        SeedCitizenAndNotice(db);
        db.FahndungEinsprueche.Add(new FahndungEinspruch
        {
            Id = ObjectionId,
            CaseNumber = "NOOSE-EIN-2026-0001",
            WantedId = WantedId,
            CitizenProfileId = "profil",
            Text = "Das bin nicht ich. Ich war zur angegebenen Zeit nachweislich in Paleto Bay.",
            Status = status,
        });
        await db.SaveChangesAsync();
        var notice = await db.OeffentlicheFahndungen.FindAsync(WantedId);
        notice!.Status = noticeStatus;
        await db.SaveChangesAsync();
        return ctx;
    }

    [Fact]
    public async Task AnObjectionDraftWordsTheAgentsNotesForTheChosenOutcome()
    {
        using var ctx = await ObjectionSeededAsync();
        NooseiCall? seen = null;
        var svc = Build(ctx, "Guten Tag, Ihrem Einspruch wird stattgegeben.", call => seen = call);

        var result = await svc.SuggestObjectionNoteAsync(ObjectionId, ObjectionStatus.Angenommen,
            "Alibi durch Zeugen bestätigt", Leader());

        Assert.StartsWith("Guten Tag", result.Text);
        var prompt = seen!.LoggedPrompt!;
        Assert.Contains("Paleto Bay", prompt);
        Assert.Contains("stattgegeben", prompt);
        Assert.Contains("Alibi durch Zeugen bestätigt", prompt);
        // the citizen reads the result; the model never needs to know who they are
        Assert.DoesNotContain("Mustermann", prompt);
        Assert.Equal(nameof(FahndungEinspruch), seen.EntityType);
        Assert.Equal(ObjectionId, seen.EntityId);
    }

    [Fact]
    public async Task WithoutNotesTheObjectionPromptSaysSo()
    {
        using var ctx = await ObjectionSeededAsync();
        NooseiCall? seen = null;
        var svc = Build(ctx, "Guten Tag, der Einspruch wird abgelehnt. [Begründung]", call => seen = call);

        await svc.SuggestObjectionNoteAsync(ObjectionId, ObjectionStatus.Abgelehnt, "  ", Leader());

        Assert.Contains("keine Stichpunkte", seen!.LoggedPrompt!);
    }

    [Fact]
    public async Task OnlyWhoMayDecideMayDraftTheDecision()
    {
        using var ctx = await ObjectionSeededAsync();
        var svc = Build(ctx, "Guten Tag.");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.SuggestObjectionNoteAsync(ObjectionId, ObjectionStatus.Abgelehnt, null, Junior()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.SuggestObjectionNoteAsync(ObjectionId, ObjectionStatus.Abgelehnt, null, Supervision()));
    }

    [Fact]
    public async Task ADecisionThatCannotBeTakenCostsNoDraft()
    {
        // already decided: no transition left
        using (var decided = await ObjectionSeededAsync(ObjectionStatus.Abgelehnt))
        {
            var svc = Build(decided, "Guten Tag.", out var gateway);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.SuggestObjectionNoteAsync(ObjectionId, ObjectionStatus.Angenommen, null, Leader()));
            await gateway.DidNotReceiveWithAnyArgs().AskAsync(default!, default!, default);
        }

        // not a decision at all
        using (var open = await ObjectionSeededAsync())
        {
            var svc = Build(open, "Guten Tag.", out var gateway);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.SuggestObjectionNoteAsync(ObjectionId, ObjectionStatus.InPruefung, null, Leader()));
            await gateway.DidNotReceiveWithAnyArgs().AskAsync(default!, default!, default);
        }

        // granting while the notice is still public: SetStatusAsync would refuse it too
        using (var online = await ObjectionSeededAsync(noticeStatus: PublicWantedStatus.Veroeffentlicht))
        {
            var svc = Build(online, "Guten Tag.", out var gateway);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => svc.SuggestObjectionNoteAsync(ObjectionId, ObjectionStatus.Angenommen, null, Leader()));
            await gateway.DidNotReceiveWithAnyArgs().AskAsync(default!, default!, default);
        }
    }

    [Fact]
    public async Task AnObjectionDraftIsClippedToTheNoteLength()
    {
        using var ctx = await ObjectionSeededAsync();
        var svc = Build(ctx, new string('x', ObjectionRules.MaxNoteLength + 500));

        var result = await svc.SuggestObjectionNoteAsync(ObjectionId, ObjectionStatus.Abgelehnt, null, Leader());

        Assert.True(result.Text.Length <= ObjectionRules.MaxNoteLength);
    }
}
