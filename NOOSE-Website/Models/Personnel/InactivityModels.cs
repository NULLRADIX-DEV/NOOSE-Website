using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Personnel;

/// <summary>Day thresholds of the three inactivity tiers; leadership sets them under settings.</summary>
public sealed record InactivityThresholds(int FollowUpDays, int NoteDays, int TerminationDays)
{
    public const int MaxDays = 365;

    public static InactivityThresholds Default { get; } = new(14, 30, 60);
}

/// <summary>How far an agent's silence has gone, and so which action the radar offers.</summary>
public enum InactivityTier
{
    None = 0,
    FollowUp = 1,
    Note = 2,
    Termination = 3,
}

/// <summary>The trace the silence is counted from.</summary>
public enum InactivityTraceKind
{
    /// <summary>No trace since the account was released; the release date stands in.</summary>
    Released = 0,
    Write = 1,
    Read = 2,
    Seen = 3,

    /// <summary>No trace since the account became selectable again (unblocked, reinstated, made internal).</summary>
    Reactivated = 4,
}

/// <summary>One silent agent on the radar.</summary>
/// <remarks>
/// <see cref="SilentDays"/> counts every calendar day since the last trace, <see cref="AbsentDays"/> the ones an
/// absence covers, <see cref="InactiveDays"/> the difference the tier is read from. A nudge only shows when it is
/// newer than the last trace: an agent who answered and fell silent again starts over. <see cref="NudgeLockedUntil"/>
/// reads the stored nudge regardless, as the service does. <see cref="Provisional"/>: the trace predates the counting
/// of page visits, so the tier is held at <see cref="InactivityTier.FollowUp"/> for now.
/// </remarks>
public sealed record InactivityRow(
    string AgentId,
    string Codename,
    string? RealName,
    string? BadgeNumber,
    Rank? Rank,
    DateTime LastTraceAt,
    InactivityTraceKind LastTraceKind,
    DateTime? LastWriteAt,
    DateTime? LastReadAt,
    DateTime? LastSeenAt,
    int SilentDays,
    int AbsentDays,
    int InactiveDays,
    InactivityTier Tier,
    DateOnly? AbsentUntil,
    DateTime? NudgedAt,
    string? NudgedByName,
    DateTime? NudgeLockedUntil,
    bool Provisional);

/// <summary>The radar as the personnel page shows it.</summary>
/// <param name="VisitsCountedFrom">When page visits started being stamped; null where nothing recorded it.</param>
public sealed record InactivityRadar(InactivityThresholds Thresholds, IReadOnlyList<InactivityRow> Rows,
    DateTime? VisitsCountedFrom);

/// <summary>Display labels and chip colours.</summary>
public static class InactivityDisplay
{
    public static string Tier(InactivityTier tier) => tier switch
    {
        InactivityTier.FollowUp => "Nachfassen",
        InactivityTier.Note => "Vermerk",
        InactivityTier.Termination => "Kündigung prüfen",
        _ => "Aktiv",
    };

    public static MudBlazor.Color TierColour(InactivityTier tier) => tier switch
    {
        InactivityTier.FollowUp => MudBlazor.Color.Info,
        InactivityTier.Note => MudBlazor.Color.Warning,
        InactivityTier.Termination => MudBlazor.Color.Error,
        _ => MudBlazor.Color.Default,
    };

    public static string Trace(InactivityTraceKind kind) => kind switch
    {
        InactivityTraceKind.Write => "geschrieben",
        InactivityTraceKind.Read => "Akte geöffnet",
        InactivityTraceKind.Seen => "Seite besucht",
        InactivityTraceKind.Reactivated => "wieder aktiv",
        _ => "freigegeben",
    };
}
