using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Common;

/// <summary>A partner unit as the app reads it: abilities, visibility allowlist and investigation tab defaults.</summary>
public sealed record PartnerUnitView(
    string Id,
    PartnerAgency Agency,
    string Name,
    int SortOrder,
    PartnerRank LegacyRank,
    bool IsProsecution,
    bool MayCreateInvestigations,
    bool LeadSeesAllInvestigations,
    PartnerUnitVisibility? Visibility,
    IReadOnlyDictionary<string, InvestigationAccessLevel> InvestigationDefaults,
    int ActiveAccounts)
{
    /// <summary>"DoJ · SID".</summary>
    public string FullName => Services.PartnerUnitDisplay.Full(Agency, Name);
}

/// <summary>What the admin editor saves for a unit (create when Id is null).</summary>
public sealed record PartnerUnitInput(
    string? Id,
    PartnerAgency Agency,
    string Name,
    int SortOrder,
    bool IsProsecution,
    bool MayCreateInvestigations,
    bool LeadSeesAllInvestigations,
    IReadOnlyDictionary<string, InvestigationAccessLevel> InvestigationDefaults);
