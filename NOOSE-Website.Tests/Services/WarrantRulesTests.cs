using System.Security.Claims;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Warrants;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>The pure half of warrants: computed state, citations and who may request or sign.</summary>
public class WarrantRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);

    private static Warrant Signed(DateTime? until) => new()
    {
        Status = WarrantStatus.Signed,
        ValidFrom = until?.AddDays(-3),
        ValidUntil = until,
    };

    [Fact]
    public void A_signed_warrant_is_valid_until_its_end_and_expired_from_then_on()
    {
        Assert.Equal(WarrantState.Valid, WarrantRules.StateAt(Signed(Now.AddSeconds(1)), Now));
        Assert.Equal(WarrantState.Expired, WarrantRules.StateAt(Signed(Now), Now));
        Assert.Equal(WarrantState.Expired, WarrantRules.StateAt(Signed(Now.AddDays(-1)), Now));
    }

    [Fact]
    public void A_signed_warrant_without_an_end_never_counts_as_valid()
        => Assert.Equal(WarrantState.Expired, WarrantRules.StateAt(Signed(null), Now));

    [Theory]
    [InlineData(WarrantStatus.Requested, WarrantState.Requested)]
    [InlineData(WarrantStatus.Rejected, WarrantState.Rejected)]
    [InlineData(WarrantStatus.Cancelled, WarrantState.Cancelled)]
    public void The_other_stages_map_one_to_one(WarrantStatus status, WarrantState expected)
    {
        // an end date in the future must not make a cancelled paper valid again
        var warrant = new Warrant { Status = status, ValidUntil = Now.AddDays(5) };

        Assert.Equal(expected, WarrantRules.StateAt(warrant, Now));
    }

    [Fact]
    public void An_unknown_stage_fails_closed()
        => Assert.Equal(WarrantState.Expired,
            WarrantRules.StateAt(new Warrant { Status = (WarrantStatus)42, ValidUntil = Now.AddDays(1) }, Now));

    [Fact]
    public void Only_a_paper_that_was_signed_counts_as_signed()
    {
        Assert.True(WarrantRules.WasSigned(Signed(Now)));
        Assert.True(WarrantRules.WasSigned(new Warrant { Status = WarrantStatus.Cancelled, ValidFrom = Now }));
        Assert.False(WarrantRules.WasSigned(new Warrant { Status = WarrantStatus.Requested }));
        Assert.False(WarrantRules.WasSigned(new Warrant { Status = WarrantStatus.Rejected, ValidFrom = Now }));
        Assert.False(WarrantRules.WasSigned(new Warrant { Status = WarrantStatus.Signed }));
    }

    [Fact]
    public void A_citation_reads_paragraph_book_and_title()
    {
        Assert.Equal("§ 12 StGB – Diebstahl",
            WarrantRules.LawLabel(new Law { Paragraph = "§ 12", LawBook = "StGB", Title = "Diebstahl" }));
        Assert.Equal("§ 3 BtMG", WarrantRules.LawLabel(new Law { Paragraph = "§ 3", LawBook = "BtMG", Title = " " }));
    }

    [Fact]
    public void A_long_citation_is_clipped_so_ten_fit_the_snapshot()
    {
        var label = WarrantRules.LawLabel(new Law { Paragraph = "§ 1", LawBook = "X", Title = new string('a', 500) });

        Assert.True(label.Length <= 191);
        Assert.EndsWith("…", label);
        // ten clipped lines and their separators stay inside the column
        Assert.True(WarrantRules.MaxLaws * 191 + WarrantRules.MaxLaws - 1 <= 2000);
    }

    [Fact]
    public void The_stored_lists_split_cleanly()
    {
        var warrant = new Warrant { LawIds = "a, b,,c", LegalBasis = "§ 1 X\n\n § 2 Y " };

        Assert.Equal(["a", "b", "c"], WarrantRules.LawIdsOf(warrant));
        Assert.Equal(["§ 1 X", "§ 2 Y"], WarrantRules.LegalBasisLines(warrant));
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(3, true)]
    [InlineData(7, true)]
    [InlineData(14, true)]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(30, false)]
    public void Only_the_offered_periods_are_valid(int days, bool expected)
        => Assert.Equal(expected, WarrantRules.IsValidityChoice(days));

    [Fact]
    public void The_default_period_is_one_of_the_choices()
        => Assert.True(WarrantRules.IsValidityChoice(WarrantRules.DefaultValidityDays));

    [Fact]
    public void The_state_filter_translates_for_the_production_provider()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseMySql("server=localhost;database=none", ServerVersion.Parse("8.0.0-mysql"))
            .Options);
        var scope = ViewerScope.From(ClaimsPrincipalBuilder.Agent("a").WithRank(Rank.SpecialAgent).Build());

        foreach (var state in WarrantStateDisplay.All)
        {
            var sql = db.Warrants.OnlyVisible(db, scope).WhereState(state, Now).ToQueryString();
            Assert.Contains("EXISTS", sql, StringComparison.Ordinal);
        }
    }
}

/// <summary>Who may request and who may sign; the predicates the guards and the pages share.</summary>
public class WarrantPermissionTests
{
    public static TheoryData<string, bool, bool> Matrix => new()
    {
        // who, may request, may sign
        { "junior", true, false },
        { "senior", true, false },
        { "supervisory", true, true },
        { "director", true, true },
        { "admin-junior", true, true },
        { "reader", false, false },
        { "teamlead-admin", false, false },
        { "demo", false, false },
        { "partner", false, false },
        { "applicant", false, false },
        { "citizen", false, false },
    };

    private static ClaimsPrincipal Principal(string who) => who switch
    {
        "junior" => ClaimsPrincipalBuilder.Agent().WithRank(Rank.JuniorAgent),
        "senior" => ClaimsPrincipalBuilder.Agent().WithRank(Rank.SeniorSpecialAgent),
        "supervisory" => ClaimsPrincipalBuilder.Agent().WithRank(Rank.SupervisorySpecialAgent),
        "director" => ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director),
        "admin-junior" => ClaimsPrincipalBuilder.Agent().WithRank(Rank.JuniorAgent).AsAdmin(),
        "reader" => ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director).AsTeamLead(),
        "teamlead-admin" => ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director).AsTeamLead().AsAdmin(),
        "demo" => ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director).AsHrb().AsDemo(),
        "partner" => ClaimsPrincipalBuilder.Agent().AsPartner(PartnerAgency.LSPD, PartnerRank.Chief),
        "applicant" => ClaimsPrincipalBuilder.Agent().WithStatus(AgentStatus.Applicant),
        "citizen" => ClaimsPrincipalBuilder.Agent().WithStatus(AgentStatus.Civilian),
        _ => throw new ArgumentOutOfRangeException(nameof(who)),
    };

    [Theory]
    [MemberData(nameof(Matrix))]
    public void Predicates_and_guards_agree(string who, bool mayRequest, bool maySign)
    {
        var user = Principal(who);

        Assert.Equal(mayRequest, user.MayWarrantRequest());
        Assert.Equal(maySign, user.MayWarrantSign());

        var request = Record.Exception(() => Permission.RequireWarrantRequest(user));
        var sign = Record.Exception(() => Permission.RequireWarrantSign(user));
        Assert.Equal(mayRequest, request is null);
        Assert.Equal(maySign, sign is null);
        if (request is not null)
        {
            Assert.IsType<UnauthorizedAccessException>(request);
        }
        if (sign is not null)
        {
            Assert.IsType<UnauthorizedAccessException>(sign);
        }
    }
}

/// <summary>How a warrant's changes read in the protocol.</summary>
public class WarrantAuditDisplayTests
{
    [Fact]
    public void A_warrant_status_is_named_not_numbered()
    {
        var change = Assert.Single(AuditDisplay.Parse("""{"Status":[0,1]}""", entityType: nameof(Warrant)));

        Assert.Equal("Status", change.Field);
        Assert.Equal("Beantragt", change.Alt);
        Assert.Equal("Unterzeichnet", change.New);
    }

    [Fact]
    public void The_validity_reads_as_a_local_instant_on_a_warrant_only()
    {
        const string json = """{"ValidUntil":[null,"2026-11-05T12:00:00Z"]}""";

        var warrant = Assert.Single(AuditDisplay.Parse(json, entityType: nameof(Warrant)));
        Assert.Equal("Gültig bis", warrant.Field);
        Assert.Equal(new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc).ToLocalTime().ToString("dd.MM.yyyy HH:mm"),
            warrant.New);

        // a public warning's end is a calendar day and stays untouched
        var other = Assert.Single(AuditDisplay.Parse(json, entityType: "OeffentlicheWarnung"));
        Assert.Equal("2026-11-05T12:00:00Z", other.New);
    }

    [Fact]
    public void The_warrant_fields_read_in_german_on_a_warrant_only()
    {
        const string json = """{"Subject":[null,"Wohnung"],"Justification":[null,"Hinweis"],"RequesterName":[null,"Hawk"],"CaseId":[null,"c1"]}""";

        var warrant = AuditDisplay.Parse(json, entityType: nameof(Warrant));
        Assert.Equal(["Gegenstand", "Begründung", "Beantragt von"], warrant.Select(c => c.Field));

        // a ticket's subject is its title and keeps the shared label
        var ticket = AuditDisplay.Parse("""{"Subject":["a","b"]}""", entityType: "Ticket");
        Assert.Equal("Subject", Assert.Single(ticket).Field);
    }

    [Fact]
    public void The_decider_id_and_the_cited_ids_stay_out()
        => Assert.Empty(AuditDisplay.Parse("""{"DeciderId":["a","b"],"DeciderRank":[1,4],"LawIds":["x","y"]}""",
            entityType: nameof(Warrant)));

    [Fact]
    public void The_audit_viewer_names_and_links_a_warrant()
    {
        Assert.Equal("Anordnung", AuditEntityDisplay.Label("Warrant"));
        Assert.Equal("/anordnungen/w1", AuditEntityDisplay.Route("Warrant", "w1"));
    }
}
