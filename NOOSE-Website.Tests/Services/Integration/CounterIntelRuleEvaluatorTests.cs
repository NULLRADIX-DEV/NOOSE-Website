using System.Runtime.CompilerServices;
using NOOSE_Website.Models.CounterIntel;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Tests for the rule engine: filter categories, their combination, buckets and count modes.</summary>
public sealed class CounterIntelRuleEvaluatorTests
{
    private static readonly DateTime Now = new(2026, 8, 10, 12, 0, 0);

    private static CounterIntelEvent Event(
        DateTime when, string agent = "a1", string type = "Person", string id = "p1",
        CounterIntelActionKind action = CounterIntelActionKind.Read,
        bool? classified = null, Classification? level = null, IReadOnlyCollection<string>? tags = null,
        Rank? rank = null, bool tru = false, bool hrb = false, bool admin = false, PartnerAgency? partner = null)
        => new()
        {
            AgentId = agent,
            AgentName = agent.ToUpperInvariant(),
            LocalTimestamp = when,
            EntityType = type,
            EntityId = id,
            Action = action,
            TargetIsClassified = classified,
            TargetClassification = level,
            TargetTagIds = tags,
            ActorRank = rank,
            ActorIsTru = tru,
            ActorIsHrb = hrb,
            ActorIsAdmin = admin,
            ActorPartnerAgency = partner,
        };

    private static CounterIntelRuleView Rule(CounterIntelRuleDefinition definition, string name = "Regel", bool active = true)
        => new("r1", name, null, CounterIntelSeverity.Warning, active, 0, definition);

    private static CounterIntelRuleDefinition Definition(Action<CounterIntelRuleDefinition>? configure = null)
    {
        var d = new CounterIntelRuleDefinition { WindowDays = 30, Threshold = 3 };
        configure?.Invoke(d);
        return d;
    }

    private static List<InsiderFlag> Run(CounterIntelRuleDefinition definition, IEnumerable<CounterIntelEvent> events)
        => CounterIntelRuleEvaluator.Evaluate(events.ToList(), [Rule(definition)], Now);

    // ==================== baseline ====================

    [Fact]
    public void Flags_WhenThresholdReached()
    {
        var events = Enumerable.Range(0, 3).Select(i => Event(Now.AddMinutes(-i), id: $"p{i}"));

        var flag = Assert.Single(Run(Definition(), events));
        Assert.Equal("a1", flag.AgentId);
        Assert.Equal("Regel", flag.Rule);
        Assert.Equal(3, flag.Severity);
    }

    [Fact]
    public void DoesNotFlag_BelowThreshold()
        => Assert.Empty(Run(Definition(), Enumerable.Range(0, 2).Select(i => Event(Now.AddMinutes(-i), id: $"p{i}"))));

    [Fact]
    public void Ignores_InactiveRule()
    {
        var events = Enumerable.Range(0, 5).Select(i => Event(Now.AddMinutes(-i), id: $"p{i}")).ToList();

        Assert.Empty(CounterIntelRuleEvaluator.Evaluate(events, [Rule(Definition(), active: false)], Now));
    }

    [Fact]
    public void Ignores_EventsOutsideTheWindow()
    {
        var events = Enumerable.Range(0, 5).Select(i => Event(Now.AddDays(-40).AddMinutes(-i), id: $"p{i}"));

        Assert.Empty(Run(Definition(d => d.WindowDays = 7), events));
    }

    [Fact]
    public void FlagsEachAgentSeparately()
    {
        var events = new[] { "a1", "a2" }
            .SelectMany(a => Enumerable.Range(0, 3).Select(i => Event(Now.AddMinutes(-i), agent: a, id: $"p{i}")));

        Assert.Equal(2, Run(Definition(), events).Count);
    }

    // ==================== filter categories ====================

    [Fact]
    public void Action_EmptyListMatchesEverything()
    {
        var events = new[]
        {
            Event(Now, id: "p1", action: CounterIntelActionKind.Read),
            Event(Now, id: "p2", action: CounterIntelActionKind.Deleted),
            Event(Now, id: "p3", action: CounterIntelActionKind.Modified),
        };

        Assert.Single(Run(Definition(), events));
    }

    [Fact]
    public void Action_ListIsOr_AndExcludesTheRest()
    {
        var events = new[]
        {
            Event(Now, id: "p1", action: CounterIntelActionKind.Deleted),
            Event(Now, id: "p2", action: CounterIntelActionKind.Modified),
            Event(Now, id: "p3", action: CounterIntelActionKind.Read),
            Event(Now, id: "p4", action: CounterIntelActionKind.Read),
        };
        var definition = Definition(d =>
        {
            d.Actions = [CounterIntelActionKind.Deleted, CounterIntelActionKind.Modified];
            d.Threshold = 2;
        });

        var flag = Assert.Single(Run(definition, events));
        Assert.Equal(2, flag.Severity); // the two reads did not count
    }

    [Fact]
    public void EntityType_Filters()
    {
        var events = new[]
        {
            Event(Now, type: "Person", id: "p1"),
            Event(Now, type: "Person", id: "p2"),
            Event(Now, type: "Faction", id: "f1"),
        };

        Assert.Empty(Run(Definition(d => d.EntityTypes = ["Faction"]), events));
        Assert.Single(Run(Definition(d => { d.EntityTypes = ["Person"]; d.Threshold = 2; }), events));
    }

    [Fact]
    public void EntityIds_FilterToBaitRecords()
    {
        var events = Enumerable.Range(0, 4).Select(i => Event(Now.AddMinutes(-i), id: i < 2 ? "bait" : $"p{i}"));

        var definition = Definition(d =>
        {
            d.EntityIds = ["bait"];
            d.Threshold = 2;
        });
        Assert.Single(Run(definition, events));
    }

    [Fact]
    public void ClassifiedOnly_RequiresAResolvedTarget()
    {
        var classified = Enumerable.Range(0, 3).Select(i => Event(Now.AddMinutes(-i), id: $"c{i}", classified: true));
        var unknown = Enumerable.Range(0, 3).Select(i => Event(Now.AddMinutes(-i), id: $"u{i}"));

        Assert.Single(Run(Definition(d => d.ClassifiedOnly = true), classified));
        Assert.Empty(Run(Definition(d => d.ClassifiedOnly = true), unknown));
        Assert.Empty(Run(Definition(d => d.ClassifiedOnly = false), classified));
    }

    [Fact]
    public void Classification_ListIsOr()
    {
        var events = new[]
        {
            Event(Now, id: "p1", level: Classification.SuspicionCase),
            Event(Now, id: "p2", level: Classification.SecuredStateThreatening),
            Event(Now, id: "p3", level: Classification.ReviewCase),
        };
        var definition = Definition(d =>
        {
            d.Classifications = [Classification.SuspicionCase, Classification.SecuredStateThreatening];
            d.Threshold = 2;
        });

        Assert.Single(Run(definition, events));
    }

    [Fact]
    public void Tags_MatchOnAnyOverlap()
    {
        var events = new[]
        {
            Event(Now, id: "p1", tags: ["t1"]),
            Event(Now, id: "p2", tags: ["t2", "t9"]),
            Event(Now, id: "p3", tags: ["t8"]),
        };
        var definition = Definition(d =>
        {
            d.TagIds = ["t1", "t2"];
            d.Threshold = 2;
        });

        Assert.Single(Run(definition, events));
    }

    [Fact]
    public void ActorRanks_Filter()
    {
        var events = Enumerable.Range(0, 3).Select(i => Event(Now.AddMinutes(-i), id: $"p{i}", rank: Rank.Trainee));

        Assert.Single(Run(Definition(d => d.ActorRanks = [Rank.Trainee, Rank.SpecialAgent]), events));
        Assert.Empty(Run(Definition(d => d.ActorRanks = [Rank.Director]), events));
    }

    [Fact]
    public void ActorIds_AndExclusions()
    {
        var events = new[] { "a1", "a2" }
            .SelectMany(a => Enumerable.Range(0, 3).Select(i => Event(Now.AddMinutes(-i), agent: a, id: $"p{i}")))
            .ToList();

        Assert.Equal("a1", Assert.Single(Run(Definition(d => d.ActorIds = ["a1"]), events)).AgentId);
        Assert.Equal("a2", Assert.Single(Run(Definition(d => d.ExcludedActorIds = ["a1"]), events)).AgentId);
    }

    [Fact]
    public void Flags_AreTriState()
    {
        var tru = Enumerable.Range(0, 3).Select(i => Event(Now.AddMinutes(-i), id: $"p{i}", tru: true)).ToList();

        Assert.Single(Run(Definition(), tru));                              // null: don't care
        Assert.Single(Run(Definition(d => d.RequireTru = true), tru));      // must carry it
        Assert.Empty(Run(Definition(d => d.RequireTru = false), tru));      // must not
        Assert.Empty(Run(Definition(d => d.RequireHrb = true), tru));
    }

    [Fact]
    public void PartnerScope_SplitsInternalFromPartners()
    {
        var internals = Enumerable.Range(0, 3).Select(i => Event(Now.AddMinutes(-i), id: $"p{i}")).ToList();
        var partners = Enumerable.Range(0, 3)
            .Select(i => Event(Now.AddMinutes(-i), agent: "p1", id: $"p{i}", partner: PartnerAgency.LSPD)).ToList();

        Assert.Single(Run(Definition(d => d.PartnerScope = CounterIntelPartnerScope.InternalOnly), internals));
        Assert.Empty(Run(Definition(d => d.PartnerScope = CounterIntelPartnerScope.PartnersOnly), internals));
        Assert.Single(Run(Definition(d => d.PartnerScope = CounterIntelPartnerScope.PartnersOnly), partners));
    }

    [Fact]
    public void Weekdays_Filter()
    {
        var day = Now.Date.AddHours(12);
        var events = Enumerable.Range(0, 3).Select(i => Event(day.AddMinutes(-i), id: $"p{i}")).ToList();
        var other = day.DayOfWeek == DayOfWeek.Sunday ? DayOfWeek.Monday : DayOfWeek.Sunday;

        Assert.Single(Run(Definition(d => d.Weekdays = [day.DayOfWeek]), events));
        Assert.Empty(Run(Definition(d => d.Weekdays = [other]), events));
    }

    // ==================== hour window ====================

    [Theory]
    [InlineData(23, 22, 6, true)]
    [InlineData(2, 22, 6, true)]
    [InlineData(6, 22, 6, false)]
    [InlineData(12, 22, 6, false)]
    [InlineData(9, 8, 17, true)]
    [InlineData(17, 8, 17, false)]
    [InlineData(7, 8, 17, false)]
    public void HourWindow_HandlesMidnightWrap(int hour, int from, int to, bool expected)
        => Assert.Equal(expected, CounterIntelRuleEvaluator.InHourWindow(hour, from, to));

    [Fact]
    public void HourWindow_EqualBoundsMeansAllDay()
    {
        var events = new[] { 3, 12, 23 }.Select(h => Event(Now.Date.AddHours(h), id: $"p{h}"));

        Assert.Single(Run(Definition(d => { d.FromHour = 0; d.ToHour = 0; }), events));
    }

    [Fact]
    public void HourWindow_NightRuleSkipsDaytime()
    {
        var events = new[] { 23, 1, 3, 12, 13, 14 }.Select(h => Event(Now.Date.AddHours(h), id: $"p{h}"));

        var flag = Assert.Single(Run(Definition(d => { d.FromHour = 22; d.ToHour = 6; }), events));
        Assert.Equal(3, flag.Severity);
    }

    // ==================== count modes and buckets ====================

    [Fact]
    public void CountMode_DistinctRecordsCollapsesRepeats()
    {
        var events = Enumerable.Range(0, 10).Select(i => Event(Now.AddMinutes(-i), id: "same")).ToList();

        Assert.Single(Run(Definition(d => d.CountMode = CounterIntelCountMode.Events), events));
        Assert.Empty(Run(Definition(d => d.CountMode = CounterIntelCountMode.DistinctRecords), events));
    }

    [Fact]
    public void Bucket_DayIsolatesCalendarDays()
    {
        // two per day over three days: 6 in the window, never 3 on one day
        var events = Enumerable.Range(0, 3)
            .SelectMany(d => Enumerable.Range(0, 2)
                .Select(i => Event(Now.Date.AddDays(-d).AddHours(10 + i), id: $"p{d}{i}")))
            .ToList();

        Assert.Single(Run(Definition(d => d.Bucket = CounterIntelBucket.Window), events));
        Assert.Empty(Run(Definition(d => d.Bucket = CounterIntelBucket.Day), events));
    }

    [Fact]
    public void Bucket_HourIsolatesClockHours()
    {
        var spread = Enumerable.Range(0, 3).Select(i => Event(Now.Date.AddHours(9 + i), id: $"p{i}")).ToList();
        var burst = Enumerable.Range(0, 3).Select(i => Event(Now.Date.AddHours(9).AddMinutes(i * 5), id: $"p{i}")).ToList();

        Assert.Empty(Run(Definition(d => d.Bucket = CounterIntelBucket.Hour), spread));
        Assert.Single(Run(Definition(d => d.Bucket = CounterIntelBucket.Hour), burst));
    }

    [Fact]
    public void Bucket_SlidingCatchesWhatClockHoursMiss()
    {
        // 10:50, 10:55, 11:05 — never three inside one clock hour, but three inside 30 minutes
        var events = new[]
        {
            Event(Now.Date.AddHours(10).AddMinutes(50), id: "p1"),
            Event(Now.Date.AddHours(10).AddMinutes(55), id: "p2"),
            Event(Now.Date.AddHours(11).AddMinutes(5), id: "p3"),
        };

        Assert.Empty(Run(Definition(d => d.Bucket = CounterIntelBucket.Hour), events));
        Assert.Single(Run(Definition(d => { d.Bucket = CounterIntelBucket.Sliding; d.SlidingMinutes = 30; }), events));
    }

    [Fact]
    public void Bucket_SlidingRespectsItsWidth()
    {
        var events = new[]
        {
            Event(Now.Date.AddHours(10), id: "p1"),
            Event(Now.Date.AddHours(11), id: "p2"),
            Event(Now.Date.AddHours(12), id: "p3"),
        };

        Assert.Empty(Run(Definition(d => { d.Bucket = CounterIntelBucket.Sliding; d.SlidingMinutes = 30; }), events));
        Assert.Single(Run(Definition(d => { d.Bucket = CounterIntelBucket.Sliding; d.SlidingMinutes = 180; }), events));
    }

    [Fact]
    public void Bucket_SlidingCountsDistinctRecordsToo()
    {
        // six events within the hour, but ids p0/p1 repeat → five distinct records
        var events = Enumerable.Range(0, 6).Select(i => Event(Now.AddMinutes(-i), id: i < 2 ? "same" : $"p{i}")).ToList();

        CounterIntelRuleDefinition Sliding(int threshold) => Definition(d =>
        {
            d.Bucket = CounterIntelBucket.Sliding;
            d.SlidingMinutes = 60;
            d.CountMode = CounterIntelCountMode.DistinctRecords;
            d.Threshold = threshold;
        });

        Assert.Single(Run(Sliding(5), events));
        Assert.Empty(Run(Sliding(6), events));
    }

    // ==================== combination ====================

    [Fact]
    public void Categories_CombineWithAnd()
    {
        var events = new[]
        {
            // matches everything
            Event(Now.Date.AddHours(23), id: "p1", classified: true, rank: Rank.Trainee),
            Event(Now.Date.AddHours(23).AddMinutes(1), id: "p2", classified: true, rank: Rank.Trainee),
            // right time, wrong classification
            Event(Now.Date.AddHours(23).AddMinutes(2), id: "p3", classified: false, rank: Rank.Trainee),
            // right classification, wrong hour
            Event(Now.Date.AddHours(12), id: "p4", classified: true, rank: Rank.Trainee),
            // right everything, wrong rank
            Event(Now.Date.AddHours(23).AddMinutes(3), id: "p5", classified: true, rank: Rank.Director),
        };
        var definition = Definition(d =>
        {
            d.ClassifiedOnly = true;
            d.ActorRanks = [Rank.Trainee];
            d.FromHour = 22;
            d.ToHour = 6;
            d.Threshold = 2;
        });

        var flag = Assert.Single(Run(definition, events));
        Assert.Equal(2, flag.Severity);
    }

    [Fact]
    public void Evaluate_SortsWorstSeverityFirst()
    {
        var events = Enumerable.Range(0, 5).Select(i => Event(Now.AddMinutes(-i), id: $"p{i}")).ToList();
        var rules = new[]
        {
            new CounterIntelRuleView("mild", "Mild", null, CounterIntelSeverity.Info, true, 0, Definition()),
            new CounterIntelRuleView("bad", "Kritisch", null, CounterIntelSeverity.Critical, true, 1, Definition()),
        };

        var flags = CounterIntelRuleEvaluator.Evaluate(events, rules, Now);

        Assert.Equal("Kritisch", flags[0].Rule);
        Assert.Equal(CounterIntelSeverity.Critical, flags[0].Grade);
    }

    // ==================== seeded defaults ====================

    [Fact]
    public void Defaults_ReproduceTheOriginalOffHoursRule()
    {
        var rule = CounterIntelRuleDefaults.All.Single(r => r.Name == "Off-Hours");
        var at2am = Now.Date.AddHours(2);
        var events = Enumerable.Range(0, 20).Select(i => Event(at2am.AddSeconds(i), id: $"p{i}")).ToList();

        Assert.Single(CounterIntelRuleEvaluator.Evaluate(events, [rule], Now));
    }

    [Fact]
    public void Defaults_ReproduceTheOriginalMassAccessRule()
    {
        var rule = CounterIntelRuleDefaults.All.Single(r => r.Name == "Massen-Zugriff");
        var noon = Now.Date.AddHours(12);
        var events = Enumerable.Range(0, 45).Select(i => Event(noon.AddSeconds(i), id: $"p{i}")).ToList();

        Assert.Single(CounterIntelRuleEvaluator.Evaluate(events, [rule], Now));
    }

    [Fact]
    public void Defaults_BurstFiresWhereMassAccessDoesNot()
    {
        var noon = Now.Date.AddHours(12);
        var repeats = Enumerable.Range(0, 32).Select(i => Event(noon.AddSeconds(i), id: "same")).ToList();

        var flags = CounterIntelRuleEvaluator.Evaluate(repeats, CounterIntelRuleDefaults.All, Now);

        Assert.Contains(flags, f => f.Rule == "Zugriffs-Burst");
        Assert.DoesNotContain(flags, f => f.Rule == "Massen-Zugriff");
    }

    [Fact]
    public void Defaults_CleanUsageFlagsNothing()
    {
        var noon = Now.Date.AddHours(12);
        var events = Enumerable.Range(0, 5).Select(i => Event(noon.AddMinutes(i), id: $"p{i}")).ToList();

        Assert.Empty(CounterIntelRuleEvaluator.Evaluate(events, CounterIntelRuleDefaults.All, Now));
    }

    [Fact]
    public void Defaults_SurviveAJsonRoundTrip()
    {
        foreach (var rule in CounterIntelRuleDefaults.All)
        {
            var parsed = CounterIntelRuleDefinition.TryParse(rule.Definition.ToJson());

            Assert.NotNull(parsed);
            Assert.Equal(rule.Definition.Threshold, parsed!.Threshold);
            Assert.Equal(rule.Definition.Bucket, parsed.Bucket);
            Assert.Equal(rule.Definition.CountMode, parsed.CountMode);
            Assert.Equal(rule.Definition.FromHour, parsed.FromHour);
            Assert.Equal(rule.Definition.ToHour, parsed.ToHour);
            Assert.Equal(rule.Definition.Actions, parsed.Actions);
        }
    }

    [Fact]
    public void TryParse_ReturnsNullForGarbage()
    {
        Assert.Null(CounterIntelRuleDefinition.TryParse("not json"));
        Assert.Null(CounterIntelRuleDefinition.TryParse(null));
        Assert.Null(CounterIntelRuleDefinition.TryParse("   "));
    }
    // ==================== organisation of actor and target ====================

    private static CounterIntelEvent TipEvent(
        DateTime when, string agent = "a1", string id = "h1", bool? shares = null,
        bool citizen = true, bool withheld = false)
        => new()
        {
            AgentId = agent,
            AgentName = agent.ToUpperInvariant(),
            LocalTimestamp = when,
            EntityType = "Hinweis",
            EntityId = id,
            Action = CounterIntelActionKind.Created,
            ActorSharesOrgWithTarget = shares,
            ActorHasNoPersonnelFile = citizen,
            ActorIdentityWithheld = withheld,
        };

    [Fact]
    public void OrgCondition_FlagsWhenBothSidesShareOne()
    {
        var events = Enumerable.Range(0, 3).Select(i => TipEvent(Now.AddMinutes(-i), id: $"h{i}", shares: true));

        Assert.Single(Run(Definition(d => d.ActorSharesOrgWithTarget = true), events));
    }

    [Fact]
    public void OrgCondition_DoesNotFlagWhenTheyShareNone()
    {
        var events = Enumerable.Range(0, 3).Select(i => TipEvent(Now.AddMinutes(-i), id: $"h{i}", shares: false));

        Assert.Empty(Run(Definition(d => d.ActorSharesOrgWithTarget = true), events));
    }

    [Fact]
    public void OrgCondition_FailsClosedOnAnUnresolvedSide()
    {
        // no civilian profile, no notice behind the tip: the condition cannot be satisfied, so it must not fire
        var events = Enumerable.Range(0, 3).Select(i => TipEvent(Now.AddMinutes(-i), id: $"h{i}", shares: null));

        Assert.Empty(Run(Definition(d => d.ActorSharesOrgWithTarget = true), events));
    }

    [Fact]
    public void OrgCondition_CanAlsoDemandTheAbsenceOfAnOverlap()
    {
        var events = Enumerable.Range(0, 3).Select(i => TipEvent(Now.AddMinutes(-i), id: $"h{i}", shares: false));

        Assert.Single(Run(Definition(d => d.ActorSharesOrgWithTarget = false), events));
    }

    [Fact]
    public void OrgCondition_IsIgnoredWhenTheRuleDoesNotAskForIt()
    {
        var events = Enumerable.Range(0, 3).Select(i => TipEvent(Now.AddMinutes(-i), id: $"h{i}", shares: null));

        Assert.Single(Run(Definition(), events));
    }

    [Fact]
    public void AnAnonymousTipsterIsNeverNamed()
    {
        var events = Enumerable.Range(0, 3)
            .Select(i => TipEvent(Now.AddMinutes(-i), id: $"h{i}", shares: true, withheld: true));

        var flag = Assert.Single(Run(Definition(d => d.ActorSharesOrgWithTarget = true), events));

        Assert.Equal("Anonymer Hinweisgeber", flag.AgentName);
        Assert.Null(flag.Href);
    }

    [Fact]
    public void OneNamedTipAmongAnonymousOnesNamesTheAccount()
    {
        var events = new[]
        {
            TipEvent(Now.AddMinutes(-1), id: "h1", shares: true, withheld: true),
            TipEvent(Now.AddMinutes(-2), id: "h2", shares: true, withheld: true),
            TipEvent(Now.AddMinutes(-3), id: "h3", shares: true, withheld: false),
        };

        var flag = Assert.Single(Run(Definition(d => d.ActorSharesOrgWithTarget = true), events));

        Assert.Equal("A1", flag.AgentName);
        Assert.Equal("/einstellungen?tab=buerger", flag.Href);
    }

    [Fact]
    public void AnAgentKeepsThePersonnelLink()
    {
        var events = Enumerable.Range(0, 3).Select(i => Event(Now.AddMinutes(-i), id: $"p{i}"));

        var flag = Assert.Single(Run(Definition(), events));

        Assert.Equal("/personal/a1", flag.Href);
    }

    [Fact]
    public void Defaults_ReproduceTheOwnCircleRule()
    {
        var rule = CounterIntelRuleDefaults.All.Single(r => r.Name == "Hinweisgeber im eigenen Umfeld");

        var fires = CounterIntelRuleEvaluator.Evaluate(
            [TipEvent(Now.AddHours(-1), shares: true)], [rule], Now);
        var silent = CounterIntelRuleEvaluator.Evaluate(
            [TipEvent(Now.AddHours(-1), shares: false)], [rule], Now);

        Assert.Single(fires);
        Assert.Empty(silent);
    }

    [Fact]
    public void Defaults_TheOwnCircleRuleIgnoresOrdinaryAccess()
    {
        var rule = CounterIntelRuleDefaults.All.Single(r => r.Name == "Hinweisgeber im eigenen Umfeld");
        var reads = Enumerable.Range(0, 50).Select(i => Event(Now.AddMinutes(-i), id: $"p{i}")).ToList();

        Assert.Empty(CounterIntelRuleEvaluator.Evaluate(reads, [rule], Now));
    }

    [Fact]
    public void Defaults_KeepTheOrgConditionUnsetOnTheOlderRules()
    {
        // the seeded JSON of the first three rules does not carry the property; a non-null default would change them
        foreach (var rule in CounterIntelRuleDefaults.All.Where(r => r.Name != "Hinweisgeber im eigenen Umfeld"))
        {
            Assert.Null(rule.Definition.ActorSharesOrgWithTarget);
        }
        Assert.Null(new CounterIntelRuleDefinition().ActorSharesOrgWithTarget);
    }

    [Fact]
    public void TheSeededOwnCircleRule_MatchesTheCodeDefault()
    {
        // the migration carries the definition as a literal; parsing it back must yield the same rule
        var expected = CounterIntelRuleDefaults.All.Single(r => r.Id == CounterIntelRuleDefaults.OwnCircleId).Definition;
        var seeded = SeededDefinitionJson();

        var parsed = CounterIntelRuleDefinition.TryParse(seeded);

        Assert.NotNull(parsed);
        Assert.Equal(expected.ToJson(), parsed!.ToJson());
    }

    private static string SeededDefinitionJson([CallerFilePath] string here = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "..", "NOOSE-Website"));
        var file = Directory.EnumerateFiles(Path.Combine(root, "Data", "Migrations"),
            "*Oeffentlich08_HinweisUebernahme.cs").Single();
        var text = File.ReadAllText(file);
        var start = text.IndexOf("{\\\"WindowDays", StringComparison.Ordinal);
        // the closing quote of the C# literal, not the escaped one right after the brace
        var end = text.IndexOf("}\"", start, StringComparison.Ordinal) + 1;
        return text[start..end].Replace("\\\"", "\"");
    }
}
