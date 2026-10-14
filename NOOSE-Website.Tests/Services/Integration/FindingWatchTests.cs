using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NOOSE_Website.Data.Entities.CounterIntel;
using NOOSE_Website.Data.Entities.Notifications;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.Notifications;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.CounterIntel;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Llm;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The finding watch: rule and NOOSEI findings ring the leadership bell once, never about oneself.</summary>
public sealed class FindingWatchTests : IDisposable
{
    private static readonly DateTime Week = new(2026, 10, 11, 22, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestContext _ctx = new();
    private readonly ILlmAnomalyService _anomalies = Substitute.For<ILlmAnomalyService>();
    private readonly NavPreferencesService _prefs;
    private readonly CounterIntelRuleService _rules;
    private readonly FindingWatchService _watch;
    private LlmAnomalySnapshot _snapshot = new(Week, []);

    public FindingWatchTests()
    {
        _anomalies.EvaluateAsync(Arg.Any<CancellationToken>()).Returns(_ => _snapshot);
        _prefs = new NavPreferencesService(_ctx.Factory, new MemoryCache(new MemoryCacheOptions()));
        _rules = new CounterIntelRuleService(_ctx.Factory);
        _watch = Watch(new NotificationService(_ctx.Factory, new NotificationBroadcaster(),
            Substitute.For<IDiscordWebhookService>()));
        SeedRoster();
    }

    public void Dispose() => _ctx.Dispose();

    // ---- rules ----

    [Fact]
    public async Task The_first_run_arms_an_unarmed_rule_without_reporting_what_came_before()
    {
        var id = SeedUnarmedRule("Massen");
        Reads("a1", 3);

        var now = DateTime.UtcNow;
        Assert.Equal(0, await _watch.RunAsync(now));

        Assert.Empty(AllNotices());
        Assert.Equal(now, Rule(id).ReportFrom);
    }

    [Fact]
    public async Task A_new_finding_rings_leadership_only()
    {
        var id = SeedRule("Massen");
        Reads("a1", 3);

        Assert.Equal(1, await _watch.RunAsync(DateTime.UtcNow));

        Assert.Equal(["admin", "lead1", "lead2"], AllNotices().Select(n => n.RecipientId).Order().ToList());
        var notice = Notices("lead1").Single();
        Assert.Equal(NotificationType.CounterIntelFinding, notice.Type);
        Assert.Equal("/nachweis?tab=auffaelligkeiten", notice.Href);
        Assert.Equal("Gegenaufklärung: 1 neue Auffälligkeit (Massen)", notice.Title);
        Assert.DoesNotContain("a1", notice.Title, StringComparison.OrdinalIgnoreCase);
        Assert.Single(Ledger(), r => r.Key == id && r.AgentId == "a1" && r.ReportedAt != null);
    }

    [Fact]
    public async Task The_flagged_leader_hears_nothing_about_himself()
    {
        SeedRule("Massen");
        Reads("lead1", 3);

        await _watch.RunAsync(DateTime.UtcNow);

        Assert.Empty(Notices("lead1"));
        Assert.Single(Notices("lead2"));
    }

    [Fact]
    public async Task The_same_events_never_ring_twice()
    {
        SeedRule("Massen", bucket: CounterIntelBucket.Day);
        Reads("a1", 3);

        Assert.Equal(1, await _watch.RunAsync(DateTime.UtcNow));
        Assert.Equal(0, await _watch.RunAsync(DateTime.UtcNow));

        Assert.Single(Notices("lead1"));
    }

    [Fact]
    public async Task A_burst_spread_over_two_runs_rings_once_it_crosses_the_threshold()
    {
        SeedRule("Massen", bucket: CounterIntelBucket.Day);
        Reads("a1", 2);
        Assert.Equal(0, await _watch.RunAsync(DateTime.UtcNow));

        Reads("a1", 1, prefix: "late");
        Assert.Equal(1, await _watch.RunAsync(DateTime.UtcNow));
    }

    [Fact]
    public async Task A_whole_window_rule_names_an_agent_once_per_window()
    {
        SeedRule("Off-Hours", bucket: CounterIntelBucket.Window);
        Reads("a1", 3);
        Assert.Equal(1, await _watch.RunAsync(DateTime.UtcNow));

        // three fresh reads after the reported ones: enough on their own, held back by the window
        Reads("a1", 3, prefix: "more", minutesAgo: 5);
        Assert.Equal(0, await _watch.RunAsync(DateTime.UtcNow));
    }

    [Fact]
    public async Task Two_findings_in_one_pass_share_one_notice()
    {
        SeedRule("Massen", order: 1);
        SeedRule("Burst", order: 2);
        Reads("a1", 3);

        await _watch.RunAsync(DateTime.UtcNow);

        var notice = Notices("lead1").Single();
        Assert.Equal("Gegenaufklärung: 2 neue Auffälligkeiten (Massen, Burst)", notice.Title);
    }

    [Fact]
    public async Task A_later_finding_folds_and_counts_everything_unseen()
    {
        SeedRule("Massen", bucket: CounterIntelBucket.Day);
        Reads("a1", 3);
        await _watch.RunAsync(DateTime.UtcNow);

        Reads("a2", 3);
        await _watch.RunAsync(DateTime.UtcNow);

        var notice = Notices("lead1").Single();
        Assert.Equal("Gegenaufklärung: 2 neue Auffälligkeiten (Massen)", notice.Title);
        Assert.Null(notice.ReadAt);
    }

    [Fact]
    public async Task Looking_at_the_findings_resets_the_count_and_reads_the_notice()
    {
        var id = SeedRule("Massen", bucket: CounterIntelBucket.Day);
        Reads("a1", 3);
        await _watch.RunAsync(DateTime.UtcNow);

        var unseen = await _watch.GetUnseenAsync(Leader("lead1"), FindingSource.Rule);
        Assert.True(unseen.IsNew(id, "a1"));
        await _watch.MarkSeenAsync(Leader("lead1"), FindingSource.Rule, unseen.AsOfUtc);

        Assert.NotNull(Notices("lead1").Single().ReadAt);
        Assert.False((await _watch.GetUnseenAsync(Leader("lead1"), FindingSource.Rule)).IsNew(id, "a1"));
        Assert.True((await _watch.GetUnseenAsync(Leader("lead2"), FindingSource.Rule)).IsNew(id, "a1"));

        Reads("a2", 3);
        await _watch.RunAsync(DateTime.UtcNow);

        var fresh = Notices("lead1").Single(n => n.ReadAt == null);
        Assert.Equal("Gegenaufklärung: 1 neue Auffälligkeit (Massen)", fresh.Title);
        Assert.Equal("Gegenaufklärung: 2 neue Auffälligkeiten (Massen)", Notices("lead2").Single().Title);
    }

    [Fact]
    public async Task A_finding_reported_after_the_look_stays_new()
    {
        var id = SeedRule("Massen", bucket: CounterIntelBucket.Day);
        var unseen = await _watch.GetUnseenAsync(Leader("lead1"), FindingSource.Rule);
        Reads("a1", 3);
        await _watch.RunAsync(DateTime.UtcNow);

        await _watch.MarkSeenAsync(Leader("lead1"), FindingSource.Rule, unseen.AsOfUtc);

        Assert.True((await _watch.GetUnseenAsync(Leader("lead1"), FindingSource.Rule)).IsNew(id, "a1"));
        Assert.Null(Notices("lead1").Single().ReadAt);
    }

    [Fact]
    public async Task A_silenced_rule_never_rings()
    {
        SeedRule("Massen", notify: false);
        Reads("a1", 3);

        Assert.Equal(0, await _watch.RunAsync(DateTime.UtcNow));

        Assert.Empty(AllNotices());
    }

    [Fact]
    public async Task A_deleted_rule_drops_out_of_the_count()
    {
        var gone = SeedRule("Massen", bucket: CounterIntelBucket.Day);
        Reads("a1", 3);
        await _watch.RunAsync(DateTime.UtcNow);
        await _rules.DeleteAsync(gone, Leader("lead1"));

        // armed after a1's reads, so only a2 is new to it
        SeedRule("Burst", bucket: CounterIntelBucket.Day, reportFrom: DateTime.UtcNow.AddMinutes(-5));
        Reads("a2", 3, minutesAgo: 1);
        await _watch.RunAsync(DateTime.UtcNow);

        Assert.Equal("Gegenaufklärung: 1 neue Auffälligkeit (Burst)", Notices("lead1").Single().Title);
    }

    [Fact]
    public async Task A_failed_notice_spends_only_its_own_claim()
    {
        SeedRule("Massen", bucket: CounterIntelBucket.Day);
        Reads("a1", 3);
        var notifications = Substitute.For<INotificationService>();
        notifications.NotifyOnceAsync("lead1", Arg.Any<NotificationType>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("down"));
        var watch = Watch(notifications);

        Assert.Equal(1, await watch.RunAsync(DateTime.UtcNow));
        Assert.Equal(0, await watch.RunAsync(DateTime.UtcNow));

        await notifications.Received(1).NotifyOnceAsync("lead2", NotificationType.CounterIntelFinding,
            Arg.Any<string>(), "/nachweis?tab=auffaelligkeiten", Arg.Any<CancellationToken>());
    }

    // ---- re-arming through the editor ----

    [Fact]
    public async Task A_changed_definition_re_arms_and_a_rename_does_not()
    {
        var id = await _rules.CreateAsync(Input("Massen"), Leader("lead1"));
        var created = Rule(id).ReportFrom;
        Assert.NotNull(created);

        await _rules.UpdateAsync(id, Input("Umbenannt"), Leader("lead1"));
        Assert.Equal(created, Rule(id).ReportFrom);

        await Task.Delay(5);
        await _rules.UpdateAsync(id, Input("Umbenannt", d => d.Threshold = 5), Leader("lead1"));
        Assert.True(Rule(id).ReportFrom > created);
    }

    [Fact]
    public async Task Switching_a_rule_back_on_re_arms_it()
    {
        var id = await _rules.CreateAsync(Input("Massen"), Leader("lead1"));
        await _rules.SetActiveAsync(id, false, Leader("lead1"));
        var before = Rule(id).ReportFrom;

        await Task.Delay(5);
        await _rules.SetActiveAsync(id, true, Leader("lead1"));

        Assert.True(Rule(id).ReportFrom > before);
    }

    [Fact]
    public async Task A_silent_rule_stays_silent_after_saving()
    {
        var id = await _rules.CreateAsync(Input("Massen", notify: false), Leader("lead1"));

        Assert.False(Rule(id).Notify);
        Assert.False((await _rules.GetActiveAsync()).Single().Notify);
    }

    // ---- NOOSEI ----

    [Fact]
    public async Task The_first_noosei_pass_lays_a_silent_baseline()
    {
        _snapshot = new LlmAnomalySnapshot(Week, [Flag("a1", LlmAnomalyKind.Burst)]);

        Assert.Equal(0, await _watch.RunAsync(DateTime.UtcNow));

        Assert.Empty(AllNotices());
        var row = Assert.Single(Ledger());
        Assert.Null(row.ReportedAt);
        Assert.NotNull(Setting(SystemSettingKeys.NooseiAnomalyWatchArmedAt));
    }

    [Fact]
    public async Task A_new_noosei_anomaly_rings_once_a_week_and_again_when_it_gets_worse()
    {
        await _watch.RunAsync(DateTime.UtcNow);

        _snapshot = new LlmAnomalySnapshot(Week, [Flag("a1", LlmAnomalyKind.BurnRate)]);
        Assert.Equal(1, await _watch.RunAsync(DateTime.UtcNow));
        Assert.Equal(0, await _watch.RunAsync(DateTime.UtcNow));

        var notice = Notices("lead1").Single();
        Assert.Equal(NotificationType.NooseiAnomaly, notice.Type);
        Assert.Equal("/einstellungen?tab=ki-kontingente", notice.Href);
        Assert.Equal($"NOOSEI: 1 neue Auffälligkeit ({LlmAnomalyKindDisplay.Name(LlmAnomalyKind.BurnRate)})",
            notice.Title);

        _snapshot = new LlmAnomalySnapshot(Week, [Flag("a1", LlmAnomalyKind.BurnRate, CounterIntelSeverity.High)]);
        Assert.Equal(1, await _watch.RunAsync(DateTime.UtcNow));

        _snapshot = new LlmAnomalySnapshot(Week.AddDays(7), [Flag("a1", LlmAnomalyKind.BurnRate)]);
        Assert.Equal(1, await _watch.RunAsync(DateTime.UtcNow));
    }

    [Fact]
    public async Task Saving_new_anomaly_thresholds_lays_a_new_noosei_baseline()
    {
        await _watch.RunAsync(DateTime.UtcNow);
        var changed = LlmQuotaConfig.Default();
        changed.Anomalies.BurstRequests += 1;
        await QuotaConfig().SaveAsync(changed, AiOwner());
        Assert.Null(Setting(SystemSettingKeys.NooseiAnomalyWatchArmedAt));

        _snapshot = new LlmAnomalySnapshot(Week, [Flag("a1", LlmAnomalyKind.Outlier)]);
        Assert.Equal(0, await _watch.RunAsync(DateTime.UtcNow));

        Assert.Empty(AllNotices());
        Assert.NotNull(Setting(SystemSettingKeys.NooseiAnomalyWatchArmedAt));
    }

    [Fact]
    public async Task Saving_unchanged_thresholds_keeps_the_noosei_watch_armed()
    {
        await _watch.RunAsync(DateTime.UtcNow);
        var quotas = LlmQuotaConfig.Default();
        quotas.Ranks.Clear();
        await QuotaConfig().SaveAsync(quotas, AiOwner());

        _snapshot = new LlmAnomalySnapshot(Week, [Flag("a1", LlmAnomalyKind.Outlier)]);
        Assert.Equal(1, await _watch.RunAsync(DateTime.UtcNow));
    }

    [Fact]
    public async Task Rules_and_noosei_ring_separate_notices()
    {
        await _watch.RunAsync(DateTime.UtcNow);
        SeedRule("Massen");
        Reads("a1", 3);
        _snapshot = new LlmAnomalySnapshot(Week, [Flag("a2", LlmAnomalyKind.Burst)]);

        Assert.Equal(2, await _watch.RunAsync(DateTime.UtcNow));

        Assert.Equal([NotificationType.CounterIntelFinding, NotificationType.NooseiAnomaly],
            Notices("lead1").Select(n => n.Type).Order().ToList());
    }

    [Fact]
    public async Task The_noosei_flagged_leader_hears_nothing_about_himself()
    {
        await _watch.RunAsync(DateTime.UtcNow);
        _snapshot = new LlmAnomalySnapshot(Week, [Flag("lead1", LlmAnomalyKind.Burst)]);

        await _watch.RunAsync(DateTime.UtcNow);

        Assert.Empty(Notices("lead1"));
        Assert.Single(Notices("lead2"));
    }

    // ---- panel gate ----

    [Fact]
    public async Task Juniors_and_the_supervision_get_no_marks_and_no_stamp()
    {
        SeedRule("Massen");
        Reads("a1", 3);
        await _watch.RunAsync(DateTime.UtcNow);
        var junior = ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.JuniorAgent).Build();
        var supervision = ClaimsPrincipalBuilder.Agent("tl").WithRank(Rank.Director).AsTeamLead().Build();

        Assert.Empty((await _watch.GetUnseenAsync(junior, FindingSource.Rule)).Slots);
        Assert.Empty((await _watch.GetUnseenAsync(supervision, FindingSource.Rule)).Slots);
        await _watch.MarkSeenAsync(junior, FindingSource.Rule, DateTime.UtcNow);
        await _watch.MarkSeenAsync(supervision, FindingSource.Rule, DateTime.UtcNow);

        Assert.Null((await _prefs.GetAsync("junior")).CounterIntelSeenUtc);
        Assert.Null((await _prefs.GetAsync("tl")).CounterIntelSeenUtc);
    }

    // ---- helpers ----

    private LlmQuotaConfigService QuotaConfig() => new(_ctx.Factory, new MemoryCache(new MemoryCacheOptions()));

    private static ClaimsPrincipal AiOwner()
        => ClaimsPrincipalBuilder.Agent("lead1").WithRank(Rank.Director).AsAiOwner().Build();

    private FindingWatchService Watch(INotificationService notifications)
        => new(_ctx.Factory, _rules, _anomalies, notifications, _prefs, NullLogger<FindingWatchService>.Instance);

    private static ClaimsPrincipal Leader(string id) => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).Build();

    private static LlmAnomalyFlag Flag(string agent, LlmAnomalyKind kind,
        CounterIntelSeverity grade = CounterIntelSeverity.Warning)
        => new(agent, agent.ToUpperInvariant(), kind, "Detail", 1, grade);

    private static CounterIntelRuleInput Input(string name, Action<CounterIntelRuleDefinition>? configure = null,
        bool notify = true)
    {
        var definition = new CounterIntelRuleDefinition { WindowDays = 30, Threshold = 3 };
        configure?.Invoke(definition);
        return new CounterIntelRuleInput { Name = name, Notify = notify, Definition = definition };
    }

    // leadership in every flavour the fan-out must tell apart
    private void SeedRoster()
    {
        using var db = _ctx.NewContext();
        db.Users.AddRange(
            Seed.Agent("lead1", Rank.Director),
            Seed.Agent("lead2", Rank.SupervisorySpecialAgent),
            Seed.Agent("admin", Rank.JuniorAgent, configure: a => a.IsAdmin = true),
            Seed.Agent("junior", Rank.JuniorAgent),
            Seed.Agent("tl", Rank.Director, configure: a => a.IsTeamLead = true),
            Seed.Agent("partner", Rank.Director, configure: a => a.PartnerAgency = PartnerAgency.LSPD),
            Seed.Agent(DemoIdentity.AgentId, Rank.Director),
            Seed.Agent("a1", Rank.JuniorAgent),
            Seed.Agent("a2", Rank.JuniorAgent));
        db.SaveChanges();
    }

    private string SeedRule(string name, CounterIntelBucket bucket = CounterIntelBucket.Window, bool notify = true,
        int order = 0, DateTime? reportFrom = null)
    {
        using var db = _ctx.NewContext();
        var rule = new CounterIntelRule
        {
            Name = name,
            IsActive = true,
            Notify = notify,
            Order = order,
            DefinitionJson = new CounterIntelRuleDefinition
            {
                WindowDays = 30, Threshold = 3, Bucket = bucket, CountMode = CounterIntelCountMode.DistinctRecords,
            }.ToJson(),
            ReportFrom = reportFrom ?? DateTime.UtcNow.AddHours(-1),
        };
        db.CounterIntelRules.Add(rule);
        db.SaveChanges();
        return rule.Id;
    }

    // a rule as the migration leaves it: never armed
    private string SeedUnarmedRule(string name)
    {
        var id = SeedRule(name);
        using var db = _ctx.NewContext();
        db.CounterIntelRules.Where(r => r.Id == id)
            .ExecuteUpdate(s => s.SetProperty(r => r.ReportFrom, (DateTime?)null));
        return id;
    }

    private void Reads(string agent, int count, string prefix = "p", int minutesAgo = 10)
    {
        using var db = _ctx.NewContext();
        for (var i = 0; i < count; i++)
        {
            db.AccessLogs.Add(new AccessLog
            {
                AgentId = agent, AgentName = agent, Timestamp = DateTime.UtcNow.AddMinutes(-minutesAgo).AddSeconds(i),
                EntityType = "Person", EntityId = $"{prefix}{i}",
            });
        }
        db.SaveChanges();
    }

    private CounterIntelRule Rule(string id)
    {
        using var db = _ctx.NewContext();
        return db.CounterIntelRules.AsNoTracking().Single(r => r.Id == id);
    }

    private List<FindingReport> Ledger()
    {
        using var db = _ctx.NewContext();
        return db.FindingReports.AsNoTracking().ToList();
    }

    private string? Setting(string key)
    {
        using var db = _ctx.NewContext();
        return db.SystemSettings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefault();
    }

    private List<Notification> AllNotices()
    {
        using var db = _ctx.NewContext();
        return db.Notifications.AsNoTracking().ToList();
    }

    private List<Notification> Notices(string recipient)
        => AllNotices().Where(n => n.RecipientId == recipient).ToList();
}
