using System.Security.Claims;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Models.Cases;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Lifting a concern into a case: what travels, what does not, and who may.</summary>
public sealed class TicketConversionServiceTests
{
    private const string TicketId = "t1";

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    private static ClaimsPrincipal Junior()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.SpecialAgent).Build();

    private static ClaimsPrincipal Supervision()
        => ClaimsPrincipalBuilder.Agent("aufsicht").WithRank(Rank.Director).AsTeamLead().Build();

    private static async Task<SqliteTestContext> SeededAsync()
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        db.Tickets.Add(new Ticket
        {
            Id = TicketId,
            CaseNumber = "FIB-T-2026-0001",
            Subject = "Beschädigtes Fahrzeug",
            Category = TicketKategorie.Anzeige,
            Status = TicketStatus.Offen,
            LastActivityAt = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc),
        });
        db.TicketNachrichten.Add(new TicketNachricht
        {
            Id = "m1",
            TicketId = TicketId,
            Audience = TicketMessageAudience.Buerger,
            Text = "Mein Fahrzeug wurde beschädigt.",
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
        await db.SaveChangesAsync();
        return ctx;
    }

    private static (TicketConversionService Service, ICaseService Cases, ILinkService Links) Build(
        SqliteTestContext ctx)
    {
        var cases = Substitute.For<ICaseService>();
        cases.CreateAsync(Arg.Any<CaseInput>(), Arg.Any<ClaimsPrincipal>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new Case
            {
                Id = "c1",
                CaseNumber = "FIB-V-2026-0007",
                Title = ci.Arg<CaseInput>().Title,
                Description = ci.Arg<CaseInput>().Description,
            }));
        var links = Substitute.For<ILinkService>();
        return (new TicketConversionService(ctx.Factory, cases, links), cases, links);
    }

    [Fact]
    public async Task TheCaseTakesTheSubjectAndTheCitizenThread()
    {
        using var ctx = await SeededAsync();
        var (service, cases, _) = Build(ctx);

        var created = await service.ToCaseAsync(TicketId, Leader());

        Assert.Equal("FIB-V-2026-0007", created.CaseNumber);
        await cases.Received(1).CreateAsync(
            Arg.Is<CaseInput>(i =>
                i.Title == "Beschädigtes Fahrzeug"
                && i.Description!.Contains("FIB-T-2026-0001")
                && i.Description!.Contains("Mein Fahrzeug wurde beschädigt")
                // the internal thread is the desk's own and does not travel into a record stock
                && !i.Description!.Contains("Interne Einschätzung")
                // roles, never names: the agency rows carry no agent at all
                && !i.Description!.Contains("lead")),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheLinkIsWrittenFromTheCaseToTheTicket()
    {
        // a ticket stays a link target: the connection is shown on the case page, never the other way round
        using var ctx = await SeededAsync();
        var (service, _, links) = Build(ctx);

        await service.ToCaseAsync(TicketId, Leader());

        await links.Received(1).CreateAsync(nameof(Case), "c1", nameof(Ticket), TicketId,
            Arg.Any<string?>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<LinkKind>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnlyTheDeskMayLiftAConcern()
    {
        using var ctx = await SeededAsync();
        var (service, cases, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ToCaseAsync(TicketId, Junior()));
        // the read-only supervision fails on the write guard, which runs before the rank check
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ToCaseAsync(TicketId, Supervision()));

        await cases.DidNotReceive().CreateAsync(Arg.Any<CaseInput>(), Arg.Any<ClaimsPrincipal>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AMissingTicketIsRefusedBeforeAnythingIsCreated()
    {
        using var ctx = await SeededAsync();
        var (service, cases, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ToCaseAsync("ghost", Leader()));

        await cases.DidNotReceive().CreateAsync(Arg.Any<CaseInput>(), Arg.Any<ClaimsPrincipal>(),
            Arg.Any<CancellationToken>());
    }
}
