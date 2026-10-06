namespace NOOSE_Website.Models.Enums;

/// <summary>Partner agency for external read-only access.</summary>
public enum PartnerAgency
{
    DoJ = 1,
    LSPD = 2,
    LSMD = 3,
    Parlament = 4,
}

/// <summary>Display labels.</summary>
public static class PartnerAgencyDisplay
{
    public static string Name(PartnerAgency? agency) => agency switch
    {
        PartnerAgency.DoJ => "DoJ",
        PartnerAgency.LSPD => "LSPD",
        PartnerAgency.LSMD => "LSMD",
        PartnerAgency.Parlament => "Parlament",
        _ => "—",
    };

    /// <summary>Full agency name.</summary>
    public static string LongName(PartnerAgency? agency) => agency switch
    {
        PartnerAgency.DoJ => "Department of Justice",
        PartnerAgency.LSPD => "Los Santos Police Department",
        PartnerAgency.LSMD => "Los Santos Medical Department",
        PartnerAgency.Parlament => "Parlament von San Andreas",
        _ => "—",
    };

    /// <summary>All agencies.</summary>
    public static readonly IReadOnlyList<PartnerAgency> All = new[]
    {
        PartnerAgency.DoJ,
        PartnerAgency.LSPD,
        PartnerAgency.LSMD,
        PartnerAgency.Parlament,
    };
}
