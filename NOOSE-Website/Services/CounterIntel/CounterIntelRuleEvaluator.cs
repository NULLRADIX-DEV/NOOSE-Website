using NOOSE_Website.Models.CounterIntel;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>
/// Runs leadership-defined rules over enriched log events. Pure: no DB, no clock, no config —
/// everything it needs arrives as arguments, so every combination is testable.
/// </summary>
/// <remarks>
/// Condition semantics are uniform: within a category OR, between categories AND, empty list = no
/// restriction. A rule flags an agent once, on that agent's worst bucket.
/// </remarks>
public static class CounterIntelRuleEvaluator
{
    /// <summary>Guard against a runaway rule list dragging every page load down.</summary>
    public const int MaxRules = 50;

    /// <summary>Evaluates every rule and returns the flags, worst severity first.</summary>
    public static List<InsiderFlag> Evaluate(
        IReadOnlyList<CounterIntelEvent> events,
        IReadOnlyList<CounterIntelRuleView> rules,
        DateTime nowLocal)
    {
        var flags = new List<InsiderFlag>();
        foreach (var rule in rules.Where(r => r.IsActive).OrderBy(r => r.Order).Take(MaxRules))
        {
            flags.AddRange(EvaluateOne(events, rule, nowLocal));
        }
        return flags
            .OrderByDescending(f => f.Grade)
            .ThenByDescending(f => f.Severity)
            .ThenBy(f => f.AgentName)
            .ToList();
    }

    /// <summary>Evaluates a single rule; exposed so the editor can preview before saving.</summary>
    public static List<InsiderFlag> EvaluateOne(
        IReadOnlyList<CounterIntelEvent> events,
        CounterIntelRuleView rule,
        DateTime nowLocal)
    {
        var definition = rule.Definition;
        var since = WindowStart(definition, nowLocal);
        var threshold = Math.Max(1, definition.Threshold);

        var flags = new List<InsiderFlag>();
        var matching = events.Where(e => e.LocalTimestamp >= since && Matches(definition, e));

        foreach (var group in matching.GroupBy(e => e.AgentId))
        {
            var rows = group.ToList();
            var peak = Peak(definition, rows);
            if (peak.Count < threshold)
            {
                continue;
            }
            flags.Add(new InsiderFlag(
                group.Key, Subject(rows), rule.Name,
                CounterIntelRuleDisplay.Detail(definition, peak.Count, peak.At),
                peak.Count, Href(rows, group.Key), rule.Id, rule.Severity));
        }
        return flags;
    }

    /// <summary>Earliest local instant the rule still looks at.</summary>
    public static DateTime WindowStart(CounterIntelRuleDefinition definition, DateTime nowLocal)
        => nowLocal.AddDays(-WindowDays(definition));

    /// <summary>The rule's observation window in days, clamped to what the loader reads.</summary>
    public static int WindowDays(CounterIntelRuleDefinition definition)
        => Math.Clamp(definition.WindowDays, 1, CounterIntelRuleDefinition.MaxWindowDays);

    /// <summary>Names the flagged account — unless every counted event is a tip whose anonymity still holds.</summary>
    /// <remarks>
    /// The cockpit must not become the back door around the audited leadership resolution: it reports the pattern, and
    /// the name still comes only that way. The actionable pointer is the conflict chip on the tip itself.
    /// </remarks>
    private static string Subject(List<CounterIntelEvent> rows)
    {
        if (rows.All(e => e.ActorIdentityWithheld))
        {
            return "Anonymer Hinweisgeber";
        }
        return rows.Select(e => e.AgentName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "(unbenannt)";
    }

    // neither a citizen nor an applicant has a personnel file; withheld identity gets no link at all
    private static string? Href(List<CounterIntelEvent> rows, string agentId)
    {
        if (rows.All(e => e.ActorIdentityWithheld))
        {
            return null;
        }
        return rows.Any(e => e.ActorHasNoPersonnelFile) ? "/einstellungen?tab=buerger" : $"/personal/{agentId}";
    }

    /// <summary>True when the event satisfies every condition category of the rule.</summary>
    public static bool Matches(CounterIntelRuleDefinition definition, CounterIntelEvent e)
    {
        if (definition.Actions.Count > 0 && !definition.Actions.Contains(e.Action))
        {
            return false;
        }
        if (definition.EntityTypes.Count > 0 && !definition.EntityTypes.Contains(e.EntityType))
        {
            return false;
        }
        if (definition.EntityIds.Count > 0 && !definition.EntityIds.Contains(e.EntityId))
        {
            return false;
        }
        // an unresolved target cannot satisfy a target condition — record deleted or type not looked up
        if (definition.ClassifiedOnly is { } classified && e.TargetIsClassified != classified)
        {
            return false;
        }
        if (definition.Classifications.Count > 0
            && (e.TargetClassification is not { } level || !definition.Classifications.Contains(level)))
        {
            return false;
        }
        if (definition.TagIds.Count > 0
            && (e.TargetTagIds is null || !e.TargetTagIds.Any(definition.TagIds.Contains)))
        {
            return false;
        }
        if (definition.ActorRanks.Count > 0
            && (e.ActorRank is not { } rank || !definition.ActorRanks.Contains(rank)))
        {
            return false;
        }
        if (definition.ActorIds.Count > 0 && !definition.ActorIds.Contains(e.AgentId))
        {
            return false;
        }
        if (definition.ExcludedActorIds.Contains(e.AgentId))
        {
            return false;
        }
        if (definition.RequireTru is { } tru && e.ActorIsTru != tru)
        {
            return false;
        }
        if (definition.RequireHrb is { } hrb && e.ActorIsHrb != hrb)
        {
            return false;
        }
        if (definition.RequireAdmin is { } admin && e.ActorIsAdmin != admin)
        {
            return false;
        }
        // fail closed like the classification arm: an unresolved side cannot satisfy the condition
        if (definition.ActorSharesOrgWithTarget is { } shared
            && (e.ActorSharesOrgWithTarget is not { } actual || actual != shared))
        {
            return false;
        }
        if (definition.PartnerScope == CounterIntelPartnerScope.InternalOnly && e.ActorPartnerAgency is not null)
        {
            return false;
        }
        if (definition.PartnerScope == CounterIntelPartnerScope.PartnersOnly && e.ActorPartnerAgency is null)
        {
            return false;
        }
        if (!definition.IsAllDay && !InHourWindow(e.LocalTimestamp.Hour, definition.FromHour, definition.ToHour))
        {
            return false;
        }
        if (definition.Weekdays.Count > 0 && !definition.Weekdays.Contains(e.LocalTimestamp.DayOfWeek))
        {
            return false;
        }
        return true;
    }

    /// <summary>Hour inside the daily window; the window may wrap past midnight (22 → 6).</summary>
    public static bool InHourWindow(int hour, int fromHour, int toHour)
        => fromHour < toHour ? hour >= fromHour && hour < toHour : hour >= fromHour || hour < toHour;

    /// <summary>The agent's worst bucket and where it sits.</summary>
    private static (int Count, DateTime? At) Peak(CounterIntelRuleDefinition definition, List<CounterIntelEvent> rows)
        => definition.Bucket switch
        {
            CounterIntelBucket.Day => Best(rows.GroupBy(e => e.LocalTimestamp.Date), definition.CountMode),
            CounterIntelBucket.Hour => Best(
                rows.GroupBy(e => new DateTime(e.LocalTimestamp.Year, e.LocalTimestamp.Month, e.LocalTimestamp.Day, e.LocalTimestamp.Hour, 0, 0)),
                definition.CountMode),
            CounterIntelBucket.Sliding => Sliding(rows, definition.CountMode, definition.SlidingMinutes),
            _ => (Count(rows, definition.CountMode), null),
        };

    private static (int Count, DateTime? At) Best(
        IEnumerable<IGrouping<DateTime, CounterIntelEvent>> buckets, CounterIntelCountMode mode)
    {
        var best = (Count: 0, At: (DateTime?)null);
        foreach (var bucket in buckets)
        {
            var value = Count(bucket, mode);
            if (value > best.Count)
            {
                best = (value, bucket.Key);
            }
        }
        return best;
    }

    // two-pointer over a half-open [t, t+span) window; keeps a record-key multiset for distinct counting
    private static (int Count, DateTime? At) Sliding(List<CounterIntelEvent> rows, CounterIntelCountMode mode, int minutes)
    {
        var span = TimeSpan.FromMinutes(Math.Clamp(minutes, 1, CounterIntelRuleDefinition.MaxSlidingMinutes));
        var ordered = rows.OrderBy(e => e.LocalTimestamp).ToList();
        var live = new Dictionary<string, int>();
        var best = (Count: 0, At: (DateTime?)null);
        var left = 0;

        for (var right = 0; right < ordered.Count; right++)
        {
            var key = ordered[right].RecordKey;
            live[key] = live.GetValueOrDefault(key) + 1;

            while (ordered[right].LocalTimestamp - ordered[left].LocalTimestamp >= span)
            {
                var leaving = ordered[left].RecordKey;
                if (--live[leaving] == 0)
                {
                    live.Remove(leaving);
                }
                left++;
            }

            var value = mode == CounterIntelCountMode.DistinctRecords ? live.Count : right - left + 1;
            if (value > best.Count)
            {
                best = (value, ordered[left].LocalTimestamp);
            }
        }
        return best;
    }

    /// <summary>How much a set of events weighs under the rule's count mode.</summary>
    public static int Count(IEnumerable<CounterIntelEvent> rows, CounterIntelCountMode mode)
        => mode == CounterIntelCountMode.DistinctRecords
            ? rows.Select(e => e.RecordKey).Distinct().Count()
            : rows.Count();
}
