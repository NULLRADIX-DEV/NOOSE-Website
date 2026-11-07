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

/// <summary>One line of "Zu quittieren": a board entry or a directive the agent still has to acknowledge.</summary>
/// <param name="Since">When it started waiting: the board entry's creation, or the owed version's publication.</param>
/// <param name="DueAt">Acknowledgment deadline; only a board entry carries one.</param>
public sealed record ShiftAcknowledgment(string Href, string Title, bool Important, bool IsDirective, DateTime Since, DateTime? DueAt);
