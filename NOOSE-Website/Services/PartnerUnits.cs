using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>The shipped partner units with fixed ids, and the bridge from the old fixed tiers to them.</summary>
public static class PartnerUnits
{
    /// <summary>Seed time stamp; seeded rows bypass the audit interceptor.</summary>
    public static readonly DateTime SeededAt = new(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc);

    public const string DojSid = "5e0d7a10-0101-4c1e-9a00-000000000101";
    public const string DojStateAttorney = "5e0d7a10-0102-4c1e-9a00-000000000102";
    public const string DojChief = "5e0d7a10-0103-4c1e-9a00-000000000103";

    /// <summary>Seeded unit for an (agency, old tier) pair; every pair has exactly one.</summary>
    public static string ForLegacy(PartnerAgency agency, PartnerRank? rank)
        => $"5e0d7a10-0{(int)agency}0{(int)(rank ?? PartnerRank.Member)}-4c1e-9a00-00000000{(int)agency:00}{(int)(rank ?? PartnerRank.Member):00}";

    /// <summary>The rows the model seeds: three per agency, DoJ with its own names and abilities.</summary>
    public static IReadOnlyList<PartnerUnit> Seed()
    {
        var rows = new List<PartnerUnit>();
        foreach (var agency in PartnerAgencyDisplay.All)
        {
            foreach (var rank in PartnerRankDisplay.All)
            {
                var unit = new PartnerUnit
                {
                    Id = ForLegacy(agency, rank),
                    Agency = agency,
                    LegacyRank = rank,
                    SortOrder = (int)rank,
                    Name = rank switch
                    {
                        PartnerRank.Special => "Special",
                        PartnerRank.Chief => "Chief",
                        _ => "Mitglied",
                    },
                    CreatedAt = SeededAt,
                };
                if (agency == PartnerAgency.DoJ)
                {
                    switch (rank)
                    {
                        case PartnerRank.Member:
                            unit.Name = "SID";
                            unit.MayCreateInvestigations = true;
                            unit.LeadSeesAllInvestigations = true;
                            break;
                        case PartnerRank.Special:
                            unit.Name = "State Attorney Office";
                            unit.IsProsecution = true;
                            break;
                    }
                }
                rows.Add(unit);
            }
        }
        return rows;
    }
}

/// <summary>Display labels for partner units.</summary>
public static class PartnerUnitDisplay
{
    // warmed by the unit service on every load, like the enum label overrides; seed names until then
    private static volatile IReadOnlyDictionary<string, string> _names =
        PartnerUnits.Seed().ToDictionary(u => u.Id, u => u.Name);

    /// <summary>Replaces the id → name map after a load.</summary>
    public static void ReplaceAll(IEnumerable<(string Id, string Name)> units)
        => _names = units.ToDictionary(u => u.Id, u => u.Name);

    /// <summary>Unit name for an id, or null.</summary>
    public static string? Name(string? unitId)
        => unitId is not null && _names.TryGetValue(unitId, out var name) ? name : null;

    /// <summary>Agency plus unit, e.g. "DoJ · SID"; just the agency without a unit.</summary>
    public static string Full(PartnerAgency? agency, string? unitName)
    {
        var name = PartnerAgencyDisplay.Name(agency);
        return string.IsNullOrWhiteSpace(unitName) ? name : $"{name} · {unitName}";
    }

    /// <summary>Label of a partner account by unit id, e.g. "DoJ · SID".</summary>
    public static string OfAccount(PartnerAgency? agency, string? unitId) => Full(agency, Name(unitId));
}
