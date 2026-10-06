using NOOSE_Website.Models.Statistics;

namespace NOOSE_Website.Services.Statistics;

/// <summary>Partner view of a frozen situation report: no doc figures, no named people, no internal workload, only released factions.</summary>
/// <remarks>The snapshot is computed with classified records included and cannot be re-filtered, so whatever names a record or derives from docs is dropped.</remarks>
public static class SituationReportRedaction
{
    public static StatisticsReport ForPartner(StatisticsReport report, IReadOnlySet<string> visibleFactionIds)
        => report with
        {
            Metrics = report.Metrics is null ? null! : report.Metrics with { OpenRequests = 0, Classified = 0, StaleRecords = 0 },
            MeasureOutcomes = null!,
            TopPeople = null!,
            TopFactions = (report.TopFactions ?? [])
                .Where(e => visibleFactionIds.Contains(LastSegment(e.Href)))
                .ToList(),
            TimeSeries = (report.TimeSeries ?? []).Select(m => m with { Measures = 0 }).ToList(),
        };

    /// <summary>Record id at the end of a detail href such as /fraktionen/{id}.</summary>
    public static string LastSegment(string? href)
        => (href ?? string.Empty).TrimEnd('/').Split('/').LastOrDefault() ?? string.Empty;
}
