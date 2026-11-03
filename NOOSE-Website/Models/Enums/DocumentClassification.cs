namespace NOOSE_Website.Models.Enums;

/// <summary>Document access classification.</summary>
public enum DocumentClassification
{
    /// <summary>Visible to all agents.</summary>
    None = 0,

    /// <summary>Leadership only.</summary>
    Leadership = 1,

    /// <summary>HRT only (stored as the legacy TRU unit).</summary>
    Tru = 2,

    /// <summary>Administration Division only (stored as the legacy HRB unit).</summary>
    Hrb = 3,
}

/// <summary>Display labels.</summary>
public static class DocumentClassificationDisplay
{
    /// <summary>Full classification label.</summary>
    public static string Label(DocumentClassification classification) => classification switch
    {
        DocumentClassification.Leadership => "Verschlusssache nur für Führung",
        DocumentClassification.Tru => "Verschlusssache nur für HRT",
        DocumentClassification.Hrb => "Verschlusssache nur für die Administration Division",
        _ => "Keine Verschlusssache",
    };

    /// <summary>Short chip label.</summary>
    public static string ChipLabel(DocumentClassification classification) => classification switch
    {
        DocumentClassification.Leadership => "Verschlusssache",
        DocumentClassification.Tru => "VS – HRT",
        DocumentClassification.Hrb => "VS – Administration",
        _ => string.Empty,
    };
}
