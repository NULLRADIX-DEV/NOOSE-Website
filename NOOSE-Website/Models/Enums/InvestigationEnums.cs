namespace NOOSE_Website.Models.Enums;

/// <summary>Stage of an investigation, as the DoJ file tracks it.</summary>
public enum InvestigationStatus
{
    Investigating = 0,
    Indicted = 1,
    Trial = 2,
    Dismissed = 3,
    Closed = 4,
}

/// <summary>Role of an account in an investigation; a whole unit carries none.</summary>
public enum InvestigationRole
{
    Investigator = 0,
    Lead = 1,
    Prosecutor = 2,
    Observer = 3,
}

/// <summary>Outcome of checking one charge.</summary>
public enum InvestigationChargeCheck
{
    Open = 0,
    Applies = 1,
    DoesNotApply = 2,
}

/// <summary>Where a witness or accused stands.</summary>
public enum InvestigationPersonStatus
{
    Listed = 0,
    Contacted = 1,
    Cautioned = 2,
    StatementTaken = 3,
    NotUsable = 4,
}

/// <summary>State of one piece of evidence.</summary>
public enum InvestigationEvidenceStatus
{
    Secured = 0,
    UnderAnalysis = 1,
    Analysed = 2,
    Returned = 3,
    Destroyed = 4,
}

/// <summary>Display labels for the investigation enums.</summary>
public static class InvestigationDisplay
{
    public static string Name(InvestigationStatus status) => status switch
    {
        InvestigationStatus.Indicted => "Anklage erhoben",
        InvestigationStatus.Trial => "Hauptverhandlung",
        InvestigationStatus.Dismissed => "Eingestellt",
        InvestigationStatus.Closed => "Abgeschlossen",
        _ => "Ermittlung",
    };

    public static bool IsOpen(InvestigationStatus status) => status is InvestigationStatus.Investigating
        or InvestigationStatus.Indicted or InvestigationStatus.Trial;

    public static string Name(InvestigationRole role) => role switch
    {
        InvestigationRole.Lead => "Ermittlungsleitung",
        InvestigationRole.Prosecutor => "Staatsanwalt",
        InvestigationRole.Observer => "Beobachter",
        _ => "Ermittler",
    };

    public static string Name(InvestigationChargeCheck check) => check switch
    {
        InvestigationChargeCheck.Applies => "Trifft zu",
        InvestigationChargeCheck.DoesNotApply => "Trifft nicht zu",
        _ => "Offen",
    };

    public static string Name(InvestigationPersonStatus status) => status switch
    {
        InvestigationPersonStatus.Contacted => "Kontaktiert",
        InvestigationPersonStatus.Cautioned => "Belehrt",
        InvestigationPersonStatus.StatementTaken => "Aussage erhalten",
        InvestigationPersonStatus.NotUsable => "Nicht verwertbar",
        _ => "Eingetragen",
    };

    public static string Name(InvestigationEvidenceStatus status) => status switch
    {
        InvestigationEvidenceStatus.UnderAnalysis => "In Auswertung",
        InvestigationEvidenceStatus.Analysed => "Ausgewertet",
        InvestigationEvidenceStatus.Returned => "Herausgegeben",
        InvestigationEvidenceStatus.Destroyed => "Vernichtet",
        _ => "Sichergestellt",
    };

    public static readonly IReadOnlyList<InvestigationStatus> Statuses = Enum.GetValues<InvestigationStatus>();
    public static readonly IReadOnlyList<InvestigationRole> Roles = Enum.GetValues<InvestigationRole>();
    public static readonly IReadOnlyList<InvestigationChargeCheck> Checks = Enum.GetValues<InvestigationChargeCheck>();
    public static readonly IReadOnlyList<InvestigationPersonStatus> PersonStatuses = Enum.GetValues<InvestigationPersonStatus>();
    public static readonly IReadOnlyList<InvestigationEvidenceStatus> EvidenceStatuses = Enum.GetValues<InvestigationEvidenceStatus>();
}
