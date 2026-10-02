using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Threat;

/// <summary>Faction activity for scoring.</summary>
public sealed record ThreatActivity(string? Kind, DateTime Timestamp);

/// <summary>Member measure doc for scoring.</summary>
public sealed record ThreatDoc(MeasureOutcome Outcome, DateTime Timestamp);

/// <summary>Pure EF-free input for faction score calculation.</summary>
public sealed class ThreatScoreInput
{
    public bool IsStateFaction { get; init; }
    public Classification Classification { get; init; }

    public int? EstimatedMemberCount { get; init; }
    public int ActiveMembersCount { get; init; }
    public bool HasActiveLead { get; init; }
    public int RanksCount { get; init; }
    public bool HasEstate { get; init; }

    /// <summary>Distinct weapon entries.</summary>
    public int DistinctWeaponsCount { get; init; }
    /// <summary>Distinct inventory entries.</summary>
    public int InventoryCount { get; init; }
    /// <summary>Distinct drug routes.</summary>
    public int DrugRoutesCount { get; init; }

    public IReadOnlyList<ThreatActivity> Activities { get; init; } = [];

    /// <summary>Measure docs grouped per membership period; per-member cap applied per inner list.</summary>
    public IReadOnlyList<IReadOnlyList<ThreatDoc>> DocsPerMember { get; init; } = [];

    public int ConflictCount { get; init; }
    public int AllianceCount { get; init; }

    /// <summary>Agent abductions committed by the faction (S3 driver count).</summary>
    public int AbductionCount { get; init; }
    /// <summary>Weighted abduction hostility feeding S3 (severity/serum/killed weighted).</summary>
    public double AbductionHostility { get; init; }

    /// <summary>Degree of manual default links incident to the faction (S4); disjoint from conflict/alliance.</summary>
    public int DefaultEdgesDegree { get; init; }

    /// <summary>Latest capture timestamp across faction + child data; confidence freshness only, not score. null = nothing captured.</summary>
    public DateTime? LatestCaptureUtc { get; init; }
}

/// <summary>An observation reduced to what scoring needs (start + optional end).</summary>
public sealed record ThreatObservation(DateTime Start, DateTime? End);

/// <summary>EF-free input for person score calculation; person-owned data only to avoid circularity.</summary>
public sealed class PersonThreatScoreInput
{
    public Classification Classification { get; init; }
    public LifeStatus LifeStatus { get; init; }
    public DateTime? DeadUntil { get; init; }

    /// <summary>Person measure docs (P1).</summary>
    public IReadOnlyList<ThreatDoc> Docs { get; init; } = [];
    /// <summary>Distinct non-empty weapon descriptions (P2).</summary>
    public int DistinctWeaponsCount { get; init; }
    /// <summary>Observations (P3).</summary>
    public IReadOnlyList<ThreatObservation> Observations { get; init; } = [];

    // P4 - social danger
    public int EnemyCount { get; init; }
    public int AllyCount { get; init; }
    public int BusinessPartnerCount { get; init; }
    /// <summary>Active memberships holding a leadership role (faction/group/party).</summary>
    public int LeadershipRolesCount { get; init; }

    /// <summary>Agent abductions committed by the person (P4 driver count).</summary>
    public int AbductionCount { get; init; }
    /// <summary>Weighted abduction hostility feeding P4 (severity/serum/killed weighted).</summary>
    public double AbductionHostility { get; init; }

    /// <summary>Degree of manual default links incident to the person (P5).</summary>
    public int DefaultEdgesDegree { get; init; }

    // confidence only - never lowers the score
    public int MembershipsCount { get; init; }
    /// <summary>Data richness = aliases + vehicles + phones + locations (confidence only, never score).</summary>
    public int DataRichness { get; init; }
    public DateTime? LatestCaptureUtc { get; init; }
}

/// <summary>Contribution of one partial score; drives the breakdown shown in the UI.</summary>
public sealed record ThreatPartialScore(string Name, double RawValue, double Points, double Cap, IReadOnlyList<string> Driver);

/// <summary>Structured breakdown of a score run; persisted as JSON, answers "why this score?".</summary>
public sealed class ThreatScoreDetail
{
    public IReadOnlyList<ThreatPartialScore> PartialScores { get; init; } = [];
    /// <summary>Sum of content partial scores (0-100), before the classification band projection.</summary>
    public double Content { get; init; }
    public string ClassificationName { get; init; } = "";
    /// <summary>Minimum band from the classification (0/12/50/75).</summary>
    public int Base { get; init; }
    public string BandHint { get; init; } = "";
    public int Score { get; init; }
    public int Confidence { get; init; }
    public bool TriageFlag { get; init; }
    public string? TriageHint { get; init; }
    /// <summary>Set when the faction is excluded from scoring (score = null).</summary>
    public string? Excluded { get; init; }
    public DateTime CalculatedAtUtc { get; init; }
}

/// <summary>Result of a score run: persisted values (null on exclusion) plus breakdown.</summary>
public sealed record ThreatScoreResult(int? Score, int? Confidence, ThreatScoreDetail Detail);

/// <summary>Aggregated score distribution from a dry-run calibration preview (no persistence). Bands mirror HazardLevel (0 / &lt;25 / &lt;50 / &lt;75 / ≥75).</summary>
public sealed record ThreatScoreDistribution(
    int Total, int Scored, int Excluded,
    int No, int Low, int Medium, int High, int Critical,
    double AverageScore, double AverageConfidence, int TriageCount);

/// <summary>Counts of one full recalculation started by a user.</summary>
public sealed record ThreatScoreRecalculation(int Factions, int People);
