using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Common;

/// <summary>One agency's profile: functions, blocked content and release rules.</summary>
public sealed record PartnerAgencyProfileView(
    PartnerAgency Agency, PartnerFeature Features, PartnerContent BlockedContent, IReadOnlyList<PartnerRuleView> Rules)
{
    /// <summary>The rule for a type, or null when only individual releases apply.</summary>
    public PartnerRuleView? RuleFor(string entityType) => Rules.FirstOrDefault(r => r.EntityType == entityType);
}

/// <summary>A release rule: which records of a type an agency sees automatically.</summary>
public sealed record PartnerRuleView(string EntityType, PartnerRuleScope Scope, bool IncludesChildren);
