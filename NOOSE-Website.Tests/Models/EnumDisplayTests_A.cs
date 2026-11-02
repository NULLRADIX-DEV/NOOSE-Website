using MudBlazor;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Tests.Models;

/// <summary>
/// Covers the static display companion classes for the enums in Models/Enums:
/// every value maps to a non-empty label, the default/fallback switch arm is
/// exercised, and the auxiliary predicate/colour/icon/All members behave.
/// </summary>
public class EnumDisplayTests_A
{
    // Undefined value used to hit the default/fallback ("_") switch arm.
    private const int Undefined = 999;

    // ---------------------------------------------------------------------
    // AgentNoteKind
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(AgentNoteKind.Commendation, "Belobigung")]
    [InlineData(AgentNoteKind.Disciplinary, "Negativer Vermerk")]
    [InlineData(AgentNoteKind.Specialization, "Spezialisierung")]
    [InlineData(AgentNoteKind.Department, "Abteilung")]
    [InlineData(AgentNoteKind.Training, "Ausbildung")]
    [InlineData(AgentNoteKind.Information, "Information")]
    public void AgentNoteKind_Name_MapsEachValue(AgentNoteKind kind, string expected)
    {
        Assert.Equal(expected, AgentNoteKindDisplay.Name(kind));
    }

    [Fact]
    public void AgentNoteKind_Name_EveryValue_NonEmpty()
    {
        foreach (var value in Enum.GetValues<AgentNoteKind>())
            Assert.False(string.IsNullOrEmpty(AgentNoteKindDisplay.Name(value)));
    }

    [Fact]
    public void AgentNoteKind_Name_Undefined_ReturnsFallback()
    {
        Assert.Equal("—", AgentNoteKindDisplay.Name((AgentNoteKind)Undefined));
    }

    // ---------------------------------------------------------------------
    // AnnouncementAudience
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(AnnouncementAudience.AllActive, "Alle aktiven Agenten")]
    [InlineData(AnnouncementAudience.Taskforce, "Bestimmte Taskforce")]
    [InlineData(AnnouncementAudience.TruUnit, "TRU-Einheit")]
    [InlineData(AnnouncementAudience.HrbUnit, "HRB-Einheit")]
    [InlineData(AnnouncementAudience.FromRank, "Ab Dienstgrad")]
    public void AnnouncementAudience_Name_MapsEachValue(AnnouncementAudience audience, string expected)
    {
        Assert.Equal(expected, AnnouncementAudienceDisplay.Name(audience));
    }

    [Fact]
    public void AnnouncementAudience_Name_EveryValue_NonEmpty()
    {
        foreach (var value in Enum.GetValues<AnnouncementAudience>())
            Assert.False(string.IsNullOrEmpty(AnnouncementAudienceDisplay.Name(value)));
    }

    [Fact]
    public void AnnouncementAudience_Name_Undefined_ReturnsFallback()
    {
        Assert.Equal("—", AnnouncementAudienceDisplay.Name((AnnouncementAudience)Undefined));
    }

    [Fact]
    public void AnnouncementAudience_All_ContainsEveryDefinedValue()
    {
        AssertAllCoversEnum(AnnouncementAudienceDisplay.All);
    }

    // ---------------------------------------------------------------------
    // AppointmentCategory
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(AppointmentCategory.CourtDate, "Gerichtstermin")]
    [InlineData(AppointmentCategory.Meeting, "Interner Termin")]
    [InlineData(AppointmentCategory.Deployment, "Einsatz")]
    [InlineData(AppointmentCategory.Deadline, "Frist")]
    [InlineData(AppointmentCategory.Misc, "Sonstiges")]
    public void AppointmentCategory_Name_MapsEachValue(AppointmentCategory category, string expected)
    {
        Assert.Equal(expected, AppointmentCategoryDisplay.Name(category));
    }

    [Fact]
    public void AppointmentCategory_Name_EveryValue_NonEmpty()
    {
        foreach (var value in Enum.GetValues<AppointmentCategory>())
            Assert.False(string.IsNullOrEmpty(AppointmentCategoryDisplay.Name(value)));
    }

    [Fact]
    public void AppointmentCategory_Name_Undefined_ReturnsFallback()
    {
        Assert.Equal("—", AppointmentCategoryDisplay.Name((AppointmentCategory)Undefined));
    }

    [Fact]
    public void AppointmentCategory_All_ContainsEveryDefinedValue()
    {
        AssertAllCoversEnum(AppointmentCategoryDisplay.All);
    }

    // ---------------------------------------------------------------------
    // AppointmentStatus
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(AppointmentStatus.Planned, "Geplant")]
    [InlineData(AppointmentStatus.Perceived, "Wahrgenommen")]
    [InlineData(AppointmentStatus.Canceled, "Abgesagt")]
    [InlineData(AppointmentStatus.Postponed, "Verschoben")]
    public void AppointmentStatus_Name_MapsEachValue(AppointmentStatus status, string expected)
    {
        Assert.Equal(expected, AppointmentStatusDisplay.Name(status));
    }

    [Fact]
    public void AppointmentStatus_Name_EveryValue_NonEmpty()
    {
        foreach (var value in Enum.GetValues<AppointmentStatus>())
            Assert.False(string.IsNullOrEmpty(AppointmentStatusDisplay.Name(value)));
    }

    [Fact]
    public void AppointmentStatus_Name_Undefined_ReturnsFallback()
    {
        Assert.Equal("—", AppointmentStatusDisplay.Name((AppointmentStatus)Undefined));
    }

    [Theory]
    [InlineData(AppointmentStatus.Planned, false)]
    [InlineData(AppointmentStatus.Perceived, false)]
    [InlineData(AppointmentStatus.Canceled, true)]
    [InlineData(AppointmentStatus.Postponed, true)]
    public void AppointmentStatus_IsObsolete_MatchesCanceledOrPostponed(AppointmentStatus status, bool expected)
    {
        Assert.Equal(expected, AppointmentStatusDisplay.IsObsolete(status));
    }

    [Fact]
    public void AppointmentStatus_All_ContainsEveryDefinedValue()
    {
        AssertAllCoversEnum(AppointmentStatusDisplay.All);
    }

    // ---------------------------------------------------------------------
    // AppointmentVisibilityLevel
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(AppointmentVisibilityLevel.Public, "Öffentlich")]
    [InlineData(AppointmentVisibilityLevel.Restricted, "Eingeschränkt")]
    [InlineData(AppointmentVisibilityLevel.Private, "Privat")]
    public void AppointmentVisibilityLevel_Name_MapsEachValue(AppointmentVisibilityLevel level, string expected)
    {
        Assert.Equal(expected, AppointmentVisibilityLevelDisplay.Name(level));
    }

    [Fact]
    public void AppointmentVisibilityLevel_Name_EveryValue_NonEmpty()
    {
        foreach (var value in Enum.GetValues<AppointmentVisibilityLevel>())
            Assert.False(string.IsNullOrEmpty(AppointmentVisibilityLevelDisplay.Name(value)));
    }

    [Fact]
    public void AppointmentVisibilityLevel_Name_Undefined_ReturnsFallback()
    {
        Assert.Equal("—", AppointmentVisibilityLevelDisplay.Name((AppointmentVisibilityLevel)Undefined));
    }

    [Fact]
    public void AppointmentVisibilityLevel_Help_EveryValue_NonEmpty()
    {
        foreach (var value in Enum.GetValues<AppointmentVisibilityLevel>())
            Assert.False(string.IsNullOrEmpty(AppointmentVisibilityLevelDisplay.Help(value)));
    }

    [Fact]
    public void AppointmentVisibilityLevel_Help_Undefined_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, AppointmentVisibilityLevelDisplay.Help((AppointmentVisibilityLevel)Undefined));
    }

    [Theory]
    [InlineData(AppointmentVisibilityLevel.Public)]
    [InlineData(AppointmentVisibilityLevel.Restricted)]
    [InlineData(AppointmentVisibilityLevel.Private)]
    public void AppointmentVisibilityLevel_Icon_EveryValue_NonEmpty(AppointmentVisibilityLevel level)
    {
        Assert.False(string.IsNullOrEmpty(AppointmentVisibilityLevelDisplay.Icon(level)));
    }

    [Fact]
    public void AppointmentVisibilityLevel_Icon_MapsKnownValues()
    {
        Assert.Equal(Icons.Material.Filled.Public, AppointmentVisibilityLevelDisplay.Icon(AppointmentVisibilityLevel.Public));
        Assert.Equal(Icons.Material.Filled.Lock, AppointmentVisibilityLevelDisplay.Icon(AppointmentVisibilityLevel.Restricted));
        Assert.Equal(Icons.Material.Filled.PersonOff, AppointmentVisibilityLevelDisplay.Icon(AppointmentVisibilityLevel.Private));
    }

    [Fact]
    public void AppointmentVisibilityLevel_Icon_Undefined_ReturnsEventFallback()
    {
        Assert.Equal(Icons.Material.Filled.Event, AppointmentVisibilityLevelDisplay.Icon((AppointmentVisibilityLevel)Undefined));
    }

    [Fact]
    public void AppointmentVisibilityLevel_All_ContainsEveryDefinedValue()
    {
        AssertAllCoversEnum(AppointmentVisibilityLevelDisplay.All);
    }

    // ---------------------------------------------------------------------
    // AttendanceAnomalyLevel
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(AttendanceAnomalyLevel.None, "Unauffällig")]
    [InlineData(AttendanceAnomalyLevel.Insufficient, "Zu wenig Daten")]
    [InlineData(AttendanceAnomalyLevel.Yellow, "Auffällig")]
    [InlineData(AttendanceAnomalyLevel.Red, "Stark auffällig")]
    public void AttendanceAnomalyLevel_Name_MapsEachValue(AttendanceAnomalyLevel level, string expected)
    {
        Assert.Equal(expected, AttendanceAnomalyLevelDisplay.Name(level));
    }

    [Fact]
    public void AttendanceAnomalyLevel_Name_EveryValue_NonEmpty()
    {
        foreach (var value in Enum.GetValues<AttendanceAnomalyLevel>())
            Assert.False(string.IsNullOrEmpty(AttendanceAnomalyLevelDisplay.Name(value)));
    }

    [Fact]
    public void AttendanceAnomalyLevel_Name_Undefined_ReturnsFallback()
    {
        Assert.Equal("—", AttendanceAnomalyLevelDisplay.Name((AttendanceAnomalyLevel)Undefined));
    }

    [Theory]
    [InlineData(AttendanceAnomalyLevel.None, "#3FB950")]
    [InlineData(AttendanceAnomalyLevel.Insufficient, "#9A9488")]
    [InlineData(AttendanceAnomalyLevel.Yellow, "#D29922")]
    [InlineData(AttendanceAnomalyLevel.Red, "#F85149")]
    public void AttendanceAnomalyLevel_Colour_MapsEachValue(AttendanceAnomalyLevel level, string expected)
    {
        Assert.Equal(expected, AttendanceAnomalyLevelDisplay.Colour(level));
    }

    [Fact]
    public void AttendanceAnomalyLevel_Colour_Undefined_ReturnsGreyFallback()
    {
        Assert.Equal("#9A9488", AttendanceAnomalyLevelDisplay.Colour((AttendanceAnomalyLevel)Undefined));
    }

    [Fact]
    public void AttendanceAnomalyLevel_All_ContainsEveryDefinedValue()
    {
        AssertAllCoversEnum(AttendanceAnomalyLevelDisplay.All);
    }

    // ---------------------------------------------------------------------
    // BewerbungStatus
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(BewerbungStatus.Eingereicht, "Eingereicht")]
    [InlineData(BewerbungStatus.InSicherheitspruefung, "Sicherheitsüberprüfung")]
    [InlineData(BewerbungStatus.ImTest, "Test")]
    [InlineData(BewerbungStatus.ImVorstellungsgespraech, "Vorstellungsgespräch")]
    [InlineData(BewerbungStatus.Angenommen, "Angenommen")]
    [InlineData(BewerbungStatus.Abgelehnt, "Abgelehnt")]
    [InlineData(BewerbungStatus.Geschlossen, "Geschlossen")]
    public void BewerbungStatus_Name_MapsEachValue(BewerbungStatus status, string expected)
    {
        Assert.Equal(expected, BewerbungStatusDisplay.Name(status));
    }

    [Fact]
    public void BewerbungStatus_Name_EveryValue_NonEmpty()
    {
        foreach (var value in Enum.GetValues<BewerbungStatus>())
            Assert.False(string.IsNullOrEmpty(BewerbungStatusDisplay.Name(value)));
    }

    [Fact]
    public void BewerbungStatus_Name_Undefined_ReturnsFallback()
    {
        Assert.Equal("—", BewerbungStatusDisplay.Name((BewerbungStatus)Undefined));
    }

    [Theory]
    [InlineData(BewerbungStatus.Eingereicht, Color.Info)]
    [InlineData(BewerbungStatus.InSicherheitspruefung, Color.Warning)]
    [InlineData(BewerbungStatus.ImTest, Color.Warning)]
    [InlineData(BewerbungStatus.ImVorstellungsgespraech, Color.Primary)]
    [InlineData(BewerbungStatus.Angenommen, Color.Success)]
    [InlineData(BewerbungStatus.Abgelehnt, Color.Error)]
    [InlineData(BewerbungStatus.Geschlossen, Color.Default)]
    public void BewerbungStatus_ChipColor_MapsEachValue(BewerbungStatus status, Color expected)
    {
        Assert.Equal(expected, BewerbungStatusDisplay.ChipColor(status));
    }

    [Fact]
    public void BewerbungStatus_ChipColor_Undefined_ReturnsDefault()
    {
        Assert.Equal(Color.Default, BewerbungStatusDisplay.ChipColor((BewerbungStatus)Undefined));
    }

    [Theory]
    [InlineData(BewerbungStatus.Eingereicht, false)]
    [InlineData(BewerbungStatus.InSicherheitspruefung, false)]
    [InlineData(BewerbungStatus.ImTest, false)]
    [InlineData(BewerbungStatus.ImVorstellungsgespraech, false)]
    [InlineData(BewerbungStatus.Angenommen, true)]
    [InlineData(BewerbungStatus.Abgelehnt, true)]
    [InlineData(BewerbungStatus.Geschlossen, true)]
    public void BewerbungStatus_IsTerminal_MatchesTerminalStates(BewerbungStatus status, bool expected)
    {
        Assert.Equal(expected, BewerbungStatusDisplay.IsTerminal(status));
    }

    // ---------------------------------------------------------------------
    // CaseStatus
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(CaseStatus.Open, "Offen")]
    [InlineData(CaseStatus.InProcessing, "In Bearbeitung")]
    [InlineData(CaseStatus.Dormant, "Ruht")]
    [InlineData(CaseStatus.Completed, "Abgeschlossen")]
    [InlineData(CaseStatus.Archived, "Archiviert")]
    public void CaseStatus_Name_MapsEachValue(CaseStatus status, string expected)
    {
        Assert.Equal(expected, CaseStatusDisplay.Name(status));
    }

    [Fact]
    public void CaseStatus_Name_EveryValue_NonEmpty()
    {
        foreach (var value in Enum.GetValues<CaseStatus>())
            Assert.False(string.IsNullOrEmpty(CaseStatusDisplay.Name(value)));
    }

    [Fact]
    public void CaseStatus_Name_Undefined_ReturnsFallback()
    {
        Assert.Equal("—", CaseStatusDisplay.Name((CaseStatus)Undefined));
    }

    [Theory]
    [InlineData(CaseStatus.Open, true)]
    [InlineData(CaseStatus.InProcessing, true)]
    [InlineData(CaseStatus.Dormant, true)]
    [InlineData(CaseStatus.Completed, false)]
    [InlineData(CaseStatus.Archived, false)]
    public void CaseStatus_IsOpen_MatchesUnfinishedStates(CaseStatus status, bool expected)
    {
        Assert.Equal(expected, CaseStatusDisplay.IsOpen(status));
    }

    [Theory]
    [InlineData(CaseStatus.Open, false)]
    [InlineData(CaseStatus.InProcessing, false)]
    [InlineData(CaseStatus.Dormant, false)]
    [InlineData(CaseStatus.Completed, true)]
    [InlineData(CaseStatus.Archived, true)]
    public void CaseStatus_IsCompleted_MatchesFinishedStates(CaseStatus status, bool expected)
    {
        Assert.Equal(expected, CaseStatusDisplay.IsCompleted(status));
    }

    [Fact]
    public void CaseStatus_All_ContainsEveryDefinedValue()
    {
        AssertAllCoversEnum(CaseStatusDisplay.All);
    }

    // ---------------------------------------------------------------------
    // CustomFieldType
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(CustomFieldType.Text, "Text")]
    [InlineData(CustomFieldType.Multiline, "Text (mehrzeilig)")]
    [InlineData(CustomFieldType.Number, "Zahl")]
    [InlineData(CustomFieldType.Date, "Datum")]
    [InlineData(CustomFieldType.YesNo, "Ja/Nein")]
    [InlineData(CustomFieldType.Selection, "Auswahl")]
    public void CustomFieldType_Name_MapsEachValue(CustomFieldType type, string expected)
    {
        Assert.Equal(expected, CustomFieldTypeDisplay.Name(type));
    }

    [Fact]
    public void CustomFieldType_Name_EveryValue_NonEmpty()
    {
        foreach (var value in Enum.GetValues<CustomFieldType>())
            Assert.False(string.IsNullOrEmpty(CustomFieldTypeDisplay.Name(value)));
    }

    [Fact]
    public void CustomFieldType_Name_Undefined_ReturnsFallback()
    {
        Assert.Equal("—", CustomFieldTypeDisplay.Name((CustomFieldType)Undefined));
    }

    [Fact]
    public void CustomFieldType_All_ContainsEveryDefinedValue()
    {
        AssertAllCoversEnum(CustomFieldTypeDisplay.All);
    }

    // ---------------------------------------------------------------------
    // DocumentClassification
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(DocumentClassification.None, "Keine Verschlusssache")]
    [InlineData(DocumentClassification.Leadership, "Verschlusssache nur für Führung")]
    [InlineData(DocumentClassification.Tru, "Verschlusssache nur für TRU")]
    [InlineData(DocumentClassification.Hrb, "Verschlusssache nur für HRB")]
    public void DocumentClassification_Label_MapsEachValue(DocumentClassification classification, string expected)
    {
        Assert.Equal(expected, DocumentClassificationDisplay.Label(classification));
    }

    [Fact]
    public void DocumentClassification_Label_EveryValue_NonEmpty()
    {
        foreach (var value in Enum.GetValues<DocumentClassification>())
            Assert.False(string.IsNullOrEmpty(DocumentClassificationDisplay.Label(value)));
    }

    [Fact]
    public void DocumentClassification_Label_Undefined_ReturnsNoneFallback()
    {
        Assert.Equal("Keine Verschlusssache", DocumentClassificationDisplay.Label((DocumentClassification)Undefined));
    }

    [Theory]
    [InlineData(DocumentClassification.Leadership, "Verschlusssache")]
    [InlineData(DocumentClassification.Tru, "VS – TRU")]
    [InlineData(DocumentClassification.Hrb, "VS – HRB")]
    public void DocumentClassification_ChipLabel_MapsClassifiedValues(DocumentClassification classification, string expected)
    {
        Assert.Equal(expected, DocumentClassificationDisplay.ChipLabel(classification));
    }

    [Fact]
    public void DocumentClassification_ChipLabel_None_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, DocumentClassificationDisplay.ChipLabel(DocumentClassification.None));
    }

    [Fact]
    public void DocumentClassification_ChipLabel_Undefined_ReturnsEmptyFallback()
    {
        Assert.Equal(string.Empty, DocumentClassificationDisplay.ChipLabel((DocumentClassification)Undefined));
    }

    // ---------------------------------------------------------------------
    // GroupsKind
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(GroupsKind.Grouping, "Gruppierung")]
    [InlineData(GroupsKind.Personality, "Persönlichkeit")]
    [InlineData(GroupsKind.PersonOfInterest, "Person of Interest")]
    public void GroupsKind_Name_MapsEachValue(GroupsKind kind, string expected)
    {
        Assert.Equal(expected, GroupsKindDisplay.Name(kind));
    }

    [Fact]
    public void GroupsKind_Name_EveryValue_NonEmpty()
    {
        foreach (var value in Enum.GetValues<GroupsKind>())
            Assert.False(string.IsNullOrEmpty(GroupsKindDisplay.Name(value)));
    }

    [Fact]
    public void GroupsKind_Name_Undefined_ReturnsFallback()
    {
        Assert.Equal("—", GroupsKindDisplay.Name((GroupsKind)Undefined));
    }

    [Fact]
    public void GroupsKind_All_ContainsEveryDefinedValue()
    {
        AssertAllCoversEnum(GroupsKindDisplay.All);
    }

    // ---------------------------------------------------------------------
    // HazardLevel  (companion: HazardLevelLogic)
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(HazardLevel.No, "Keine")]
    [InlineData(HazardLevel.Low, "Niedrig")]
    [InlineData(HazardLevel.Medium, "Mittel")]
    [InlineData(HazardLevel.High, "Hoch")]
    [InlineData(HazardLevel.Critical, "Kritisch")]
    public void HazardLevel_Name_MapsEachValue(HazardLevel level, string expected)
    {
        Assert.Equal(expected, HazardLevelLogic.Name(level));
    }

    [Fact]
    public void HazardLevel_Name_EveryValue_NonEmpty()
    {
        foreach (var value in Enum.GetValues<HazardLevel>())
            Assert.False(string.IsNullOrEmpty(HazardLevelLogic.Name(value)));
    }

    [Fact]
    public void HazardLevel_Name_Undefined_ReturnsFallback()
    {
        Assert.Equal("—", HazardLevelLogic.Name((HazardLevel)Undefined));
    }

    [Theory]
    [InlineData(null, HazardLevel.No)]
    [InlineData(-5, HazardLevel.No)]     // <= 0
    [InlineData(0, HazardLevel.No)]      // boundary <= 0
    [InlineData(1, HazardLevel.Low)]     // just above 0
    [InlineData(24, HazardLevel.Low)]    // < 25
    [InlineData(25, HazardLevel.Medium)] // boundary
    [InlineData(49, HazardLevel.Medium)] // < 50
    [InlineData(50, HazardLevel.High)]   // boundary
    [InlineData(74, HazardLevel.High)]   // < 75
    [InlineData(75, HazardLevel.Critical)] // boundary
    [InlineData(100, HazardLevel.Critical)]
    [InlineData(1000, HazardLevel.Critical)]
    public void HazardLevel_From_MapsScoreToLevel(int? score, HazardLevel expected)
    {
        Assert.Equal(expected, HazardLevelLogic.From(score));
    }

    [Fact]
    public void HazardLevel_All_ContainsEveryDefinedValue()
    {
        AssertAllCoversEnum(HazardLevelLogic.All);
    }

    // ---------------------------------------------------------------------
    // JobPriority
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(JobPriority.Low, "Niedrig")]
    [InlineData(JobPriority.Normal, "Normal")]
    [InlineData(JobPriority.High, "Hoch")]
    public void JobPriority_Name_MapsEachValue(JobPriority priority, string expected)
    {
        Assert.Equal(expected, JobPriorityDisplay.Name(priority));
    }

    [Fact]
    public void JobPriority_Name_EveryValue_NonEmpty()
    {
        foreach (var value in Enum.GetValues<JobPriority>())
            Assert.False(string.IsNullOrEmpty(JobPriorityDisplay.Name(value)));
    }

    [Fact]
    public void JobPriority_Name_Undefined_ReturnsFallback()
    {
        Assert.Equal("—", JobPriorityDisplay.Name((JobPriority)Undefined));
    }

    [Fact]
    public void JobPriority_All_ContainsEveryDefinedValue()
    {
        AssertAllCoversEnum(JobPriorityDisplay.All);
    }

    // ---------------------------------------------------------------------
    // JobStatus
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(JobStatus.Open, "Offen")]
    [InlineData(JobStatus.InProcessing, "In Bearbeitung")]
    [InlineData(JobStatus.Done, "Erledigt")]
    [InlineData(JobStatus.Aborted, "Abgebrochen")]
    public void JobStatus_Name_MapsEachValue(JobStatus status, string expected)
    {
        Assert.Equal(expected, JobStatusDisplay.Name(status));
    }

    [Fact]
    public void JobStatus_Name_EveryValue_NonEmpty()
    {
        foreach (var value in Enum.GetValues<JobStatus>())
            Assert.False(string.IsNullOrEmpty(JobStatusDisplay.Name(value)));
    }

    [Fact]
    public void JobStatus_Name_Undefined_ReturnsFallback()
    {
        Assert.Equal("—", JobStatusDisplay.Name((JobStatus)Undefined));
    }

    [Theory]
    [InlineData(JobStatus.Open, true)]
    [InlineData(JobStatus.InProcessing, true)]
    [InlineData(JobStatus.Done, false)]
    [InlineData(JobStatus.Aborted, false)]
    public void JobStatus_IsOpen_MatchesUnfinishedStates(JobStatus status, bool expected)
    {
        Assert.Equal(expected, JobStatusDisplay.IsOpen(status));
    }

    [Theory]
    [InlineData(JobStatus.Open, false)]
    [InlineData(JobStatus.InProcessing, false)]
    [InlineData(JobStatus.Done, true)]
    [InlineData(JobStatus.Aborted, true)]
    public void JobStatus_IsCompleted_MatchesFinishedStates(JobStatus status, bool expected)
    {
        Assert.Equal(expected, JobStatusDisplay.IsCompleted(status));
    }

    [Fact]
    public void JobStatus_All_ContainsEveryDefinedValue()
    {
        AssertAllCoversEnum(JobStatusDisplay.All);
    }

    // ---------------------------------------------------------------------
    // MeetingAbsenceOrigin
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(MeetingAbsenceOrigin.None, "—")]
    [InlineData(MeetingAbsenceOrigin.Absence, "Abmeldung (Zeitraum)")]
    [InlineData(MeetingAbsenceOrigin.MeetingSignOff, "Abmeldung (Besprechung)")]
    [InlineData(MeetingAbsenceOrigin.Manual, "Manuell erfasst")]
    public void MeetingAbsenceOrigin_Name_MapsEachValue(MeetingAbsenceOrigin origin, string expected)
    {
        Assert.Equal(expected, MeetingAbsenceOriginDisplay.Name(origin));
    }

    [Fact]
    public void MeetingAbsenceOrigin_Name_EveryValue_NonEmpty()
    {
        foreach (var value in Enum.GetValues<MeetingAbsenceOrigin>())
            Assert.False(string.IsNullOrEmpty(MeetingAbsenceOriginDisplay.Name(value)));
    }

    [Fact]
    public void MeetingAbsenceOrigin_Name_Undefined_ReturnsFallback()
    {
        Assert.Equal("—", MeetingAbsenceOriginDisplay.Name((MeetingAbsenceOrigin)Undefined));
    }

    [Fact]
    public void MeetingAbsenceOrigin_All_ContainsEveryDefinedValue()
    {
        AssertAllCoversEnum(MeetingAbsenceOriginDisplay.All);
    }

    // ---------------------------------------------------------------------
    // MeetingAttendanceStatus
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(MeetingAttendanceStatus.Open, "Offen")]
    [InlineData(MeetingAttendanceStatus.Present, "Anwesend")]
    [InlineData(MeetingAttendanceStatus.SignedOff, "Abgemeldet")]
    [InlineData(MeetingAttendanceStatus.Missing, "Fehlend")]
    public void MeetingAttendanceStatus_Name_MapsEachValue(MeetingAttendanceStatus status, string expected)
    {
        Assert.Equal(expected, MeetingAttendanceStatusDisplay.Name(status));
    }

    [Fact]
    public void MeetingAttendanceStatus_Name_EveryValue_NonEmpty()
    {
        foreach (var value in Enum.GetValues<MeetingAttendanceStatus>())
            Assert.False(string.IsNullOrEmpty(MeetingAttendanceStatusDisplay.Name(value)));
    }

    [Fact]
    public void MeetingAttendanceStatus_Name_Undefined_ReturnsFallback()
    {
        Assert.Equal("—", MeetingAttendanceStatusDisplay.Name((MeetingAttendanceStatus)Undefined));
    }

    [Fact]
    public void MeetingAttendanceStatus_Icon_EveryValue_NonEmpty()
    {
        foreach (var value in Enum.GetValues<MeetingAttendanceStatus>())
            Assert.False(string.IsNullOrEmpty(MeetingAttendanceStatusDisplay.Icon(value)));
    }

    [Fact]
    public void MeetingAttendanceStatus_Icon_MapsKnownValues()
    {
        Assert.Equal(Icons.Material.Filled.HelpOutline, MeetingAttendanceStatusDisplay.Icon(MeetingAttendanceStatus.Open));
        Assert.Equal(Icons.Material.Filled.CheckCircle, MeetingAttendanceStatusDisplay.Icon(MeetingAttendanceStatus.Present));
        Assert.Equal(Icons.Material.Filled.EventBusy, MeetingAttendanceStatusDisplay.Icon(MeetingAttendanceStatus.SignedOff));
        Assert.Equal(Icons.Material.Filled.Cancel, MeetingAttendanceStatusDisplay.Icon(MeetingAttendanceStatus.Missing));
    }

    [Fact]
    public void MeetingAttendanceStatus_Icon_Undefined_ReturnsHelpOutlineFallback()
    {
        Assert.Equal(Icons.Material.Filled.HelpOutline, MeetingAttendanceStatusDisplay.Icon((MeetingAttendanceStatus)Undefined));
    }

    [Theory]
    [InlineData(MeetingAttendanceStatus.Open, Color.Default)]
    [InlineData(MeetingAttendanceStatus.Present, Color.Success)]
    [InlineData(MeetingAttendanceStatus.SignedOff, Color.Info)]
    [InlineData(MeetingAttendanceStatus.Missing, Color.Error)]
    public void MeetingAttendanceStatus_Colour_MapsEachValue(MeetingAttendanceStatus status, Color expected)
    {
        Assert.Equal(expected, MeetingAttendanceStatusDisplay.Colour(status));
    }

    [Fact]
    public void MeetingAttendanceStatus_Colour_Undefined_ReturnsDefaultFallback()
    {
        Assert.Equal(Color.Default, MeetingAttendanceStatusDisplay.Colour((MeetingAttendanceStatus)Undefined));
    }

    [Fact]
    public void MeetingAttendanceStatus_All_ContainsEveryDefinedValue()
    {
        AssertAllCoversEnum(MeetingAttendanceStatusDisplay.All);
    }

    // ---------------------------------------------------------------------
    // MeetingStatus
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(MeetingStatus.Planned, "Geplant")]
    [InlineData(MeetingStatus.Held, "Durchgeführt")]
    [InlineData(MeetingStatus.Canceled, "Abgesagt")]
    [InlineData(MeetingStatus.Postponed, "Verschoben")]
    public void MeetingStatus_Name_MapsEachValue(MeetingStatus status, string expected)
    {
        Assert.Equal(expected, MeetingStatusDisplay.Name(status));
    }

    [Fact]
    public void MeetingStatus_Name_EveryValue_NonEmpty()
    {
        foreach (var value in Enum.GetValues<MeetingStatus>())
            Assert.False(string.IsNullOrEmpty(MeetingStatusDisplay.Name(value)));
    }

    [Fact]
    public void MeetingStatus_Name_Undefined_ReturnsFallback()
    {
        Assert.Equal("—", MeetingStatusDisplay.Name((MeetingStatus)Undefined));
    }

    [Theory]
    [InlineData(MeetingStatus.Planned, false)]
    [InlineData(MeetingStatus.Held, false)]
    [InlineData(MeetingStatus.Canceled, true)]
    [InlineData(MeetingStatus.Postponed, true)]
    public void MeetingStatus_IsObsolete_MatchesCanceledOrPostponed(MeetingStatus status, bool expected)
    {
        Assert.Equal(expected, MeetingStatusDisplay.IsObsolete(status));
    }

    [Fact]
    public void MeetingStatus_All_ContainsEveryDefinedValue()
    {
        AssertAllCoversEnum(MeetingStatusDisplay.All);
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    // Asserts an "All" list contains exactly the enum's defined values (no gaps).
    private static void AssertAllCoversEnum<T>(IReadOnlyList<T> all) where T : struct, Enum
    {
        var defined = Enum.GetValues<T>();
        Assert.Equal(defined.Length, all.Count);
        foreach (var value in defined)
            Assert.Contains(value, all);
    }
}
