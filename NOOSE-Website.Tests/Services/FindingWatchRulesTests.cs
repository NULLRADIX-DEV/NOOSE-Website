using NOOSE_Website.Data.Entities.CounterIntel;
using NOOSE_Website.Models.CounterIntel;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>What the finding watch counts as new: consumed events, re-arming, NOOSEI weeks, the unseen count and the title.</summary>
public sealed class FindingWatchRulesTests
{
    private static readonly DateTime NowUtc = new(2026, 10, 14, 10, 0, 0, DateTimeKind.Utc);

    // a fixed offset keeps local and UTC apart without depending on the machine's zone
    private static readonly TimeSpan Offset = TimeSpan.FromHours(2);
    private static readonly DateTime NowLocal = NowUtc.Add(Offset);
    private static readonly DateTime Armed = NowUtc.AddDays(-5);

    private static CounterIntelEvent Event(DateTime utc, string agent = "a1", string id = "p1",
        CounterIntelActionKind action = CounterIntelActionKind.Read)
        => new()
        {
            AgentId = agent,
            AgentName = agent.ToUpperInvariant(),
            LocalTimestamp = utc.Add(Offset),
            TimestampUtc = utc,
            EntityType = "Person",
            EntityId = id,
            Action = action,
        };

    private static CounterIntelRuleView Rule(Action<CounterIntelRuleDefinition>? configure = null,
        string id = "r1", bool notify = true, DateTime? reportFrom = null, bool active = true)
    {
        var definition = new CounterIntelRuleDefinition
        {
            WindowDays = 30, Threshold = 3, Bucket = CounterIntelBucket.Window,
        };
        configure?.Invoke(definition);
        return new CounterIntelRuleView(id, $"Regel {id}", null, CounterIntelSeverity.Warning, active, 0, definition,
            notify, reportFrom ?? Armed);
    }

    private static FindingReport Row(string key, string agent, DateTime coveredUntil, DateTime? reportedAt = null,
        CounterIntelSeverity grade = CounterIntelSeverity.Warning, FindingSource source = FindingSource.Rule)
        => new()
        {
            Source = source, Key = key, AgentId = agent, CoveredUntil = coveredUntil, ReportedAt = reportedAt, Grade = grade,
        };

    private static Dictionary<string, FindingReport> Ledger(params FindingReport[] rows)
        => rows.ToDictionary(r => FindingWatchRules.Slot(r.Key, r.AgentId));

    private static List<FindingWatchRules.RuleIncident> Run(IEnumerable<CounterIntelEvent> events,
        CounterIntelRuleView rule, Dictionary<string, FindingReport>? ledger = null)
        => FindingWatchRules.Incidents(events.ToList(), [rule], ledger ?? [], NowUtc, NowLocal);

    private static IEnumerable<CounterIntelEvent> Reads(int count, DateTime startUtc, string agent = "a1", string prefix = "p")
        => Enumerable.Range(0, count).Select(i => Event(startUtc.AddMinutes(i), agent, $"{prefix}{i}"));

    // ==================== incidents ====================

    [Fact]
    public void A_finding_on_fresh_events_is_an_incident_up_to_its_newest_event()
    {
        var events = Reads(3, NowUtc.AddHours(-3)).ToList();

        var incident = Assert.Single(Run(events, Rule()));

        Assert.Equal("r1", incident.RuleId);
        Assert.Equal("a1", incident.AgentId);
        Assert.Equal(events.Max(e => e.TimestampUtc), incident.ConsumedUntilUtc);
    }

    [Fact]
    public void Events_before_the_rule_was_armed_do_not_count()
    {
        var events = Reads(2, Armed.AddHours(-2)).Concat(Reads(2, Armed.AddHours(1), prefix: "q"));

        Assert.Empty(Run(events, Rule()));
    }

    [Fact]
    public void Events_already_consumed_by_a_report_never_count_again()
    {
        var old = Reads(3, NowUtc.AddHours(-5)).ToList();
        var ledger = Ledger(Row("r1", "a1", old.Max(e => e.TimestampUtc), NowUtc.AddHours(-4)));
        var events = old.Concat(Reads(2, NowUtc.AddHours(-1), prefix: "q"));

        Assert.Empty(Run(events, Rule(d => d.Bucket = CounterIntelBucket.Day), ledger));
    }

    [Fact]
    public void A_burst_spread_over_two_runs_is_reported_once_it_crosses_the_threshold()
    {
        var first = Reads(2, NowUtc.AddMinutes(-30)).ToList();
        Assert.Empty(Run(first, Rule(d => d.Bucket = CounterIntelBucket.Hour)));

        var both = first.Append(Event(NowUtc.AddMinutes(-5), id: "late")).ToList();

        Assert.Single(Run(both, Rule(d => d.Bucket = CounterIntelBucket.Hour)));
    }

    [Fact]
    public void A_new_burst_after_a_report_is_a_new_incident()
    {
        var old = Reads(3, NowUtc.AddDays(-2)).ToList();
        var ledger = Ledger(Row("r1", "a1", old.Max(e => e.TimestampUtc), NowUtc.AddDays(-2)));
        var events = old.Concat(Reads(3, NowUtc.AddHours(-1), prefix: "q"));

        Assert.Single(Run(events, Rule(d => d.Bucket = CounterIntelBucket.Day), ledger));
    }

    [Fact]
    public void Consumed_until_is_the_newest_event_the_rule_actually_counts()
    {
        var reads = Reads(3, NowUtc.AddHours(-3)).ToList();
        var laterWrite = Event(NowUtc.AddMinutes(-1), action: CounterIntelActionKind.Modified);
        var rule = Rule(d => d.Actions = [CounterIntelActionKind.Read]);

        var incident = Assert.Single(Run(reads.Append(laterWrite), rule));

        Assert.Equal(reads.Max(e => e.TimestampUtc), incident.ConsumedUntilUtc);
    }

    [Fact]
    public void Floors_are_kept_per_agent()
    {
        var a1 = Reads(3, NowUtc.AddHours(-3), "a1").ToList();
        var a2 = Reads(3, NowUtc.AddHours(-3), "a2").ToList();
        var ledger = Ledger(Row("r1", "a1", a1.Max(e => e.TimestampUtc), NowUtc.AddHours(-2)));

        var incident = Assert.Single(Run(a1.Concat(a2), Rule(d => d.Bucket = CounterIntelBucket.Day), ledger));

        Assert.Equal("a2", incident.AgentId);
    }

    [Fact]
    public void A_whole_window_rule_names_an_agent_at_most_once_per_window()
    {
        var events = Reads(6, NowUtc.AddDays(-3)).ToList();
        var ledger = Ledger(Row("r1", "a1", events[2].TimestampUtc, NowUtc.AddDays(-3)));

        Assert.Empty(Run(events, Rule(), ledger));
        // once the window has passed since that report, fresh events may ring again
        var later = Ledger(Row("r1", "a1", events[2].TimestampUtc, NowUtc.AddDays(-31)));
        Assert.Single(Run(events, Rule(), later));
    }

    [Fact]
    public void A_report_from_before_a_re_arm_does_not_hold_back_a_whole_window_rule()
    {
        // reported under the old definition, then edited: the new definition starts with a clean slate
        var events = Reads(3, NowUtc.AddHours(-2)).ToList();
        var ledger = Ledger(Row("r1", "a1", NowUtc.AddDays(-4), NowUtc.AddDays(-4)));

        Assert.Single(Run(events, Rule(reportFrom: NowUtc.AddDays(-3)), ledger));
    }

    [Fact]
    public void A_day_rule_uses_up_only_the_day_that_crossed_the_threshold()
    {
        // three reads late yesterday, one just after midnight: the run after midnight reports yesterday
        var yesterday = NowLocal.Date.AddMinutes(-10).Subtract(Offset);
        var late = Reads(3, yesterday).ToList();
        var early = Event(NowLocal.Date.AddMinutes(5).Subtract(Offset), id: "t0");
        var rule = Rule(d => d.Bucket = CounterIntelBucket.Day);

        var incident = Assert.Single(Run(late.Append(early), rule));
        Assert.Equal(late.Max(e => e.TimestampUtc), incident.ConsumedUntilUtc);

        // today's early read stays fresh and counts towards today's own burst
        var ledger = Ledger(Row("r1", "a1", incident.ConsumedUntilUtc, NowUtc.AddHours(-11)));
        var today = new[] { early, Event(NowUtc.AddMinutes(-2), id: "t1"), Event(NowUtc.AddMinutes(-1), id: "t2") };
        Assert.Single(Run(late.Concat(today), rule, ledger));
    }

    [Fact]
    public void A_sliding_rule_uses_up_the_burst_but_not_a_lone_read_after_it()
    {
        var burst = Reads(3, NowUtc.AddHours(-5)).ToList();
        var lone = Event(NowUtc.AddMinutes(-1), id: "later");
        var rule = Rule(d =>
        {
            d.Bucket = CounterIntelBucket.Sliding;
            d.SlidingMinutes = 30;
        });

        var incident = Assert.Single(Run(burst.Append(lone), rule));

        Assert.Equal(burst.Max(e => e.TimestampUtc), incident.ConsumedUntilUtc);
    }

    [Fact]
    public void A_silenced_or_inactive_or_unarmed_rule_has_no_incidents()
    {
        var events = Reads(5, NowUtc.AddHours(-3)).ToList();
        var unarmed = Rule() with { ReportFrom = null };

        Assert.Empty(Run(events, Rule(notify: false)));
        Assert.Empty(Run(events, Rule(active: false)));
        Assert.Empty(Run(events, unarmed));
    }

    [Theory]
    [InlineData(CounterIntelBucket.Window)]
    [InlineData(CounterIntelBucket.Day)]
    [InlineData(CounterIntelBucket.Hour)]
    [InlineData(CounterIntelBucket.Sliding)]
    public void Every_incident_is_also_a_flag_of_the_cockpit(CounterIntelBucket bucket)
    {
        var events = Reads(4, NowUtc.AddMinutes(-20)).ToList();
        var rule = Rule(d => d.Bucket = bucket);

        var incidents = Run(events, rule);
        var flags = CounterIntelRuleEvaluator.EvaluateOne(events, rule, NowLocal);

        Assert.Single(incidents);
        Assert.Contains(flags, f => f.AgentId == incidents[0].AgentId);
    }

    [Fact]
    public void The_floor_is_utc_so_the_repeated_hour_of_a_clock_change_stays_apart()
    {
        // both events read 02:30 on the wall clock; only the second one lies after the report
        var first = new CounterIntelEvent
        {
            AgentId = "a1", LocalTimestamp = NowLocal.Date.AddHours(-21.5), TimestampUtc = NowUtc.AddHours(-24),
            EntityType = "Person", EntityId = "p1", Action = CounterIntelActionKind.Read,
        };
        var second = first with { TimestampUtc = first.TimestampUtc.AddHours(1), EntityId = "p2" };
        var ledger = Ledger(Row("r1", "a1", first.TimestampUtc, NowUtc.AddHours(-23)));

        var rule = Rule(d =>
        {
            d.Threshold = 1;
            d.Bucket = CounterIntelBucket.Day;
        });

        var incident = Assert.Single(Run([first, second], rule, ledger));

        Assert.Equal(second.TimestampUtc, incident.ConsumedUntilUtc);
    }

    // ==================== re-arming ====================

    private static CounterIntelRuleInput Input(Action<CounterIntelRuleDefinition>? configure = null,
        bool active = true, bool notify = true)
    {
        var definition = new CounterIntelRuleDefinition { WindowDays = 30, Threshold = 3 };
        configure?.Invoke(definition);
        return new CounterIntelRuleInput { Name = "Neu benannt", IsActive = active, Notify = notify, Definition = definition };
    }

    private static readonly string Stored = new CounterIntelRuleDefinition { WindowDays = 30, Threshold = 3 }.ToJson();

    [Fact]
    public void A_changed_definition_re_arms()
        => Assert.True(FindingWatchRules.Rearms(Stored, true, true, Input(d => d.Threshold = 4)));

    [Fact]
    public void The_same_definition_does_not_re_arm()
        => Assert.False(FindingWatchRules.Rearms(Stored, true, true, Input()));

    [Fact]
    public void Switching_on_activity_or_the_bell_re_arms()
    {
        Assert.True(FindingWatchRules.Rearms(Stored, false, true, Input()));
        Assert.True(FindingWatchRules.Rearms(Stored, true, false, Input()));
        Assert.False(FindingWatchRules.Rearms(Stored, true, true, Input(active: false, notify: false)));
    }

    [Fact]
    public void A_stored_definition_in_another_spelling_is_compared_by_meaning()
    {
        // what a migration seeds is not what the serializer writes
        const string seeded = "{ \"windowDays\": 30,  \"threshold\": 3 }";
        var parsed = CounterIntelRuleDefinition.TryParse(seeded);
        Assert.NotNull(parsed);

        Assert.False(FindingWatchRules.Rearms(seeded, true, true, new CounterIntelRuleInput
        {
            Name = "x", Definition = parsed!.Clone(),
        }));
    }

    // ==================== NOOSEI ====================

    private static readonly DateTime Week = new(2026, 10, 11, 22, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void A_noosei_anomaly_rings_once_a_week_and_again_when_it_gets_worse()
    {
        Assert.True(FindingWatchRules.ShouldReport(null, CounterIntelSeverity.Warning, Week));

        var reported = Row("Burst", "a1", Week, NowUtc, CounterIntelSeverity.Warning, FindingSource.Noosei);
        Assert.False(FindingWatchRules.ShouldReport(reported, CounterIntelSeverity.Warning, Week));
        Assert.True(FindingWatchRules.ShouldReport(reported, CounterIntelSeverity.High, Week));

        var lastWeek = Row("Burst", "a1", Week.AddDays(-7), NowUtc.AddDays(-7), CounterIntelSeverity.High,
            FindingSource.Noosei);
        Assert.True(FindingWatchRules.ShouldReport(lastWeek, CounterIntelSeverity.Warning, Week));
    }

    [Fact]
    public void A_baseline_row_is_known_too()
    {
        var baseline = Row("Burst", "a1", Week, null, CounterIntelSeverity.High, FindingSource.Noosei);

        Assert.False(FindingWatchRules.ShouldReport(baseline, CounterIntelSeverity.High, Week));
        Assert.False(FindingWatchRules.ShouldReport(baseline, CounterIntelSeverity.Warning, Week));
    }

    [Fact]
    public void The_level_never_drops_within_a_week()
    {
        var high = Row("BurnRate", "a1", Week, NowUtc, CounterIntelSeverity.High, FindingSource.Noosei);

        Assert.Equal(CounterIntelSeverity.High, FindingWatchRules.NextGrade(high, CounterIntelSeverity.Warning, Week));
        Assert.Equal(CounterIntelSeverity.Warning,
            FindingWatchRules.NextGrade(high, CounterIntelSeverity.Warning, Week.AddDays(7)));
        Assert.Equal(CounterIntelSeverity.Warning, FindingWatchRules.NextGrade(null, CounterIntelSeverity.Warning, Week));
    }

    // ==================== unseen ====================

    [Fact]
    public void Own_findings_never_count_as_new()
    {
        var rule = Rule();
        var own = Row("r1", "lead", NowUtc.AddHours(-2), NowUtc.AddHours(-1));

        Assert.False(FindingWatchRules.IsUnseenRule(own, "lead", null, rule, NowUtc));
        Assert.True(FindingWatchRules.IsUnseenRule(own, "other", null, rule, NowUtc));
    }

    [Fact]
    public void A_rule_finding_is_new_until_the_viewer_looked_after_it()
    {
        var rule = Rule();
        var row = Row("r1", "a1", NowUtc.AddHours(-2), NowUtc.AddHours(-1));

        Assert.True(FindingWatchRules.IsUnseenRule(row, "lead", NowUtc.AddHours(-3), rule, NowUtc));
        Assert.False(FindingWatchRules.IsUnseenRule(row, "lead", NowUtc.AddHours(-1), rule, NowUtc));
        Assert.False(FindingWatchRules.IsUnseenRule(row, "lead", NowUtc, rule, NowUtc));
    }

    [Fact]
    public void A_rule_finding_the_cockpit_cannot_show_any_more_does_not_count()
    {
        var row = Row("r1", "a1", NowUtc.AddDays(-40), NowUtc.AddDays(-35));
        var baseline = Row("r1", "a1", NowUtc.AddHours(-2));

        Assert.False(FindingWatchRules.IsUnseenRule(row, "lead", null, Rule(reportFrom: NowUtc.AddDays(-60)), NowUtc));
        Assert.False(FindingWatchRules.IsUnseenRule(Row("r1", "a1", NowUtc, NowUtc), "lead", null, null, NowUtc));
        Assert.False(FindingWatchRules.IsUnseenRule(Row("r1", "a1", NowUtc, NowUtc), "lead", null, Rule(active: false), NowUtc));
        Assert.False(FindingWatchRules.IsUnseenRule(baseline, "lead", null, Rule(), NowUtc));
    }

    [Fact]
    public void A_report_from_before_the_rule_was_re_armed_does_not_count()
    {
        var row = Row("r1", "a1", NowUtc.AddHours(-3), NowUtc.AddHours(-2));

        Assert.False(FindingWatchRules.IsUnseenRule(row, "lead", null, Rule(reportFrom: NowUtc.AddHours(-1)), NowUtc));
    }

    [Fact]
    public void A_noosei_finding_counts_only_in_its_week()
    {
        var thisWeek = Row("Burst", "a1", Week, NowUtc, source: FindingSource.Noosei);
        var lastWeek = Row("Burst", "a2", Week.AddDays(-7), NowUtc, source: FindingSource.Noosei);

        Assert.True(FindingWatchRules.IsUnseenNoosei(thisWeek, "lead", null, Week));
        Assert.False(FindingWatchRules.IsUnseenNoosei(lastWeek, "lead", null, Week));
        Assert.False(FindingWatchRules.IsUnseenNoosei(thisWeek, "a1", null, Week));
    }

    // ==================== title ====================

    [Fact]
    public void The_title_names_count_and_rules_never_an_agent()
    {
        Assert.Equal("Gegenaufklärung: 1 neue Auffälligkeit (Massen-Zugriff)",
            FindingWatchRules.Title(FindingSource.Rule, 1, ["Massen-Zugriff"]));
        Assert.Equal("NOOSEI: 2 neue Auffälligkeiten (Burst, Verbrauchsrate)",
            FindingWatchRules.Title(FindingSource.Noosei, 2, ["Burst", "Verbrauchsrate"]));
    }

    [Fact]
    public void The_title_shortens_a_long_rule_list()
    {
        var title = FindingWatchRules.Title(FindingSource.Rule, 5, ["A", "B", "C", "D", "E"]);

        Assert.Equal("Gegenaufklärung: 5 neue Auffälligkeiten (A, B, C und 2 weitere)", title);
    }

    [Fact]
    public void The_title_fits_the_column()
    {
        var title = FindingWatchRules.Title(FindingSource.Rule, 3, [new string('x', 200), new string('y', 200), "z"]);

        Assert.Equal(300, title.Length);
        Assert.EndsWith("…", title);
    }
}
