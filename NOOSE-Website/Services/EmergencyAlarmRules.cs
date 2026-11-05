using System.Security.Claims;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data.Entities.Operations;

namespace NOOSE_Website.Services;

/// <summary>Who may trigger, read and end an emergency alarm, and when it counts as running. Sole source of these rules.</summary>
public static class EmergencyAlarmRules
{
    /// <summary>How long agents may answer; fixed, not chosen per alarm.</summary>
    public static readonly TimeSpan Duration = TimeSpan.FromMinutes(30);

    /// <summary>Max length of the situation.</summary>
    public const int MaxSituation = 500;

    /// <summary>Max length of the location.</summary>
    public const int MaxLocation = 128;

    /// <summary>Internal writers; never partners or read-only supervision.</summary>
    public static bool MayTrigger(ClaimsPrincipal actor) => actor.IsInternalAgent() && actor.MayWrite();

    /// <summary>Below leadership the triggerer has to say why waiting was not possible.</summary>
    public static bool NeedsReason(ClaimsPrincipal actor) => !actor.IsLeadership();

    /// <summary>Running = not ended by hand and not yet past its expiry.</summary>
    public static bool IsOpen(EmergencyAlarm alarm, DateTime nowUtc) => alarm.EndedAt is null && nowUtc < alarm.Expiry;

    /// <summary>The triggerer or leadership, always with write access.</summary>
    public static bool MayEnd(ClaimsPrincipal actor, EmergencyAlarm alarm)
        => actor.MayWrite() && (IsTriggerer(actor, alarm) || actor.IsLeadership());

    /// <summary>Internal agents including read-only supervision; partners see nothing.</summary>
    public static bool MayRead(ClaimsPrincipal actor) => actor.IsInternalAgent();

    private static bool IsTriggerer(ClaimsPrincipal actor, EmergencyAlarm alarm)
        => actor.GetAgentId() is { Length: > 0 } id && string.Equals(id, alarm.TriggeredById, StringComparison.Ordinal);
}
