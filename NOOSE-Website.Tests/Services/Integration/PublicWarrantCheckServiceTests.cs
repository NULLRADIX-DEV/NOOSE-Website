using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Warrants;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Public;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The anonymous warrant check: one answer for every miss, and only what the paper itself prints.</summary>
public sealed class PublicWarrantCheckServiceTests
{
    private const string SignedId = "3f2a9c1e-5b7d-4e8f-9a0b-1c2d3e4f5a6b";

    private static Warrant Signed(string id = SignedId, Action<Warrant>? configure = null)
    {
        var now = DateTime.UtcNow;
        var warrant = new Warrant
        {
            Id = id,
            CaseNumber = "FIB-AN-2026-0007",
            Type = WarrantType.Search,
            Status = WarrantStatus.Signed,
            PersonId = "p1",
            Subject = "Wohnung Grove Street 12",
            Justification = "Geheime Begründung",
            LegalBasis = "§ 12 StGB – Diebstahl",
            ValidityDays = 3,
            ValidFrom = now.AddDays(-1),
            ValidUntil = now.AddDays(2),
            RequesterName = "Hawk",
            DeciderId = "lead",
            DeciderName = "Falcon",
            DeciderRank = Rank.SpecialAgentInCharge,
            DecidedAt = now.AddDays(-1),
            DecisionNote = "Interne Notiz",
            CreatedAt = now.AddDays(-2),
        };
        configure?.Invoke(warrant);
        return warrant;
    }

    private static async Task<SqliteTestContext> SeededAsync(bool moduleOn = true, params Warrant[] warrants)
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        await PublicModuleSeeder.SeedAsync(db);
        (await db.OeffentlicheModule.SingleAsync(m => m.Key == PublicModules.WarrantCheck)).IsEnabled = moduleOn;
        db.People.Add(Seed.Person("p1", "Otto Offen"));
        db.Warrants.AddRange(warrants);
        await db.SaveChangesAsync();
        return ctx;
    }

    private static PublicWarrantCheckService NewService(SqliteTestContext ctx)
    {
        var discord = Substitute.For<IDiscordWebhookService>();
        discord.GetConfigAsync(Arg.Any<CancellationToken>()).Returns(new DiscordWebhookConfig(
            false, "https://fibhq.com", new Dictionary<NotificationType, string?>(),
            new Dictionary<NotificationType, string?>(), false));
        return new PublicWarrantCheckService(ctx.Factory,
            new PublicModuleService(ctx.Factory, new MemoryCache(new MemoryCacheOptions())), discord);
    }

    [Fact]
    public void The_module_is_off_by_default_has_no_tab_and_does_not_open_the_internal_routes()
    {
        var module = PublicModules.All.Single(m => m.Key == PublicModules.WarrantCheck);

        Assert.True(module.Available);
        Assert.False(module.DefaultEnabled);
        Assert.Null(module.NavRoute);
        Assert.True(PublicRoutes.IsPublic(WarrantCheckAddress.Path(SignedId)));
        Assert.False(PublicRoutes.IsPublic("/anordnungen"));
        Assert.False(PublicRoutes.IsPublic($"/anordnungen/{SignedId}/druck"));
    }

    [Fact]
    public async Task A_valid_paper_answers_with_what_it_prints()
    {
        using var ctx = await SeededAsync(true, Signed());

        var check = await NewService(ctx).CheckAsync(SignedId);

        Assert.NotNull(check);
        Assert.Equal(WarrantState.Valid, check.State);
        Assert.Equal(WarrantType.Search, check.Type);
        Assert.Equal("FIB-AN-2026-0007", check.CaseNumber);
        Assert.Equal("Falcon", check.SignerName);
        Assert.Equal(RankDisplay.Name(Rank.SpecialAgentInCharge), check.SignerTitle);
        Assert.Null(check.CancelledAt);
    }

    [Fact]
    public async Task The_id_is_read_case_insensitively_and_trimmed()
    {
        using var ctx = await SeededAsync(true, Signed());

        Assert.NotNull(await NewService(ctx).CheckAsync("  " + SignedId.ToUpperInvariant() + " "));
    }

    [Fact]
    public async Task An_expired_paper_reads_as_expired()
    {
        using var ctx = await SeededAsync(true, Signed(configure: w => w.ValidUntil = DateTime.UtcNow.AddMinutes(-1)));

        var check = await NewService(ctx).CheckAsync(SignedId);

        Assert.Equal(WarrantState.Expired, check?.State);
    }

    [Fact]
    public async Task A_cancelled_paper_reads_as_cancelled_with_its_date()
    {
        var at = DateTime.UtcNow.AddHours(-3);
        using var ctx = await SeededAsync(true, Signed(configure: w =>
        {
            w.Status = WarrantStatus.Cancelled;
            w.CancelledAt = at;
            w.CancellationReason = "Interner Grund";
        }));

        var check = await NewService(ctx).CheckAsync(SignedId);

        Assert.Equal(WarrantState.Cancelled, check?.State);
        Assert.NotNull(check?.CancelledAt);
    }

    [Theory]
    [InlineData(WarrantStatus.Requested)]
    [InlineData(WarrantStatus.Rejected)]
    public async Task A_request_or_a_refusal_is_a_plain_miss(WarrantStatus status)
    {
        using var ctx = await SeededAsync(true, Signed(configure: w =>
        {
            w.Status = status;
            w.ValidFrom = null;
            w.ValidUntil = null;
        }));

        Assert.Null(await NewService(ctx).CheckAsync(SignedId));
    }

    [Fact]
    public async Task A_deleted_warrant_is_a_plain_miss()
    {
        using var ctx = await SeededAsync(true, Signed(configure: w => w.IsDeleted = true));

        Assert.Null(await NewService(ctx).CheckAsync(SignedId));
    }

    [Fact]
    public async Task The_module_off_or_the_kill_switch_is_a_plain_miss()
    {
        using (var off = await SeededAsync(false, Signed()))
        {
            Assert.Null(await NewService(off).CheckAsync(SignedId));
        }

        using var ctx = await SeededAsync(true, Signed());
        await using (var db = ctx.NewContext())
        {
            db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.PublicAreaKillSwitch, Value = "true" });
            await db.SaveChangesAsync();
        }

        Assert.Null(await NewService(ctx).CheckAsync(SignedId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("FIB-AN-2026-0007")]
    [InlineData("3f2a9c1e5b7d4e8f9a0b1c2d3e4f5a6b")]
    [InlineData("{3f2a9c1e-5b7d-4e8f-9a0b-1c2d3e4f5a6b}")]
    [InlineData("00000000-0000-0000-0000-000000000001")]
    public async Task A_malformed_or_unknown_id_is_a_plain_miss(string? id)
    {
        using var ctx = await SeededAsync(true, Signed());

        Assert.Null(await NewService(ctx).CheckAsync(id));
    }

    [Fact]
    public void The_check_carries_no_person_subject_reason_or_note()
    {
        var names = typeof(PublicWarrantCheck).GetProperties().Select(p => p.Name).ToHashSet();

        Assert.Equal(
            ["CancelledAt", "CaseNumber", "SignedAt", "SignerName", "SignerTitle", "State", "Type", "ValidFrom", "ValidUntil"],
            names.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task The_paper_link_points_at_the_configured_site_and_says_whether_the_check_is_on()
    {
        using var off = await SeededAsync(false, Signed());

        var link = await NewService(off).GetLinkAsync(SignedId);

        Assert.Equal($"fibhq.com/anordnung/{SignedId}", link.Address);
        Assert.NotNull(link.QrSvg);
        Assert.False(link.CheckOnline);

        using var on = await SeededAsync(true, Signed());
        Assert.True((await NewService(on).GetLinkAsync(SignedId)).CheckOnline);
    }
}
