namespace NOOSE_Website.Models.Shift;

/// <summary>Why the "since your last visit" block of "Meine Schicht" starts where it does.</summary>
public enum ShiftWindowKind
{
    /// <summary>At the last look of the previous visit.</summary>
    SinceLastVisit,
    /// <summary>No previous visit: the last day.</summary>
    FirstVisit,
    /// <summary>The previous visit lies further back than the block reaches.</summary>
    Capped,
}

/// <summary>Start of the "since your last visit" block and why it starts there.</summary>
public sealed record ShiftWindow(DateTime SinceUtc, ShiftWindowKind Kind);
