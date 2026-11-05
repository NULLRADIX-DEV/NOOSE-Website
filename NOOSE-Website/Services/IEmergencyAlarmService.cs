using System.Security.Claims;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Operations;

namespace NOOSE_Website.Services;

/// <summary>Emergency alarms: call agents in, collect their answers live, re-ping once, end.</summary>
public interface IEmergencyAlarmService
{
    /// <summary>Freezes the recipients, notifies them and returns the alarm id.</summary>
    Task<string> TriggerAsync(EmergencyAlarmInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Sets or changes the caller's answer while the alarm runs.</summary>
    Task RespondAsync(string alarmId, EmergencyAlarmAnswer answer, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    /// <summary>Pings the recipients without an answer again; triggerer only, once per alarm.</summary>
    Task RepingAsync(string alarmId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Ends a running alarm by hand; ending a closed one is a no-op.</summary>
    Task EndAsync(string alarmId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Opens a running operation from an ended alarm, allocates everyone coming and returns its id.</summary>
    Task<string> ConvertToOperationAsync(string alarmId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Links an ended alarm to an existing operation and allocates those coming who are not on it yet.</summary>
    Task AttachToOperationAsync(string alarmId, string operationId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    /// <summary>The latest 100 alarms, newest first; empty for partners.</summary>
    Task<List<EmergencyAlarmSummary>> GetListAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>One alarm with its answer list; null when missing or not readable.</summary>
    Task<EmergencyAlarmDetail?> GetDetailAsync(string alarmId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    /// <summary>Running alarms that still wait for the caller's answer, for the banner.</summary>
    Task<List<EmergencyAlarmSummary>> GetOpenForMeAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}
