namespace NOOSE_Website.Services.Public;

/// <summary>The single truth about what may leave the house — the public counterpart to <see cref="Visibility"/>.</summary>
/// <remarks>
/// Every entity is either listed in <see cref="Publishable"/> with what exactly leaves, or in
/// <see cref="NeverPublic"/> with why it never does. <c>PublicVisibilityCoverageTests</c> reflects over all
/// <c>DbSet</c>s and fails the build on anything undecided, so a new table forces the author to answer the question
/// instead of leaving it to a reviewer's attention.
/// <para>
/// Being listed as publishable is a statement about the entity's own publication path, not a permission: the record
/// still only goes out through its publish snapshot, and a classified file never goes out at all.
/// </para>
/// </remarks>
public static class PublicVisibility
{
    private const string InternalRecord = "Interner Aktenbestand; nach außen geht allenfalls eine eigene Publikations-Tabelle.";
    private const string Assignment = "Zuordnungszeile ohne eigene Aussage; nach außen nie.";
    private const string Personnel = "Personaldaten der Behörde; würde Agenten enttarnen.";
    private const string Operational = "Operative Interna; nach außen nie.";
    private const string Configuration = "Konfiguration ohne Inhalt für außen.";
    private const string Protocol = "Protokollzeile; nach außen nie.";
    private const string Finance = "Interne Finanzdaten; öffentlich erscheint höchstens eine Summe aus einer eigenen Tabelle.";
    private const string Recruiting = "Bewerbungsverfahren; vertraulich zwischen Bewerber und Behörde.";
    private const string Assistant = "NOOSEI-Interna; nach außen nie.";
    private const string SearchInternal = "Suchindex des internen Bestands; nach außen nie.";

    /// <summary>Entity name → what exactly is published from it.</summary>
    public static readonly IReadOnlyDictionary<string, string> Publishable =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["OeffentlichesModul"] = "Nur Beschriftung, Icon und Reihenfolge eines Nav-Eintrags; kein Aktenbezug.",
            ["OeffentlichesFuehrungsprofil"] = "Die eine Ausnahme von der Anonymität nach außen, und zwar eine "
                + "redaktionelle: freigegebener Klarname, Dienstgradbezeichnung, Funktion und ein KOPIERTES Foto "
                + "einer Führungskraft ab Supervisory Special Agent. Jeder Eintrag wird einzeln von Hand "
                + "freigegeben; nichts davon wird zur Lesezeit aus `Agent` projiziert, und `Agent` selbst bleibt "
                + "in NeverPublic. Weder Codename noch Dienstgrad-Wert, Kennungen oder Flags gehen mit.",
            ["SystemSetting"] = "Sechs Zeilen, nicht mehr: die vier der Gefahrenlage (Stufe, Einschätzung, "
                + "Seit-Datum, zuvor gesetzte Stufe), die Anforderungsliste der Karriereseite "
                + "(KarriereAnforderungen) und die Logo-Metadaten (LogoFileName, LogoContentType), die der "
                + "anonyme Endpoint /system/logo ausliefert. Abgegrenzt wird nach Bereich, nicht nach "
                + "Beispielen: alles, was die interne Konfiguration steuert — Discord-Webhooks, Wartungstext, "
                + "Theme-Farben, Demo-Modus, Not-Aus, Worker-Stempel — verlässt das Haus nie. Die Tabelle steht "
                + "hier und nicht in NeverPublic, weil dieses Verzeichnis sagt, *was genau* rausgeht.",
            ["OeffentlicheSeite"] = "Titel, Menütitel, Icon und der veröffentlichte Inhalt einer redaktionellen "
                + "Seite. Der Entwurf bleibt drinnen, und die Seite trägt keinen Aktenbezug.",
            ["OeffentlicheFaqRubrik"] = "Titel, Beschreibungszeile und Icon eines sichtbar geschalteten "
                + "FAQ-Abschnitts auf /faq. Eine ausgeschaltete Rubrik geht samt ihrer Fragen nicht "
                + "hinaus; einen Aktenbezug hat die Zeile nicht.",
            ["OeffentlicheFaqEintrag"] = "Frage, Antworttext und der Anker, unter dem die Frage adressiert "
                + "wird — alles nur, solange Frage und Rubrik sichtbar geschaltet sind und die Seite "
                + "faq veröffentlicht ist, die Überschrift und Einleitung von /faq liefert. Einen "
                + "Aktenbezug hat die Zeile nicht.",
            ["Pressemitteilung"] = "Öffentliches Aktenzeichen, Titel, Teaser und der veröffentlichte Inhalt einer "
                + "amtlichen Verlautbarung. Der Entwurf, der veröffentlichende Agent und der Discord-Stempel bleiben "
                + "drinnen; einen Aktenbezug hat die Zeile nicht.",
            ["OeffentlicheWarnung"] = "Titel, Text und Gültigkeitsdatum einer amtlichen Warnung. Der Entwurf und "
                + "der veröffentlichende Agent bleiben drinnen; einen Aktenbezug hat die Zeile nicht.",
            ["OeffentlicherLagebericht"] = "Zeitraum, Titel und der freigegebene Text eines Monatsberichts — ein "
                + "von der Führung geschriebener Text, kein Auszug des internen Zahlen-Snapshots. Entwurf, Anker "
                + "und veröffentlichender Agent bleiben drinnen.",
            ["Law"] = "Gesetzbuch, Paragraf, Titel, Text und Strafmaß eines ausdrücklich freigegebenen "
                + "Paragrafen. Der Text geht als bereinigtes HTML ohne Erwähnungen hinaus; Abschnitt und "
                + "Reihenfolge ordnen ihn nur. Standardmäßig bleibt jeder Paragraf drinnen; die Freigabe ist eine "
                + "eigene Entscheidung je Zeile, und wer sie getroffen hat, steht nicht auf der öffentlichen Seite.",
            ["LawBook"] = "Kürzel und Name eines Gesetzbuchs als Überschrift über seinen freigegebenen Paragrafen — "
                + "nur solange mindestens einer davon freigegeben ist. Beschreibung und Reihenfolge bleiben drinnen.",
            ["OeffentlicheFahndung"] = "Der Publikations-Snapshot einer Ausschreibung: öffentliches Aktenzeichen, "
                + "Art, Anzeigename, die vom Autor gewählten Aliase, Vorwurfstext, letzte Gegend, Fahrzeugtext, die "
                + "beim Publizieren festgehaltene Gefahrenstufe, eine Kopie des Fotos und — im Archiv — das "
                + "Gefasst-Datum, dazu die Kopfgeld-Obergrenze als \"bis X\". Bei einer Fahrzeug- oder "
                + "Waffen-Ausschreibung ist der Anzeigename das Kennzeichen bzw. die Bezeichnung, und ein Foto gibt "
                + "es dort nicht. Der Aktenbezug (PersonId/FraktionId), die Steckbrief-Zeile, aus der vorbefüllt "
                + "wurde, der rohe Bedrohungs-Score, der Aufrufzähler und der Rückzugsgrund bleiben drinnen. "
                + "Aus dem Bestand geht zusätzlich je eine Anzahl nach außen: wie viele Ausschreibungen laufen und "
                + "wie viele abgeschlossen sind — beide hinter demselben Unterdrückungsgürtel gezählt.",
            ["FahndungKopfgeldAnteil"] = "Ausschließlich die Summe aller zugesagten und gesicherten Anteile "
                + "einer laufenden Ausschreibung, als eine Zahl. Herkunft, Stifter, der Betrag eines einzelnen "
                + "Anteils, ihre Anzahl, das Konto, die Kassenbuchung und der Status bleiben drinnen — eine "
                + "Aufschlüsselung verriete, welcher Agent wie viel eigenes Geld auf einen Kopf gesetzt hat.",
            ["Hinweis"] = "Ausschließlich Anzahlen über den gesamten Bestand: wie viele Hinweise eingegangen "
                + "sind, wie viele davon bestätigt wurden und wie viele zur Ergreifung führten. Text, Anhang, "
                + "Bezug, Status und Hinweisgeber einer einzelnen Zeile bleiben drinnen, und eine Anzahl je "
                + "Ausschreibung ebenfalls — die wäre ein öffentliches Verzeichnis darüber, wer wie viel "
                + "Aufmerksamkeit auf sich zieht, und über kleine Zahlen wieder einer Person zuzuordnen. Den "
                + "eigenen Hinweis liest der Hinweisgeber angemeldet im Bürgerbereich; das ist sein Konto, nicht "
                + "die Öffentlichkeit.",
            ["HinweisBelohnung"] = "Ausschließlich die Summe aller ausgezahlten Belohnungen, als eine Zahl. Der "
                + "Betrag einer einzelnen Auszahlung, der Anteil, die Kassenbuchung, die Belegnummer und der "
                + "Empfänger bleiben drinnen; darüber hinaus geht nach draußen nur der eigene Beleg eines Bürgers, "
                + "angemeldet im Bürgerbereich.",
            ["Warnhinweis"] = "Bezeichnung und Farbe eines zugeordneten Warnhinweises, als Chip auf Board, "
                + "Steckbrief und Poster. Reihenfolge, Aktiv-Kennzeichen und die Zeilen-Id bleiben drinnen.",
            ["OeffentlichesFraktionsprofil"] = "Der Publikations-Snapshot einer Organisation: Anzeigename, "
                + "Einordnung (beobachtet/verboten), die beim Publizieren festgehaltene Gefahrenstufe und die "
                + "Kurzbeschreibung. Der Aktenbezug (FraktionId), der rohe Bedrohungs-Score, die Mitglieder und "
                + "der Rückzugsgrund bleiben drinnen.",
        };

    /// <summary>Entity name → why it can never appear outside.</summary>
    public static readonly IReadOnlyDictionary<string, string> NeverPublic =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["FahndungEinspruch"] = "Widerspruch eines Bürgers gegen eine Ausschreibung; vertraulich zwischen "
                + "Bürger und Behörde. Nach außen liest ausschließlich der Einreicher selbst seinen eigenen "
                + "Einspruch samt Entscheidung, im Bürgerbereich.",

            // --- the agency itself ---
            ["Agent"] = "Agenten bleiben nach außen anonym; weder Codename noch Klarname noch Dienstgrad verlassen das Haus.",

            // --- person files ---
            ["Person"] = "Die Akte bleibt drinnen; nach außen geht ausschließlich der Publikations-Snapshot einer Ausschreibung.",
            ["PersonDoc"] = "Maßnahmenprotokoll; nach außen nie.",
            ["PersonPhoto"] = "Bilddatei der Akte; ein Foto geht nur über den geprüften Publikations-Pfad hinaus.",
            ["ClassificationHistory"] = "Einstufungs-Verlauf; würde die Existenz eingestufter Akten verraten.",
            ["PersonAlias"] = "Aliasfeld der Akte; nach außen nur als Teil eines Snapshots.",
            ["PersonPhone"] = InternalRecord,
            ["PersonVehicle"] = "Fahrzeugdaten der Akte; nach außen nur als Teil eines Snapshots.",
            ["PersonLocation"] = InternalRecord,
            ["PersonWeapon"] = "Waffendaten der Akte; nach außen nur als Teil eines Snapshots.",
            ["ProfileSuggestion"] = "Unbestätigter Steckbrief-Vorschlag; nach außen nie.",
            ["PersonRelation"] = "Beziehungsgeflecht; nach außen nie.",

            // --- groupings ---
            ["Faction"] = "Die Akte bleibt drinnen; nach außen geht ausschließlich das Publikations-Profil.",
            ["FactionRank"] = InternalRecord,
            ["FactionWeaponStock"] = Operational,
            ["FactionInventory"] = Operational,
            ["FactionDrugRoute"] = Operational,
            ["FactionMember"] = "Mitgliederliste; nach außen nie.",
            ["FactionAgent"] = Assignment,
            ["FactionPhoto"] = "Bilddatei der Akte; nach außen nur über den geprüften Publikations-Pfad.",
            ["PersonGroup"] = InternalRecord,
            ["PersonGroupMember"] = "Mitgliederliste; nach außen nie.",
            ["PersonGroupAgent"] = Assignment,
            ["PersonGroupPhoto"] = "Bilddatei der Akte; nach außen nur über den geprüften Publikations-Pfad.",
            ["Party"] = InternalRecord,
            ["PartyMember"] = "Mitgliederliste; nach außen nie.",
            ["PartyAgent"] = Assignment,
            ["PartyPhoto"] = "Bilddatei der Akte; nach außen nur über den geprüften Publikations-Pfad.",

            // --- casework ---
            ["Case"] = InternalRecord,
            ["CaseAgent"] = Assignment,
            ["Operation"] = Operational,
            ["OperationAgent"] = Assignment,
            ["Taskforce"] = Operational,
            ["TaskforceAgent"] = Assignment,
            ["TaskforceMessage"] = "Interner Einheiten-Chat; nach außen nie.",
            ["RadioChannel"] = "Funkfrequenzen der Behörde und ihrer Partner; wer sie kennt, hört mit.",
            ["Observation"] = Operational,
            ["AgentAbduction"] = Operational,
            ["AbductionCompromise"] = Operational,
            ["EvidenceItem"] = Operational,
            ["EvidenceEntry"] = Operational,
            ["EvidenceEntryLine"] = Operational,
            ["Informant"] = "Quellenschutz; die Existenz einer Quelle verlässt das Haus nie.",
            ["InformantMeeting"] = "Quellenschutz; nach außen nie.",
            ["CounterIntelRule"] = "Gegenaufklärungs-Regel; ihre Kenntnis wäre eine Anleitung zur Umgehung.",
            ["WatchlistEntry"] = "Persönliche Merkliste eines Agenten; nach außen nie.",
            ["LeadDismissal"] = Operational,
            ["Followup"] = "Wiedervorlage an einer Akte; nach außen nie.",

            // --- attachments to records ---
            ["Source"] = "Quellenangabe an einer Akte; nach außen nie.",
            ["Comment"] = "Interner Kommentar an einer Akte; nach außen nie.",
            ["TextImage"] = "Eingefügtes Bild an einem internen Text; die Trägerakte entscheidet, nach außen nie.",
            ["Link"] = "Verknüpfung zwischen Akten; nach außen nie.",
            ["Tag"] = Configuration,
            ["TagMapping"] = Assignment,
            ["CustomFieldDefinition"] = Configuration,
            ["CustomFieldValue"] = "Zusatzfeld an einer Akte; nach außen nie.",

            // --- library and documents ---
            ["Document"] = "Bibliotheks-Dokument mit eigener VS-Stufe; nach außen nie.",
            ["DocumentTemplate"] = Configuration,
            ["DocTemplate"] = Configuration,
            ["LibraryFile"] = "Anhang der Bibliothek; nach außen nie.",
            ["DocumentAccessExclusion"] = Assignment,
            ["PartnerShare"] = "Freigabe an eine Partnerbehörde; das ist ein eigener Kanal, nicht die Öffentlichkeit.",
            ["PartnerAgencyProfile"] = "Zugangseinstellung einer Partnerbehörde; Verwaltung, kein Inhalt.",
            ["PartnerReleaseRule"] = "Regelfreigabe an eine Partnerbehörde; derselbe eigene Kanal wie die Einzelfreigabe.",

            // --- personnel ---
            ["AgentRankHistory"] = Personnel,
            ["AgentNote"] = Personnel,
            ["AgentPromotionRequest"] = Personnel,
            ["TrainingModule"] = Personnel,
            ["AgentModuleCompletion"] = Personnel,
            ["AgentBadge"] = Personnel,
            ["Absence"] = Personnel,
            ["AgentActivity"] = Personnel,
            ["AgentActivityLink"] = Assignment,
            ["ActivityTemplate"] = Configuration,
            ["PersonnelTemplate"] = Configuration,
            ["AgentInvite"] = "Einladungslink in die Behörde; nach außen nie.",

            // --- internal operations ---
            ["Request"] = "Antrag im Haus; nach außen nie.",
            ["Notification"] = "Benachrichtigung an einen Agenten; nach außen nie.",
            ["Job"] = "Aufgabe im Haus; nach außen nie.",
            ["JobAssignment"] = Assignment,
            ["Appointment"] = "Termin im Haus; nach außen nie.",
            ["AppointmentAssignment"] = Assignment,
            ["Meeting"] = "Besprechung im Haus; nach außen nie.",
            ["MeetingAgendaItem"] = "Tagesordnungspunkt; nach außen nie.",
            ["MeetingSignOff"] = Assignment,
            ["MeetingAttendance"] = Assignment,
            ["Announcement"] = "Mitteilung an die Belegschaft; nach außen geht stattdessen eine Pressemitteilung.",
            ["AnnouncementAcknowledgment"] = Assignment,
            ["Feedback"] = "Rückmeldung eines Agenten zur Anwendung; nach außen nie.",
            ["SituationReport"] = "Gefrorener Statistik-Snapshot eines Monats — er zählt Verschlusssachen und nennt "
                + "Personen mit ihrem internen Aktenzeichen. Nach außen geht kein Auszug daraus, sondern ein "
                + "von der Führung geschriebener Text (siehe OeffentlicherLagebericht).",
            ["DossierSummary"] = "Maschineller Kurzbrief zu einer Akte; nach außen nie.",

            // --- money ---
            ["KassenBuchung"] = Finance,
            ["KassenBuchungVorlage"] = Configuration,
            ["FinancingItem"] = Finance,
            ["FinancingRequest"] = Finance,
            ["FinancingRequestLine"] = Finance,
            ["FinancingBudgetPeriod"] = Finance,

            // --- assistant ---
            ["LlmRequestLog"] = Assistant,
            ["LlmQuotaPeriod"] = Assistant,
            ["LlmQuotaAdjustment"] = Assistant,
            ["NooseiConversation"] = Assistant,
            ["NooseiMessage"] = Assistant,

            // --- scoring and config ---
            ["ThreatScoreConfig"] = "Gewichtung des Bedrohungs-Scores; ihre Kenntnis wäre eine Anleitung zur Umgehung.",
            ["ThreatScoreHistory"] = "Score-Verlauf je Akte. Auch aggregiert nicht: die Reihe deckt jede Person "
                + "und Fraktion ab, Verschlusssachen eingeschlossen, und eine veröffentlichte Kurve exportierte den "
                + "rohen Score in abgeleiteter Form — beobachtbar und damit rückwärts prüfbar gegen die Gewichtung. "
                + "Der öffentliche Trend ist stattdessen die zuvor gesetzte Gefahrenlage-Stufe.",
            ["RecencyThreshold"] = Configuration,
            ["EnumLabelOverride"] = Configuration,
            ["CaseNumberCounter"] = "Zählerstand der Aktenzeichen; verrät die Zahl der Akten.",
            ["SavedSearch"] = "Gespeicherte Suche eines Agenten; nach außen nie.",
            ["GraphCanvasLayout"] = "Persönliche Graph-Ansicht; nach außen nie.",
            ["TextSnippet"] = "Persönlicher Textbaustein eines Agenten; nach außen nie.",
            ["SearchPhoneticKey"] = SearchInternal,
            ["SearchStemToken"] = SearchInternal,

            // --- logs ---
            ["AuditLog"] = Protocol,
            ["AccessLog"] = Protocol,

            // --- recruiting ---
            ["Bewerbung"] = Recruiting,
            ["BewerbungMessage"] = Recruiting,
            ["BewerbungTest"] = Recruiting,
            ["BewerbungTestQuestion"] = "Testfragen; öffentlich wären sie eine Lösungshilfe.",
            ["BewerbungTestOption"] = "Antwortoptionen; öffentlich wären sie eine Lösungshilfe.",
            ["BewerbungTestAssignment"] = Recruiting,
            ["BewerbungTestAnswer"] = Recruiting,
            ["Bewerbungssperre"] = "Bewerbungssperre einer Person; nach außen nie.",

            // --- public area's own tables ---
            ["BuergerProfil"] = "Konto eines Bürgers; sein Name gehört ihm, nicht der Website. Nach außen sieht ihn nur er selbst.",
            ["FahndungWarnhinweis"] = Assignment,
            ["HinweisNachricht"] = "Schriftwechsel zu einem Hinweis; er gehört den beiden Beteiligten. "
                + "Die interne Zielgruppe verlässt das Haus nie.",
            ["Ticket"] = "Anliegen eines namentlich bekannten Bürgers an die Führungsebene. Nach draußen "
                + "geht nur der eigene Faden, angemeldet im Bürgerbereich — das ist sein Konto, nicht die "
                + "Öffentlichkeit.",
            ["TicketNachricht"] = "Schriftwechsel eines Tickets; er gehört den beiden Beteiligten. "
                + "Die interne Zielgruppe verlässt das Haus nie.",
            ["TicketParticipant"] = "Welcher Agent an einem Anliegen sitzt, ist nach außen nie eine "
                + "Information — weder gegenüber dem Bürger noch öffentlich.",
            ["OeffentlicheVorlage"] = "Rohtext einer Bürger-Nachricht, mit unaufgelösten Tokens und dem "
                + "Arbeitstitel der Redaktion. Nach draußen geht die gerenderte Nachrichtenzeile, nie die "
                + "Vorlage selbst.",

            // --- handbook ---
            ["HandbookChapter"] = "Kapitelüberschrift des internen Handbuchs; es erklärt die Bedienung der "
                + "Dienststelle und richtet sich an Agenten, nicht an Bürger.",
            ["HandbookArticle"] = "Anleitung zur internen Arbeit — welche Schaltfläche was tut, welche Einstufung "
                + "wann gilt, wie eine Maßnahme abläuft. Nach außen wäre das eine Gebrauchsanweisung für die "
                + "eigenen Verfahren.",
            ["HandbookStep"] = "Einzelner Schritt einer internen Anleitung; teilt das Schicksal seines Artikels.",
            ["GlossaryTerm"] = "Begriffserklärung der internen Fachsprache. Was nach draußen erklärt werden "
                + "soll, steht im FAQ und wird dort redaktionell freigegeben.",

            // --- changelog ---
            ["ChangelogRelease"] = "Fassung der Anwendung samt Datum und Build-Nummer. Die Neuerungen richten "
                + "sich an die Agenten, die mit der Seite arbeiten; nach außen wäre es eine Inventarliste der "
                + "eingesetzten Software und ihres Alters.",
            ["ChangelogEntry"] = "Eine Zeile der Neuerungen. Sie benennt interne Abläufe und Bereiche, die es "
                + "nach außen gar nicht gibt — und welche Funktion wann dazukam oder repariert wurde, ist nach "
                + "draußen keine Information, sondern eine Zeitleiste der eigenen Lücken.",
        };

    /// <summary>True when someone has decided the entity's public fate either way.</summary>
    public static bool IsDecided(string entityName)
        => Publishable.ContainsKey(entityName) || NeverPublic.ContainsKey(entityName);

    /// <summary>True when the entity has a publication path at all.</summary>
    public static bool MayBePublished(string entityName) => Publishable.ContainsKey(entityName);
}
