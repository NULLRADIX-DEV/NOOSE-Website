using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Common;

/// <summary>Old per-tier visibility, stored as JSON in a SystemSetting row; read once to carry it over to the units.</summary>
public sealed class PartnerVisibilityConfig
{
    /// <summary>Keyed by <see cref="RankKey"/>; a present key means allowlist mode for that tier.</summary>
    public Dictionary<string, PartnerUnitVisibility> Ranks { get; set; } = new();

    /// <summary>Stable key for an (agency, rank) pair.</summary>
    public static string RankKey(PartnerAgency agency, PartnerRank rank) => $"{(int)agency}:{(int)rank}";
}

/// <summary>Allowlist for one partner unit: record types and, per type, tabs.</summary>
public sealed class PartnerUnitVisibility
{
    /// <summary>Visible record-type keys (CLR type names).</summary>
    public List<string> Types { get; set; } = new();

    /// <summary>Per type, the visible tab slugs; a missing entry for a listed type means all tabs.</summary>
    public Dictionary<string, List<string>> Tabs { get; set; } = new();
}
