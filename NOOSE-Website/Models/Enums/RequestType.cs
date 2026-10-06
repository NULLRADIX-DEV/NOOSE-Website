namespace NOOSE_Website.Models.Enums;

/// <summary>Inbox request type.</summary>
public enum RequestType
{
    /// <summary>Classification upgrade request.</summary>
    Upgrade = 0,
    /// <summary>Partner-agency record release request.</summary>
    PartnerFreigabe = 1,
    /// <summary>Public wanted notice awaiting leadership approval.</summary>
    Veroeffentlichung = 2,
    /// <summary>Official bounty share awaiting leadership approval.</summary>
    Kopfgeld = 3,
    /// <summary>A partner asks in free text for records it cannot see yet.</summary>
    PartnerAnfrage = 4,
}

/// <summary>Display labels.</summary>
public static class RequestTypeDisplay
{
    public static string Name(RequestType type) => type switch
    {
        RequestType.Upgrade => "Hochstufung",
        RequestType.PartnerFreigabe => "Partner-Freigabe",
        RequestType.Veroeffentlichung => "Veröffentlichung",
        RequestType.Kopfgeld => "Kopfgeld",
        RequestType.PartnerAnfrage => "Partner-Anfrage",
        _ => "—",
    };
}
