using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.CounterIntel;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Turns a rule definition into the German one-liners the panel, the editor and the findings tab show.</summary>
public static class CounterIntelRuleDisplay
{
    private static readonly string[] WeekdayNames =
        ["So", "Mo", "Di", "Mi", "Do", "Fr", "Sa"];

    /// <summary>Compact description of every condition the rule sets, e.g. for the rule list.</summary>
    public static string Summary(CounterIntelRuleDefinition definition)
    {
        var parts = new List<string>();

        parts.Add(definition.Actions.Count == 0
            ? "Alle Aktionen"
            : string.Join(" / ", definition.Actions.Select(CounterIntelActionKindDisplay.Name)));

        if (definition.EntityTypes.Count > 0)
        {
            parts.Add(string.Join(", ", definition.EntityTypes.Select(CustomFieldRecordTypes.Display)));
        }
        if (definition.EntityIds.Count > 0)
        {
            parts.Add(definition.EntityIds.Count == 1 ? "1 bestimmte Akte" : $"{definition.EntityIds.Count} bestimmte Akten");
        }
        if (definition.ClassifiedOnly is { } classified)
        {
            parts.Add(classified ? "nur VS" : "nur ohne VS");
        }
        if (definition.Classifications.Count > 0)
        {
            parts.Add(string.Join(" / ", definition.Classifications.Select(ClassificationDisplay.Name)));
        }
        if (definition.TagIds.Count > 0)
        {
            parts.Add($"{definition.TagIds.Count} Tag(s)");
        }
        if (definition.ActorRanks.Count > 0)
        {
            parts.Add(string.Join(" / ", definition.ActorRanks.Select(r => RankDisplay.Name(r))));
        }
        if (definition.ActorIds.Count > 0)
        {
            parts.Add($"{definition.ActorIds.Count} bestimmte Agenten");
        }
        if (definition.ExcludedActorIds.Count > 0)
        {
            parts.Add($"ohne {definition.ExcludedActorIds.Count} Agenten");
        }
        foreach (var (flag, label) in new (bool?, string)[]
        {
            (definition.RequireTru, "HRT"), (definition.RequireHrb, "Administration"), (definition.RequireAdmin, "Admin"),
        })
        {
            if (flag is { } value)
            {
                parts.Add(value ? $"nur {label}" : $"ohne {label}");
            }
        }
        if (definition.ActorSharesOrgWithTarget is { } shared)
        {
            parts.Add(shared
                ? "teilt Organisation mit der Ziel-Akte"
                : "teilt keine Organisation mit der Ziel-Akte");
        }
        if (definition.PartnerScope != CounterIntelPartnerScope.Any)
        {
            parts.Add(CounterIntelPartnerScopeDisplay.Name(definition.PartnerScope));
        }
        if (!definition.IsAllDay)
        {
            parts.Add($"{definition.FromHour}–{definition.ToHour} Uhr");
        }
        if (definition.Weekdays.Count is > 0 and < 7)
        {
            parts.Add(string.Join("/", definition.Weekdays.Select(d => WeekdayNames[(int)d])));
        }

        parts.Add(Trigger(definition));
        parts.Add($"{definition.WindowDays} Tage");
        return string.Join(" · ", parts);
    }

    /// <summary>The trigger clause alone, e.g. "≥ 10 verschiedene Akten pro Tag".</summary>
    public static string Trigger(CounterIntelRuleDefinition definition)
    {
        var what = CounterIntelCountModeDisplay.Name(definition.CountMode);
        var when = definition.Bucket == CounterIntelBucket.Sliding
            ? $"in {definition.SlidingMinutes} Min."
            : CounterIntelBucketDisplay.Name(definition.Bucket);
        return $"≥ {definition.Threshold} {what} {when}";
    }

    /// <summary>Detail line of one finding: the agent's worst bucket and where it sits.</summary>
    public static string Detail(CounterIntelRuleDefinition definition, int count, DateTime? at)
    {
        var what = CounterIntelCountModeDisplay.Name(definition.CountMode);
        var where = definition.Bucket switch
        {
            CounterIntelBucket.Day when at is { } day => $" am {day:dd.MM.yyyy}",
            CounterIntelBucket.Hour when at is { } hour => $" am {hour:dd.MM.yyyy} um {hour:HH} Uhr",
            CounterIntelBucket.Sliding when at is { } start => $" innerhalb von {definition.SlidingMinutes} Min. ab {start:dd.MM.yyyy HH:mm}",
            CounterIntelBucket.Window => " im gesamten Zeitraum",
            _ => string.Empty,
        };
        return $"{count} {what}{where} (Schwelle {definition.Threshold}).";
    }
}
