using NOOSE_Website.Data.Entities.CounterIntel;
using NOOSE_Website.Models.CounterIntel;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>What the finding watch counts as new and what the bell says; shared by the worker and the panels.</summary>
/// <remarks>
/// A rule finding is new when it rests only on events no earlier report consumed: each report moves the ledger's
/// floor for that rule and agent up to the newest event it counted. A burst spread over two runs stays reportable,
/// and the same events never ring twice.
/// </remarks>
public static class FindingWatchRules
{
    public const string RuleHref = "/nachweis?tab=auffaelligkeiten";
    public const string NooseiHref = "/einstellungen?tab=ki-kontingente";

    private const int TitleNames = 3;
    private const int MaxTitle = 300;

    /// <summary>A rule ledger row older than this sits outside every rule window.</summary>
    public static readonly TimeSpan RuleRetention = TimeSpan.FromDays(CounterIntelRuleDefinition.MaxWindowDays + 1);

    /// <summary>One new rule finding: whose, how far it consumed the log, at which level.</summary>
    public sealed record RuleIncident(string RuleId, string AgentId, DateTime ConsumedUntilUtc, CounterIntelSeverity Grade);

    /// <summary>Ledger lookup key of one source key and flagged account.</summary>
    public static string Slot(string key, string agentId) => $"{key}\n{agentId}";

    /// <summary>The findings of the given rules that rest only on events no earlier report consumed.</summary>
    /// <param name="ledger">Rule rows by <see cref="Slot"/>.</param>
    public static List<RuleIncident> Incidents(
        IReadOnlyList<CounterIntelEvent> events,
        IReadOnlyList<CounterIntelRuleView> rules,
        IReadOnlyDictionary<string, FindingReport> ledger,
        DateTime nowUtc,
        DateTime nowLocal)
    {
        var result = new List<RuleIncident>();
        if (events.Count == 0)
        {
            return result;
        }
        var byAgent = events.GroupBy(e => e.AgentId).ToList();

        foreach (var rule in rules)
        {
            // an unarmed rule has no floor yet; the watch arms it before it ever gets here
            if (!rule.IsActive || !rule.Notify || rule.ReportFrom is not { } from)
            {
                continue;
            }
            var definition = rule.Definition;
            var windowStart = CounterIntelRuleEvaluator.WindowStart(definition, nowLocal);
            var windowDays = CounterIntelRuleEvaluator.WindowDays(definition);

            foreach (var agent in byAgent)
            {
                ledger.TryGetValue(Slot(rule.Id, agent.Key), out var row);
                // a whole-window rule names an agent at most once per window; a report from before a re-arm does not count
                if (definition.Bucket == CounterIntelBucket.Window
                    && row?.ReportedAt is { } last && last >= from && last > nowUtc.AddDays(-windowDays))
                {
                    continue;
                }

                var floor = row is not null && row.CoveredUntil > from ? row.CoveredUntil : from;
                var fresh = agent.Where(e => e.TimestampUtc > floor).ToList();
                if (fresh.Count == 0 || CounterIntelRuleEvaluator.EvaluateOne(fresh, rule, nowLocal).Count == 0)
                {
                    continue;
                }

                var matching = fresh
                    .Where(e => e.LocalTimestamp >= windowStart && CounterIntelRuleEvaluator.Matches(definition, e))
                    .ToList();
                result.Add(new RuleIncident(rule.Id, agent.Key, ConsumedUntil(definition, matching), rule.Severity));
            }
        }
        return result;
    }

    /// <summary>The newest event the reported burst used up; what came after it stays fresh for the next burst.</summary>
    /// <remarks>
    /// A bucketed rule consumes up to the end of its latest bucket over the threshold, not every event seen so far:
    /// otherwise the first minutes of the next day would be spent on yesterday's report.
    /// </remarks>
    private static DateTime ConsumedUntil(CounterIntelRuleDefinition definition, List<CounterIntelEvent> matching)
    {
        var threshold = Math.Max(1, definition.Threshold);
        var mode = definition.CountMode;
        var buckets = definition.Bucket switch
        {
            CounterIntelBucket.Day => matching.GroupBy(e => e.LocalTimestamp.Date),
            CounterIntelBucket.Hour => matching.GroupBy(e => new DateTime(
                e.LocalTimestamp.Year, e.LocalTimestamp.Month, e.LocalTimestamp.Day, e.LocalTimestamp.Hour, 0, 0)),
            _ => null,
        };
        if (buckets is not null)
        {
            var crossed = buckets
                .Where(b => CounterIntelRuleEvaluator.Count(b, mode) >= threshold)
                .MaxBy(b => b.Key);
            if (crossed is not null)
            {
                return crossed.Max(e => e.TimestampUtc);
            }
        }
        else if (definition.Bucket == CounterIntelBucket.Sliding
                 && LastCrossing(matching.OrderBy(e => e.LocalTimestamp).ToList(), definition) is { } end)
        {
            return end;
        }
        return matching.Max(e => e.TimestampUtc);
    }

    // the last event at which a sliding window still sat over the threshold; same half-open window as the evaluator
    private static DateTime? LastCrossing(List<CounterIntelEvent> ordered, CounterIntelRuleDefinition definition)
    {
        var span = TimeSpan.FromMinutes(Math.Clamp(definition.SlidingMinutes, 1, CounterIntelRuleDefinition.MaxSlidingMinutes));
        var threshold = Math.Max(1, definition.Threshold);
        var distinct = definition.CountMode == CounterIntelCountMode.DistinctRecords;
        var live = new Dictionary<string, int>();
        DateTime? last = null;
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
            if ((distinct ? live.Count : right - left + 1) >= threshold)
            {
                last = ordered[right].TimestampUtc;
            }
        }
        return last;
    }

    /// <summary>Whether saving the input makes the rule mean something new, so only what follows may be reported.</summary>
    /// <remarks>Compares the normalized definition: a seeded rule's stored JSON differs in text from a re-serialization.</remarks>
    public static bool Rearms(string? storedDefinitionJson, bool wasActive, bool wasNotify, CounterIntelRuleInput input)
        => CounterIntelRuleDefinition.TryParse(storedDefinitionJson)?.ToJson() != input.Definition.ToJson()
           || (!wasActive && input.IsActive)
           || (!wasNotify && input.Notify);

    /// <summary>A NOOSEI anomaly rings once per agent, kind and week, and again when its level rises.</summary>
    public static bool ShouldReport(FindingReport? row, CounterIntelSeverity grade, DateTime weekStartUtc)
        => row is null
           || row.CoveredUntil < weekStartUtc
           || (row.CoveredUntil == weekStartUtc && grade > row.Grade);

    /// <summary>The level a NOOSEI row keeps: the highest seen this week, otherwise the current one.</summary>
    public static CounterIntelSeverity NextGrade(FindingReport? row, CounterIntelSeverity grade, DateTime weekStartUtc)
        => row is not null && row.CoveredUntil == weekStartUtc && row.Grade > grade ? row.Grade : grade;

    /// <summary>A reported rule finding the viewer has not looked at yet and the cockpit can still show.</summary>
    /// <param name="rule">The rule as it is now; null when it is gone.</param>
    public static bool IsUnseenRule(FindingReport row, string viewerId, DateTime? seenUtc,
        CounterIntelRuleView? rule, DateTime nowUtc)
        => row.ReportedAt is { } at
           && IsForeignAndAfter(row, viewerId, seenUtc, at)
           && rule is { IsActive: true }
           // a report from before the rule's last re-arm speaks for a rule that no longer exists in that form
           && (rule.ReportFrom is not { } from || at >= from)
           && at > nowUtc.AddDays(-CounterIntelRuleEvaluator.WindowDays(rule.Definition));

    /// <summary>A reported NOOSEI anomaly of the running week the viewer has not looked at yet.</summary>
    public static bool IsUnseenNoosei(FindingReport row, string viewerId, DateTime? seenUtc, DateTime weekStartUtc)
        => row.ReportedAt is { } at
           && IsForeignAndAfter(row, viewerId, seenUtc, at)
           && row.CoveredUntil == weekStartUtc;

    // nobody hears about their own finding
    private static bool IsForeignAndAfter(FindingReport row, string viewerId, DateTime? seenUtc, DateTime at)
        => !string.Equals(row.AgentId, viewerId, StringComparison.Ordinal)
           && (seenUtc is not { } seen || at > seen);

    /// <summary>The bell line: source, count and the first few rule or kind names, never an agent.</summary>
    public static string Title(FindingSource source, int count, IReadOnlyList<string> names)
    {
        var prefix = source == FindingSource.Noosei ? "NOOSEI" : "Gegenaufklärung";
        var noun = count == 1 ? "neue Auffälligkeit" : "neue Auffälligkeiten";
        var shown = string.Join(", ", names.Take(TitleNames));
        if (names.Count > TitleNames)
        {
            shown += $" und {names.Count - TitleNames} weitere";
        }
        var title = names.Count == 0 ? $"{prefix}: {count} {noun}" : $"{prefix}: {count} {noun} ({shown})";
        return title.Length <= MaxTitle ? title : title[..(MaxTitle - 1)] + "…";
    }

    /// <summary>Bell target of a source.</summary>
    public static string Href(FindingSource source) => source == FindingSource.Noosei ? NooseiHref : RuleHref;

    /// <summary>Bell category of a source.</summary>
    public static NotificationType Type(FindingSource source)
        => source == FindingSource.Noosei ? NotificationType.NooseiAnomaly : NotificationType.CounterIntelFinding;
}
