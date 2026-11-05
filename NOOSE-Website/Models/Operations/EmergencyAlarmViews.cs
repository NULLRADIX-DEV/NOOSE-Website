using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Operations;

/// <summary>What the trigger dialog sends; <see cref="Target"/> null = all agents.</summary>
public record EmergencyAlarmInput(string Situation, string? Location, Division? Target, string? Reason);

/// <summary>One alarm in the list and the banner, with its answer counts.</summary>
public record EmergencyAlarmSummary(string Id, string Situation, Division? Target, string TriggeredByName,
    DateTime TriggeredAt, bool IsOpen, int Coming, int NotComing, int Open, string? OperationId, bool IsEmergency);

/// <summary>One recipient and their answer; null = still open.</summary>
public record EmergencyAlarmRow(string AgentId, string Codename, EmergencyAlarmAnswer? Answer, DateTime? AnsweredAt);

/// <summary>The alarm page: the summary, the answer list and what the viewer may do.</summary>
public record EmergencyAlarmDetail(EmergencyAlarmSummary Summary, string? Location, string? Reason, DateTime Expiry,
    bool MayAnswer, EmergencyAlarmAnswer? MyAnswer, bool MayReping, bool MayEnd, bool MayConvert,
    IReadOnlyList<EmergencyAlarmRow> Rows);
