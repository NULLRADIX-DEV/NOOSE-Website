namespace NOOSE_Website.Services;

/// <summary>The steps of an application an applicant hears about by direct message.</summary>
public enum ApplicantDmEvent
{
    Received,
    TestAssigned,
    TestExpired,
    NewMessage,
    StatusChanged,
}

/// <summary>Tells an applicant by direct message that something happened to their application.</summary>
public interface IApplicantDmNotifier
{
    /// <summary>Queues the fixed notice (text from <see cref="ApplicantDmTexts"/>, then the portal link) when the switch is on and the account is an applicant with a Discord id; never throws, never calls Discord.</summary>
    Task NotifyAsync(string applicantAgentId, ApplicantDmEvent applicantEvent, CancellationToken cancellationToken = default);
}
