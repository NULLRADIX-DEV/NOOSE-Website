namespace NOOSE_Website.Models.Personnel;

/// <summary>What the printed service ID card shows.</summary>
/// <param name="DisplayName">Picked through <c>AgentNameDisplay</c>: the real name only for viewers who may read it.</param>
/// <param name="PhotoUrl">The approved profile picture, or null for the initials.</param>
/// <param name="VerifyAddress">Public check address without scheme, as printed under the QR code.</param>
/// <param name="QrSvg">Encoder output for the check address; null when it does not fit.</param>
/// <param name="CheckOnline">False while the public check module is off: the QR code then leads nowhere yet.</param>
public sealed record ServiceIdCard(
    string AgentId,
    string DisplayName,
    string RankName,
    string? DivisionName,
    string Number,
    string? PhotoUrl,
    string Initials,
    DateTime? ServingSince,
    string VerifyAddress,
    string? QrSvg,
    bool CheckOnline);

/// <summary>Outcome of asking for a card: the access verdict, and the card only when it is allowed.</summary>
public sealed record ServiceIdResult(NOOSE_Website.Services.ServiceIdAccess Access, ServiceIdCard? Card);
