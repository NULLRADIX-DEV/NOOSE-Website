namespace NOOSE_Website.Services;

/// <summary>Everything the bot tells an applicant: one fixed notice per step, never a name, result or reason.</summary>
public static class ApplicantDmTexts
{
    /// <summary>Notice and portal route; both from this table only, so no free text can ever reach an applicant.</summary>
    public static (string Text, string Route) For(ApplicantDmEvent applicantEvent) => applicantEvent switch
    {
        ApplicantDmEvent.Received => ("Deine Bewerbung beim FIB ist eingegangen.", "/portal/status"),
        ApplicantDmEvent.TestAssigned => ("Für deine Bewerbung ist ein Test freigegeben.", "/portal/test"),
        ApplicantDmEvent.TestExpired => ("Die Bearbeitungszeit deines Tests ist abgelaufen.", "/portal/status"),
        ApplicantDmEvent.NewMessage => ("Du hast eine neue Nachricht zu deiner Bewerbung.", "/portal/status"),
        ApplicantDmEvent.StatusChanged => ("Es gibt einen neuen Stand zu deiner Bewerbung.", "/portal/status"),
        _ => throw new ArgumentOutOfRangeException(nameof(applicantEvent)),
    };
}
