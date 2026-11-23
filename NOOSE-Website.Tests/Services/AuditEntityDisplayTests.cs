using System.Reflection;
using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Navigation;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

public class AuditEntityDisplayTests
{
    [Theory]
    [InlineData("Person", "Person")]
    [InlineData("PersonDoc", "Personen-Dok")]
    [InlineData("Observation", "Observation")]
    [InlineData("PersonRelation", "Personen-Beziehung")]
    [InlineData("Faction", "Fraktion")]
    [InlineData("PersonGroup", "Personengruppe")]
    [InlineData("Party", "Partei")]
    [InlineData("DrugRoute", "Drogenroute")]
    [InlineData("DrugRouteStation", "Drogenroute: Station")]
    [InlineData("Operation", "Operation")]
    [InlineData("AgentActivity", "Aktivität")]
    [InlineData("Taskforce", "Taskforce")]
    [InlineData("Case", "Vorgang")]
    [InlineData("Job", "Aufgabe")]
    [InlineData("Appointment", "Termin")]
    [InlineData("Document", "Dokument")]
    [InlineData("Law", "Gesetz")]
    [InlineData("Announcement", "Ankündigung")]
    [InlineData("Agent", "Agent")]
    [InlineData("Meeting", "Besprechung")]
    [InlineData("MeetingAgendaItem", "Tagesordnungspunkt")]
    [InlineData("MeetingAttendance", "Anwesenheit")]
    [InlineData("MeetingSignOff", "Abmeldung (Besprechung)")]
    [InlineData("Absence", "Abmeldung")]
    [InlineData("Pressemitteilung", "Pressemitteilung")]
    [InlineData("Warnhinweis", "Warnhinweis")]
    [InlineData("FirearmUse", "Schusswaffengebrauch")]
    [InlineData("SystemSetting", "Systemeinstellung")]
    [InlineData("BuergerProfil", "Bürgerkonto")]
    [InlineData("OeffentlichesModul", "Öffentliches Modul")]
    [InlineData("OeffentlicheSeite", "Öffentliche Seite")]
    [InlineData("OeffentlicheFahndung", "Öffentliche Ausschreibung")]
    [InlineData("PublicArea", "Öffentlicher Bereich")]
    [InlineData("RegulationSection", "Abschnitt (Dienstverordnung)")]
    [InlineData("RegulationParagraph", "Absatz (Dienstverordnung)")]
    [InlineData("RegulationParagraphVersion", "Fassung (Dienstverordnung)")]
    [InlineData("RegulationAcknowledgment", "Kenntnisnahme (Dienstverordnung)")]
    public void Label_KnownType_ReturnsGermanLabel(string type, string expected)
    {
        Assert.Equal(expected, AuditEntityDisplay.Label(type));
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("person")]
    [InlineData("PERSON")]
    [InlineData("Foo")]
    [InlineData("SomeOtherEntity")]
    public void Label_UnknownType_ReturnsInputVerbatim(string type)
    {
        Assert.Equal(type, AuditEntityDisplay.Label(type));
    }

    [Fact]
    public void Label_EmptyString_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, AuditEntityDisplay.Label(string.Empty));
    }

    [Fact]
    public void Label_Whitespace_ReturnsWhitespaceVerbatim()
    {
        Assert.Equal("   ", AuditEntityDisplay.Label("   "));
    }

    [Fact]
    public void Label_LookupIsCaseSensitive()
    {
        // exact "Person" maps, differently-cased does not
        Assert.Equal("Person", AuditEntityDisplay.Label("Person"));
        Assert.Equal("person", AuditEntityDisplay.Label("person"));
    }

    [Theory]
    [InlineData("Person", "abc", "/personen/abc")]
    [InlineData("Faction", "abc", "/fraktionen/abc")]
    [InlineData("PersonGroup", "abc", "/personengruppen/abc")]
    [InlineData("Party", "abc", "/parteien/abc")]
    [InlineData("DrugRoute", "abc", "/drogenrouten/abc")]
    [InlineData("Operation", "abc", "/operationen/abc")]
    [InlineData("AgentActivity", "abc", "/aktivitaeten/abc")]
    [InlineData("Taskforce", "abc", "/taskforces/abc")]
    [InlineData("Case", "abc", "/vorgaenge/abc")]
    [InlineData("Job", "abc", "/aufgaben/abc")]
    [InlineData("Appointment", "abc", "/kalender/abc")]
    [InlineData("Document", "abc", "/dokumente/abc")]
    [InlineData("Law", "abc", "/gesetze/abc")]
    [InlineData("Agent", "abc", "/personal/abc")]
    [InlineData("Meeting", "abc", "/besprechungen/abc")]
    [InlineData("RegulationParagraph", "abc", "/dienstverordnung/absatz/abc")]
    public void Route_KnownRoutableType_ReturnsDeepLinkWithId(string type, string id, string expected)
    {
        Assert.Equal(expected, AuditEntityDisplay.Route(type, id));
    }

    [Fact]
    public void Route_Absence_ReturnsStaticOverviewIgnoringId()
    {
        Assert.Equal("/abmeldungen/uebersicht", AuditEntityDisplay.Route("Absence", "any-id"));
        Assert.Equal("/abmeldungen/uebersicht", AuditEntityDisplay.Route("Absence", string.Empty));
    }

    [Fact]
    public void Route_RegulationSection_ReturnsTheRegulationIgnoringId()
        => Assert.Equal("/dienstverordnung", AuditEntityDisplay.Route("RegulationSection", "any-id"));

    [Fact]
    public void Route_FirearmUse_ReturnsTheOverviewIgnoringId()
    {
        Assert.Equal("/schusswaffengebrauch", AuditEntityDisplay.Route("FirearmUse", "any-id"));
        Assert.Equal("/schusswaffengebrauch", AuditEntityDisplay.Route("FirearmUse", string.Empty));
    }

    [Theory]
    [InlineData("PersonDoc")]
    [InlineData("Observation")]
    [InlineData("PersonRelation")]
    [InlineData("AgentActivity_wrong")]
    [InlineData("Announcement")]
    [InlineData("MeetingAgendaItem")]
    [InlineData("MeetingAttendance")]
    [InlineData("MeetingSignOff")]
    [InlineData("RegulationParagraphVersion")]
    [InlineData("RegulationAcknowledgment")]
    [InlineData("Unknown")]
    [InlineData("")]
    [InlineData("person")]
    public void Route_ChildOrUnknownType_ReturnsNull(string type)
    {
        Assert.Null(AuditEntityDisplay.Route(type, "abc"));
    }

    [Fact]
    public void Route_RoutableTypeWithEmptyId_EmbedsEmptyId()
    {
        Assert.Equal("/personen/", AuditEntityDisplay.Route("Person", string.Empty));
    }

    [Fact]
    public void Route_LabelableButNonRoutableType_ReturnsNull()
    {
        // types that have a Label but no detail page
        Assert.Null(AuditEntityDisplay.Route("Announcement", "x"));
        Assert.Null(AuditEntityDisplay.Route("PersonDoc", "x"));
    }

    [Theory]
    [InlineData("BuergerProfil", "/einstellungen?tab=buerger")]
    [InlineData("OeffentlichesModul", "/einstellungen?tab=oeffentliche-module")]
    [InlineData("PublicArea", "/einstellungen?tab=oeffentliche-module")]
    [InlineData("OeffentlicheSeite", "/einstellungen?tab=oeffentliche-seiten")]
    [InlineData("OeffentlicheFahndung", "/fahndung?tab=oeffentlich")]
    public void Route_PublicAreaConfig_PointsAtTheEditingSection(string type, string expected)
    {
        // these have no detail page; the audit row should still be followable to where it was changed
        Assert.Equal(expected, AuditEntityDisplay.Route(type, "any-id"));
    }

    [Theory]
    [InlineData("EnumLabelOverride", "Bezeichnung (Wertelisten)", "/einstellungen?tab=basisdaten")]
    [InlineData("KassenBuchungVorlage", "Kassen-Vorlage", "/kasse?tab=vorlagen")]
    public void ConfigRowsWithoutAPage_HaveLabelAndPointAtTheirSection(string type, string label, string route)
    {
        // the id of an override is "list:key", the id of a template is its own; neither reaches the route
        Assert.Equal(label, AuditEntityDisplay.Label(type));
        Assert.Equal(route, AuditEntityDisplay.Route(type, "any-id"));
        Assert.Equal(route, AuditEntityDisplay.Route(type, string.Empty));

        var split = route.Split("?tab=");
        Assert.True(MergedPageSections.ByRoute.TryGetValue(split[0], out var slugs), $"{type}: unknown page {split[0]}");
        Assert.Contains(split[1], slugs!);
    }

    [Fact]
    public void AgentTestAssignment_HasALabelAndOpensTheGradingPageWithTheAssignmentId()
    {
        Assert.Equal("Test-Zuweisung", AuditEntityDisplay.Label("AgentTestAssignment"));
        Assert.Equal("/tests/korrektur/a1", AuditEntityDisplay.Route("AgentTestAssignment", "a1"));
    }

    [Fact]
    public void Tips_carry_a_German_label_and_a_route()
    {
        Assert.Equal("Bürgerhinweis", AuditEntityDisplay.Label("Hinweis"));
        Assert.Equal("Hinweis-Nachricht", AuditEntityDisplay.Label("HinweisNachricht"));
        Assert.Equal("/hinweise/h1", AuditEntityDisplay.Route("Hinweis", "h1"));
        // a message has no page of its own; the inbox is where it is read
        Assert.Equal("/hinweise", AuditEntityDisplay.Route("HinweisNachricht", "m1"));
    }

    public static TheoryData<string, string, string> GlobalSettingAudits => new()
    {
        { EmergencyAlarmService.SettingAuditType, "Einsatzalarm-Dauer", "/einstellungen?tab=einsatzalarm" },
        { InactivityRadarService.AuditType, "Inaktivitäts-Schwellen", "/einstellungen?tab=inaktivitaet" },
        { CareerRequirementsService.AuditType, "Bewerbungs-Anforderungen", "/bewerbungen?tab=anforderungen" },
        { LlmQuotaConfigService.AuditType, "KI-Kontingente", "/einstellungen?tab=ki-regeln" },
        { NooseiSettingsService.AuditType, "FIBI-Prompt-Zusatz", "/einstellungen?tab=ki-regeln" },
        { NooseiProviderService.AuditType, "KI-Anbieter", "/einstellungen?tab=ki-anbieter" },
    };

    [Theory]
    [MemberData(nameof(GlobalSettingAudits))]
    public void GlobalSettingAudit_HasLabelAndEditingRoute(string type, string label, string route)
    {
        Assert.Equal(label, AuditEntityDisplay.Label(type));
        Assert.Equal(route, AuditEntityDisplay.Route(type, "any-id"));
        Assert.Equal(route, AuditEntityDisplay.Route(type, string.Empty));
    }

    public static TheoryData<string, string> SystemSettingRoutes => new()
    {
        { SystemSettingKeys.MaintenanceModeActive, "/einstellungen?tab=system" },
        { SystemSettingKeys.ThemePrimary, "/einstellungen?tab=system" },
        { SystemSettingKeys.WantedBoardMinHazard, "/einstellungen?tab=system" },
        { SystemSettingKeys.MeetingAnomalyRed, "/einstellungen?tab=system" },
        { SystemSettingKeys.EmergencyAlarmDurationMinutes, "/einstellungen?tab=einsatzalarm" },
        { SystemSettingKeys.DiscordEnabled, "/einstellungen?tab=discord" },
        { SystemSettingKeys.DiscordShareChannels, "/einstellungen?tab=discord" },
        { SystemSettingKeys.DiscordBotDvoChannel, "/einstellungen?tab=discord" },
        { SystemSettingKeys.DiscordGuildId, "/einstellungen?tab=discord-rollen" },
        { SystemSettingKeys.DiscordRoleMapping, "/einstellungen?tab=discord-rollen" },
        { SystemSettingKeys.DiscordInviteLink, "/einstellungen?tab=discord-direktnachrichten" },
        { SystemSettingKeys.BestAgentIntervalDays, "/einstellungen?tab=bester-agent" },
        { SystemSettingKeys.CertificatePlace, "/einstellungen?tab=ehrenzeichen" },
        { SystemSettingKeys.CertificateSignatoryHrt, "/einstellungen?tab=ehrenzeichen" },
        { SystemSettingKeys.PublicAreaKillSwitch, "/einstellungen?tab=oeffentliche-module" },
        { SystemSettingKeys.PublicSituationLevel, "/einstellungen?tab=lage" },
        { FinancingConfigService.SettingKey, "/einstellungen?tab=finanzierung" },
        { FoodLimitConfigService.SettingKey, "/einstellungen?tab=verpflegung" },
        { InactivityRadarService.SettingKey, "/einstellungen?tab=inaktivitaet" },
        { ProbationRules.SettingKey, "/einstellungen?tab=module" },
        { LlmQuotaConfigService.SettingKey, "/einstellungen?tab=ki-regeln" },
        { NooseiPrompts.AddendumKey, "/einstellungen?tab=ki-regeln" },
        { NooseiProviderService.SettingKey, "/einstellungen?tab=ki-anbieter" },
        { CareerRequirementsService.SettingKey, "/bewerbungen?tab=anforderungen" },
        { RecruitingAutomationService.EnabledKey, "/bewerbungen?tab=automatik" },
        { RecruitingAutomationService.TemplateKey, "/bewerbungen?tab=automatik" },
    };

    [Theory]
    [MemberData(nameof(SystemSettingRoutes))]
    public void SystemSetting_PointsAtTheSectionThatEditsTheKey(string key, string route)
        => Assert.Equal(route, AuditEntityDisplay.Route("SystemSetting", key));

    [Theory]
    [InlineData("Announcement")]
    [InlineData("FactionThreatBoard")]
    public void SystemSetting_PrefixKeys_PointAtTheDiscordSection(string category)
    {
        Assert.Equal("/einstellungen?tab=discord",
            AuditEntityDisplay.Route("SystemSetting", SystemSettingKeys.DiscordWebhookPrefix + category));
        Assert.Equal("/einstellungen?tab=discord",
            AuditEntityDisplay.Route("SystemSetting", SystemSettingKeys.DiscordRolePrefix + category));
    }

    [Fact]
    public void SystemSetting_LabelStaysUnchanged()
        => Assert.Equal("Systemeinstellung", AuditEntityDisplay.Label("SystemSetting"));

    [Theory]
    [InlineData("SomethingElse")]
    [InlineData("DiscordWebhook")]
    [InlineData("discordaktiv")]
    [InlineData("PartnerRangSichtbarkeitUebernommen")]
    [InlineData("")]
    public void SystemSetting_UnknownKey_HasNoRoute(string key)
        => Assert.Null(AuditEntityDisplay.Route("SystemSetting", key));

    // worker state and guards: nobody edits them, so there is no section to point at
    private static readonly string[] WorkerStateKeys =
    [
        SystemSettingKeys.DiscordThreatBoardMessage,
        SystemSettingKeys.DiscordThreatBoardState,
        SystemSettingKeys.DiscordRoleSyncLastRun,
        SystemSettingKeys.DiscordBotFactionBoardState,
        SystemSettingKeys.DiscordBotWantedBoardState,
        SystemSettingKeys.DiscordBotCommandsBoardState,
        SystemSettingKeys.DiscordBotAlarmBoards,
        SystemSettingKeys.DiscordDvoState,
        SystemSettingKeys.BestAgentLastRun,
        SystemSettingKeys.PingDigestLastRun,
        SystemSettingKeys.NooseiAnomalyWatchArmedAt,
        SystemSettingKeys.SearchIndexBackfillDone,
    ];

    [Fact]
    public void SystemSetting_WorkerState_HasNoRoute()
    {
        foreach (var key in WorkerStateKeys)
        {
            Assert.Null(AuditEntityDisplay.Route("SystemSetting", key));
        }
    }

    [Fact]
    public void SystemSetting_EveryKeyIsEitherRoutedToAnExistingSectionOrWorkerState()
    {
        // a key added to SystemSettingKeys has to be classified here, or its audit rows stay link-less unnoticed
        var keys = typeof(SystemSettingKeys)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true } && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .Where(k => !k.EndsWith('.'))
            .ToList();
        Assert.NotEmpty(keys);

        foreach (var key in keys)
        {
            var route = AuditEntityDisplay.Route("SystemSetting", key);
            if (WorkerStateKeys.Contains(key))
            {
                Assert.Null(route);
                continue;
            }

            Assert.True(route is not null, $"{key} has neither a section nor a place in the worker-state list");
            var split = route!.Split("?tab=");
            Assert.True(MergedPageSections.ByRoute.TryGetValue(split[0], out var slugs), $"{key}: unknown page {split[0]}");
            Assert.Contains(split[1], slugs!);
        }
    }

    [Theory]
    [MemberData(nameof(SystemSettingRoutes))]
    public void SystemSetting_EveryRoute_NamesAnExistingSection(string key, string route)
    {
        var split = route.Split("?tab=");
        Assert.True(MergedPageSections.ByRoute.TryGetValue(split[0], out var slugs), $"{key}: unknown page {split[0]}");
        Assert.Contains(split[1], slugs!);
    }

    [Fact]
    public void Route_IsCaseSensitive_MismatchedCaseYieldsNull()
    {
        Assert.Equal("/personen/1", AuditEntityDisplay.Route("Person", "1"));
        Assert.Null(AuditEntityDisplay.Route("person", "1"));
    }

    [Theory]
    [InlineData("AbductionCompromise", "Entführung: Kompromittierte Akte")]
    [InlineData("AgentTestAnswer", "Test: Antwort")]
    [InlineData("AnnouncementAcknowledgment", "Ankündigung: Kenntnisnahme")]
    [InlineData("AppointmentAssignment", "Termin: Teilnehmer")]
    [InlineData("BewerbungMessage", "Bewerbung: Nachricht")]
    [InlineData("BewerbungTestAnswer", "Bewerbungstest: Antwort")]
    [InlineData("BewerbungTestOption", "Bewerbungstest: Antwortoption")]
    [InlineData("BewerbungTestQuestion", "Bewerbungstest: Frage")]
    [InlineData("CaseAgent", "Vorgang: Zugewiesener Agent")]
    [InlineData("CustomFieldValue", "Custom-Feld: Wert")]
    [InlineData("DocumentAccessExclusion", "Dokument: Zugriffssperre")]
    [InlineData("EmergencyAlarmRecipient", "Einsatzalarm: Empfänger")]
    [InlineData("EvidenceEntryLine", "Asservat-Eintrag: Position")]
    [InlineData("FactionAgent", "Fraktion: Zugewiesener Agent")]
    [InlineData("FactionMember", "Fraktion: Mitglied")]
    [InlineData("InformantMeeting", "Informant: Treffen")]
    [InlineData("JobAssignment", "Aufgabe: Zugewiesener Agent")]
    [InlineData("OperationAgent", "Operation: Zugewiesener Agent")]
    [InlineData("OperationLogEntry", "Operation: Logeintrag")]
    [InlineData("PartyAgent", "Partei: Zugewiesener Agent")]
    [InlineData("PartyMember", "Partei: Mitglied")]
    [InlineData("PersonGroupAgent", "Personengruppe: Zugewiesener Agent")]
    [InlineData("PersonGroupMember", "Personengruppe: Mitglied")]
    [InlineData("TaskforceAgent", "Taskforce: Zugewiesener Agent")]
    [InlineData("TaskforceMessage", "Taskforce: Nachricht")]
    [InlineData("AgentBadge", "Meilenstein-Abzeichen")]
    [InlineData("AgentEvaluation", "Dienstbeurteilung")]
    [InlineData("AgentModuleCompletion", "Personalakte: Ausbildungsmodul")]
    [InlineData("AgentNote", "Personalakte: Eintrag")]
    [InlineData("AgentProbation", "Probezeit")]
    [InlineData("AgentPromotionRequest", "Beförderungsantrag")]
    [InlineData("ActivityTemplate", "Aktivitäts-Vorlage")]
    [InlineData("AgentAbduction", "Entführung")]
    [InlineData("AgentInvite", "Einladungslink")]
    [InlineData("Bewerbung", "Bewerbung")]
    [InlineData("BewerbungTest", "Bewerbungstest")]
    [InlineData("BewerbungTestAssignment", "Bewerbungstest-Zuweisung")]
    [InlineData("Bewerbungssperre", "Bewerbungssperre")]
    [InlineData("CounterIntelRule", "Gegenaufklärungs-Regel")]
    [InlineData("CustomFieldDefinition", "Custom-Feld")]
    [InlineData("DocTemplate", "Dok-Vorlage")]
    [InlineData("DocumentTemplate", "Dokument-Vorlage")]
    [InlineData("EmergencyAlarm", "Einsatzalarm")]
    [InlineData("EvidenceEntry", "Asservat-Eintrag")]
    [InlineData("EvidenceItem", "Asservat")]
    [InlineData("Informant", "Informant")]
    [InlineData("PartnerAgencyProfile", "Partner-Behörde: Sichtbarkeit")]
    [InlineData("PartnerReleaseRule", "Partner-Regelfreigabe")]
    [InlineData("PersonnelTemplate", "Personal-Vorlage")]
    [InlineData("RecencyThreshold", "Aktualitäts-Schwelle")]
    [InlineData("SituationReport", "Monatslagebericht")]
    [InlineData("Tag", "Tag")]
    [InlineData("ThreatScoreConfig", "Bedrohungs-Score-Konfiguration")]
    [InlineData("TrainingModule", "Ausbildungsmodul")]
    [InlineData("Comment", "Kommentar")]
    [InlineData("Followup", "Wiedervorlage")]
    [InlineData("GraphCanvasLayout", "Graph-Layout")]
    [InlineData("LeadDismissal", "Ermittlungshinweis: Verwerfung")]
    [InlineData("LibraryFile", "Bibliotheksdatei")]
    [InlineData("Link", "Verknüpfung")]
    [InlineData("NooseiConversation", "FIBI-Unterhaltung")]
    [InlineData("Notification", "Benachrichtigung")]
    [InlineData("PartnerShare", "Partner-Freigabe")]
    [InlineData("Request", "Antrag")]
    [InlineData("SavedSearch", "Gespeicherte Suche")]
    [InlineData("Source", "Quelle")]
    [InlineData("TextImage", "Textbild")]
    [InlineData("WatchlistEntry", "Watchlist-Eintrag")]
    public void Label_AuditedTypeWithoutALabelBefore_ReturnsGermanLabel(string type, string expected)
        => Assert.Equal(expected, AuditEntityDisplay.Label(type));

    [Theory]
    [InlineData("Bewerbung", "abc", "/bewerbungen/abc")]
    [InlineData("BewerbungTest", "abc", "/bewerbungs-tests/abc")]
    [InlineData("Informant", "abc", "/informanten/abc")]
    [InlineData("AgentAbduction", "abc", "/entfuehrungen/abc")]
    [InlineData("EvidenceItem", "abc", "/asservatenkammer/item/abc")]
    [InlineData("EvidenceEntry", "abc", "/asservatenkammer/eintrag/abc")]
    [InlineData("EmergencyAlarm", "abc", "/einsatzalarme/abc")]
    [InlineData("SituationReport", "abc", "/lageberichte/abc")]
    [InlineData("ActivityTemplate", "abc", "/admin/vorlagen/aktivitaet-vorlage/abc/bearbeiten")]
    [InlineData("DocumentTemplate", "abc", "/admin/vorlagen/dokument-vorlage/abc/bearbeiten")]
    [InlineData("PersonnelTemplate", "abc", "/admin/vorlagen/personal-vorlage/abc/bearbeiten")]
    public void Route_AuditedRecordWithItsOwnPage_OpensItWithTheId(string type, string id, string expected)
        => Assert.Equal(expected, AuditEntityDisplay.Route(type, id));

    [Theory]
    [InlineData("Bewerbungssperre", "/bewerbungen?tab=sperren")]
    [InlineData("DocTemplate", "/einstellungen?tab=vorlagen-dok")]
    [InlineData("CustomFieldDefinition", "/einstellungen?tab=custom-felder")]
    [InlineData("Tag", "/einstellungen?tab=tags")]
    [InlineData("RecencyThreshold", "/einstellungen?tab=aktualitaet")]
    [InlineData("ThreatScoreConfig", "/einstellungen?tab=bedrohungs-score")]
    [InlineData("AgentInvite", "/einstellungen?tab=einladungen")]
    [InlineData("TrainingModule", "/einstellungen?tab=module")]
    [InlineData("PartnerAgencyProfile", "/einstellungen?tab=partner")]
    [InlineData("PartnerReleaseRule", "/einstellungen?tab=partner")]
    [InlineData("CounterIntelRule", "/nachweis?tab=regeln")]
    public void Route_AuditedConfigWithoutAPage_PointsAtTheSectionThatEditsIt(string type, string route)
    {
        // the id is the row's own and never reaches the route
        Assert.Equal(route, AuditEntityDisplay.Route(type, "any-id"));
        Assert.Equal(route, AuditEntityDisplay.Route(type, string.Empty));

        var split = route.Split("?tab=");
        Assert.True(MergedPageSections.ByRoute.TryGetValue(split[0], out var slugs), $"{type}: unknown page {split[0]}");
        Assert.Contains(split[1], slugs!);
    }

    [Theory]
    [InlineData("AbductionCompromise")]
    [InlineData("AgentTestAnswer")]
    [InlineData("AnnouncementAcknowledgment")]
    [InlineData("AppointmentAssignment")]
    [InlineData("BewerbungMessage")]
    [InlineData("BewerbungTestAnswer")]
    [InlineData("BewerbungTestOption")]
    [InlineData("BewerbungTestQuestion")]
    [InlineData("CaseAgent")]
    [InlineData("CustomFieldValue")]
    [InlineData("DocumentAccessExclusion")]
    [InlineData("EmergencyAlarmRecipient")]
    [InlineData("EvidenceEntryLine")]
    [InlineData("FactionAgent")]
    [InlineData("FactionMember")]
    [InlineData("InformantMeeting")]
    [InlineData("JobAssignment")]
    [InlineData("OperationAgent")]
    [InlineData("OperationLogEntry")]
    [InlineData("PartyAgent")]
    [InlineData("PartyMember")]
    [InlineData("PersonGroupAgent")]
    [InlineData("PersonGroupMember")]
    [InlineData("TaskforceAgent")]
    [InlineData("TaskforceMessage")]
    [InlineData("AgentBadge")]
    [InlineData("AgentModuleCompletion")]
    [InlineData("AgentNote")]
    [InlineData("AgentProbation")]
    [InlineData("BewerbungTestAssignment")]
    [InlineData("Comment")]
    [InlineData("Followup")]
    [InlineData("GraphCanvasLayout")]
    [InlineData("LeadDismissal")]
    [InlineData("LibraryFile")]
    [InlineData("Link")]
    [InlineData("NooseiConversation")]
    [InlineData("Notification")]
    [InlineData("PartnerShare")]
    [InlineData("Request")]
    [InlineData("SavedSearch")]
    [InlineData("Source")]
    [InlineData("TextImage")]
    [InlineData("WatchlistEntry")]
    public void Route_AuditedChildOrPersonalRow_HasNoRoute(string type)
        => Assert.Null(AuditEntityDisplay.Route(type, "abc"));

    [Fact]
    public void Route_EvaluationAndPromotionRequest_OpenTheirAdministrationOverview()
    {
        Assert.Equal("/administration/beurteilungen", AuditEntityDisplay.Route("AgentEvaluation", "abc"));
        Assert.Equal("/administration/befoerderungen", AuditEntityDisplay.Route("AgentPromotionRequest", "abc"));
    }

    [Fact]
    public void EveryAuditableEntity_HasAGermanLabel()
    {
        // any IAuditable row can show up in the audit log, so none may fall back to the class name
        var sameInGerman = new HashSet<string>(StringComparer.Ordinal)
        {
            "Person", "Observation", "Operation", "Taskforce", "Pressemitteilung", "Warnhinweis", "Bewerbung", "Bewerbungssperre",
            "Informant", "Tag",
        };
        var missing = typeof(AuditEntityDisplay).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IAuditable).IsAssignableFrom(t))
            .Select(t => t.Name)
            .Where(name => !sameInGerman.Contains(name) && AuditEntityDisplay.Label(name) == name)
            .Distinct()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0, "No German label for: " + string.Join(", ", missing));
    }
}
