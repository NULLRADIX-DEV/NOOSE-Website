namespace NOOSE_Website.Models.Enums;

/// <summary>How a probation ended; stored as int.</summary>
public enum ProbationOutcome
{
    Passed = 1,
    Failed = 2,
}

/// <summary>Which probation reminder has gone out; stored as int.</summary>
public enum ProbationReminderStage
{
    None = 0,
    Soon = 1,
    Due = 2,
}

/// <summary>Traffic light of an onboarding plan.</summary>
public enum ProbationLight
{
    Green,
    Yellow,
    Red,
    Passed,
    Failed,
}
