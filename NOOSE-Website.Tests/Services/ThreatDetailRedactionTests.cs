using System.Text.Json;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Threat;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>A partner whose agency never sees docs or observations must not read them off the threat breakdown.</summary>
public sealed class ThreatDetailRedactionTests
{
    private static string Json(params ThreatPartialScore[] partials)
        => JsonSerializer.Serialize(new ThreatScoreDetail { PartialScores = partials, Score = 61, Confidence = 70 }, ThreatScoreService.JsonOptions);

    private static ThreatScoreDetail Read(string? json)
        => JsonSerializer.Deserialize<ThreatScoreDetail>(json!, ThreatScoreService.JsonOptions)!;

    private static readonly ThreatPartialScore DocHeat = new(ThreatDetailRedaction.PersonDocHeat, 3, 12, 30, ["2 Maßnahme(n), jüngste vor 4 Tagen"]);
    private static readonly ThreatPartialScore ObservationHeat = new(ThreatDetailRedaction.PersonObservationHeat, 1, 5, 15, ["1 Observation(en)"]);
    private static readonly ThreatPartialScore Danger = new("Gefährlichkeit", 1, 8, 20, ["bewaffnet"]);
    private static readonly ThreatPartialScore FactionHeat = new("Aktivitäts- & Maßnahmen-Heat", 4, 20, 35,
        ["3 Aktivität(en), jüngste vor 2 Tagen", $"5 {ThreatDetailRedaction.FactionDocLine} (im Mitgliedschaftszeitraum)"]);

    [Fact]
    public void NothingRelevantBlocked_LeavesTheJsonAlone()
    {
        var json = Json(DocHeat, Danger);

        Assert.Same(json, ThreatDetailRedaction.WithoutContent(json, PartnerContent.Comments));
    }

    [Fact]
    public void BlockedDocs_DropThePersonDocPartial_AndTheFactionDocLine()
    {
        var person = Read(ThreatDetailRedaction.WithoutContent(Json(DocHeat, ObservationHeat, Danger), PartnerContent.Docs));
        var faction = Read(ThreatDetailRedaction.WithoutContent(Json(FactionHeat), PartnerContent.Docs));

        Assert.Equal(new[] { ThreatDetailRedaction.PersonObservationHeat, "Gefährlichkeit" }, person.PartialScores.Select(p => p.Name));
        Assert.Equal(61, person.Score);
        Assert.Equal(new[] { "3 Aktivität(en), jüngste vor 2 Tagen" }, faction.PartialScores.Single().Driver);
    }

    [Fact]
    public void BlockedObservations_DropTheObservationPartial()
    {
        var person = Read(ThreatDetailRedaction.WithoutContent(Json(DocHeat, ObservationHeat), PartnerContent.Observations));

        Assert.Equal(new[] { ThreatDetailRedaction.PersonDocHeat }, person.PartialScores.Select(p => p.Name));
    }

    [Fact]
    public void UnreadableJson_ShowsNoBreakdownRatherThanAnUnfilteredOne()
        => Assert.Null(ThreatDetailRedaction.WithoutContent("{not json", PartnerContent.Docs));

    [Fact]
    public void Recency_WithoutDocs_IgnoresTheDocsStamp()
    {
        var created = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var faction = new Faction
        {
            CreatedAt = created,
            MembersRefreshedAt = created.AddDays(30),
            StockRefreshedAt = created.AddDays(20),
            ActivitiesRefreshedAt = created.AddDays(25),
            DocsRefreshedAt = created.AddDays(1),
        };

        Assert.Equal(FactionRecencyFacet.Docs, FactionRecency.Oldest(faction));
        Assert.Equal(FactionRecencyFacet.Stock, FactionRecency.Oldest(faction, includeDocs: false));
        Assert.Equal(created.AddDays(20), FactionRecency.Reference(faction, includeDocs: false));
        Assert.DoesNotContain(FactionRecency.Facets(faction, includeDocs: false), s => s.Facet == FactionRecencyFacet.Docs);
    }
}
