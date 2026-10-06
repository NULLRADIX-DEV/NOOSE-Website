using MudBlazor;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Tests.Models;

/// <summary>
/// Covers the Name/Icon/All display helpers for the second batch of enums plus the
/// standalone display classes in RankDisplay.cs and PeopleDisplay.cs. Every switch arm
/// (including the default fallback) and every All-list is asserted.
/// </summary>
public class EnumDisplayTests_B
{
    // ---------------------------------------------------------------------
    // NotificationTypeDisplay
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(NotificationType.RequestDecided, "Antrag entschieden")]
    [InlineData(NotificationType.Mention, "Erwähnung")]
    [InlineData(NotificationType.Account, "Konto")]
    [InlineData(NotificationType.RecordModified, "Beobachtete Akte geändert")]
    [InlineData(NotificationType.JobAssigned, "Aufgabe")]
    [InlineData(NotificationType.Announcement, "Ankündigung")]
    [InlineData(NotificationType.Followup, "Wiedervorlage fällig")]
    [InlineData(NotificationType.AppointmentAssigned, "Termin")]
    [InlineData(NotificationType.AppointmentScheduled, "Neuer Termin")]
    [InlineData(NotificationType.AgentTerminated, "Kündigung")]
    [InlineData(NotificationType.SituationReport, "Lagebericht")]
    [InlineData(NotificationType.Recruiting, "Bewerbung")]
    [InlineData(NotificationType.JobDueSoon, "Aufgabe fällig")]
    [InlineData(NotificationType.MeetingScheduled, "Besprechung")]
    [InlineData(NotificationType.MeetingReminder, "Besprechung beginnt bald")]
    [InlineData(NotificationType.AbsenceFiled, "Abmeldung")]
    [InlineData(NotificationType.PublicWantedPublished, "Öffentliche Ausschreibung")]
    [InlineData(NotificationType.PublicWantedExpired, "Ausschreibung abgelaufen")]
    [InlineData(NotificationType.PublicTicketCreated, "Neues Ticket")]
    [InlineData(NotificationType.PublicTipCreated, "Neuer Bürgerhinweis")]
    public void NotificationTypeName_definedValue_mapsToLabel(NotificationType type, string expected)
        => Assert.Equal(expected, NotificationTypeDisplay.Name(type));

    [Fact]
    public void NotificationTypeName_undefinedValue_returnsFallback()
        => Assert.Equal("Benachrichtigung", NotificationTypeDisplay.Name((NotificationType)999));

    [Fact]
    public void NotificationTypeIcon_definedValues_mapToExpectedIcons()
    {
        Assert.Equal(Icons.Material.Filled.Gavel, NotificationTypeDisplay.Icon(NotificationType.RequestDecided));
        Assert.Equal(Icons.Material.Filled.AlternateEmail, NotificationTypeDisplay.Icon(NotificationType.Mention));
        Assert.Equal(Icons.Material.Filled.ManageAccounts, NotificationTypeDisplay.Icon(NotificationType.Account));
        Assert.Equal(Icons.Material.Filled.Visibility, NotificationTypeDisplay.Icon(NotificationType.RecordModified));
        Assert.Equal(Icons.Material.Filled.AssignmentInd, NotificationTypeDisplay.Icon(NotificationType.JobAssigned));
        Assert.Equal(Icons.Material.Filled.Campaign, NotificationTypeDisplay.Icon(NotificationType.Announcement));
        Assert.Equal(Icons.Material.Filled.EventRepeat, NotificationTypeDisplay.Icon(NotificationType.Followup));
        Assert.Equal(Icons.Material.Filled.Event, NotificationTypeDisplay.Icon(NotificationType.AppointmentAssigned));
        Assert.Equal(Icons.Material.Filled.EventAvailable, NotificationTypeDisplay.Icon(NotificationType.AppointmentScheduled));
        Assert.Equal(Icons.Material.Filled.PersonRemove, NotificationTypeDisplay.Icon(NotificationType.AgentTerminated));
        Assert.Equal(Icons.Material.Filled.Assessment, NotificationTypeDisplay.Icon(NotificationType.SituationReport));
        Assert.Equal(Icons.Material.Filled.HowToReg, NotificationTypeDisplay.Icon(NotificationType.Recruiting));
        Assert.Equal(Icons.Material.Filled.AssignmentLate, NotificationTypeDisplay.Icon(NotificationType.JobDueSoon));
        Assert.Equal(Icons.Material.Filled.Groups, NotificationTypeDisplay.Icon(NotificationType.MeetingScheduled));
        Assert.Equal(Icons.Material.Filled.NotificationsActive, NotificationTypeDisplay.Icon(NotificationType.MeetingReminder));
        Assert.Equal(Icons.Material.Filled.EventBusy, NotificationTypeDisplay.Icon(NotificationType.AbsenceFiled));
        Assert.Equal(Icons.Material.Filled.PersonSearch, NotificationTypeDisplay.Icon(NotificationType.PublicWantedPublished));
        Assert.Equal(Icons.Material.Filled.QuestionAnswer, NotificationTypeDisplay.Icon(NotificationType.PublicTicketCreated));
        Assert.Equal(Icons.Material.Filled.TimerOff, NotificationTypeDisplay.Icon(NotificationType.PublicWantedExpired));
    }

    [Fact]
    public void EveryNotificationType_HasADisplayNameAndIcon()
    {
        // both switches end in a silent fallback, so a new enum value shows up nameless in the bell and in the
        // Discord admin panel without any test going red — this is that test
        var nameless = Enum.GetValues<NotificationType>()
            .Where(t => NotificationTypeDisplay.Name(t) == "Benachrichtigung"
                || NotificationTypeDisplay.Icon(t) == Icons.Material.Filled.Notifications)
            .Select(t => t.ToString())
            .Order()
            .ToArray();

        Assert.True(nameless.Length == 0,
            "Jeder Benachrichtigungstyp braucht Namen und Icon: " + string.Join(", ", nameless));
    }

    [Fact]
    public void NotificationTypeIcon_undefinedValue_returnsFallbackIcon()
        => Assert.Equal(Icons.Material.Filled.Notifications, NotificationTypeDisplay.Icon((NotificationType)999));

    // ---------------------------------------------------------------------
    // OperationStatusDisplay
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(OperationStatus.Planned, "Geplant")]
    [InlineData(OperationStatus.Running, "Laufend")]
    [InlineData(OperationStatus.Completed, "Abgeschlossen")]
    [InlineData(OperationStatus.Aborted, "Abgebrochen")]
    public void OperationStatusName_definedValue_mapsToLabel(OperationStatus status, string expected)
        => Assert.Equal(expected, OperationStatusDisplay.Name(status));

    [Fact]
    public void OperationStatusName_undefinedValue_returnsDash()
        => Assert.Equal("—", OperationStatusDisplay.Name((OperationStatus)99));

    [Fact]
    public void OperationStatusAll_containsAllInDeclarationOrder()
        => Assert.Equal(
            new[] { OperationStatus.Planned, OperationStatus.Running, OperationStatus.Completed, OperationStatus.Aborted },
            OperationStatusDisplay.All);

    // ---------------------------------------------------------------------
    // PartnerAgencyDisplay
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(PartnerAgency.DoJ, "DoJ")]
    [InlineData(PartnerAgency.LSPD, "LSPD")]
    [InlineData(PartnerAgency.LSMD, "LSMD")]
    [InlineData(PartnerAgency.Parlament, "Parlament")]
    public void PartnerAgencyName_definedValue_mapsToShortLabel(PartnerAgency agency, string expected)
        => Assert.Equal(expected, PartnerAgencyDisplay.Name(agency));

    [Fact]
    public void PartnerAgencyName_null_returnsDash()
        => Assert.Equal("—", PartnerAgencyDisplay.Name(null));

    [Fact]
    public void PartnerAgencyName_undefinedValue_returnsDash()
        => Assert.Equal("—", PartnerAgencyDisplay.Name((PartnerAgency)99));

    [Theory]
    [InlineData(PartnerAgency.DoJ, "Department of Justice")]
    [InlineData(PartnerAgency.LSPD, "Los Santos Police Department")]
    [InlineData(PartnerAgency.LSMD, "Los Santos Medical Department")]
    [InlineData(PartnerAgency.Parlament, "Parlament von San Andreas")]
    public void PartnerAgencyLongName_definedValue_mapsToFullLabel(PartnerAgency agency, string expected)
        => Assert.Equal(expected, PartnerAgencyDisplay.LongName(agency));

    [Fact]
    public void PartnerAgencyLongName_null_returnsDash()
        => Assert.Equal("—", PartnerAgencyDisplay.LongName(null));

    [Fact]
    public void PartnerAgencyAll_containsAllInDeclarationOrder()
        => Assert.Equal(
            new[] { PartnerAgency.DoJ, PartnerAgency.LSPD, PartnerAgency.LSMD, PartnerAgency.Parlament },
            PartnerAgencyDisplay.All);

    // ---------------------------------------------------------------------
    // PartnerRankDisplay
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(PartnerRank.Member, "")]
    [InlineData(PartnerRank.Special, "Special")]
    [InlineData(PartnerRank.Chief, "Chief")]
    public void PartnerRankSuffix_definedValue_mapsToSuffix(PartnerRank rank, string expected)
        => Assert.Equal(expected, PartnerRankDisplay.Suffix(rank));

    [Fact]
    public void PartnerRankSuffix_undefinedValue_returnsEmpty()
        => Assert.Equal(string.Empty, PartnerRankDisplay.Suffix((PartnerRank)99));

    [Theory]
    [InlineData(PartnerAgency.LSPD, PartnerRank.Member, "LSPD")]
    [InlineData(PartnerAgency.LSPD, PartnerRank.Special, "LSPD Special")]
    [InlineData(PartnerAgency.LSPD, PartnerRank.Chief, "LSPD Chief")]
    [InlineData(PartnerAgency.DoJ, PartnerRank.Special, "DoJ Special")]
    [InlineData(PartnerAgency.LSMD, PartnerRank.Member, "LSMD")]
    public void PartnerRankFull_agencyAndRank_composesLabel(PartnerAgency agency, PartnerRank rank, string expected)
        => Assert.Equal(expected, PartnerRankDisplay.Full(agency, rank));

    [Fact]
    public void PartnerRankFull_nullRank_returnsAgencyNameOnly()
        => Assert.Equal("LSPD", PartnerRankDisplay.Full(PartnerAgency.LSPD, null));

    [Fact]
    public void PartnerRankFull_nullAgencyWithSuffix_prefixesDash()
        => Assert.Equal("— Special", PartnerRankDisplay.Full(null, PartnerRank.Special));

    [Fact]
    public void PartnerRankFull_nullAgencyMemberRank_returnsDash()
        => Assert.Equal("—", PartnerRankDisplay.Full(null, PartnerRank.Member));

    [Fact]
    public void PartnerRankFull_bothNull_returnsDash()
        => Assert.Equal("—", PartnerRankDisplay.Full(null, null));

    [Fact]
    public void PartnerRankAll_containsAllLowestFirst()
        => Assert.Equal(
            new[] { PartnerRank.Member, PartnerRank.Special, PartnerRank.Chief },
            PartnerRankDisplay.All);

    // ---------------------------------------------------------------------
    // PersonnelTemplateKindDisplay
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(PersonnelTemplateKind.Commendation, "Belobigung")]
    [InlineData(PersonnelTemplateKind.Disciplinary, "Disziplinarisch")]
    [InlineData(PersonnelTemplateKind.Promotion, "Beförderung")]
    public void PersonnelTemplateKindName_definedValue_mapsToLabel(PersonnelTemplateKind kind, string expected)
        => Assert.Equal(expected, PersonnelTemplateKindDisplay.Name(kind));

    [Fact]
    public void PersonnelTemplateKindName_undefinedValue_returnsDash()
        => Assert.Equal("—", PersonnelTemplateKindDisplay.Name((PersonnelTemplateKind)99));

    [Fact]
    public void PersonnelTemplateKindAll_containsAllInDeclarationOrder()
        => Assert.Equal(
            new[] { PersonnelTemplateKind.Commendation, PersonnelTemplateKind.Disciplinary, PersonnelTemplateKind.Promotion },
            PersonnelTemplateKindDisplay.All);

    // ---------------------------------------------------------------------
    // PromotionStatusDisplay
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(PromotionStatus.Requested, "Beantragt")]
    [InlineData(PromotionStatus.Approved, "Genehmigt")]
    [InlineData(PromotionStatus.Rejected, "Abgelehnt")]
    public void PromotionStatusName_definedValue_mapsToLabel(PromotionStatus status, string expected)
        => Assert.Equal(expected, PromotionStatusDisplay.Name(status));

    [Fact]
    public void PromotionStatusName_undefinedValue_returnsDash()
        => Assert.Equal("—", PromotionStatusDisplay.Name((PromotionStatus)99));

    // ---------------------------------------------------------------------
    // RelationTypeDisplay
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(RelationType.Family, "Familie")]
    [InlineData(RelationType.Ally, "Verbündeter")]
    [InlineData(RelationType.Enemy, "Feind")]
    [InlineData(RelationType.BusinessPartner, "Geschäftspartner")]
    [InlineData(RelationType.Known, "Bekannt")]
    [InlineData(RelationType.Misc, "Sonstige")]
    public void RelationTypeName_definedValue_mapsToLabel(RelationType type, string expected)
        => Assert.Equal(expected, RelationTypeDisplay.Name(type));

    [Fact]
    public void RelationTypeName_undefinedValue_returnsDash()
        => Assert.Equal("—", RelationTypeDisplay.Name((RelationType)99));

    [Fact]
    public void RelationTypeAll_containsAllInDeclarationOrder()
        => Assert.Equal(
            new[]
            {
                RelationType.Family, RelationType.Ally, RelationType.Enemy,
                RelationType.BusinessPartner, RelationType.Known, RelationType.Misc,
            },
            RelationTypeDisplay.All);

    // ---------------------------------------------------------------------
    // RequestStatusDisplay
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(RequestStatus.Requested, "Beantragt")]
    [InlineData(RequestStatus.Approved, "Genehmigt")]
    [InlineData(RequestStatus.Rejected, "Abgelehnt")]
    public void RequestStatusName_definedValue_mapsToLabel(RequestStatus status, string expected)
        => Assert.Equal(expected, RequestStatusDisplay.Name(status));

    [Fact]
    public void RequestStatusName_undefinedValue_returnsDash()
        => Assert.Equal("—", RequestStatusDisplay.Name((RequestStatus)99));

    [Fact]
    public void RequestStatusAll_containsAllInDeclarationOrder()
        => Assert.Equal(
            new[] { RequestStatus.Requested, RequestStatus.Approved, RequestStatus.Rejected },
            RequestStatusDisplay.All);

    // ---------------------------------------------------------------------
    // RequestTypeDisplay
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(RequestType.Upgrade, "Hochstufung")]
    [InlineData(RequestType.PartnerFreigabe, "Partner-Freigabe")]
    [InlineData(RequestType.Veroeffentlichung, "Veröffentlichung")]
    public void RequestTypeName_definedValue_mapsToLabel(RequestType type, string expected)
        => Assert.Equal(expected, RequestTypeDisplay.Name(type));

    [Fact]
    public void RequestTypeName_undefinedValue_returnsDash()
        => Assert.Equal("—", RequestTypeDisplay.Name((RequestType)99));

    // ---------------------------------------------------------------------
    // PublicWantedKindDisplay / PublicWantedStatusDisplay
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(PublicWantedKind.Fahndung, "Fahndung")]
    [InlineData(PublicWantedKind.Vermisst, "Vermisst")]
    [InlineData(PublicWantedKind.Zeugenaufruf, "Zeugenaufruf")]
    [InlineData(PublicWantedKind.Fahrzeug, "Fahrzeug")]
    [InlineData(PublicWantedKind.Waffe, "Waffe")]
    public void PublicWantedKindName_definedValue_mapsToLabel(PublicWantedKind kind, string expected)
        => Assert.Equal(expected, PublicWantedKindDisplay.Name(kind));

    [Fact]
    public void PublicWantedKindName_undefinedValue_returnsDash()
        => Assert.Equal("—", PublicWantedKindDisplay.Name((PublicWantedKind)99));

    [Theory]
    [InlineData(PublicWantedStatus.Entwurf, "Entwurf")]
    [InlineData(PublicWantedStatus.Beantragt, "Beantragt")]
    [InlineData(PublicWantedStatus.Veroeffentlicht, "Veröffentlicht")]
    [InlineData(PublicWantedStatus.Gefasst, "Gefasst")]
    [InlineData(PublicWantedStatus.Zurueckgezogen, "Zurückgezogen")]
    [InlineData(PublicWantedStatus.Abgelaufen, "Abgelaufen")]
    public void PublicWantedStatusName_definedValue_mapsToLabel(PublicWantedStatus status, string expected)
        => Assert.Equal(expected, PublicWantedStatusDisplay.Name(status));

    [Fact]
    public void PublicWantedStatusName_undefinedValue_returnsDash()
        => Assert.Equal("—", PublicWantedStatusDisplay.Name((PublicWantedStatus)99));

    [Theory]
    [InlineData(ObjectionStatus.Neu, "Neu", "Eingegangen")]
    [InlineData(ObjectionStatus.InPruefung, "In Prüfung", "In Prüfung")]
    [InlineData(ObjectionStatus.Angenommen, "Stattgegeben", "Stattgegeben")]
    [InlineData(ObjectionStatus.Abgelehnt, "Abgelehnt", "Zurückgewiesen")]
    public void ObjectionStatusName_definedValue_mapsToBothLabels(
        ObjectionStatus status, string expected, string citizen)
    {
        Assert.Equal(expected, ObjectionStatusDisplay.Name(status));
        Assert.Equal(citizen, ObjectionStatusDisplay.CitizenName(status));
    }

    [Fact]
    public void ObjectionStatusName_undefinedValue_returnsDash()
    {
        Assert.Equal("—", ObjectionStatusDisplay.Name((ObjectionStatus)99));
        Assert.Equal("—", ObjectionStatusDisplay.CitizenName((ObjectionStatus)99));
        Assert.Equal(Enum.GetValues<ObjectionStatus>().Length, ObjectionStatusDisplay.All.Count);
    }

    [Fact]
    public void PublicWantedDisplays_ListEveryDefinedValue()
    {
        Assert.Equal(Enum.GetValues<PublicWantedKind>().Length, PublicWantedKindDisplay.All.Count);
        Assert.Equal(Enum.GetValues<PublicWantedStatus>().Length, PublicWantedStatusDisplay.All.Count);
    }

    // ---------------------------------------------------------------------
    // SourceTypeDisplay
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(SourceType.Upload, "Datei-Upload")]
    [InlineData(SourceType.Link, "Web-Link")]
    [InlineData(SourceType.Internal, "Interne Verknüpfung")]
    [InlineData(SourceType.FreeText, "Freitext")]
    [InlineData(SourceType.Document, "Dokument")]
    public void SourceTypeName_definedValue_mapsToLabel(SourceType type, string expected)
        => Assert.Equal(expected, SourceTypeDisplay.Name(type));

    [Fact]
    public void SourceTypeName_undefinedValue_returnsDash()
        => Assert.Equal("—", SourceTypeDisplay.Name((SourceType)99));

    [Fact]
    public void SourceTypeAll_containsAllInDeclarationOrder()
        => Assert.Equal(
            new[] { SourceType.Upload, SourceType.Link, SourceType.Internal, SourceType.FreeText, SourceType.Document },
            SourceTypeDisplay.All);

    // ---------------------------------------------------------------------
    // TaskforceRoleDisplay
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(TaskforceRole.Member, "Mitglied")]
    [InlineData(TaskforceRole.LeadInvestigator, "Chefermittler")]
    [InlineData(TaskforceRole.CidLead, "CID-Lead")]
    [InlineData(TaskforceRole.TruLead, "TRU-Lead")]
    public void TaskforceRoleName_definedValue_mapsToLabel(TaskforceRole role, string expected)
        => Assert.Equal(expected, TaskforceRoleDisplay.Name(role));

    [Fact]
    public void TaskforceRoleName_undefinedValue_returnsDash()
        => Assert.Equal("—", TaskforceRoleDisplay.Name((TaskforceRole)99));

    [Theory]
    [InlineData(TaskforceRole.Member, false)]
    [InlineData(TaskforceRole.LeadInvestigator, true)]
    [InlineData(TaskforceRole.CidLead, true)]
    [InlineData(TaskforceRole.TruLead, true)]
    public void TaskforceRoleIsLead_nonMemberRolesAreLeads(TaskforceRole role, bool expected)
        => Assert.Equal(expected, TaskforceRoleDisplay.IsLead(role));

    [Fact]
    public void TaskforceRoleAll_containsAllInDeclarationOrder()
        => Assert.Equal(
            new[] { TaskforceRole.Member, TaskforceRole.LeadInvestigator, TaskforceRole.CidLead, TaskforceRole.TruLead },
            TaskforceRoleDisplay.All);

    // ---------------------------------------------------------------------
    // TaskforceScopeDisplay
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(TaskforceScope.InternalAgency, "Innerbehördlich")]
    [InlineData(TaskforceScope.CrossAgency, "Überbehördlich")]
    public void TaskforceScopeName_definedValue_mapsToLabel(TaskforceScope scope, string expected)
        => Assert.Equal(expected, TaskforceScopeDisplay.Name(scope));

    [Fact]
    public void TaskforceScopeName_undefinedValue_returnsDash()
        => Assert.Equal("—", TaskforceScopeDisplay.Name((TaskforceScope)99));

    [Fact]
    public void TaskforceScopeAll_containsAllInDeclarationOrder()
        => Assert.Equal(
            new[] { TaskforceScope.InternalAgency, TaskforceScope.CrossAgency },
            TaskforceScopeDisplay.All);

    // ---------------------------------------------------------------------
    // TaskforceStatusDisplay
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(TaskforceStatus.Requested, "Beantragt")]
    [InlineData(TaskforceStatus.Approved, "Genehmigt")]
    [InlineData(TaskforceStatus.Rejected, "Abgelehnt")]
    [InlineData(TaskforceStatus.Resolved, "Aufgelöst")]
    public void TaskforceStatusName_definedValue_mapsToLabel(TaskforceStatus status, string expected)
        => Assert.Equal(expected, TaskforceStatusDisplay.Name(status));

    [Fact]
    public void TaskforceStatusName_undefinedValue_returnsDash()
        => Assert.Equal("—", TaskforceStatusDisplay.Name((TaskforceStatus)99));

    [Fact]
    public void TaskforceStatusAll_containsAllInDeclarationOrder()
        => Assert.Equal(
            new[] { TaskforceStatus.Requested, TaskforceStatus.Approved, TaskforceStatus.Rejected, TaskforceStatus.Resolved },
            TaskforceStatusDisplay.All);

    // ---------------------------------------------------------------------
    // TestQuestionTypeDisplay
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(TestQuestionType.MultipleChoice, "Multiple Choice")]
    [InlineData(TestQuestionType.YesNo, "Ja / Nein")]
    [InlineData(TestQuestionType.FreeText, "Freitext")]
    public void TestQuestionTypeName_definedValue_mapsToLabel(TestQuestionType type, string expected)
        => Assert.Equal(expected, TestQuestionTypeDisplay.Name(type));

    [Fact]
    public void TestQuestionTypeName_undefinedValue_returnsDash()
        => Assert.Equal("—", TestQuestionTypeDisplay.Name((TestQuestionType)99));

    // ---------------------------------------------------------------------
    // TestAttemptStateDisplay
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(TestAttemptState.NotStarted, "Nicht begonnen")]
    [InlineData(TestAttemptState.Running, "Läuft")]
    [InlineData(TestAttemptState.Expired, "Zeit abgelaufen")]
    [InlineData(TestAttemptState.Submitted, "Abgegeben")]
    public void TestAttemptStateName_definedValue_mapsToLabel(TestAttemptState state, string expected)
        => Assert.Equal(expected, TestAttemptStateDisplay.Name(state));

    [Fact]
    public void TestAttemptStateName_undefinedValue_returnsDash()
        => Assert.Equal("—", TestAttemptStateDisplay.Name((TestAttemptState)99));

    [Fact]
    public void TestAttemptStateName_everyValue_nonEmpty()
    {
        foreach (var value in Enum.GetValues<TestAttemptState>())
        {
            Assert.False(string.IsNullOrEmpty(TestAttemptStateDisplay.Name(value)));
        }
    }

    [Theory]
    [InlineData(TestAttemptState.NotStarted, Color.Default)]
    [InlineData(TestAttemptState.Running, Color.Info)]
    // Warning, never Error: Error is the failed-verdict colour in the grading panel
    [InlineData(TestAttemptState.Expired, Color.Warning)]
    [InlineData(TestAttemptState.Submitted, Color.Success)]
    public void TestAttemptStateChipColor_definedValue_mapsToColour(TestAttemptState state, Color expected)
        => Assert.Equal(expected, TestAttemptStateDisplay.ChipColor(state));

    // ---------------------------------------------------------------------
    // AbsenceCategoryDisplay
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(AbsenceCategory.Vacation, "Urlaub")]
    [InlineData(AbsenceCategory.Work, "Arbeit (RL)")]
    [InlineData(AbsenceCategory.Sick, "Krank")]
    [InlineData(AbsenceCategory.RpBreak, "RP-Pause")]
    [InlineData(AbsenceCategory.Misc, "Sonstiges")]
    public void AbsenceCategoryName_definedValue_mapsToLabel(AbsenceCategory category, string expected)
        => Assert.Equal(expected, AbsenceCategoryDisplay.Name(category));

    [Fact]
    public void AbsenceCategoryName_undefinedValue_returnsDash()
        => Assert.Equal("—", AbsenceCategoryDisplay.Name((AbsenceCategory)99));

    [Fact]
    public void AbsenceCategoryIcon_definedValues_mapToExpectedIcons()
    {
        Assert.Equal(Icons.Material.Filled.BeachAccess, AbsenceCategoryDisplay.Icon(AbsenceCategory.Vacation));
        Assert.Equal(Icons.Material.Filled.Work, AbsenceCategoryDisplay.Icon(AbsenceCategory.Work));
        Assert.Equal(Icons.Material.Filled.LocalHospital, AbsenceCategoryDisplay.Icon(AbsenceCategory.Sick));
        Assert.Equal(Icons.Material.Filled.PauseCircle, AbsenceCategoryDisplay.Icon(AbsenceCategory.RpBreak));
        Assert.Equal(Icons.Material.Filled.MoreHoriz, AbsenceCategoryDisplay.Icon(AbsenceCategory.Misc));
    }

    [Fact]
    public void AbsenceCategoryIcon_undefinedValue_returnsFallbackIcon()
        => Assert.Equal(Icons.Material.Filled.EventBusy, AbsenceCategoryDisplay.Icon((AbsenceCategory)99));

    [Fact]
    public void AbsenceCategoryAll_containsAllInDeclarationOrder()
        => Assert.Equal(
            new[]
            {
                AbsenceCategory.Vacation, AbsenceCategory.Work, AbsenceCategory.Sick,
                AbsenceCategory.RpBreak, AbsenceCategory.Misc,
            },
            AbsenceCategoryDisplay.All);

    // ---------------------------------------------------------------------
    // RankDisplay
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(Rank.JuniorAgent, "Junior Agent")]
    [InlineData(Rank.SpecialAgent, "Special Agent")]
    [InlineData(Rank.SeniorSpecialAgent, "Senior Special Agent")]
    [InlineData(Rank.SupervisorySpecialAgent, "Supervisory Special Agent")]
    [InlineData(Rank.DeputyDirector, "Deputy Director")]
    [InlineData(Rank.Director, "Director")]
    public void RankName_definedValue_mapsToLabel(Rank rank, string expected)
        => Assert.Equal(expected, RankDisplay.Name(rank));

    [Fact]
    public void RankName_null_returnsNoRankFallback()
        => Assert.Equal("— (kein Rang)", RankDisplay.Name(null));

    [Fact]
    public void RankName_undefinedValue_returnsNoRankFallback()
        => Assert.Equal("— (kein Rang)", RankDisplay.Name((Rank)99));

    [Fact]
    public void RankAll_containsAllAscending()
        => Assert.Equal(
            new[]
            {
                Rank.JuniorAgent, Rank.SpecialAgent, Rank.SeniorSpecialAgent,
                Rank.SupervisorySpecialAgent, Rank.DeputyDirector, Rank.Director,
            },
            RankDisplay.All);

    // ---------------------------------------------------------------------
    // AgentStatusDisplay
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(AgentStatus.Pending, "Ausstehend")]
    [InlineData(AgentStatus.Active, "Aktiv")]
    [InlineData(AgentStatus.Blocked, "Gesperrt")]
    [InlineData(AgentStatus.Applicant, "Bewerber")]
    [InlineData(AgentStatus.Terminated, "Gekündigt")]
    [InlineData(AgentStatus.Civilian, "Bürger")]
    public void AgentStatusName_definedValue_mapsToLabel(AgentStatus status, string expected)
        => Assert.Equal(expected, AgentStatusDisplay.Name(status));

    [Fact]
    public void AgentStatusName_undefinedValue_returnsDash()
        => Assert.Equal("—", AgentStatusDisplay.Name((AgentStatus)99));

    [Theory]
    [InlineData(AgentStatus.Active, Color.Success)]
    [InlineData(AgentStatus.Pending, Color.Warning)]
    [InlineData(AgentStatus.Blocked, Color.Error)]
    [InlineData(AgentStatus.Terminated, Color.Error)]
    public void AgentStatusColour_definedValue_mapsToColour(AgentStatus status, Color expected)
        => Assert.Equal(expected, AgentStatusDisplay.Colour(status));

    [Fact]
    public void AgentStatusColour_undefinedValue_returnsDefault()
        => Assert.Equal(Color.Default, AgentStatusDisplay.Colour((AgentStatus)99));

    [Fact]
    public void AgentStatusIcon_terminated_differsFromBlocked()
        => Assert.NotEqual(AgentStatusDisplay.Icon(AgentStatus.Blocked),
            AgentStatusDisplay.Icon(AgentStatus.Terminated));

    // ---------------------------------------------------------------------
    // LifeStatusDisplay
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(LifeStatus.Alive, "Lebend")]
    [InlineData(LifeStatus.Dead, "Tot")]
    [InlineData(LifeStatus.Fugitive, "Flüchtig")]
    public void LifeStatusName_definedValue_mapsToLabel(LifeStatus status, string expected)
        => Assert.Equal(expected, LifeStatusDisplay.Name(status));

    [Fact]
    public void LifeStatusName_undefinedValue_returnsDash()
        => Assert.Equal("—", LifeStatusDisplay.Name((LifeStatus)99));

    [Fact]
    public void LifeStatusAll_containsAllInDeclarationOrder()
        => Assert.Equal(
            new[] { LifeStatus.Alive, LifeStatus.Dead, LifeStatus.Fugitive },
            LifeStatusDisplay.All);

    // ---------------------------------------------------------------------
    // ClassificationDisplay
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(Classification.Unknown, "Unbekannt")]
    [InlineData(Classification.ReviewCase, "Prüffall")]
    [InlineData(Classification.SuspicionCase, "Verdachtsfall")]
    [InlineData(Classification.SecuredStateThreatening, "Gesichert staatsgefährdend")]
    public void ClassificationName_definedValue_mapsToLabel(Classification classification, string expected)
        => Assert.Equal(expected, ClassificationDisplay.Name(classification));

    [Fact]
    public void ClassificationName_undefinedValue_returnsDash()
        => Assert.Equal("—", ClassificationDisplay.Name((Classification)99));

    [Fact]
    public void ClassificationAll_containsAllInDeclarationOrder()
        => Assert.Equal(
            new[]
            {
                Classification.Unknown, Classification.ReviewCase,
                Classification.SuspicionCase, Classification.SecuredStateThreatening,
            },
            ClassificationDisplay.All);

    // ---------------------------------------------------------------------
    // MeasureOutcomeDisplay
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(MeasureOutcome.RunningStill, "Läuft noch")]
    [InlineData(MeasureOutcome.OfficiallyReleased, "Offiziell entlassen")]
    [InlineData(MeasureOutcome.Injection, "Amnestie-Spritze")]
    [InlineData(MeasureOutcome.Shot, "Erschossen")]
    public void MeasureOutcomeName_definedValue_mapsToLabel(MeasureOutcome outcome, string expected)
        => Assert.Equal(expected, MeasureOutcomeDisplay.Name(outcome));

    [Fact]
    public void MeasureOutcomeName_undefinedValue_returnsDash()
        => Assert.Equal("—", MeasureOutcomeDisplay.Name((MeasureOutcome)99));

    [Fact]
    public void MeasureOutcomeAll_containsAllInDeclarationOrder()
        => Assert.Equal(
            new[]
            {
                MeasureOutcome.RunningStill, MeasureOutcome.OfficiallyReleased,
                MeasureOutcome.Injection, MeasureOutcome.Shot,
            },
            MeasureOutcomeDisplay.All);

    // ---------------------------------------------------------------------
    // PublicFactionStandingDisplay / PublicProfileStatusDisplay
    // ---------------------------------------------------------------------

    [Theory]
    [InlineData(PublicFactionStanding.Beobachtet, "Beobachtet")]
    [InlineData(PublicFactionStanding.Verboten, "Verboten")]
    public void PublicFactionStandingName_definedValue_mapsToLabel(PublicFactionStanding standing, string expected)
        => Assert.Equal(expected, PublicFactionStandingDisplay.Name(standing));

    [Fact]
    public void PublicFactionStandingName_undefinedValue_returnsDash()
        => Assert.Equal("—", PublicFactionStandingDisplay.Name((PublicFactionStanding)99));

    [Fact]
    public void PublicFactionStandingAll_containsAllInDeclarationOrder()
        => Assert.Equal(
            new[] { PublicFactionStanding.Beobachtet, PublicFactionStanding.Verboten },
            PublicFactionStandingDisplay.All);

    [Theory]
    [InlineData(PublicProfileStatus.Entwurf, "Entwurf")]
    [InlineData(PublicProfileStatus.Veroeffentlicht, "Veröffentlicht")]
    [InlineData(PublicProfileStatus.Zurueckgezogen, "Zurückgezogen")]
    public void PublicProfileStatusName_definedValue_mapsToLabel(PublicProfileStatus status, string expected)
        => Assert.Equal(expected, PublicProfileStatusDisplay.Name(status));

    [Fact]
    public void PublicProfileStatusName_undefinedValue_returnsDash()
        => Assert.Equal("—", PublicProfileStatusDisplay.Name((PublicProfileStatus)99));

    [Fact]
    public void PublicProfileStatusAll_containsAllInDeclarationOrder()
        => Assert.Equal(
            new[]
            {
                PublicProfileStatus.Entwurf, PublicProfileStatus.Veroeffentlicht,
                PublicProfileStatus.Zurueckgezogen,
            },
            PublicProfileStatusDisplay.All);
}
