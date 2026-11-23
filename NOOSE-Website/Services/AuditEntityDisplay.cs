using NOOSE_Website.Models.Common;

namespace NOOSE_Website.Services;

/// <summary>German labels and deep-links for audit entity type names (CLR type names).</summary>
public static class AuditEntityDisplay
{
    // keys are CLR type names as stamped by the audit interceptor
    public static string Label(string type) => type switch
    {
        "Person" => "Person",
        "PersonDoc" => "Personen-Dok",
        "Observation" => "Observation",
        "PersonRelation" => "Personen-Beziehung",
        // no route for any of these: the audit row carries the photo id, not the file it hangs on
        "PersonPhoto" => "Personen-Foto",
        "FactionPhoto" => "Fraktions-Foto",
        "PersonGroupPhoto" => "Gruppen-Foto",
        "PartyPhoto" => "Partei-Foto",
        "Faction" => "Fraktion",
        "PersonGroup" => "Personengruppe",
        "Party" => "Partei",
        "DrugRoute" => "Drogenroute",
        // no route for these: the audit row carries the child id, not the route it hangs on
        "DrugRouteStation" => "Drogenroute: Station",
        "DrugRoutePerson" => "Drogenroute: Beteiligte Person",
        "DrugRoutePhoto" => "Drogenrouten-Foto",
        "Operation" => "Operation",
        "AgentActivity" => "Aktivität",
        "Taskforce" => "Taskforce",
        "Case" => "Vorgang",
        "Job" => "Aufgabe",
        "Appointment" => "Termin",
        "Document" => "Dokument",
        "Law" => "Gesetz",
        "LawBook" => "Gesetzbuch",
        "RadioChannel" => "Funkkanal",
        "Announcement" => "Ankündigung",
        "Agent" => "Agent",
        "Meeting" => "Besprechung",
        "MeetingAgendaItem" => "Tagesordnungspunkt",
        "MeetingAttendance" => "Anwesenheit",
        "MeetingSignOff" => "Abmeldung (Besprechung)",
        "MeetingMeasure" => "Maßnahme (Besprechung)",
        "Absence" => "Abmeldung",
        "FirearmUse" => "Schusswaffengebrauch",
        "AdministrationWorkspace" => "Administration (Arbeitsbereich)",
        "AdministrationMessage" => "Administration-Chat",
        "DisciplinaryProceeding" => "Disziplinarverfahren",
        "TransferRequest" => "Versetzungsantrag",
        "DivisionTransfer" => "Versetzung",
        "ServiceReference" => "Dienstzeugnis",
        "AgentRelationship" => "Personalbeziehung",
        "DecorationProposal" => "Ehrungsvorschlag",
        "ParliamentaryInquiry" => "Parlamentarische Anfrage",
        "Decoration" => "Ehrenzeichen",
        // no route: the row carries the award id, the personnel file needs the agent's
        "DecorationAward" => "Ehrung",
        "Feedback" => "Feedback-Meldung",
        "TextSnippet" => "Textbaustein",
        "KassenBuchung" => "Kassenbuchung",
        "FinancingRequest" => "Finanzierungsantrag",
        "FoodRequest" => "Verpflegungsanforderung",
        "Warrant" => "Anordnung",
        "Investigation" => "Ermittlung",
        "InvestigationParticipant" => "Ermittlung: Beteiligung",
        "InvestigationCharge" => "Ermittlung: Tatvorwurf",
        "InvestigationPerson" => "Ermittlung: Zeuge/Beschuldigter",
        "InvestigationEvidence" => "Ermittlung: Beweismittel",
        "InvestigationEvidencePhoto" => "Ermittlung: Beweismittelbild",
        "InvestigationInterrogation" => "Ermittlung: Vernehmung",
        "InvestigationInterrogationEntry" => "Ermittlung: Vernehmungsfrage",
        "InvestigationTask" => "Ermittlung: Aufgabe",
        "InvestigationNote" => "Ermittlung: Notiz",
        "InvestigationItemRestriction" => "Ermittlung: Eintragsfreigabe",
        "InvestigationLink" => "Ermittlung: Verknüpfung",
        "PartnerUnit" => "Partner-Einheit",
        "PartnerUnitShare" => "Partner-Freigabe an Einheit",
        "PartnerUnitReleaseRule" => "Partner-Regelfreigabe an Einheit",
        "TaskforcePartnerAgency" => "Taskforce: beteiligte Behörde",
        "FinancingRequestLine" => "Finanzierungsposition (Antrag)",
        "FinancingItem" => "Finanzierungs-Katalogposition",
        "FinancingBudgetConfig" => "Finanzierungs-Budgets",
        "FoodRequestLine" => "Verpflegungsposition (Anforderung)",
        "FoodItem" => "Verpflegungs-Katalogposition",
        "FoodLimitConfig" => "Verpflegungs-Limits",
        "SystemSetting" => "Systemeinstellung",
        "DiscordShareChannels" => "Discord-Kanäle zum Teilen",
        "BuergerProfil" => "Bürgerkonto",
        "OeffentlichesModul" => "Öffentliches Modul",
        "OeffentlicheSeite" => "Öffentliche Seite",
        "OeffentlicheFaqRubrik" => "FAQ-Rubrik",
        "OeffentlicheFaqEintrag" => "FAQ-Frage",
        "Pressemitteilung" => "Pressemitteilung",
        "ChangelogRelease" => "Fassung der Neuerungen",
        "ChangelogEntry" => "Eintrag der Neuerungen",
        "HandbookChapter" => "Handbuch-Kapitel",
        "HandbookArticle" => "Handbuch-Artikel",
        "HandbookStep" => "Handbuch-Schritt",
        "GlossaryTerm" => "Glossar-Begriff",
        "OeffentlicheWarnung" => "Öffentliche Warnung",
        "OeffentlicherLagebericht" => "Öffentlicher Lagebericht",
        "OeffentlicheFahndung" => "Öffentliche Ausschreibung",
        "Warnhinweis" => "Warnhinweis",
        "FahndungKopfgeldAnteil" => "Kopfgeld-Anteil",
        "Hinweis" => "Bürgerhinweis",
        "HinweisNachricht" => "Hinweis-Nachricht",
        "HinweisBelohnung" => "Hinweis-Belohnung",
        "Ticket" => "Bürger-Ticket",
        "TicketNachricht" => "Ticket-Nachricht",
        "TicketParticipant" => "Ticket-Beteiligter",
        "OeffentlichesFuehrungsprofil" => "Öffentliches Führungsprofil",
        "OeffentlicheTelefonnummer" => "Öffentliche Führungskarte",
        "OeffentlicheVorlage" => "Öffentliche Vorlage",
        "OeffentlichesFraktionsprofil" => "Öffentliches Organisationsprofil",
        "FahndungEinspruch" => "Einspruch gegen eine Ausschreibung",
        "PublicArea" => "Öffentlicher Bereich",
        "PublicSituation" => "Gefahrenlage",
        "Presentation" => "Präsentation",
        "PresentationSlide" => "Folie",
        "PresentationInvite" => "Einladung (Präsentation)",
        "PresentationFile" => "Google-Import (Präsentation)",
        "RegulationSection" => "Abschnitt (Dienstverordnung)",
        "RegulationParagraph" => "Absatz (Dienstverordnung)",
        "RegulationParagraphVersion" => "Fassung (Dienstverordnung)",
        "RegulationAcknowledgment" => "Kenntnisnahme (Dienstverordnung)",
        "EmergencyAlarmSettings" => "Einsatzalarm-Dauer",
        "InactivityThresholds" => "Inaktivitäts-Schwellen",
        "CareerRequirements" => "Bewerbungs-Anforderungen",
        "LlmQuotaConfig" => "KI-Kontingente",
        "NooseiPromptAddendum" => "FIBI-Prompt-Zusatz",
        "LlmProviderSettings" => "KI-Anbieter",
        "EnumLabelOverride" => "Bezeichnung (Wertelisten)",
        "KassenBuchungVorlage" => "Kassen-Vorlage",
        "AgentTestAssignment" => "Test-Zuweisung",
        // child rows below: the audit row carries the child's id, so none of them has a route
        "AbductionCompromise" => "Entführung: Kompromittierte Akte",
        "AgentTestAnswer" => "Test: Antwort",
        "AnnouncementAcknowledgment" => "Ankündigung: Kenntnisnahme",
        "AppointmentAssignment" => "Termin: Teilnehmer",
        "BewerbungMessage" => "Bewerbung: Nachricht",
        "BewerbungTestAnswer" => "Bewerbungstest: Antwort",
        "BewerbungTestOption" => "Bewerbungstest: Antwortoption",
        "BewerbungTestQuestion" => "Bewerbungstest: Frage",
        "CaseAgent" => "Vorgang: Zugewiesener Agent",
        "CustomFieldValue" => "Custom-Feld: Wert",
        "DocumentAccessExclusion" => "Dokument: Zugriffssperre",
        "EmergencyAlarmRecipient" => "Einsatzalarm: Empfänger",
        "EvidenceEntryLine" => "Asservat-Eintrag: Position",
        "FactionAgent" => "Fraktion: Zugewiesener Agent",
        "FactionMember" => "Fraktion: Mitglied",
        "InformantMeeting" => "Informant: Treffen",
        "JobAssignment" => "Aufgabe: Zugewiesener Agent",
        "OperationAgent" => "Operation: Zugewiesener Agent",
        "OperationLogEntry" => "Operation: Logeintrag",
        "PartyAgent" => "Partei: Zugewiesener Agent",
        "PartyMember" => "Partei: Mitglied",
        "PersonGroupAgent" => "Personengruppe: Zugewiesener Agent",
        "PersonGroupMember" => "Personengruppe: Mitglied",
        "TaskforceAgent" => "Taskforce: Zugewiesener Agent",
        "TaskforceMessage" => "Taskforce: Nachricht",
        // personnel file rows: the page is addressed by the agent's id, the audit row carries the entry's
        "AgentBadge" => "Meilenstein-Abzeichen",
        "AgentModuleCompletion" => "Personalakte: Ausbildungsmodul",
        "AgentNote" => "Personalakte: Eintrag",
        "AgentProbation" => "Probezeit",
        // records and configuration with a page or an editing section
        "ActivityTemplate" => "Aktivitäts-Vorlage",
        "AgentEvaluation" => "Dienstbeurteilung",
        "AgentPromotionRequest" => "Beförderungsantrag",
        "AgentAbduction" => "Entführung",
        "AgentInvite" => "Einladungslink",
        "Bewerbung" => "Bewerbung",
        "BewerbungTest" => "Bewerbungstest",
        "Bewerbungssperre" => "Bewerbungssperre",
        "CounterIntelRule" => "Gegenaufklärungs-Regel",
        "CustomFieldDefinition" => "Custom-Feld",
        "DocTemplate" => "Dok-Vorlage",
        "DocumentTemplate" => "Dokument-Vorlage",
        "EmergencyAlarm" => "Einsatzalarm",
        "EvidenceEntry" => "Asservat-Eintrag",
        "EvidenceItem" => "Asservat",
        "Informant" => "Informant",
        "PartnerAgencyProfile" => "Partner-Behörde: Sichtbarkeit",
        "PartnerReleaseRule" => "Partner-Regelfreigabe",
        "PersonnelTemplate" => "Personal-Vorlage",
        "RecencyThreshold" => "Aktualitäts-Schwelle",
        "SituationReport" => "Monatslagebericht",
        "Tag" => "Tag",
        "ThreatScoreConfig" => "Bedrohungs-Score-Konfiguration",
        "TrainingModule" => "Ausbildungsmodul",
        // no route: the blind grading page must not open from a row that names the applicant
        "BewerbungTestAssignment" => "Bewerbungstest-Zuweisung",
        // rows no link can open: personal, private or without a page of their own
        "Comment" => "Kommentar",
        "Followup" => "Wiedervorlage",
        "GraphCanvasLayout" => "Graph-Layout",
        "LeadDismissal" => "Ermittlungshinweis: Verwerfung",
        "LibraryFile" => "Bibliotheksdatei",
        "Link" => "Verknüpfung",
        "NooseiConversation" => "FIBI-Unterhaltung",
        "Notification" => "Benachrichtigung",
        "PartnerShare" => "Partner-Freigabe",
        "Request" => "Antrag",
        "SavedSearch" => "Gespeicherte Suche",
        "Source" => "Quelle",
        "TextImage" => "Textbild",
        "WatchlistEntry" => "Watchlist-Eintrag",
        _ => type,
    };

    /// <summary>Deep-link to the record, or null for child entities without their own page.</summary>
    public static string? Route(string type, string id) => type switch
    {
        "Person" => $"/personen/{id}",
        "Faction" => $"/fraktionen/{id}",
        "PersonGroup" => $"/personengruppen/{id}",
        "Party" => $"/parteien/{id}",
        "DrugRoute" => $"/drogenrouten/{id}",
        "Operation" => $"/operationen/{id}",
        "AgentActivity" => $"/aktivitaeten/{id}",
        "Taskforce" => $"/taskforces/{id}",
        "Case" => $"/vorgaenge/{id}",
        "Job" => $"/aufgaben/{id}",
        "Appointment" => $"/kalender/{id}",
        "Document" => $"/dokumente/{id}",
        "Law" => $"/gesetze/{id}",
        // the book page is addressed by abbreviation, not id
        "LawBook" => "/gesetze",
        // a channel has no detail page; the plan opens on it
        "RadioChannel" => $"/funk?kanal={id}",
        "Agent" => $"/personal/{id}",
        "Meeting" => $"/besprechungen/{id}",
        "Presentation" => $"/praesentationen/{id}",
        // absences have no detail page; the overview lists them
        "Absence" => "/abmeldungen/uebersicht",
        // a report has no detail page; the overview lists it
        "FirearmUse" => "/schusswaffengebrauch",
        "AdministrationWorkspace" or "AdministrationMessage" => "/administration",
        "DisciplinaryProceeding" => $"/administration/disziplinar/{id}",
        "TransferRequest" or "DivisionTransfer" => "/administration/versetzungen",
        "ServiceReference" => "/administration/dienstzeugnisse",
        "AgentRelationship" => "/administration/beziehungen",
        "DecorationProposal" => "/administration/auszeichnungen",
        "AgentEvaluation" => "/administration/beurteilungen",
        "AgentPromotionRequest" => "/administration/befoerderungen",
        "ParliamentaryInquiry" => $"/administration/parlament/{id}",
        // feedback entries have no detail page either; point at the feedback hub
        "Feedback" => "/feedback",
        // a snippet has no page of its own; it lives in a section of the owner's profile
        "TextSnippet" => "/profil",
        "KassenBuchung" => $"/kasse/buchung/{id}",
        "FinancingRequest" => $"/finanzierungen/{id}",
        "FoodRequest" => $"/verpflegung/{id}",
        "Warrant" => $"/anordnungen/{id}",
        "Investigation" => $"/ermittlungen/{id}",
        "RegulationParagraph" => $"/dienstverordnung/absatz/{id}",
        "RegulationSection" => "/dienstverordnung",
        // catalog positions and config have no own page; point at the section that edits them
        "FinancingItem" => "/finanzierungen?tab=katalog",
        // the template has no page of its own; the quick-book templates are managed in a section of the cash desk
        "KassenBuchungVorlage" => "/kasse?tab=vorlagen",
        // the override id is "list:key", not a record id; the value lists are edited in this section
        "EnumLabelOverride" => "/einstellungen?tab=basisdaten",
        // the id is the assignment's, and the grading page opens an assignment by exactly that id
        "AgentTestAssignment" => $"/tests/korrektur/{id}",
        "FinancingBudgetConfig" => "/einstellungen?tab=finanzierung",
        "FoodItem" => "/verpflegung?tab=katalog",
        "FoodLimitConfig" => "/einstellungen?tab=verpflegung",
        "BuergerProfil" => "/einstellungen?tab=buerger",
        "DiscordShareChannels" => "/einstellungen?tab=discord",
        "OeffentlichesModul" => "/einstellungen?tab=oeffentliche-module",
        "PublicArea" => "/einstellungen?tab=oeffentliche-module",
        "PublicSituation" => "/einstellungen?tab=lage",
        "OeffentlicheSeite" => "/einstellungen?tab=oeffentliche-seiten",
        "OeffentlicheFaqRubrik" => "/einstellungen?tab=oeffentliche-faq",
        "OeffentlicheFaqEintrag" => "/einstellungen?tab=oeffentliche-faq",
        "Pressemitteilung" => "/einstellungen?tab=presse",
        "ChangelogRelease" => "/einstellungen?tab=neuerungen",
        "ChangelogEntry" => "/einstellungen?tab=neuerungen",
        "HandbookChapter" => "/handbuch",
        "HandbookArticle" => "/handbuch",
        "HandbookStep" => "/handbuch",
        "GlossaryTerm" => "/handbuch?tab=glossar",
        "OeffentlicheWarnung" => "/einstellungen?tab=warnungen",
        "OeffentlicherLagebericht" => "/einstellungen?tab=berichte",
        "OeffentlicheFahndung" => "/fahndung?tab=oeffentlich",
        "Warnhinweis" => "/einstellungen?tab=warnhinweise",
        // the share has no page of its own; it is managed at the notice, which is managed here
        "FahndungKopfgeldAnteil" => "/fahndung?tab=oeffentlich",
        "Hinweis" => $"/hinweise/{id}",
        // the reward has no page of its own; it is worked at the notice, like the share
        "HinweisBelohnung" => "/fahndung?tab=oeffentlich",
        // a message has no page; point at the tip that carries the conversation is impossible from here, so the inbox
        "HinweisNachricht" => "/hinweise",
        "Ticket" => $"/tickets/{id}",
        // same as the tip message: the row has no page, so the desk it belongs to
        // the id is the assignment's, not the ticket's, so the route stops at the desk — same as the message
        "TicketNachricht" => "/tickets",
        "TicketParticipant" => "/tickets",
        "OeffentlichesFuehrungsprofil" => "/einstellungen?tab=oeffentliche-fuehrung",
        "OeffentlicheTelefonnummer" => "/einstellungen?tab=oeffentliche-fuehrung",
        "Decoration" => "/einstellungen?tab=ehrenzeichen",
        "OeffentlicheVorlage" => "/einstellungen?tab=oeffentliche-vorlagen",
        "OeffentlichesFraktionsprofil" => "/fahndung?tab=organisationen",
        "FahndungEinspruch" => "/fahndung?tab=einsprueche",
        // global settings have no record page; point at the section that edits them
        "EmergencyAlarmSettings" => "/einstellungen?tab=einsatzalarm",
        "InactivityThresholds" => "/einstellungen?tab=inaktivitaet",
        "CareerRequirements" => "/bewerbungen?tab=anforderungen",
        "LlmQuotaConfig" => "/einstellungen?tab=ki-regeln",
        // the prompt addendum is edited in the same panel as the token rules
        "NooseiPromptAddendum" => "/einstellungen?tab=ki-regeln",
        "LlmProviderSettings" => "/einstellungen?tab=ki-anbieter",
        // the id is the settings key, not a record id
        "SystemSetting" => SystemSettingRoute(id),
        "Bewerbung" => $"/bewerbungen/{id}",
        "BewerbungTest" => $"/bewerbungs-tests/{id}",
        "Bewerbungssperre" => "/bewerbungen?tab=sperren",
        "Informant" => $"/informanten/{id}",
        "AgentAbduction" => $"/entfuehrungen/{id}",
        "EvidenceItem" => $"/asservatenkammer/item/{id}",
        "EvidenceEntry" => $"/asservatenkammer/eintrag/{id}",
        "EmergencyAlarm" => $"/einsatzalarme/{id}",
        "SituationReport" => $"/lageberichte/{id}",
        "ActivityTemplate" => $"/admin/vorlagen/aktivitaet-vorlage/{id}/bearbeiten",
        "DocumentTemplate" => $"/admin/vorlagen/dokument-vorlage/{id}/bearbeiten",
        "PersonnelTemplate" => $"/admin/vorlagen/personal-vorlage/{id}/bearbeiten",
        // the doc templates are edited in a dialog of this section, so the row opens the section
        "DocTemplate" => "/einstellungen?tab=vorlagen-dok",
        "CustomFieldDefinition" => "/einstellungen?tab=custom-felder",
        "Tag" => "/einstellungen?tab=tags",
        "RecencyThreshold" => "/einstellungen?tab=aktualitaet",
        "ThreatScoreConfig" => "/einstellungen?tab=bedrohungs-score",
        "AgentInvite" => "/einstellungen?tab=einladungen",
        "TrainingModule" => "/einstellungen?tab=module",
        // the agency profile and the release rules are edited together in the partner section
        "PartnerAgencyProfile" => "/einstellungen?tab=partner",
        "PartnerReleaseRule" => "/einstellungen?tab=partner",
        "CounterIntelRule" => "/nachweis?tab=regeln",
        _ => null,
    };

    /// <summary>Section that edits a settings key, or null for worker state and keys nobody edits.</summary>
    /// <remarks>
    /// Worker state has no editing section on purpose: the threat-board and bot-board messages and hashes, the role-sync
    /// and digest and best-agent timestamps, the regulation-post marker, the anomaly-watch arming time, the search
    /// backfill marker and the one-shot partner migration markers. Unknown keys fall through to null as well.
    /// </remarks>
    private static string? SystemSettingRoute(string key)
    {
        // a category's webhook URL and role mention are edited per notification category in the Discord panel
        if (key.StartsWith(SystemSettingKeys.DiscordWebhookPrefix, StringComparison.Ordinal)
            || key.StartsWith(SystemSettingKeys.DiscordRolePrefix, StringComparison.Ordinal))
        {
            return "/einstellungen?tab=discord";
        }

        return key switch
        {
            SystemSettingKeys.MaintenanceModeActive or SystemSettingKeys.MaintenanceModeText
                or SystemSettingKeys.BannerText or SystemSettingKeys.BannerLevel
                or SystemSettingKeys.ThemePrimary or SystemSettingKeys.ThemeSecondary or SystemSettingKeys.ThemeTertiary
                or SystemSettingKeys.LogoFileName or SystemSettingKeys.LogoContentType
                or SystemSettingKeys.DemoModeActive
                // the board threshold and the attendance thresholds sit in the system panel as well
                or SystemSettingKeys.WantedBoardMinHazard
                or SystemSettingKeys.MeetingWindowSize or SystemSettingKeys.MeetingAnomalyYellow
                or SystemSettingKeys.MeetingAnomalyRed => "/einstellungen?tab=system",
            SystemSettingKeys.EmergencyAlarmDurationMinutes => "/einstellungen?tab=einsatzalarm",
            SystemSettingKeys.DiscordEnabled or SystemSettingKeys.SiteBaseUrl
                or SystemSettingKeys.DiscordIncludeHeadline or SystemSettingKeys.DiscordThreatBoardThreshold
                or SystemSettingKeys.DiscordShareChannels
                or SystemSettingKeys.DiscordBotEnabled or SystemSettingKeys.DiscordBotAlarmChannel
                or SystemSettingKeys.DiscordBotWantedChannel or SystemSettingKeys.DiscordBotFactionChannel
                or SystemSettingKeys.DiscordBotCommandsChannel or SystemSettingKeys.DiscordBotShareChannels
                or SystemSettingKeys.DiscordBotLinkPreview or SystemSettingKeys.DiscordBotDvoChannel
                => "/einstellungen?tab=discord",
            SystemSettingKeys.DiscordRoleSyncEnabled or SystemSettingKeys.DiscordGuildId
                or SystemSettingKeys.DiscordRoleMapping => "/einstellungen?tab=discord-rollen",
            SystemSettingKeys.DiscordDmPersonal or SystemSettingKeys.DiscordDmApplicants
                or SystemSettingKeys.DiscordInviteLink => "/einstellungen?tab=discord-direktnachrichten",
            SystemSettingKeys.BestAgentEnabled or SystemSettingKeys.BestAgentIntervalDays
                or SystemSettingKeys.BestAgentCreateNote => "/einstellungen?tab=bester-agent",
            SystemSettingKeys.CertificateSignatoryId or SystemSettingKeys.CertificateSignatoryCid
                or SystemSettingKeys.CertificateSignatoryCtd or SystemSettingKeys.CertificateSignatoryHrt
                or SystemSettingKeys.CertificatePlace => "/einstellungen?tab=ehrenzeichen",
            SystemSettingKeys.PublicAreaKillSwitch => "/einstellungen?tab=oeffentliche-module",
            // the four rows are written together; the level and the assessment are what the section edits
            SystemSettingKeys.PublicSituationLevel or SystemSettingKeys.PublicSituationNote
                or SystemSettingKeys.PublicSituationSince or SystemSettingKeys.PublicSituationPrevious
                => "/einstellungen?tab=lage",
            // keys that live in the config services rather than in SystemSettingKeys
            FinancingConfigService.SettingKey => "/einstellungen?tab=finanzierung",
            FoodLimitConfigService.SettingKey => "/einstellungen?tab=verpflegung",
            InactivityRadarService.SettingKey => "/einstellungen?tab=inaktivitaet",
            ProbationRules.SettingKey => "/einstellungen?tab=module",
            LlmQuotaConfigService.SettingKey or NooseiPrompts.AddendumKey => "/einstellungen?tab=ki-regeln",
            NooseiProviderService.SettingKey => "/einstellungen?tab=ki-anbieter",
            CareerRequirementsService.SettingKey => "/bewerbungen?tab=anforderungen",
            RecruitingAutomationService.EnabledKey or RecruitingAutomationService.TemplateKey
                => "/bewerbungen?tab=automatik",
            _ => null,
        };
    }
}
