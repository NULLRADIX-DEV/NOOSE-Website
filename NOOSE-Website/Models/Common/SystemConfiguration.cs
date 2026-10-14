using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Common;

/// <summary>Key constants for the system-settings table.</summary>
public static class SystemSettingKeys
{
    public const string MaintenanceModeActive = "WartungsmodusAktiv";
    public const string MaintenanceModeText = "WartungsmodusText";
    public const string BannerText = "BannerText";
    public const string BannerLevel = "BannerStufe";
    public const string ThemePrimary = "ThemePrimary";
    public const string ThemeSecondary = "ThemeSecondary";
    public const string ThemeTertiary = "ThemeTertiary";
    public const string LogoFileName = "LogoDateiname";
    public const string LogoContentType = "LogoContentType";
    public const string DemoModeActive = "DemoModusAktiv";

    /// <summary>Min hazard level for auto-listing a person on the wanted board; manual entries always show.</summary>
    public const string WantedBoardMinHazard = "FahndungMinGefahrenstufe";

    /// <summary>How many closed meetings the attendance anomaly check looks back over.</summary>
    public const string MeetingWindowSize = "BesprechungFensterGroesse";

    /// <summary>Unexcused absences within the window that turn an agent yellow.</summary>
    public const string MeetingAnomalyYellow = "BesprechungAnomalieGelb";

    /// <summary>Unexcused absences within the window that turn an agent red.</summary>
    public const string MeetingAnomalyRed = "BesprechungAnomalieRot";

    // Discord outgoing webhooks (one channel per notification category)
    public const string DiscordEnabled = "DiscordAktiv";
    public const string SiteBaseUrl = "SeitenBasisUrl";

    /// <summary>Row key of a category's webhook URL: prefix + NotificationType name.</summary>
    public const string DiscordWebhookPrefix = "DiscordWebhook.";

    /// <summary>Row key of a category's Discord role mention: prefix + NotificationType name.</summary>
    public const string DiscordRolePrefix = "DiscordRolle.";

    /// <summary>Whether role-ping Discord posts include the record header/title (Announcement, Recruiting). Default on.</summary>
    public const string DiscordIncludeHeadline = "DiscordHeaderInhalt";

    // periodic top-3 agent announcement; the interval also drives its wording
    public const string BestAgentEnabled = "BesterAgentAktiv";
    public const string BestAgentIntervalDays = "BesterAgentIntervallTage";
    public const string BestAgentCreateNote = "BesterAgentVermerkAktiv";
    /// <summary>Last time the announcement ran (round-trip UTC); worker guard, not user-edited.</summary>
    public const string BestAgentLastRun = "BesterAgentZuletzt";

    /// <summary>Last time the daily ping digest went out (round-trip UTC); worker guard, not user-edited.</summary>
    public const string PingDigestLastRun = "TagesueberblickZuletzt";

    /// <summary>Since when the NOOSEI anomaly watch reports (round-trip UTC); empty = lay a silent baseline first. Not user-edited.</summary>
    public const string NooseiAnomalyWatchArmedAt = "KiAuffaelligkeitenScharfAb";

    /// <summary>Set once the one-shot search side-index backfill has fully completed; guards against re-scanning. Not user-edited.</summary>
    public const string SearchIndexBackfillDone = "SuchIndexBackfillFertig";

    /// <summary>Shuts the whole public area down at once; beats every single module switch.</summary>
    public const string PublicAreaKillSwitch = "OeffentlicherBereichNotAus";

    /// <summary>Published situation level, stored by name rather than by number.</summary>
    /// <remarks>
    /// Four rows instead of a table, the way the kill switch is stored: there is exactly one of these, ever. The
    /// name is stored because a bare "2" says nothing to whoever reads the settings table or the audit row.
    /// </remarks>
    public const string PublicSituationLevel = "GefahrenlageStufe";

    /// <summary>Assessment shown under the level; plain text, never HTML.</summary>
    public const string PublicSituationNote = "GefahrenlageEinschaetzung";

    /// <summary>Round-trip UTC of the last change of level; a corrected assessment does not move it.</summary>
    public const string PublicSituationSince = "GefahrenlageSeit";

    /// <summary>The level that stood before the current one; empty while the first one still stands.</summary>
    public const string PublicSituationPrevious = "GefahrenlageZuvor";
}

/// <summary>Banner urgency, stored as text; UI maps it to MudBlazor severity.</summary>
public static class BannerLevels
{
    public const string Info = "Info";
    public const string Warning = "Warnung";
    public const string Error = "Fehler";

    public static readonly IReadOnlyList<string> All = new[] { Info, Warning, Error };
}

/// <summary>Cached read snapshot of all system settings; null values fall back to code defaults.</summary>
public sealed record SystemConfiguration(
    bool MaintenanceModeActive,
    string? MaintenanceModeText,
    string? BannerText,
    string BannerLevel,
    string? ThemePrimary,
    string? ThemeSecondary,
    string? ThemeTertiary,
    string? LogoFileName,
    string? LogoContentType,
    bool DemoModeActive,
    HazardLevel WantedBoardMinHazard,
    int MeetingWindowSize,
    int MeetingAnomalyYellow,
    int MeetingAnomalyRed)
{
    public bool HasLogo => !string.IsNullOrWhiteSpace(LogoFileName);
}

/// <summary>Input model for the admin System page; logo goes through the separate upload path.</summary>
public class SystemConfigurationInput
{
    public bool MaintenanceModeActive { get; set; }
    public string? MaintenanceModeText { get; set; }
    public string? BannerText { get; set; }
    public string BannerLevel { get; set; } = BannerLevels.Info;
    public string? ThemePrimary { get; set; }
    public string? ThemeSecondary { get; set; }
    public string? ThemeTertiary { get; set; }
    public bool DemoModeActive { get; set; }
    public HazardLevel WantedBoardMinHazard { get; set; } = HazardLevel.Critical;
    public int MeetingWindowSize { get; set; } = MeetingAnomalyDefaults.WindowSize;
    public int MeetingAnomalyYellow { get; set; } = MeetingAnomalyDefaults.Yellow;
    public int MeetingAnomalyRed { get; set; } = MeetingAnomalyDefaults.Red;
}

/// <summary>Code defaults for the attendance anomaly thresholds.</summary>
public static class MeetingAnomalyDefaults
{
    public const int WindowSize = 5;
    public const int Yellow = 2;
    public const int Red = 3;
}
