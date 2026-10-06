namespace NOOSE_Website.Models.Enums;

/// <summary>Functions an agency profile switches on beyond reading released records.</summary>
[Flags]
public enum PartnerFeature
{
    None = 0,
    Noosei = 1,
    Graph = 2,
    Radio = 4,
    SituationReports = 8,
    ShareRequests = 16,
}

/// <summary>Child content an agency never sees, whatever was released.</summary>
[Flags]
public enum PartnerContent
{
    None = 0,
    Docs = 1,
    Observations = 2,
    Sources = 4,
    Comments = 8,
    Followups = 16,
}

/// <summary>Which records of a type a release rule covers, now and in future.</summary>
public enum PartnerRuleScope
{
    All = 1,
    BadFactions = 2,
    BadFactionMembers = 3,
}

/// <summary>Display labels for the agency profile.</summary>
public static class PartnerProfileDisplay
{
    public static string Name(PartnerFeature feature) => feature switch
    {
        PartnerFeature.Noosei => "NOOSEI-Rechtsauskunft",
        PartnerFeature.Graph => "Beziehungsgraph",
        PartnerFeature.Radio => "Funkplan",
        PartnerFeature.SituationReports => "Lageberichte",
        PartnerFeature.ShareRequests => "Freigaben anfragen",
        _ => "—",
    };

    public static string Name(PartnerContent content) => content switch
    {
        PartnerContent.Docs => "Doks / Befragungen",
        PartnerContent.Observations => "Überwachung",
        PartnerContent.Sources => "Quellen",
        PartnerContent.Comments => "Kommentare",
        PartnerContent.Followups => "Wiedervorlagen",
        _ => "—",
    };

    public static string Name(PartnerRuleScope scope) => scope switch
    {
        PartnerRuleScope.All => "Alle, auch künftige",
        PartnerRuleScope.BadFactions => "Nur Badfraks",
        PartnerRuleScope.BadFactionMembers => "Mitglieder von Badfraks",
        _ => "—",
    };

    /// <summary>Functions that already work and may be switched, in display order.</summary>
    public static readonly IReadOnlyList<PartnerFeature> AvailableFeatures = new[]
    {
        PartnerFeature.Noosei,
        PartnerFeature.Graph,
        PartnerFeature.Radio,
        PartnerFeature.SituationReports,
        PartnerFeature.ShareRequests,
    };

    /// <summary>All blockable content kinds in display order.</summary>
    public static readonly IReadOnlyList<PartnerContent> AllContent = new[]
    {
        PartnerContent.Docs,
        PartnerContent.Observations,
        PartnerContent.Sources,
        PartnerContent.Comments,
        PartnerContent.Followups,
    };
}
