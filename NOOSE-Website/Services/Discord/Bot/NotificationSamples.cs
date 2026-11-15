using System.Globalization;
using NetCord.Rest;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Services.Discord.Bot;

/// <summary>Sample cards for the test buttons in the settings; pure, marked as test, never a real record.</summary>
public static class NotificationSamples
{
    /// <summary>First line of every sample card.</summary>
    public const string Marker = "-# 🧪 Testnachricht – kein echtes Ereignis";

    /// <summary>Record id the sample buttons carry; a click finds nothing.</summary>
    public const string RecordId = "test";

    private const string Reason = "🧪 Testnachricht – so sieht diese Kategorie im Kanal aus.";

    /// <summary>The real card of a category with sample data; chips are shown, the caller sends without pings.</summary>
    public static IReadOnlyList<IMessageComponentProperties> Card(NotificationType type, string baseUrl,
        string? testerDiscordId, string? roleId, DateTime nowUtc)
    {
        var root = (string.IsNullOrWhiteSpace(baseUrl) ? DiscordWebhookConfig.DefaultBaseUrl : baseUrl).TrimEnd('/');
        var tester = UserChip(testerDiscordId);
        var chip = DiscordRouting.PingsRole(type) ? RoleChip(roleId)
            : DiscordRouting.PingsRecipients(type) ? tester
            : string.Empty;
        var ping = chip.Length > 0 ? $"{Marker}\n{chip}" : Marker;
        var link = root + DiscordWebhookService.FallbackRoute(type);
        return type switch
        {
            NotificationType.EmergencyAlarm => Alarm(root, testerDiscordId, nowUtc),
            NotificationType.PersonnelEntry => NotificationCards.PersonnelEntry(ping, "Max Mustermann", "Belobigung", nowUtc,
                Reason, "Max Mustermann", link),
            NotificationType.DecorationAwarded => NotificationCards.Decoration(ping, "MUSTERMANN", "Verdienstmedaille", nowUtc,
                Reason, "Director Muster", link),
            NotificationType.AgentTerminated => NotificationCards.Termination("Max Mustermann", tester.Length > 0 ? tester : null,
                "Director Muster", nowUtc, Reason, link),
            NotificationType.PublicWantedPublished or NotificationType.PublicWantedBountyRaised
                => NotificationCards.Wanted(Wanted(type, nowUtc), root, ping),
            NotificationType.PublicPressPublished => NotificationCards.Custom(type,
                "📰 **Neue Pressemitteilung** — Testmeldung (FIB-PR-2026-0001)", link, ping),
            NotificationType.PingDigest => NotificationCards.Digest(
                $"{Marker}\n**Tagesüberblick**\n{(tester.Length > 0 ? tester + " " : "")}3 ungelesene Meldungen: 1 Erwähnung, 2 Aufgaben",
                link),
            _ => NotificationCards.Generic(type, root, Href(type), null, ping, acknowledgeable: true),
        };
    }

    /// <summary>A sample DVO post with one change, one new paragraph and one correction.</summary>
    public static IReadOnlyList<IReadOnlyList<IMessageComponentProperties>> Regulation(string? roleId, string? testerDiscordId,
        string baseUrl)
    {
        var root = (string.IsNullOrWhiteSpace(baseUrl) ? DiscordWebhookConfig.DefaultBaseUrl : baseUrl).TrimEnd('/');
        var tester = UserChip(testerDiscordId);
        RegulationChange[] changes =
        [
            new("TEST · DVO Abschnitt 5 · Dienstkleidung", RegulationChangeKind.Changed,
                "<p>Im Dienst tragen Agenten <del class=\"noosei-weg\">eine Weste</del> "
                + "<ins class=\"noosei-neu\">die Dienstkleidung nach Anlage 2</ins>.</p>",
                null, "Testnachricht", null, []),
            new("TEST · DVO Abschnitt 6 · Funkdisziplin", RegulationChangeKind.Introduced, null,
                "<p>Funksprüche sind kurz zu halten.</p><p>Dies ist eine Testnachricht, kein echtes Ereignis.</p>",
                null, null, []),
            new("TEST · DVO Abschnitt 7 · Fahrzeuge", RegulationChangeKind.Correction, null, null,
                "Tippfehler korrigiert (Testnachricht)", null, []),
        ];
        var role = DiscordWebhookService.IsSnowflake(roleId) ? roleId! : string.Empty;
        return RegulationChangePost.Render(changes, role,
            [(tester.Length > 0 ? tester : "Director Muster", "Director")], root);
    }

    /// <summary>A sample href per category, with an id where the real one has one.</summary>
    internal static string Href(NotificationType type) => type switch
    {
        NotificationType.JobAssigned or NotificationType.JobDueSoon => $"/aufgaben/{RecordId}",
        NotificationType.Announcement or NotificationType.AcknowledgmentReminder => $"/brett/{RecordId}",
        NotificationType.Recruiting => $"/bewerbungen/{RecordId}",
        NotificationType.PublicTipCreated => $"/hinweise/{RecordId}",
        NotificationType.PublicTicketCreated => $"/tickets/{RecordId}",
        NotificationType.Mention or NotificationType.Followup => $"/personen/{RecordId}",
        NotificationType.SituationReport => $"/lageberichte/{RecordId}",
        NotificationType.TaskforceChat => $"/taskforces/{RecordId}",
        NotificationType.TestDueReminder => $"/tests/{RecordId}",
        _ => DiscordWebhookService.FallbackRoute(type),
    };

    // open alarm, three answers
    private static IReadOnlyList<IMessageComponentProperties> Alarm(string root, string? testerDiscordId, DateTime nowUtc)
    {
        var alarm = new EmergencyAlarm
        {
            Id = RecordId,
            Situation = "🧪 Testalarm – kein echter Einsatz",
            Location = "Mission Row",
            TriggeredByName = "TESTER",
            TriggeredAt = nowUtc,
            Expiry = nowUtc.AddMinutes(30),
        };
        AlarmCardRow[] rows =
        [
            new(EmergencyAlarmAnswer.Coming, "ALPHA", null),
            new(EmergencyAlarmAnswer.NotComing, "BRAVO", null),
            new(null, "TESTER", testerDiscordId),
        ];
        return DiscordLiveBoards.RenderAlarm(alarm, rows, root, nowUtc).Card.Components;
    }

    // outward card only
    private static WantedDiscordEmbed Wanted(NotificationType type, DateTime nowUtc)
        => new(type == NotificationType.PublicWantedBountyRaised ? "🧪 Testfahndung – Kopfgeld erhöht" : "🧪 Testfahndung",
            "/gesucht", 0, [
                new("Name", "Max Mustermann", true),
                new("Belohnung", 5000.ToString("N0", CultureInfo.GetCultureInfo("de-DE")) + " $", true),
                new("Hinweis", "Testnachricht – kein echtes Ereignis.", false),
            ], null, null, "FIB", nowUtc);

    private static string UserChip(string? discordId)
        => DiscordWebhookService.IsSnowflake(discordId) && discordId!.Length <= 20 ? $"<@{discordId}>" : string.Empty;

    private static string RoleChip(string? roleId)
        => DiscordWebhookService.IsSnowflake(roleId) ? NotificationCards.RolePing(roleId!) : string.Empty;
}
