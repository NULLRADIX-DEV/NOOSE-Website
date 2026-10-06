using System.Text.Json;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Threat;

namespace NOOSE_Website.Services;

/// <summary>Strips the breakdown lines derived from content a partner agency never sees; the score itself stays.</summary>
public static class ThreatDetailRedaction
{
    /// <summary>Person partial fed only by docs.</summary>
    public const string PersonDocHeat = "Maßnahmen-Heat";

    /// <summary>Person partial fed only by observations.</summary>
    public const string PersonObservationHeat = "Observations-Heat";

    /// <summary>Faction driver line counting the members' docs.</summary>
    public const string FactionDocLine = "Maßnahme(n) von Mitgliedern";

    /// <summary>Breakdown JSON without doc- or observation-derived parts; unchanged when nothing relevant is blocked.</summary>
    public static string? WithoutContent(string? detailJson, PartnerContent blocked)
    {
        var docs = blocked.HasFlag(PartnerContent.Docs);
        var observations = blocked.HasFlag(PartnerContent.Observations);
        if (string.IsNullOrWhiteSpace(detailJson) || (!docs && !observations))
        {
            return detailJson;
        }
        ThreatScoreDetail? detail;
        try
        {
            detail = JsonSerializer.Deserialize<ThreatScoreDetail>(detailJson, ThreatScoreService.JsonOptions);
        }
        catch (JsonException)
        {
            // fail closed
            return null;
        }
        if (detail is null)
        {
            return null;
        }

        var partials = detail.PartialScores
            .Where(p => !(docs && p.Name == PersonDocHeat) && !(observations && p.Name == PersonObservationHeat))
            .Select(p => docs ? p with { Driver = p.Driver.Where(d => !d.Contains(FactionDocLine, StringComparison.Ordinal)).ToList() } : p)
            .ToList();
        var redacted = new ThreatScoreDetail
        {
            PartialScores = partials,
            Content = detail.Content,
            ClassificationName = detail.ClassificationName,
            Base = detail.Base,
            BandHint = detail.BandHint,
            Score = detail.Score,
            Confidence = detail.Confidence,
            TriageFlag = detail.TriageFlag,
            TriageHint = detail.TriageHint,
            Excluded = detail.Excluded,
            CalculatedAtUtc = detail.CalculatedAtUtc,
        };
        return JsonSerializer.Serialize(redacted, ThreatScoreService.JsonOptions);
    }
}
