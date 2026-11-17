using MudBlazor;

namespace NOOSE_Website.Models.Enums;

/// <summary>Stage of a disciplinary proceeding.</summary>
public enum DisciplinaryStatus
{
    Open = 1,
    Suspended = 2,
    Discontinued = 3,
    Decided = 4,
}

/// <summary>Disciplinary measures, mildest first.</summary>
public enum DisciplinaryMeasure
{
    Entry = 1,
    Withdrawal = 2,
    Reprimand = 3,
    Demotion = 4,
    Leave = 5,
    Dismissal = 6,
    HiringBan = 7,
}

/// <summary>Stage of a transfer request.</summary>
public enum TransferRequestStatus
{
    Open = 1,
    Heard = 2,
    Granted = 3,
    Refused = 4,
    Withdrawn = 5,
}

/// <summary>Why a service reference was issued.</summary>
public enum ServiceReferenceReason
{
    Leaving = 1,
    Transfer = 2,
    OnRequest = 3,
}

/// <summary>Kind of a reported relationship between two agents.</summary>
public enum AgentRelationshipKind
{
    Marriage = 1,
    Partnership = 2,
    Family = 3,
    Other = 4,
}

/// <summary>Stage of a decoration proposal.</summary>
public enum DecorationProposalStatus
{
    Open = 1,
    Accepted = 2,
    Refused = 3,
}

/// <summary>Stage of a parliamentary inquiry.</summary>
public enum ParliamentaryInquiryStatus
{
    Received = 1,
    InProgress = 2,
    Answered = 3,
    Withdrawn = 4,
}

/// <summary>German labels and colors for the Administration enums.</summary>
public static class AdministrationDisplay
{
    public static string Name(DisciplinaryStatus status) => status switch
    {
        DisciplinaryStatus.Open => "Laufend",
        DisciplinaryStatus.Suspended => "Ausgesetzt",
        DisciplinaryStatus.Discontinued => "Eingestellt",
        DisciplinaryStatus.Decided => "Verfügung erlassen",
        _ => status.ToString(),
    };

    public static Color Color(DisciplinaryStatus status) => status switch
    {
        DisciplinaryStatus.Open => MudBlazor.Color.Warning,
        DisciplinaryStatus.Suspended => MudBlazor.Color.Default,
        DisciplinaryStatus.Discontinued => MudBlazor.Color.Success,
        DisciplinaryStatus.Decided => MudBlazor.Color.Error,
        _ => MudBlazor.Color.Default,
    };

    public static readonly IReadOnlyList<DisciplinaryMeasure> Measures = Enum.GetValues<DisciplinaryMeasure>();

    public static string Name(DisciplinaryMeasure measure) => measure switch
    {
        DisciplinaryMeasure.Entry => "Eintrag",
        DisciplinaryMeasure.Withdrawal => "Entzug von Aufgaben, Dienstmitteln oder Fortbildungen",
        DisciplinaryMeasure.Reprimand => "Abmahnung",
        DisciplinaryMeasure.Demotion => "Degradierung",
        DisciplinaryMeasure.Leave => "Freistellung",
        DisciplinaryMeasure.Dismissal => "Entlassung",
        DisciplinaryMeasure.HiringBan => "Einstellungsverbot",
        _ => measure.ToString(),
    };

    public static string Name(TransferRequestStatus status) => status switch
    {
        TransferRequestStatus.Open => "Offen",
        TransferRequestStatus.Heard => "Angehört",
        TransferRequestStatus.Granted => "Genehmigt",
        TransferRequestStatus.Refused => "Abgelehnt",
        TransferRequestStatus.Withdrawn => "Zurückgezogen",
        _ => status.ToString(),
    };

    public static Color Color(TransferRequestStatus status) => status switch
    {
        TransferRequestStatus.Open => MudBlazor.Color.Warning,
        TransferRequestStatus.Heard => MudBlazor.Color.Info,
        TransferRequestStatus.Granted => MudBlazor.Color.Success,
        TransferRequestStatus.Refused => MudBlazor.Color.Error,
        _ => MudBlazor.Color.Default,
    };

    public static string Name(ServiceReferenceReason reason) => reason switch
    {
        ServiceReferenceReason.Leaving => "Ausscheiden",
        ServiceReferenceReason.Transfer => "Versetzung",
        ServiceReferenceReason.OnRequest => "Auf Anfrage",
        _ => reason.ToString(),
    };

    public static string Name(AgentRelationshipKind kind) => kind switch
    {
        AgentRelationshipKind.Marriage => "Ehe",
        AgentRelationshipKind.Partnership => "Partnerschaft",
        AgentRelationshipKind.Family => "Enge Verwandtschaft",
        AgentRelationshipKind.Other => "Sonstige Nähe",
        _ => kind.ToString(),
    };

    public static string Name(DecorationProposalStatus status) => status switch
    {
        DecorationProposalStatus.Open => "Offen",
        DecorationProposalStatus.Accepted => "Verliehen",
        DecorationProposalStatus.Refused => "Abgelehnt",
        _ => status.ToString(),
    };

    public static Color Color(DecorationProposalStatus status) => status switch
    {
        DecorationProposalStatus.Open => MudBlazor.Color.Warning,
        DecorationProposalStatus.Accepted => MudBlazor.Color.Success,
        DecorationProposalStatus.Refused => MudBlazor.Color.Error,
        _ => MudBlazor.Color.Default,
    };

    public static string Name(ParliamentaryInquiryStatus status) => status switch
    {
        ParliamentaryInquiryStatus.Received => "Eingegangen",
        ParliamentaryInquiryStatus.InProgress => "In Bearbeitung",
        ParliamentaryInquiryStatus.Answered => "Beantwortet",
        ParliamentaryInquiryStatus.Withdrawn => "Zurückgezogen",
        _ => status.ToString(),
    };

    public static Color Color(ParliamentaryInquiryStatus status) => status switch
    {
        ParliamentaryInquiryStatus.Received => MudBlazor.Color.Warning,
        ParliamentaryInquiryStatus.InProgress => MudBlazor.Color.Info,
        ParliamentaryInquiryStatus.Answered => MudBlazor.Color.Success,
        _ => MudBlazor.Color.Default,
    };
}
