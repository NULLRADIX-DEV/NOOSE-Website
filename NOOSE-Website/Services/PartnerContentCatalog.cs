using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Maps blockable partner content to its child CLR types and detail-page tab slugs.</summary>
public static class PartnerContentCatalog
{
    private static readonly Dictionary<string, PartnerContent> ByChildType = new()
    {
        [nameof(PersonDoc)] = PartnerContent.Docs,
        [nameof(Observation)] = PartnerContent.Observations,
        [nameof(Source)] = PartnerContent.Sources,
        [nameof(Comment)] = PartnerContent.Comments,
        [nameof(Followup)] = PartnerContent.Followups,
    };

    private static readonly Dictionary<PartnerContent, string> TabSlug = new()
    {
        [PartnerContent.Docs] = "doks",
        [PartnerContent.Observations] = "ueberwachung",
        [PartnerContent.Sources] = "quellen",
        [PartnerContent.Comments] = "kommentare",
        [PartnerContent.Followups] = "wiedervorlagen",
    };

    /// <summary>Content flag of a child type; None when the type cannot be blocked.</summary>
    public static PartnerContent ForChildType(string childType)
        => ByChildType.GetValueOrDefault(childType, PartnerContent.None);

    /// <summary>True when the child type falls under a blocked flag.</summary>
    public static bool Blocks(PartnerContent blocked, string childType)
        => ForChildType(childType) is var flag && flag != PartnerContent.None && blocked.HasFlag(flag);

    /// <summary>Tab slugs hidden by a blocked set.</summary>
    public static IEnumerable<string> BlockedTabs(PartnerContent blocked)
        => TabSlug.Where(t => blocked.HasFlag(t.Key)).Select(t => t.Value);
}
