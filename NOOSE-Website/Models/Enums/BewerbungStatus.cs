using MudBlazor;

namespace NOOSE_Website.Models.Enums;

/// <summary>Lifecycle of a job application.</summary>
public enum BewerbungStatus
{
    Eingereicht = 0,
    InSicherheitspruefung = 1,
    ImTest = 2,
    ImVorstellungsgespraech = 3,
    Angenommen = 4,
    Abgelehnt = 5,
    Geschlossen = 6,
}

/// <summary>Display labels and chip colors.</summary>
public static class BewerbungStatusDisplay
{
    public static string Name(BewerbungStatus status) => status switch
    {
        BewerbungStatus.Eingereicht => "Eingereicht",
        BewerbungStatus.InSicherheitspruefung => "Sicherheitsüberprüfung",
        BewerbungStatus.ImTest => "Test",
        BewerbungStatus.ImVorstellungsgespraech => "Vorstellungsgespräch",
        BewerbungStatus.Angenommen => "Angenommen",
        BewerbungStatus.Abgelehnt => "Abgelehnt",
        BewerbungStatus.Geschlossen => "Geschlossen",
        _ => "—",
    };

    public static Color ChipColor(BewerbungStatus status) => status switch
    {
        BewerbungStatus.Eingereicht => Color.Info,
        BewerbungStatus.InSicherheitspruefung => Color.Warning,
        BewerbungStatus.ImTest => Color.Warning,
        BewerbungStatus.ImVorstellungsgespraech => Color.Primary,
        BewerbungStatus.Angenommen => Color.Success,
        BewerbungStatus.Abgelehnt => Color.Error,
        BewerbungStatus.Geschlossen => Color.Default,
        _ => Color.Default,
    };

    /// <summary>Label shown to the applicant; test and interview stage are merged.</summary>
    /// <remarks>Advancing past ImTest would otherwise tell the applicant the test was acceptable, which
    /// is the one verdict inference left. The internal Name stays untouched so HRB sees the real stage.</remarks>
    public static string ApplicantName(BewerbungStatus status) => status switch
    {
        BewerbungStatus.ImTest or BewerbungStatus.ImVorstellungsgespraech => "Auswahlverfahren",
        _ => Name(status),
    };

    /// <summary>Whether the applicant sees a different name afterwards; test to interview is no change.</summary>
    public static bool ChangesApplicantView(BewerbungStatus from, BewerbungStatus to)
        => ApplicantName(from) != ApplicantName(to);

    /// <summary>Collapses the interview stage onto the test stage for the applicant stepper.</summary>
    public static BewerbungStatus ApplicantStep(BewerbungStatus status)
        => status == BewerbungStatus.ImVorstellungsgespraech ? BewerbungStatus.ImTest : status;

    /// <summary>Terminal states allow no further transitions.</summary>
    public static bool IsTerminal(BewerbungStatus status)
        => status is BewerbungStatus.Angenommen or BewerbungStatus.Abgelehnt or BewerbungStatus.Geschlossen;
}
