namespace NOOSE_Website.Services;

/// <summary>Notification titles of an emergency alarm; the title is also the Discord headline.</summary>
public static class EmergencyAlarmText
{
    /// <summary>Longest notification title; the notification service does not cut.</summary>
    public const int MaxTitle = 300;

    /// <summary>Recipient title: situation, then the location when given.</summary>
    public static string Title(string situation, string? location) => Cut("Einsatzalarm: " + Where(situation, location));

    /// <summary>Recipient title of the one re-ping.</summary>
    public static string RepingTitle(string situation, string? location)
        => Cut("Einsatzalarm (erneut): " + Where(situation, location));

    /// <summary>Leadership title for an alarm on an agent's own word; never carries the reason.</summary>
    public static string ByAgentTitle(string triggeredByName, string situation)
        => Cut($"Gefahr im Verzug: {triggeredByName} hat einen Einsatzalarm ausgelöst – {situation}");

    private static string Where(string situation, string? location)
        => string.IsNullOrWhiteSpace(location) ? situation : $"{situation} · {location.Trim()}";

    private static string Cut(string title) => title.Length <= MaxTitle ? title : title[..(MaxTitle - 1)] + "…";
}
