using System.Data.Common;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.CurrentUser;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Sharing a record into Discord: who may, what never leaves, that every share is logged and rationed.</summary>
public sealed class ChannelShareServiceTests : IDisposable
{
    private const string Hook = "https://discord.com/api/webhooks/111111111111111111/secret-token_A";
    private const string OtherHook = "https://discord.com/api/webhooks/222222222222222222/other-token_B";
    private const string BaseUrl = "https://fibhq.test";

    private static readonly ClaimsPrincipal Agent =
        ClaimsPrincipalBuilder.Agent("agent").WithRank(Rank.SpecialAgent).WithCodename("Fox");

    private static readonly ClaimsPrincipal Leader =
        ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.SectionChief).AsTru().AsHrb().WithCodename("Hawk");

    private static readonly ClaimsPrincipal Supervision =
        ClaimsPrincipalBuilder.Agent("sup").WithRank(Rank.Director).AsTeamLead().WithCodename("Owl");

    private static readonly ClaimsPrincipal Demo =
        ClaimsPrincipalBuilder.Agent("demo").WithRank(Rank.Director).AsDemo().AsHrb();

    private static readonly ClaimsPrincipal Partner =
        ClaimsPrincipalBuilder.Agent("partner").AsPartner(PartnerAgency.LSPD, PartnerRank.Chief);

    private static readonly ClaimsPrincipal Applicant =
        ClaimsPrincipalBuilder.Agent("applicant").WithStatus(AgentStatus.Applicant);

    private readonly SqliteTestContext _ctx = new();
    private readonly IDiscordWebhookService _discord = Substitute.For<IDiscordWebhookService>();
    private readonly IAccessLogService _log = Substitute.For<IAccessLogService>();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
    private readonly ChannelShareService _service;

    public ChannelShareServiceTests()
    {
        _discord.PushToShareChannelAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);
        // real audit trail on writes
        var audited = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_ctx.Connection)
            .AddInterceptors(new AuditSaveChangesInterceptor(new FixedUser()))
            .Options;
        _service = new ChannelShareService(new TestDbContextFactory(audited), _discord, _log, _cache,
            new ChannelShareQuota(_clock));
        SeedSettings(enabled: true, new StoredShareChannel("ch1", "Ermittlungen", Hook),
            new StoredShareChannel("ch2", "Allgemein", OtherHook));
    }

    public void Dispose()
    {
        _cache.Dispose();
        _ctx.Dispose();
    }

    private void SeedSettings(bool enabled, params StoredShareChannel[] channels)
    {
        using var db = _ctx.NewContext();
        db.SystemSettings.RemoveRange(db.SystemSettings);
        db.SystemSettings.AddRange(
            new SystemSetting { Key = SystemSettingKeys.DiscordEnabled, Value = enabled ? "true" : "false" },
            new SystemSetting { Key = SystemSettingKeys.SiteBaseUrl, Value = BaseUrl },
            new SystemSetting { Key = SystemSettingKeys.DiscordShareChannels, Value = ChannelShareRules.Serialize(channels) });
        db.SaveChanges();
    }

    private string SeedPerson(Action<Person>? configure = null, string name = "Max Mustermann")
    {
        using var db = _ctx.NewContext();
        var person = Seed.Person(name: name, configure: configure);
        db.People.Add(person);
        db.SaveChanges();
        return person.Id;
    }

    private Task<string> ShareAsync(string id, ClaimsPrincipal actor, string type = nameof(Person),
        string channel = "ch1", string? note = null)
        => _service.ShareAsync(new ChannelShareRequest(type, id, channel, note), actor);

    private Task NothingPosted()
        => _discord.DidNotReceiveWithAnyArgs().PushToShareChannelAsync(default!, default!, default);

    private Task NothingLogged() => _log.DidNotReceiveWithAnyArgs().LogViewAsync(default!, default!, default);

    // ---- the post ----

    [Fact]
    public async Task A_share_posts_type_name_case_number_note_and_link_into_the_chosen_channel_and_is_logged()
    {
        var id = SeedPerson();
        string caseNumber;
        using (var db = _ctx.NewContext())
        {
            caseNumber = db.People.Single(p => p.Id == id).CaseNumber;
        }

        var name = await ShareAsync(id, Agent, note: "Bitte\nansehen");

        Assert.Equal("Ermittlungen", name);
        await _discord.Received(1).PushToShareChannelAsync(Hook,
            Arg.Is<string>(c => c.Contains("Person „Max Mustermann“") && c.Contains(caseNumber)
                && c.Contains("> Bitte ansehen") && c.EndsWith($"\n{BaseUrl}/personen/{id}")),
            Arg.Any<CancellationToken>());
        await _log.Received(1).LogViewAsync("Teilen", $"Person:{id}", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Every_one_of_the_six_record_types_can_be_shared()
    {
        string group, party, operation, faction, @case;
        using (var db = _ctx.NewContext())
        {
            var f = Seed.Faction();
            var c = Seed.Case(title: "Kokain");
            var g = new PersonGroup { Name = "Crew", CaseNumber = $"{CaseNumberService.AgencyPrefix}-G-2026-9001" };
            var p = new Party { Name = "Partei", CaseNumber = $"{CaseNumberService.AgencyPrefix}-PA-2026-9001" };
            var o = new Operation { Title = "Nachtfalke", CaseNumber = $"{CaseNumberService.AgencyPrefix}-O-2026-9001" };
            db.AddRange(f, c, g, p, o);
            db.SaveChanges();
            (faction, @case, group, party, operation) = (f.Id, c.Id, g.Id, p.Id, o.Id);
        }

        await ShareAsync(faction, Agent, nameof(Data.Entities.Factions.Faction));
        await ShareAsync(@case, Agent, nameof(Data.Entities.Cases.Case));
        await ShareAsync(group, Agent, nameof(PersonGroup));
        await ShareAsync(party, Agent, nameof(Party));
        await ShareAsync(operation, Leader, nameof(Operation));

        await _discord.Received(1).PushToShareChannelAsync(Hook,
            Arg.Is<string>(c => c.Contains("Vorgang „Kokain“") && c.EndsWith($"/vorgaenge/{@case}")), Arg.Any<CancellationToken>());
        await _discord.Received(1).PushToShareChannelAsync(Hook,
            Arg.Is<string>(c => c.Contains("Operation „Nachtfalke“") && c.EndsWith($"/operationen/{operation}")),
            Arg.Any<CancellationToken>());
        await _discord.Received(1).PushToShareChannelAsync(Hook,
            Arg.Is<string>(c => c.EndsWith($"/personengruppen/{group}")), Arg.Any<CancellationToken>());
        await _discord.Received(1).PushToShareChannelAsync(Hook,
            Arg.Is<string>(c => c.EndsWith($"/parteien/{party}")), Arg.Any<CancellationToken>());
        await _discord.Received(1).PushToShareChannelAsync(Hook,
            Arg.Is<string>(c => c.EndsWith($"/fraktionen/{faction}")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_post_never_names_the_sharer()
    {
        var id = SeedPerson();

        await ShareAsync(id, Leader);

        await _discord.Received(1).PushToShareChannelAsync(Arg.Any<string>(),
            Arg.Is<string>(c => !c.Contains("Hawk") && !c.Contains("lead")), Arg.Any<CancellationToken>());
    }

    // ---- what never leaves ----

    public static TheoryData<bool, bool, bool> SecrecyLevels => new()
    {
        { true, false, false },
        { true, true, false },
        { true, false, true },
        // broken invariant too
        { false, true, false },
        { false, false, true },
    };

    [Theory]
    [MemberData(nameof(SecrecyLevels))]
    public async Task A_classified_record_is_refused_even_to_who_may_read_it(bool classified, bool tru, bool hrb)
    {
        var id = SeedPerson(p => (p.IsClassified, p.IsTRUClassified, p.IsHRBClassified) = (classified, tru, hrb));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ShareAsync(id, Leader));

        Assert.Equal(ChannelShareRules.Refused, ex.Message);
        await NothingPosted();
        await NothingLogged();
    }

    [Fact]
    public async Task Missing_deleted_hidden_classified_and_foreign_types_are_refused_alike()
    {
        var classified = SeedPerson(p => (p.IsClassified, p.IsTRUClassified) = (true, true));
        var deleted = SeedPerson(p => p.IsDeleted = true);
        var open = SeedPerson();

        var messages = new List<string>();
        foreach (var attempt in new Func<Task>[]
                 {
                     () => ShareAsync("gibt-es-nicht", Agent),
                     () => ShareAsync(deleted, Agent),
                     // HRT-only, agent isn't HRT
                     () => ShareAsync(classified, Agent),
                     () => ShareAsync(classified, Leader),
                     // wrong type
                     () => ShareAsync(open, Agent, nameof(Data.Entities.Factions.Faction)),
                     () => ShareAsync(open, Agent, "Document"),
                     () => ShareAsync("", Agent),
                 })
        {
            messages.Add((await Assert.ThrowsAsync<InvalidOperationException>(attempt)).Message);
        }

        Assert.All(messages, m => Assert.Equal(ChannelShareRules.Refused, m));
        await NothingPosted();
        await NothingLogged();
    }

    public static TheoryData<string> Outsiders => new() { "supervision", "demo", "partner", "applicant", "blocked" };

    private static ClaimsPrincipal Outsider(string who) => who switch
    {
        "supervision" => Supervision,
        "demo" => Demo,
        "partner" => Partner,
        "applicant" => Applicant,
        _ => ClaimsPrincipalBuilder.Agent("gone").WithRank(Rank.SpecialAgent).WithStatus(AgentStatus.Blocked),
    };

    [Theory]
    [MemberData(nameof(Outsiders))]
    public async Task Supervision_demo_partners_and_inactive_accounts_may_not_share(string who)
    {
        var id = SeedPerson();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ShareAsync(id, Outsider(who)));
        Assert.Empty(await _service.GetChannelsAsync(Outsider(who)));
        await NothingPosted();
    }

    // ---- channel and switch ----

    [Fact]
    public async Task With_discord_off_nothing_is_shared_and_no_channel_is_offered()
    {
        SeedSettings(enabled: false, new StoredShareChannel("ch1", "Ermittlungen", Hook));
        var id = SeedPerson();

        await Assert.ThrowsAsync<InvalidOperationException>(() => ShareAsync(id, Agent));
        Assert.Empty(await _service.GetChannelsAsync(Agent));
        await NothingPosted();
    }

    [Fact]
    public async Task An_unknown_channel_is_refused()
    {
        var id = SeedPerson();

        await Assert.ThrowsAsync<InvalidOperationException>(() => ShareAsync(id, Agent, channel: "weg"));
        await NothingPosted();
    }

    [Fact]
    public async Task A_post_discord_refused_stays_logged_because_it_may_have_arrived()
    {
        _discord.PushToShareChannelAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var id = SeedPerson();

        await Assert.ThrowsAsync<InvalidOperationException>(() => ShareAsync(id, Agent));
        await _log.Received(1).LogViewAsync("Teilen", $"Person:{id}", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Without_a_log_entry_nothing_is_posted()
    {
        _log.LogViewAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("db down")));
        var id = SeedPerson();

        await Assert.ThrowsAsync<InvalidOperationException>(() => ShareAsync(id, Agent));
        await NothingPosted();
    }

    [Fact]
    public async Task A_too_long_note_is_refused_before_anything_is_posted()
    {
        var id = SeedPerson();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ShareAsync(id, Agent, note: new string('a', ChannelShareRules.MaxNoteLength + 1)));
        await NothingPosted();
    }

    // ---- quota ----

    [Fact]
    public async Task Shares_are_rationed_per_agent_and_a_refusal_costs_nothing()
    {
        var open = SeedPerson();
        var classified = SeedPerson(p => p.IsClassified = true);

        for (var i = 0; i < ChannelShareQuota.PerWindow + 2; i++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => ShareAsync(classified, Leader));
        }
        for (var i = 0; i < ChannelShareQuota.PerWindow; i++)
        {
            await ShareAsync(open, Leader);
        }

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ShareAsync(open, Leader));
        Assert.Contains("Warte kurz", ex.Message);
        await ShareAsync(open, Agent);

        _clock.Now += ChannelShareQuota.Window;
        await ShareAsync(open, Leader);
        await _discord.Received(ChannelShareQuota.PerWindow + 2)
            .PushToShareChannelAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ---- channel list for the button ----

    [Fact]
    public async Task A_writer_sees_channel_names_only()
    {
        var channels = await _service.GetChannelsAsync(Agent);

        Assert.Equal(new[] { "Ermittlungen", "Allgemein" }, channels.Select(c => c.Name));
        Assert.Equal(new[] { "ch1", "ch2" }, channels.Select(c => c.Id));
    }

    // ---- settings ----

    [Fact]
    public async Task The_settings_show_the_webhook_masked_to_leadership_and_supervision()
    {
        foreach (var reader in new[] { Leader, Supervision })
        {
            var settings = (await _service.GetSettingsAsync(reader)).Channels;

            Assert.Equal(2, settings.Count);
            Assert.All(settings, s => Assert.DoesNotContain("token", s.MaskedWebhook));
            Assert.Contains("111111111111111111", settings[0].MaskedWebhook);
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.GetSettingsAsync(Agent));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.GetSettingsAsync(Partner));
    }

    public static TheoryData<string> NonAdministrators => new() { "agent", "supervision", "demo", "partner" };

    [Theory]
    [MemberData(nameof(NonAdministrators))]
    public async Task Only_leadership_with_write_access_sets_up_channels(string who)
    {
        var actor = who switch
        {
            "agent" => Agent,
            "supervision" => Supervision,
            "demo" => Demo,
            _ => Partner,
        };

        var version = await VersionAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => _service.SaveSettingsAsync(
            [new ShareChannelInput { Name = "Neu", WebhookUrl = Hook }], version, actor));
    }

    [Fact]
    public async Task Saving_keeps_an_untouched_webhook_and_never_writes_one_into_the_protocol()
    {
        var replacement = "https://discord.com/api/webhooks/333333333333333333/fresh-token_C";

        await _service.SaveSettingsAsync(
        [
            new ShareChannelInput { Id = "ch1", Name = "Ermittlung", WebhookUrl = "" },
            new ShareChannelInput { Name = "Neu", WebhookUrl = replacement },
        ], await VersionAsync(), Leader);

        using var db = _ctx.NewContext();
        var stored = ChannelShareRules.Parse(db.SystemSettings.Single(s => s.Key == SystemSettingKeys.DiscordShareChannels).Value);
        Assert.Equal(2, stored.Count);
        Assert.Equal(new StoredShareChannel("ch1", "Ermittlung", Hook), stored[0]);
        Assert.Equal(replacement, stored[1].Url);

        var audit = Assert.Single(db.AuditLogs.Where(a => a.EntityType == ChannelShareRules.AuditType));
        Assert.Contains("Ermittlung", audit.ChangesJson);
        // update bypasses the tracked audit
        Assert.Empty(db.AuditLogs.Where(a => a.EntityType == nameof(SystemSetting)));
        Assert.All(db.AuditLogs.ToList(), a => Assert.DoesNotContain("token", a.ChangesJson ?? string.Empty));
    }

    [Fact]
    public async Task The_first_save_inserts_the_list_without_a_webhook_in_the_protocol()
    {
        using (var db = _ctx.NewContext())
        {
            db.SystemSettings.RemoveRange(db.SystemSettings.Where(s => s.Key == SystemSettingKeys.DiscordShareChannels));
            db.SaveChanges();
        }

        await _service.SaveSettingsAsync([new ShareChannelInput { Name = "Erster", WebhookUrl = Hook }],
            await VersionAsync(), Leader);

        using var check = _ctx.NewContext();
        Assert.Single(ChannelShareRules.Parse(
            check.SystemSettings.Single(s => s.Key == SystemSettingKeys.DiscordShareChannels).Value));
        // the tracked insert carries no values
        var insert = Assert.Single(check.AuditLogs.Where(a => a.EntityType == nameof(SystemSetting)).ToList());
        Assert.Equal(AuditAction.Created, insert.Action);
        Assert.Null(insert.ChangesJson);
        Assert.All(check.AuditLogs.ToList(), a => Assert.DoesNotContain("token", a.ChangesJson ?? string.Empty));
    }

    [Fact]
    public async Task A_save_that_changes_nothing_writes_nothing()
    {
        await _service.SaveSettingsAsync(
        [
            new ShareChannelInput { Id = "ch1", Name = "Ermittlungen" },
            new ShareChannelInput { Id = "ch2", Name = "Allgemein" },
        ], await VersionAsync(), Leader);

        using var db = _ctx.NewContext();
        Assert.Empty(db.AuditLogs.Where(a => a.EntityType == ChannelShareRules.AuditType));
    }

    [Fact]
    public async Task A_removed_channel_disappears_from_the_button_at_once_and_takes_no_more_posts()
    {
        Assert.Equal(2, (await _service.GetChannelsAsync(Agent)).Count);

        await _service.SaveSettingsAsync([new ShareChannelInput { Id = "ch2", Name = "Allgemein" }],
            await VersionAsync(), Leader);

        Assert.Equal("ch2", Assert.Single(await _service.GetChannelsAsync(Agent)).Id);
        var id = SeedPerson();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ShareAsync(id, Agent, channel: "ch1"));
        await NothingPosted();
    }

    // ---- stale editor ----

    [Fact]
    public async Task A_stale_editor_cannot_drop_a_channel_added_since_it_loaded()
    {
        var stale = await VersionAsync();
        var added = "https://discord.com/api/webhooks/333333333333333333/fresh-token_C";
        // a second leader adds a channel first
        await _service.SaveSettingsAsync(
        [
            new ShareChannelInput { Id = "ch1", Name = "Ermittlungen" },
            new ShareChannelInput { Id = "ch2", Name = "Allgemein" },
            new ShareChannelInput { Name = "Neu", WebhookUrl = added },
        ], stale, Leader);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SaveSettingsAsync(
        [
            new ShareChannelInput { Id = "ch1", Name = "Ermittlung" },
            new ShareChannelInput { Id = "ch2", Name = "Allgemein" },
        ], stale, Leader));

        Assert.Equal(ChannelShareRules.StaleList, ex.Message);
        using var db = _ctx.NewContext();
        var kept = ChannelShareRules.Parse(db.SystemSettings.Single(s => s.Key == SystemSettingKeys.DiscordShareChannels).Value);
        Assert.Equal(new[] { "Ermittlungen", "Allgemein", "Neu" }, kept.Select(c => c.Name));
        Assert.Equal(added, kept[2].Url);
        Assert.Single(db.AuditLogs.Where(a => a.EntityType == ChannelShareRules.AuditType));
    }

    [Fact]
    public async Task A_version_is_good_for_one_save_only_even_when_only_a_webhook_changed()
    {
        var version = await VersionAsync();
        await _service.SaveSettingsAsync(
        [
            new ShareChannelInput { Id = "ch1", Name = "Ermittlungen", WebhookUrl = "https://discord.com/api/webhooks/333333333333333333/fresh-token_C" },
            new ShareChannelInput { Id = "ch2", Name = "Allgemein" },
        ], version, Leader);

        Assert.NotEqual(version, await VersionAsync());
        // refused although nothing would change
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SaveSettingsAsync(
        [
            new ShareChannelInput { Id = "ch1", Name = "Ermittlungen" },
            new ShareChannelInput { Id = "ch2", Name = "Allgemein" },
        ], version, Leader));
        Assert.Equal(ChannelShareRules.StaleList, ex.Message);
    }

    [Fact]
    public async Task A_write_slipping_in_between_the_read_and_the_update_is_refused()
    {
        var rival = new RivalWrite();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_ctx.Connection)
            .AddInterceptors(new AuditSaveChangesInterceptor(new FixedUser()), rival)
            .Options;
        var service = new ChannelShareService(new TestDbContextFactory(options), _discord, _log, _cache,
            new ChannelShareQuota(_clock));
        var version = await VersionAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveSettingsAsync(
            [new ShareChannelInput { Id = "ch2", Name = "Allgemein" }], version, Leader));

        Assert.True(rival.Fired);
        Assert.Equal(ChannelShareRules.StaleList, ex.Message);
        using var db = _ctx.NewContext();
        Assert.Empty(db.AuditLogs.Where(a => a.EntityType == ChannelShareRules.AuditType));
    }

    private async Task<string> VersionAsync() => (await _service.GetSettingsAsync(Leader)).Version;

    /// <summary>Overwrites the list right before the service's own update runs, as a parallel save would.</summary>
    private sealed class RivalWrite : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Fired && command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal))
            {
                Fired = true;
                // same connection, rolls back with the save
                using var rival = command.Connection!.CreateCommand();
                rival.Transaction = command.Transaction;
                rival.CommandText = $"UPDATE \"SystemEinstellungen\" SET \"Wert\" = '[]' WHERE \"Schluessel\" = '{SystemSettingKeys.DiscordShareChannels}'";
                rival.ExecuteNonQuery();
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class FixedUser : ICurrentUserService
    {
        public Task<CurrentUserInfo> GetAsync() => Task.FromResult(Get());

        public CurrentUserInfo Get() => new("lead", "Hawk", true, false, false);
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
