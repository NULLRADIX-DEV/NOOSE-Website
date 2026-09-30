# NOOSE-Website

**Zentrale Akten- und Intelligence-Datenbank der NOOSE (National Office of Security Enforcement)** - einer fiktiven Sicherheitsbehörde auf einem FiveM-/GTA-RP-Server.

Live Demo: **https://demo.noose.info** (nicht alle Features sind in der Demo verfügbar!)

---

## Überblick

NOOSE-Website ersetzt verstreute Discord-Threads durch eine zentrale, durchsuchbare, bidirektional verlinkte Akten-Datenbank. Pro Person und pro Fraktion existiert genau eine Akte, in der alles zusammenläuft - Beobachtungen, Dokumente, Beziehungen, Einstufungen, Bedrohungsbewertungen.

Dazu kommt der **öffentliche Bereich** unter derselben Domain: Fahndungsboard, Kopfgeld, Presse, Warnungen, Gefahrenlage und Gesetzesauszüge nach außen - und ein Bürgerkonto, über das Bürger Hinweise geben, eine Ergreifung melden, Belohnungen erhalten, Einspruch einlegen und mit der Führungsebene schreiben. Was dort erscheint, entscheidet immer ein ausdrücklicher Publikationsvorgang; ab Werk ist fast jedes Modul **aus**, und ein Not-Aus nimmt den gesamten Außenauftritt vom Netz.

Alles ist auditiert (Created/Modified/Deleted), Soft-Delete-fähig und rang-/rollengestaffelt. Codebase anglisiert (englische Identifier), Domänen-Vokabular und UI sind Deutsch.

---

## Features

Die Anwendung hat zwei Hälften unter derselben Domain: den **internen Aktenbereich** der Behörde und den
**öffentlichen Bereich**, in dem Bürger fahnden lesen, Hinweise geben, Belohnungen erhalten und mit der
Behörde schreiben. Was nach außen geht, entscheidet immer ein ausdrücklicher Publikationsvorgang - der
öffentliche Bereich liest nie live im Aktenbestand, sondern nur in Veröffentlichungs-Schnappschüsse.

### Interner Bereich

**Akten & Bereiche**
- **Personen** `/personen` - Kern-Akten: Steckbrief (Aliase, Telefone, Fahrzeuge/Kennzeichen, Orte, Waffen), Lebensstatus, Aktenzeichen (`NOOSE-P-2026-0001`), Fotogalerie, Zugehörigkeiten mit Historie.
- **Personen-Doks** - Verhöre/Maßnahmen mit Ausgang (freigelassen/injiziert/erschossen); „Erschossen" → Lebensstatus Tot mit 20-Min-Respawn-Logik.
- **Duplikat-Erkennung** beim Anlegen (Name/Telefon) und **Merge** zweier Personenakten inkl. aller Verknüpfungen (Führung).
- **Observationen** - Überwachungseinträge mit Zeit/Ort an Personen und Organisationen.
- **Fraktionen** `/fraktionen` - Mitglieder (Bulk-Pflege, Leitungs-Flags), Ränge, Waffenbestand, Inventar, Drogenrouten, Konflikte/Allianzen, Erkennungsfarbe, Galerie.
- **Personengruppen** `/personengruppen` - lose Sammlungen/PoI mit Mitgliedern und Erfassungsfortschritt „x/y Mitglieder mit Akte".
- **Parteien** `/parteien` - politische Organisationen mit Leitung, Mitgliedern, Ausrichtung.
- **Foto-Galerien** - Fraktionen, Personengruppen und Parteien haben dieselbe Galerie: Massen-Upload (bis 20 Bilder je Vorgang), Lightbox und genau ein Titelbild je Akte, das als Profilbild in Liste, Karte und Detail erscheint; fällt es weg, rückt das älteste verbliebene Foto nach. Die Dateien liegen außerhalb `wwwroot` und laufen über autorisierte Endpoints (VS-Stufe der Akte, für Partner die Kindfreigabe), jeder Abruf landet im Zugriffsprotokoll.
- **Vorgänge** `/vorgaenge` - Fallakten, die Personen, Doks, Operationen und Observationen bündeln; eigener Status, Fallbearbeiter/Leitung. Personen am Vorgang tragen eine Rolle (Beschuldigter, Tatverdächtiger, Zeuge, Geschädigter, Informant, Anzeigeerstatter, Mittäter, Kontaktperson) mit Freitext-Option.
- **Operationen** `/operationen` - Einsatzberichte: Zeitraum, Ort, Beteiligte, Ergebnis, Status.
- **Taskforces** `/taskforces` - Leitungsrollen (Chefermittler, CID-Lead, TRU-Lead), Geltungsbereich, Genehmigungs-Workflow, **Live-Taskforce-Chat**.
- **Aufgaben** `/aufgaben` - Kanban-Board mit Drag-and-Drop, Prioritäten, Fälligkeit, „Nur meine"-Filter, gestuften Fälligkeits-Erinnerungen.
- **Termine & Kalender** `/kalender` - Termin-Akten mit Sichtbarkeitsstufen und Teilnehmern; FullCalendar-Ansicht (Monat/Woche/Liste, „Mein"/„Behörden"-Modus), aggregiert Termine, Operationen, Observationen, Fristen.
- **Dienstbesprechungen** `/besprechungen` - Tagesordnung, Protokoll-Notizen, Anwesenheitsliste, Klonen der Folgebesprechung, Erinnerungs-Worker (24 h/30 min).
- **Abmeldungen** `/abmeldungen` - Urlaub/Krank/RP-Pause mit Sichtbarkeits-Scopes, Kenntnisnahme und Anwesenheits-Statistik mit Anomalie-Ampel.
- **Dienst-Aktivitäten** `/aktivitaeten` - Diensteinträge der Agenten mit Vorlagen und Akten-Verknüpfung.
- **Informanten** `/informanten` - V-Personen mit Klarname (kein Deckname), Zuverlässigkeit, Kontaktdaten, Treffen-Protokollen; optional mit Personen- und Fraktionsakte verknüpft. Jeder interne Agent darf sie anlegen, lesen, bearbeiten und in den Papierkorb legen (Wiederherstellen wie überall nur die Führung); Partnerbehörden sehen nichts davon. Aufrufe landen im Zugriffsprotokoll.
- **Dokumente** `/dokumente` - Dokumenten-Bibliothek mit Rich-Text (Quill), Kategorien, Anheften, Datei-Anhängen; VS-Stufen (Leadership/TRU/HRB) und individuelle Freigabe-/Sperrlisten pro Agent.
- **Datei-Bibliothek** - zentrale Upload-Ablage (Formulare, SOPs) mit Kategorien und Download-Endpoint.
- **Gesetzbuch** `/gesetze` - durchsuchbare Gesetzes-Referenz, verknüpfbar mit Fällen/Doks, partner-freigebbar und paragrafenweise nach außen freigebbar.
- **Schwarzes Brett** `/brett` - Ankündigungen und gezielte Broadcasts (alle/Taskforce/TRU/Rang/HRB), Glocken-Push, Pflicht-Quittierung mit Zähler.
- **Fahndung** `/fahndung` - interne Fahndungsliste ab konfigurierbarer Gefahrenstufe plus manuelle Ausschreibungen; Filter „Nur Flüchtige"; Abschnitte Observationen und Vernehmungen sowie die behördenweiten Listen der öffentlichen Ausschreibungen, Organisationsprofile und Einsprüche.

**Geld, Material & Vorfälle**
- **Kasse** `/kasse` - zwei Kassenbücher (Schwarzgeld, Grüngeld) als Buchungsjournal; der Kontostand wird chronologisch aus den lebenden Buchungen gerechnet, nie gespeichert. Buchungsarten Einzahlung, Auszahlung und „Stand setzen", Aktenzeichen je Buchung (`KAS`), Schnellbuchen über Vorlagen, Druckansicht. Einzahlen darf jeder schreibberechtigte Agent, Auszahlung und Korrektur nur die Führung.
- **Asservatenkammer** `/asservatenkammer` - Katalog von Gegenständen mit Bild und Kategorie; der Bestand ergibt sich aus Ein- und Auslagerungs-Einträgen (mehrere Positionen je Eintrag, Präfix `ASS`). Besitzer ist NOOSE, ein Agent oder eine Person - entsprechend erscheinen die Einträge in der Personenakte und über die Mitglieder in der Fraktionsakte. Die Räumung bucht ausgewählte Bestände auf null, ein Negativbestand wird über eine Korrektur-Einlagerung ausgeglichen.
- **Finanzierungen** `/finanzierungen` - Ausrüstungs-Anträge aus einem von der Führung gepflegten Katalog: je Position Stückpreis, Menge und Zuschussanteil, Eigen- und NOOSE-Anteil getrennt ausgewiesen. Ablauf Beantragt → Angenommen/Abgelehnt → Ausgezahlt, dazu Rückzug, Rücknahme der Zusage und Storno; ausgezahlt wird als Buchung in der Grüngeld-Kasse, Buchung und Antrag committen zusammen. Monatsbudget je Agent aus Rang-Basis plus anteiligem Übertrag des Vormonats.
- **Entführungen** `/entfuehrungen` - Akte über die Entführung eines NOOSE-Agenten: Täter (Fraktion, Personengruppe oder Person), Zeitpunkt, Ort, Wahrheitsserum, Informationsabfluss mit Kategorien und Schweregrad, Ausgang (In Gefangenschaft, Geflohen, Befreit, Freigelassen, Getötet, Lösegeld gezahlt); Präfix `ENT`, Druckansicht, Meldung an die Führung beim Anlegen. Betroffene Akten lassen sich als **kompromittiert** markieren und später wieder normalisieren; ein eigener Abschnitt listet alle kompromittierten Akten.

**Querschnitt pro Akte (generisch an jeder Akte)**
- **Bidirektionale Verknüpfungen** zwischen allen Akten (Standard/Konflikt/Bündnis), in beide Richtungen klickbar; die Bezeichnung einer bestehenden Verknüpfung ist nachträglich änderbar, ohne sie zu löschen und neu anzulegen.
- **Person↔Person-Beziehungen** (Familie/Verbündeter/Feind/…) plus automatische „Kollegen"-Links bei gemeinsamer Mitgliedschaft.
- **Quellen/Anhänge** - Upload, Link, intern, Freitext, Dokument; pinnbar.
- **Kommentare** mit **@-Erwähnungen** (Akten + Agenten, verlinkt, mit Benachrichtigung inkl. Edit-Delta) und nachträglicher Bearbeitung - inline, nur durch den Verfasser, mit Marker „bearbeitet" und erneuter Sichtbarkeitsprüfung beim Speichern; die Führung darf weiterhin nur löschen.
- **Tags** mit Picker, Verwaltung und Nutzungsstatistik.
- **Custom-Felder** - admin-definierte Zusatzfelder je Aktentyp (6 Feldtypen).
- **Wiedervorlagen** mit Fälligkeit, Erledigen/Wiederöffnen und Hintergrund-Erinnerung an Zuständige + Follower.
- **Beobachtete Akten** `/watchlist` - „Folgen"-Knopf an jeder Akte; ein SaveChanges-Interceptor erkennt Änderungen an der Akte und ihren Kind-Datensätzen und fächert erst nach dem Commit aus. Folgen setzt Sichtbarkeit voraus, Entfolgen ist ein Soft-Delete; eine nicht mehr sichtbare Akte bleibt ohne Name und Link in der Liste, damit man sie noch entfolgen kann.
- **Zeitstrahl** pro Akte - Audit- und Fach-Ereignisse chronologisch, mit Kategorie-Filtern; die Vorgänge des öffentlichen Bereichs hängen bis zu drei Ebenen tief daran (Ausschreibung → Kopfgeld-Anteil → Belohnung, dazu Bürgerhinweis und Einspruch) und tragen bewusst inhaltsleere Titel ohne Bürgernamen.
- **Änderungs-Historie** - vollständiger Alt→Neu-Diff aus dem Audit-Log.
- **Aktualitäts-Ampel** - Frische-Badges mit Warn-/Stale-Schwellen je Aktentyp.
- **Einstufung mit Verlauf** - Prüffall → Verdachtsfall → Gesichert staatsgefährdend; höchste Stufe nur ab Senior Special Agent, sonst per Antrag.
- **Verschlusssachen** - Sichtbarkeit nur Führung/zugewiesene Agenten; greift in Suche, Listen, Graph und Zeitstrahl.
- **Verknüpfungs-Vorschläge** - Hinweise über Signale (gleiche Telefonnummer/Fraktion/Tag), 1-Klick-Verknüpfen.
- **Druckansicht/PDF-Export** für nahezu alle Aktenarten; die sechs großen Druckansichten (Person, Vorgang, Fraktion, Personengruppe, Partei, Operation) respektieren die rangabhängige Tab-Beschränkung einer Partnerbehörde.
- **Papierkorb (Soft-Delete)** `/papierkorb` - **29 Record-Typen** auf einer Sammelseite; wiederhergestellt wird über denselben Dienst wie die Akten, also mit Rechte-Guard, Statusprüfung (Publikationen kommen als Entwurf zurück) und erneuter Eindeutigkeits-/Kontingentprüfung.

**Suche & Navigation**
- **Globale Volltextsuche** `/suche` über **67 Kategorien** - Akten, Inhalte (Doks, Quellen, Kommentare,
  Wiedervorlagen, Verknüpfungen, Zusatzfelder, Vermerke, Taskforce-Chat, Besprechungsprotokolle, Observationen,
  Informanten-Treffen …), Verwaltung/Kataloge/Vorlagen, Protokolle und persönliche Einträge. Immer nur, was der
  Suchende ohnehin sehen darf.
- **Öffentliche Inhalte auch intern auffindbar** - neun eigene Suchanbieter für Ausschreibungen, Organisationsprofile,
  Bürgerhinweise, Tickets, Einsprüche, Pressemitteilungen, Informationsseiten, Warnungen und Lageberichte.
  Ausschreibungs- und Profiltreffer führen auf die dahinterliegende Personen- bzw. Fraktionsakte statt auf die
  Veröffentlichung; für Partnerbehörden ist keiner davon sichtbar.
- **Keine Vorfilter:** es wird immer alles Sichtbare durchsucht; Kategorien filtern als **Facetten mit
  Trefferzahlen** das Ergebnis, damit man sich keine Treffer wegklicken kann.
- **Zeitbudget mit ehrlichem Teilergebnis** - reißt es, kommt zurück was fertig ist, plus Hinweis welche Kategorien
  unvollständig sind (und ein „Rest nachladen"-Knopf). Nie stilles Weglassen.
- **Smart-Suche** (tippfehlertolerant, inkl. phonetischem Index) und **Deep-Scan** (alle Nebenfelder inkl. Steckbrief).
- **Gespeicherte Suchen** als wiederaufrufbare Chips; Tag-Filter.
- **Befehlspalette (Strg+K)** und Schnellsuche in der Topbar.
- **Quick-Add** - Schnellerfassung beliebiger Akten per Plus-Button.
- **Personalisierbare Navigation** - Favoriten, Reihenfolge, Ausblenden, individuelle Startseite, „Zuletzt besucht".
- **Legacy-Redirects** - entfernte Routen leiten auf die Hub-Seiten um.

**Analyse & Visualisierung**
- **Beziehungsgraph** `/graph` (vis-network) - Fokus-/Gesamtmodus, Tiefe 1–3, Typ-/Art-Filter, Vollbild, PNG-Export, gespeicherte Layouts pro Agent.
- **Pfad-Suche** - „Wie hängen A und B zusammen?" (kürzeste Kette).
- **Graph-Analytik** - Betweenness-Zentralität und Community Detection.
- **Ermittlungshinweise** `/ermittlungshinweise` - Link-Vorhersagen, neue Konflikte, veraltete Einstufungen; ignorierbar.
- **Nachweis & Gegenaufklärung** `/nachweis` - alle Agenten-Überwachung auf einer Sammelseite: **Chronik** (behördenweiter Ereignis-Feed nach Tagen gruppiert inkl. Feld-Diffs, darüber KPI-Kacheln und ein gestapeltes Aktivitätsband mit Achse, Tooltip und Klick-zum-Tagesfilter; Filter stehen in der URL), **Änderungs-** und **Zugriffsprotokoll** (Führung und Nur-Lese-Aufsicht) sowie **Gegenaufklärung** mit Lagebild, Agenten-Profil, Auffälligkeiten und einem Regel-Baukasten (Führung ohne Nur-Lese-Aufsicht).
- **Regel-Bedingung „gemeinsame Organisation"** - eine Auffälligkeitsregel kann fordern, dass der Handelnde eine Organisation mit der Ziel-Akte teilt (aufgelöst über die verknüpfte Personenakte des Kontos); mitgeliefert wird „Hinweisgeber im eigenen Umfeld". Auffälligkeiten nennen niemanden, dessen Anonymitätszusage noch gilt, und die Nur-Lese-Aufsicht ist nie Gegenstand der Auswertung.
- **Organigramm** `/organigramm` - Dienstgrad-Hierarchie, TRU/HRB, Taskforce-Besetzung (Klarname nur Führung).

**NOOSEI (KI-Integration)**
- **KI-Assistent** `/ki-assistent` - Chat, der nicht aus dem Prompt antwortet, sondern über **15 Lesewerkzeuge** auf der Aktendatenbank: Aktensuche, Bestandssichtung, Bereich, Akte und Akteninhalt, Verbindungen und Verbindungsweg, Zeitstrahl, letzte Änderungen, Erwähnung auflösen, Kalender, Kennzahlen, Kurzbrief, Beobachtungsliste und Aufschlüsselung eines Bedrohungs-Scores. Der Chat zeigt den laufenden Arbeitsschritt und die benutzten Werkzeuge, hängt Quellen-Chips der berührten Akten an und markiert Aktenzeichen als „nicht belegt", wenn sie in der Antwort stehen, aber in keinem Werkzeugergebnis vorkamen.
- **Sichtbarkeit als harte Grenze** - jedes Werkzeug filtert selbst über die Sichtbarkeit des fragenden Agenten; „gibt es nicht" und „darfst du nicht sehen" sind ununterscheidbar, Schreibwerkzeuge existieren nicht. Auch der öffentliche Bereich ist lesbar (Ausschreibungen und Einsprüche zu einer Person, Fraktionsprofil, Bürgerhinweise, Ticket-Schalter, Presse/Info/Warnungen/Lage).
- **Kontingente & Betrieb** - jeder Dienstgrad hat ein Wochenkontingent in Kontingent-Token (1.000 Token = 1 Cent echter Kosten) mit anteiligem Übertrag und Tagesdeckel; eigener Stand unter `/ki-assistent?tab=kontingent`. Alle Anfragen laufen über **ein Gateway**, das Recht und Kontingent prüft, genau einmal abbucht und protokolliert (Abschlussgrund, Werkzeugaufrufe, Versuche, Modell-Dauer). Vier konfigurierbare Auffälligkeitsregeln melden Ausreißer; Kontingente und Prompt-Zusatz ändert allein der KI-Eigner, echte Geldbeträge sieht auch nur er.
- **KI-Kurzbrief** pro Akte - LLM-generierte Dossier-Zusammenfassung mit Caching und Staleness-Erkennung.
- **Schreibhilfe im Rich-Text-Editor** - Rechtschreib-/Grammatikkorrektur ohne Formatierungsänderung und Textentwurf aus einer Anweisung, beides mit Diff-Ansicht. Eine Korrektur wird verworfen, sobald sie Zahlen, Aktenzeichen, Erwähnungen oder Vorlagen-Platzhalter verändert - bei Bewerbungs-Anschreiben zählen Anzahl und Schreibweise der Tokens, weil sonst die Schwärzung des Absendernamens still ausfiele.

**Bedrohungs-Score (automatisch, admin-konfigurierbar)**
- **Fraktions-Score** - S1 Aktivitäts-Heat, S2 Organisation & Reichweite, S3 Konflikt & Bündnis, S4 Netzwerk-Zentralität.
- **Personen-Score** - P1 Maßnahmen, P2 Bewaffnung & Flucht, P3 Observation, P4 soziale Gefahr, P5 Netzwerk.
- Halbwertszeit-Decay, Sättigungskurven, Teilscore-Treiber, Konfidenz-Wert, Triage-Hinweise.
- **Täglicher Sweep-Worker** rechnet alle Scores neu; Score-Historie mit Sparklines, Trends, Top-Movern.
- **Threat-Spike-Alarm** an die Führung bei Score-Sprüngen.
- Alle Gewichte/Caps admin-editierbar inkl. Vorschau-Verteilung.

**Dashboard & Statistik**
- **Dashboard** `/dashboard` - Kennzahlen-Kacheln, Verteilungs-Charts, Top-Gefährdungen, veraltete Akten, fällige Wiedervorlagen, Aktivitäts-Feed.
- **Statistik** `/statistik` - Verteilungen (Donuts), 12-Monats-Zeitreihen, Top-Listen; VS-gefiltert.
- **CSV-Export** (Excel-tauglich, UTF-8-BOM) und Druck/PDF der Statistik.
- **Bedrohungs-Trend**-Auswertung mit Verlaufskurven.
- **Automatischer Lagebericht** - monatlicher Snapshot, Archiv `/lageberichte`, Führungs-Benachrichtigung, täglicher Worker.

**Benachrichtigungen & Live-Updates**
- **In-App-Glocke** mit **35 Benachrichtigungstypen** und Ungelesen-Zähler; für laufende Konversationen faltet der Dienst zusammen: solange eine ungelesene Meldung derselben Kategorie auf dasselbe Ziel steht, wird sie auf den neuen Text gehoben und nach oben datiert, statt eine zweite Zeile zu schreiben.
- **Live-Push** über neun Singleton-Broadcaster plus Watchlist-Dispatcher (Glocke, Taskforce-Chat, Freigaben, Quittierungen, Dokumentzugriffe, Finanzierungen, Bewerbungen sowie neu Bürgerhinweise und Ticket-Chat).
- **Discord-Outgoing-Webhooks** pro Benachrichtigungskategorie, konfigurierbar unter `/einstellungen?tab=discord`, mit Test-Funktion und je Kategorie einstellbarem Rollen-Ping.
- **„Neuer Termin"** meldet jeden angelegten Kalender-Termin an Führung und Admins samt Discord-Kanal - mit Titel, Zeit und Kategorie, ohne Ort und Beschreibung, über alle Sichtbarkeitsstufen hinweg.
- **„Kündigung"** hat einen eigenen Kanal mit rotem Embed (Name, Datum, Ausführender, Begründung) und pingt bewusst niemanden; beim Kündigen sind Personalakten-Vermerk und Discord-Post einzeln zuschaltbar.
- **Zehn Hintergrund-Worker** - Wiedervorlagen, Aufgaben-Erinnerung (gestuft), Besprechungs-Erinnerung, Threat-Sweep, Abzeichen-Sweep, Top-Agent-Ankündigung, Suchindex-Backfill, Lagebericht, Ablauf von Eignungstests (1 min) und Ablauf öffentlicher Ausschreibungen (15 min).

**Personal & Recruiting**
- **Personalakten** `/personal` - Dienstgrad-Verlauf, 7 Vermerk-Arten (Lob/Disziplinar/…), Beförderungsanträge, Ausbildungsmodule.
- **Beförderungs-Workflow** - Vorschlag ab Führung, Entscheidung ab Deputy Director.
- **Agenten-Verwaltung** `/admin/agenten` - Rang, Status, Flags (Admin/TRU/HRB/TeamLead), Sperren, Kündigung, Namensänderungs-Workflow.
- **Einladungs-Links** - Token mit Ablaufdatum, einlösbar/widerrufbar (Invite-only-Onboarding).
- **Bewerber-Portal** `/portal` - eigener Bereich für Konten mit Status „Bewerber": Bewerbung einreichen, Eignungstest ablegen, Status einsehen.
- **Bewerbungs-Pipeline** `/bewerbungen` - Eingereicht → Sicherheitsprüfung → Test → Gespräch → Angenommen/Abgelehnt.
- **Test-Builder** - MC/Ja-Nein/Freitext mit Auto-Bewertung (Fuzzy-Matching) und manueller Nachkorrektur; Multiple-Choice optional mit Mehrfachauswahl, automatisch als alles-oder-nichts gewertet.
- **Eignungstest mit Bearbeitungszeit** `/portal/test` - je Test 1–600 Minuten hinterlegbar (leer = unbegrenzt). Die Uhr startet serverseitig beim ersten Abruf des Fragebogens, nicht beim Zuweisen; die Frist liegt in der Datenbank und übersteht Reload, Reconnect und Neustart. Der Countdown sichert offene Eingaben höchstens alle 10 Sekunden als Entwurf, warnt einmal bei fünf Minuten Restzeit und gibt bei Ablauf ab, was eingetragen ist.
- **Ablauf-Sweep und Zeitsteuerung** - ein Minuten-Worker gibt abgelaufene Versuche auch bei geschlossenem Browser ab (unbeantwortete Fragen als leere Antwortzeile, getippte Entwürfe bleiben stehen) und benachrichtigt Bewerber und zuständigen Agenten. Im Testpanel der Bewerbung stehen Zusatzminuten (+5/+10/+15/+30) und „Test neu freigeben" mit Begründung, dazu Startzeitpunkt, Restzeit, Zeitbudget und Versuchszähler - beides nur mit Schreibrecht plus HRB/Führung und gesperrt, sobald über die Bewerbung entschieden ist.
- **Fragebogen-Härtung** - Antwortoptionen werden je Versuch deterministisch gemischt (stabil über Reload und Neustart, je Frage abschaltbar), Fragen und Optionen sind gesperrt, solange ein befristeter Versuch läuft, und der Fragebogen liegt unter einem Wasserzeichen aus Aktenzeichen und Zeitstempel mit unterbundenem Markieren, Kopieren, Kontextmenü und Strg+C/X/A/P/S.
- **Statusseite ohne Ergebnis-Rückschluss** `/portal/status` - Test- und Gesprächsphase erscheinen dem Bewerber als eine Stufe „Auswahlverfahren", damit das Weiterrücken das Testergebnis nicht verrät; die Seite liest bewusst nur die Zusammenfassung, damit ihr Aufruf die Uhr nicht startet.
- **Anschreiben-Vorlagen** mit Tokens (Absendername immer geschwärzt), interne Threads, Nachrichten an Bewerber.
- **Bewerbungssperren**/Blacklist mit Verkürzen/Aufheben; Ablehnung, Schließen und eine nicht bestandene Sicherheitsüberprüfung setzen automatisch eine 14-tägige Sperre am Discord-Konto, eine Annahme hebt eine laufende Zeitsperre auf. Übernahme als Agent inkl. Personen-Verknüpfung.
- **Selbstbewerbung aus dem Bürgerkonto** `/karriere` - ein bestehendes Bürgerkonto meldet sich über die Karriereseite an und wechselt auf Status „Bewerber"; dasselbe Discord-Konto, rotierter Security-Stamp, Audit-Zeile, derselbe Modul-Schalter „Karriere".
- **Agenten-Profilbild** `/profil` - JPG/PNG/WebP/GIF bis 2 MB; unterhalb der Führung geht es als Antrag in den Freigabe-Posteingang und wird erst nach Genehmigung sichtbar, die Führung setzt ihr Bild sofort. Ohne Bild zeigen Kopfleiste, Organigramm-Kachel und Profilkarte die Initialen des Codenamens.

**Team & Motivation**
- **Bestenliste & Abzeichen** `/bestenliste` - Ranking nach dokumentierter Ermittlungsarbeit: angelegte Akten (×3), Doks (×2), Verknüpfungen, Einstufungen (×2), Observationen und abgeschlossene Vorgänge (×5), wahlweise 7 Tage, 30 Tage oder Gesamtzeitraum. Ab Supervisory Special Agent zählt die Arbeit weiter mit, wird aber „außer Wertung" geführt - keine Platzierung, keine Medaille. Neun Meilenstein-Abzeichen vergibt ein täglicher Worker; sie stehen im eigenen Profil und in der Personalakte.
- **„Bester Agent"-Automatik** - in einstellbarem Takt (1–366 Tage) meldet die Behörde die Top 3 als Ankündigung samt Discord-Kanal und legt optional je Platzierung ein Lob in die Personalakte; die Überschrift folgt dem Intervall (Tag, Woche mit KW, Monat, Quartal, Jahr, sonst „letzte N Tage"). Konfiguration unter `/einstellungen?tab=bester-agent`.
- **Feedback zur Website** `/feedback` - Verbesserungsvorschläge, Bugs, Mängel und Feature-Requests direkt aus der Anwendung, wahlweise mit Seite und Tab als Kontext. Jeder interne Agent sieht neben den eigenen auch alle anderen Meldungen (Partnerbehörden nichts davon); Status (Neu, Angenommen, In Umsetzung, Umgesetzt, Abgelehnt, Zurückgestellt) und Antwort setzt nur die Führung, der Melder wird über jede Entscheidung benachrichtigt.
- **Mein Profil** `/profil` - eigene Stammdaten (Klarname nur für die Führungsebene sichtbar, Codename, Dienstnummer); wer nicht zur Führung gehört, reicht die Änderung zur Freigabe ein statt sie zu speichern. Dazu Profilbild, die eigenen Leistungswerte mit den verdienten Abzeichen und der Stand der eigenen Hochstufungs-Anträge.

**Anträge & Freigaben**
- **Zentraler Posteingang** `/admin/freigaben` mit Live-Updates.
- Account-Freigaben, Hochstufungs-Anträge, Taskforce-Genehmigungen, Beförderungen, Stammdatenänderungen, Partner-Freigaben, Profilbilder sowie aus dem öffentlichen Bereich Veröffentlichungs- und Kopfgeld-Anträge.
- Einheitlicher Genehmigen/Ablehnen-Workflow mit Begründung und Verlauf.

**Partner-Zugriff (DoJ/LSPD/LSMD)**
- Partner-Accounts mit Behörde + Rang, strikt read-only, eigene eingeschränkte Navigation.
- **Akten-Freigaben** an Behörde oder Einzelperson, inkl. Kind-Datensätzen; typ-weite Massenfreigaben. Neu freigebbar ist der Foto-Abschnitt von Personengruppen und Parteien; der Abschnitt „Öffentliches Profil" einer Fraktion liegt bewusst außerhalb.
- **Sichtbarkeits-Policies** pro Behörde/Rang - welche Akten-Typen und Detail-Tabs sichtbar sind; die Beschränkung gilt jetzt auch in den Druckansichten.
- Der öffentliche Bereich und der Bürgerbereich stehen Partnern offen wie jedem anderen Konto; **einreichen** (Zivil-Identität, Ticket, Hinweis) dürfen sie ausdrücklich, die Nur-Lese-Aufsicht nicht.

**System & Administration**
- **Audit-Log** - jede Änderung mit Wer/Wann/Alt→Neu, unveränderlich, filterbar.
- **Zugriffsprotokoll** - wer hat welche Akte wann angesehen.
- **Gegenaufklärungs-Cockpit** - Zugriffs-Heatmap, Agenten-Profile, Insider-Flags (z. B. Off-Hours-Zugriffe), eigener Regel-Baukasten.
- **Wartungsmodus + Ankündigungsbanner** (Info/Warnung/Fehler).
- **Theming** - Dark-Mode „Anthrazit + Cyan"; Akzentfarben und Logo zur Laufzeit änderbar.
- **Wertelisten** - 9 auto-lernende Vorschlagskataloge mit Umbenennen/Löschen und Massenpropagation; Enum-Anzeigenamen per DB überschreibbar.
- **Vorlagen-Systeme** - Dok-, Dokument-, Aktivitäts-, Personal- und Bürger-Vorlagen mit Platzhalter-Ersetzung beim Anwenden.
- **Einstellungen-Hub** `/einstellungen` - **34 Sektionen in 9 Gruppen** (Betrieb, Akten-Konfiguration, Vorlagen, Personal, Finanzierung, KI, Partnerbehörden, Öffentlicher Bereich, Nachweis); allein der öffentliche Bereich stellt zwölf davon.
- **Geschützte Uploads** außerhalb `wwwroot`, autorisierte Download-Endpoints, Content-Type-Whitelists, Pfad-Traversal-Schutz.
- **Health-Check** `/health`, automatische DB-Migration beim Start, Prod-/Lokal-Umschaltung per Connection-Probe.

**Auth & Demo**
- **Discord-OAuth** als einziger Login (Rate-Limit), Account-Lifecycle Ausstehend → Freigabe → Aktiv; daneben die Status **Bewerber**, **Bürger**, Gesperrt und Gekündigt (6 Werte insgesamt).
- **6 Dienstgrade** + Flags (Admin/TRU/HRB/TeamLead) → **19 Policies**; Berechtigungen serverseitig im Service-Layer erzwungen.
- **OnlyReader** (TeamLead ohne Admin) - liest alles, schreibt nichts, sieht nie Klarnamen.
- **Kill-Switch** - Sperrung/Rangänderung beendet Sessions in ≤30 s (Security-Stamp-Rotation).
- **Demo-Instanz** (demo.noose.info) - read-only, anonym browsbar als Demo-Agent, idempotenter Demo-Daten-Seed.
- **Deploy/Backup** - GitHub-Actions-Workflow „Deploy“ (Prod und Demo, Rollback auf das vorherige Release), Server-Checks vor dem Umschalten, DB-Dump vor jedem Deploy; nächtliche DB-Backups laufen über 1Panel auf dem Server.

### Öffentlicher Bereich

Vollständig gebaut (Phase 1–18). Ab Werk ist fast alles **aus** - ein Deploy veröffentlicht nichts.

**Grundgerüst & Betrieb**
- **Öffentliche Startseite** `/` - anonyme Landing-Seite mit Gefahrenlage-Kachel, Zahlenband, zuletzt ausgeschriebenen Steckbriefen, aktueller Pressemitteilung und Einstiegen zu Hinweis, Karriere und Bürgerkonto. Jeder Block liest seinen eigenen Modul-Schalter und seine eigene Quelle, damit ein Ausfall nur den eigenen Block leert; die Tab-Leiste baut sich aus den aktiven Modulen. Einheitliche Außen-Shell (`PublicSiteLayout` + `PublicHeader` + `PublicNav`), ein Agenten-Login wird nach außen nicht angeboten.
- **Modulbaukasten** `/einstellungen?tab=oeffentliche-module` - **23 Katalog-Module** in drei Gruppen (Fahndung, Behörde, Service), von Gesucht, Gefasst und Gefahrenlisten über Presse, Führung, Recht und Lage bis Hinweis geben, Belohnung, Ticket-Chat, Einspruch und Bürger-Registrierung. Je Modul ein Schalter, dazu Reihenfolge, eigene Bezeichnung, Icon aus einer Allowlist und ein eigener Offline-Text. Der Seeder legt fehlende Schalterzeilen idempotent an und überschreibt eine gespeicherte Wahl nie.
- **Not-Aus** - ein Admin-Schalter nimmt den gesamten Außenauftritt vom Netz und schlägt jeden Einzelschalter, ohne eine gespeicherte Modulwahl zu verändern: alle Tabs verschwinden, ein Warnbalken erscheint in der Außen-Shell, die Dienste werfen. Der interne Wartungsmodus greift nach außen nicht - der Not-Aus ist sein Gegenstück.
- **Abgeschaltetes Modul antwortet 404** - `PublicModuleGate` sitzt in 20 öffentlichen Seiten, setzt beim statischen Rendern Status 404 und `noindex, nofollow`; die Dienste prüfen den Schalter zusätzlich selbst, die UI ist also nie die einzige Schranke. **Publizieren braucht ein lebendes Modul, Depublizieren nie.**
- **Indexierung** - `PublicRoutes` ist die einzige Wahrheit darüber, was indexierbar ist; alles außerhalb bekommt über eine eigene Middleware `X-Robots-Tag: noindex, nofollow`. Dazu eine `robots.txt`, die drei interne Pfade zusätzlich sperrt. Der Bürgerbereich fehlt bewusst: er ist privat, nicht öffentlich.
- **Link-Vorschau** - jede öffentliche Seite trägt Titel, Kurztext und Bild für die Karte, die Discord und andere aus einem geteilten Link bauen; der Fahndungssteckbrief nimmt dafür das veröffentlichte Fahndungsfoto, alle übrigen die Behördenmarke. Der Kurztext kommt aus dem, was die Seite ohnehin sagt, und wird nie aus der Mitte eines Tags geschnitten - eine Pressemitteilung trägt ihre Bilder als base64 im Text. Vorschau und `noindex` stehen in **einer** Komponente, weil zwei Kopf-Blöcke auf einer Seite sich nicht addieren: der Ausgabepunkt behält nur den zuletzt registrierten. Ein Scan-Test fordert die Zeile für jede öffentliche Seite ein, die beiden Ausnahmen tragen ihren Grund.
- **Partitionierte Rate-Limits** - Login 10/min je IP, Hinweis-Dateiendpoint 60/min je Konto, öffentliche Suche 20/min je IP; Abweisungen antworten mit 429, `Retry-After` und Klartext-Body. Der Limiter läuft nach dem Antiforgery-Schritt, damit ein tokenloser POST kein Kontingent verbraucht.
- **Veröffentlichungs-Register mit Deckungstest** - `PublicVisibility` führt für jede Tabelle des Datenmodells auf, was von ihr nach außen geht oder warum sie nie hinausgeht. Ein Test reflektiert über alle DbSets und schlägt fehl, sobald eine neue Tabelle unentschieden bleibt - die Frage „darf das raus?" wird erzwungen, nicht der Aufmerksamkeit im Review überlassen.
- **Öffentliche Dateien** - Uploads liegen außerhalb `wwwroot` unter eigenen Pfaden je Zweck und sind **Kopien**, damit kein anonymer Abruf an ein internes Aktenfoto oder einen Agenten-Avatar kommt (10 MB, JPEG/PNG/WebP/GIF, serverseitig geprüft). Genau zwei Endpoints antworten anonym - Fahndungsfoto und Führungsfoto -, beide mit genau einer 404-Antwort für jeden Fehlschlag, damit sie kein Existenz-Orakel werden.
- **Rechtstexte** `/datenschutz`, `/nutzungsbedingungen` - anonyme Seiten in eigener Shell, verlinkt aus einem gemeinsamen Footer der drei Außen-Shells (öffentlich, Bürger, Bewerber-Portal); Impressum extern. Die Seiten des öffentlichen Bereichs rendern statisch, einzige Ausnahme ist das Hinweisformular.

**Bürgerkonto**
- **Bürgerkonto per Discord** `/buerger` - Anmeldung von der Startseite legt ein Konto mit Status `Civilian` an: Discord-authentifiziert, ohne jedes Behördenrecht. Der Modul-Schalter „Bürger-Registrierung" wird im Login-Endpoint selbst geprüft und bremst ausschließlich Neuanmeldungen; bestehende Konten behalten ihren Zugang.
- **„Mein Bereich"** - eigene Shell mit derselben Kopfzeile wie die öffentliche Seite und einer zweiten Reihe: Übersicht `/buerger`, Meine Hinweise `/buerger/hinweise`, Meine Tickets `/buerger/tickets`, Einspruch `/buerger/einspruch`, Mein Konto `/buerger/profil`. Die Übersicht zeigt Ungelesen-Zähler und einen Sperrhinweis samt Begründung; Agenten, Partner und Aufsicht behalten den Rückweg „Interner Bereich".
- **Zivil-Identität mit Namenssperre** `/buerger/profil` - Vor- und Nachname (je max. 64 Zeichen) legt der Bürger einmalig selbst fest, nach Pflicht-Häkchen, dass der Name dem IC-Namen entspricht und endgültig ist. Danach ist er gesperrt, eine Korrektur nimmt nur die Führungsebene vor. Ohne Namen bleibt der öffentliche Bereich lesbar, aber jede Einreichung scheitert - die Sperre macht den Namen als Identitätsnachweis brauchbar (Einspruch legt nur ein, dessen Kontoname dem veröffentlichten Namen entspricht).
- **Zugang, Identität und Einreichen sind drei Fragen** - `MayUseCitizenPortal()` (jedes angemeldete Konto darf in den Bürgerbereich), `IsCitizen()` (Status Civilian) und `MayCitizenSubmit()` (eigene Zivil-Identität: Partner ja, Nur-Lese-Aufsicht und Demo-Besucher nein). Durchgesetzt im Service, nicht in der Oberfläche.
- **Bürgerkonto wird Bewerbung** - meldet sich ein bestehendes Bürgerkonto über die Karriere-Seite an, wechselt es auf Status `Applicant`: dasselbe Discord-Konto, zusätzlich das Bewerber-Portal. Der Statuswechsel wird auditiert und rotiert den Security-Stamp, damit eine offene Sitzung nicht weiter Bürgerstatus behauptet.
- **Bürgerkonten-Verwaltung** `/einstellungen?tab=buerger` - Roster mit Suche über Name und Discord-Handle, Registrierdatum, Zahl der bestätigten Hinweise und Status. Die Führung korrigiert Namen (der einzige Weg an der Selbst-Sperre vorbei), verknüpft eine Personenakte, **sperrt** (nimmt nur das Einreichen, der öffentliche Bereich bleibt lesbar) oder **sperrt aus** (Status `Blocked` plus Security-Stamp-Rotation, laufende Sitzung endet binnen 30 s). Bürgerkonten erscheinen in keiner Agenten-Auswahlliste.

**Fahndung**
- **Öffentliches Fahndungsboard** `/gesucht` - anonym erreichbares Kachelboard mit Filter-Chips nach Art und Gefahrenstufe, dahinter der Steckbrief `/gesucht/{Aktenzeichen}` mit Foto, Aliasen, Vorwurf, „Zuletzt gesehen", Fahrzeugangabe, Warnhinweis-Chips und ausgeschriebener Belohnung. Beide Seiten rendern statisch ohne Circuit; jeder Steckbrief-Aufruf erhöht einen internen Aufrufzähler.
- **Ausschreiben aus der Personenakte** - eine Ausschreibung entsteht im Abschnitt „Öffentliche Ausschreibung" der Personenakte als eigenständiger **Publikations-Snapshot**: der Entwurf übernimmt nur Name und Vorwurf, alles Weitere pflegt der Autor am Fahndungseintrag - spätere Änderungen an der Akte wandern nicht von selbst nach außen. Ab Senior Special Agent wird direkt veröffentlicht (Präfix `FA`, Discord-Post), darunter entsteht ein Veröffentlichungsantrag mit Pflicht-Begründung. Eine Verschlusssache lässt sich nicht ausschreiben; Einstufung, Löschung oder Zusammenführung der Akte zieht bestehende Ausschreibungen automatisch mit Grund offline.
- **Manuelle Gefahrenstufe** - die nach außen gezeigte Stufe ist frei wählbar, der Bedrohungs-Score belegt sie nur vor. Eine Änderung setzt ein Kennzeichen, sodass spätere Veröffentlichungen die Handauswahl nicht überschreiben; nur „Stufe aktualisieren" holt den Score zurück. Die Stufe ordnet die Gefahrenliste und geht als Faktor in die Priorität der Hinweise ein.
- **Sachfahndung (Fahrzeuge & Waffen)** - aus den Steckbrief-Einträgen einer Akte erzeugbar; nach außen gehen Kennzeichen bzw. Bezeichnung, nie der Name der Halterin und nie ein Foto, und der Vorwurf wird bewusst nicht vorbefüllt. Intern bleibt der Aktenbezug bestehen, damit die Unterdrückung bei Verschlusssache greift. Eigener Unterschalter.
- **Warnhinweise** `/einstellungen?tab=warnhinweise` - redaktionelle Werteliste der Warn-Chips („bewaffnet", „gewaltbereit", „flieht mit Fahrzeug", „nicht selbst eingreifen" als Startbestand), Farben aus einer festen Allowlist. Ein Hinweis auf „inaktiv" verschwindet sofort von allen laufenden Ausschreibungen - der übliche Weg, ihn zurückzunehmen.
- **Fahndungsposter** `/gesucht/{Aktenzeichen}/druck` - druckfertiges Aushang-Blatt mit Foto, Gefahrenstufe, Belohnung, Warnhinweisen, Vorwurf und Fußzeile mit der eigenen URL; eigener Modul-Schalter, immer `noindex`, zählt keinen Aufruf.
- **Ablauf, Zurückziehen und Archiv** `/gefasst` - eine Ausschreibung kann ein Ablaufdatum tragen (bis Tagesende), ein 15-Minuten-Worker setzt fällige Einträge auf „Abgelaufen" und meldet das der Führung. Zurückziehen verlangt immer einen Grund und funktioniert auch bei abgeschaltetem Modul, Löschen erst danach. Abgeschlossene Fälle stehen unter `/gefasst` ohne Vorwurf, mit Graustufen-Foto und Datum, gedeckelt auf die 100 jüngsten samt genannter Gesamtzahl.
- **Foto-Auslieferung** `/gesucht/{Aktenzeichen}/foto` - beim Veröffentlichen und beim Ändern wird das gewählte Aktenfoto in einen eigenen öffentlichen Pfad kopiert; ausgeliefert wird nur diese Kopie. Unbekanntes Aktenzeichen, Entwurf, zurückgezogen, Modul aus oder fehlende Datei beantwortet der Endpoint einheitlich mit 404, ein Abruf schreibt keine Zugriffszeile.
- **Ergreifungsmeldung** `/hinweis/gestellt` - vom Steckbrief aus meldet ein Bürger mit Konto, dass er die gesuchte Person selbst gestellt hat: „wird festgehalten" oder „bereits übergeben", Pflicht-Ortsangabe, Freitext, optionaler Anhang. Nie anonym, höchstens zwei Meldungen je Konto in 24 h, nur eine offene je Ausschreibung, nicht zur eigenen und nur zu einer echten Personenfahndung. Läuft als Hinweis mit eigenem Aktenzeichen in den Bürgerschalter, bekommt den höchsten Prioritätsboden und benachrichtigt die Führung.

**Kopfgeld & Belohnung**
- **Kopfgeld-Anteile** - Kopfgeld besteht aus einzelnen Anteilen je Ausschreibung, behördlich aus einem Kassenkonto oder privat aus dem Geld eines Agenten. Nach außen geht ausschließlich die Gesamtsumme aller zugesagten und gesicherten Anteile, wahlweise als Obergrenze („bis X"), nie Herkunft, Stifter, Konto, Status oder Anzahl. Anteil je Eintrag auf 100 Mio. $ gedeckelt, die Historie ist anhängend; eine Erhöhung meldet der Discord-Kanal, eine Senkung nicht.
- **Antrag & Entscheidung** `/admin/freigaben` - behördliches Geld sagt ab Senior Special Agent direkt zu, darunter entsteht ein Kopfgeld-Antrag mit Pflicht-Begründung. Je Ausschreibung nur ein offener Antrag; ein beantragter Anteil zählt nicht zur öffentlichen Summe, ein abgelehnter gilt als zurückgezogen. Bürger, Partnerbehörden und die Nur-Lese-Aufsicht setzen kein Kopfgeld.
- **Einzahlung und Rückzug privater Anteile** - ein zugesagter privater Anteil wird an die Kasse übergeben: Kassenbuchung (Verwendungszweck aus Aktenzeichen und **Codename** des Stifters, nie ein Klarname) und Statuswechsel auf „Gesichert" laufen in einer Transaktion, gegen Doppel-Einzahlung per Compare-and-Swap gesichert. Rückzug nur mit Begründung und nur vor der Sicherung.
- **Deckungsansicht** `/kasse` - der Abschnitt „Kopfgeld" stellt je Konto das gebundene Kopfgeld dem Bestand gegenüber und weist eine Deckungslücke aus - als Warnung, nie als Sperre.
- **Auszahlung an Hinweisgeber** - steht eine Ausschreibung auf „Gefasst", verteilt die Führung das Kopfgeld über einen Dialog auf bis zu zehn Bürgerhinweise derselben Ausschreibung. Die Verteilung zieht zuerst Kassengeld (je Teilbetrag eine Kassen-Auszahlung), zugesagtes Privatgeld zuletzt. Nicht auszahlbar sind Hinweise mit ungelöster Anonymität oder unpassendem Status, jeweils mit angezeigtem Grund. Die Auszahlung schließt die Ausschreibung einmalig ab.
- **Beleg für den Bürger** `/buerger/belohnung/{Belegnummer}/druck` - jeder belohnte Hinweis erhält eine eigene Belegnummer (Präfix `BEL`) samt automatischer Nachricht im Hinweis-Chat; Betrag, Belegnummer und Datum stehen im Bürgerbereich, der Beleg ist druckbar. Ihn sieht nur der Empfänger selbst oder die Führung - jede andere Abfrage bekommt „gibt es nicht", damit die Route keine Auszahlungen verrät.
- **Eigene Modul-Schalter** - „Kopfgeld" blendet die Summen im gesamten Fahndungs-Snapshot aus, „Belohnung" die Bürgersicht auf Auszahlungen und Belege. Die Auszahlung selbst hängt bewusst an keinem öffentlichen Schalter, damit der Not-Aus keine Kassenbewegung blockiert.

**Bürgerhinweise**
- **Hinweis-Formular** `/hinweis` - 30 bis 5.000 Zeichen, optional ein Bild und ein Bezug auf eine Ausschreibung über deren Aktenzeichen; das Aktenzeichen `NOOSE-H-<Jahr>-<Nummer>` fällt sofort, die Eingangsbestätigung entsteht in derselben Transaktion. Voraussetzung ist ein Bürgerkonto mit hinterlegtem Namen; Tageskontingent 5/8/12/20 Einreichungen je 24 h nach Vertrauensstufe.
- **Posteingang** `/hinweise` - interne Sammelseite mit Eingang, In Arbeit und Erledigt: Priorität, Aktenzeichen, Hinweisgeber samt Vertrauensstufe, Fahndungsbezug, Status, Bearbeiter und Eingang, sortiert nach Priorität und Alter; dazu Suche und die Schalter „Nur meine", „Dubletten zusammenfassen" und „Nur Ergreifungen". Ungelesene Meldungen erscheinen als Zähler am Navigationseintrag, jede Änderung spiegelt ein Broadcaster live.
- **Übernahme in Akten** `/hinweise/{Id}` - „Übernehmen" schreibt den Hinweis auf den eigenen Namen; der Status folgt einer festen Übergangstabelle (Neu, In Prüfung, Rückfrage, Bestätigt, Verworfen, Führte zur Ergreifung - letzterer eine Einbahnstraße). Aus dem Hinweis entsteht per Knopf eine Personenakte, ein Vorgang, eine Observation oder die Verknüpfung mit einer vorhandenen Akte; jede Übernahme legt eine Verknüpfung „Übernahme aus Bürgerhinweis <Aktenzeichen>" an, damit die Herkunft am Zeitstrahl sichtbar bleibt.
- **Priorität und Vertrauensstufe** - die Reihenfolge ist das Produkt dreier Bänder: ausgelobtes Kopfgeld (1–5), veröffentlichte Gefahrenstufe (1–5) und Vertrauensstufe des Hinweisgebers (Neu/Bekannt/Verlässlich/Vertraut, abgeleitet aus der Zahl bestätigter Hinweise). Der Wert wird neu gestempelt, sobald sich einer der drei ändert; dieselbe Vertrauensstufe steuert das Tageskontingent. Die Führung kann die Priorität von Hand festsetzen und wieder auf automatisch zurückstellen.
- **Anonymitätszusage** - wer anonym meldet, erscheint im Posteingang und im Detail als „anonym"; derselbe Hinweis fehlt im Hinweisgeber-Panel der Personenakte, und das einreichende Konto bleibt auf Zeitstrahl und Chronik ausgeblendet - sichtbar allein im Änderungsprotokoll als Missbrauchskontrolle. Auflösen darf nur die Führung mit Pflichtbegründung und eigenem Audit-Eintrag; ohne diesen Schritt lehnt die Belohnung die Auszahlung ab.
- **Rückfrage-Chat und interne Notizen** - jeder Hinweis trägt zwei getrennte Stränge: den Schriftwechsel mit dem Hinweisgeber (die Behörde antwortet immer als „NOOSE", nie mit Codename, wahlweise aus einer Bürger-Vorlage) und rein interne Notizen, die der Bürger nie sieht. Eine Rückfrage hebt den Status auf „Rückfrage" und benachrichtigt den Bürger; ein abgeschlossener Hinweis nimmt von keiner Seite mehr eine Nachricht an.
- **Meine Hinweise** `/buerger/hinweise` - eigene Hinweise mit Aktenzeichen, Auszug, Anhang und Fahndungsbezug, dazu ein entschärfter Statuswortlaut („Verworfen" → „Abgeschlossen", „Neu" → „Eingegangen") und ein Ungelesen-Zähler für Antworten der Behörde.
- **Dubletten-Erkennung** - ein neuer Hinweis wird gegen die jüngsten Meldungen derselben Art und desselben Fahndungsbezugs aus 30 Tagen verglichen (Wortähnlichkeit, Schwelle 0,6) und bei Treffer einer Gruppe zugeordnet; das Detail listet die Geschwister-Meldungen, die Liste klappt sie auf eine Zeile zusammen.

**Anliegen & Tickets**
- **Bürger-Tickets** `/buerger/tickets` - jedes angemeldete Konto mit Zivil-Identität (Bürger wie Partnerbehörde, nicht die Nur-Lese-Aufsicht) eröffnet ein Anliegen an die Führungsebene und verfolgt es unter `/buerger/tickets/{Aktenzeichen}`. Zwei Kontingente: höchstens 2 laufende Tickets und 3 neue je 24 h. Der Bürger sieht eigene Statusnamen (Eingegangen, In Bearbeitung, Antwort erwartet, Abgeschlossen). Modul-Aus stoppt nur neue Tickets, laufende bleiben lesbar und beantwortbar.
- **Ticket-Schalter** `/tickets` - Eingang für Bürger- und interne Tickets mit vier Statusregistern, Suche über Aktenzeichen, Betreff und Bürgername, Schalter „Nur meine" und Ungelesen-Zähler je Zeile. Übernehmen setzt Bearbeiter und Status; Statuswechsel laufen nur entlang der erlaubten Übergänge. Die Register sind Führung/Admin vorbehalten, die Nur-Lese-Aufsicht liest mit, antwortet aber nicht.
- **Zwei Stränge je Ticket** `/tickets/{Id}` - interne Notizen, die den Bürger nie erreichen, und der Schriftwechsel mit dem Bürger. Nach außen tragen Behördenzeilen strukturell keinen Agenten und erscheinen konstant als „NOOSE – Führungsebene"; eine Antwort setzt das Ticket automatisch auf „Wartet auf Bürger", eine interne Notiz verschiebt die Aktivität nicht. Beide Stränge sind reiner Text - kein Rich-Text, keine Erwähnungen.
- **Interne Tickets** - jeder interne Agent eröffnet ein eigenes Anliegen an die Führung und kann dabei gleich Agenten beteiligen. Ein internes Ticket hat keinen Bürger, keinen Bürger-Strang, keinen Modul-Schalter und kein Kontingent; Statuslauf, Aktenzeichen (Präfix `T`) und Schalter sind dieselben.
- **Beteiligte Agenten** `/tickets?tab=beteiligt` - nachträglich hängt die Führung einzelne Agenten an ein Ticket. Eine Beteiligung öffnet genau dieses eine Ticket samt internem Strang, ohne den restlichen Schalter sichtbar zu machen; das Entfernen löscht die Zeile hart, weil sie selbst die Berechtigung ist. Jeder Beteiligte trägt einen eigenen Lesestand.
- **Benachrichtigung** - ein neu eröffnetes Ticket klingelt bei der Führung und geht als Discord-Webhook heraus, generisch ohne Betreff und ohne Bürgernamen, mit Rollen-Ping. Bürgerantworten gehen an den Bearbeiter, interne Notizen nur an Beteiligte, Statuswechsel an den Bürger. Von den Kategorien des öffentlichen Bereichs erreichen bewusst nur sechs einen Discord-Kanal - ein eingehender Bürgerhinweis darunter, aber nur die Einreichung selbst und ausschließlich als generische Meldung mit Rollen-Ping; Belohnungen und Einsprüche nie, weil ein Kanalbeitrag den Einreichenden preisgäbe.

**Redaktion & Publikationen**
- **Informationsseiten** `/info`, `/info/{slug}` - frei anlegbare Seiten mit Menütitel, Icon und Reihenfolge; der Slug wird aus dem Titel gefaltet (ä/ö/ü/ß → ae/oe/ue/ss) und geprüft, bevor er in einer Route landet. Entwurf und veröffentlichte Fassung liegen in getrennten Spalten, paralleles Bearbeiten wird über einen Stand-Vergleich abgewiesen; Entwurfsvorschau über `?vorschau=1`. Vier Startseiten (Auftrag, Befugnisse, Zuständigkeiten, FAQ) werden als Entwurf geseedet.
- **Häufige Fragen** `/faq` - eigene Seite mit eigenem Modul-Schalter, kein Unterpunkt von `/info`. Rubriken gruppieren Fragen, jede Frage klappt einzeln auf; aufgeklappt wird serverseitig über `<details>`, weil die Seite ohne Circuit rendert. Überschrift, Einleitung und der Veröffentlicht-Schalter kommen weiterhin aus der redaktionellen Seite mit dem Slug `faq`, die dafür aus dem `/info`-Menü herausgehalten wird. Jede Frage trägt einen einmal geprägten Anker und ist über `/faq?frage=<anker>#<anker>` direkt verlinkbar; die alte Adresse `/info/faq` leitet samt Frage-Query dorthin um. Rubrik und Frage haben je einen Sichtbar-Schalter, es gibt keinen zweiten Veröffentlichen-Schritt und keinen Papierkorb.
- **Pressemitteilungen** `/presse`, `/presse/{Aktenzeichen}` - das öffentliche Aktenzeichen (`NOOSE-PM-…`) entsteht transaktional bei der ersten Veröffentlichung, ein Entwurf hat damit keine öffentliche Adresse. Titel, Teaser und Text werden beim Veröffentlichen in eine zweite Spaltengruppe kopiert, das Datum nur einmal gestempelt, damit eine Tippfehler-Korrektur eine alte Mitteilung nicht nach oben zieht. Eine Veröffentlichung meldet sich höchstens einmal im Discord-Kanal.
- **Automatischer Presse-Entwurf nach Ergreifung** - wird eine öffentliche Fahndung abgeschlossen, entsteht ein Pressemitteilungs-Entwurf mit Titel, Teaser und Fließtext, getrennt nach Personen- und Sachfahndung. Der Text wird ausschließlich aus der öffentlichen Fahndungskarte gebaut und kann deshalb weder Personen-Id noch internes Aktenzeichen, Codename oder Score enthalten; veröffentlicht wird nie automatisch.
- **Amtliche Warnungen** `/warnungen` - Karten mit vollem Text, Veröffentlichungsdatum und Gültigkeits-Chip. Ein „Gültig bis" wirkt ohne erneutes Veröffentlichen: abgelaufene Warnungen fallen von selbst aus dem Lesepfad. Anders als bei der Presse gibt es bewusst keine Discord-Meldung, weil ein Kanalbeitrag nach Ablauf weiter Gefahr behaupten würde.
- **Organisationsprofile** `/organisationen` - anonym lesbare Kartenliste beobachteter und verbotener Organisationen mit Anzeigename, Einordnung, Kurzbeschreibung und der beim Veröffentlichen festgehaltenen Gefahrenstufe. Jede Karte ist ein Publikations-Snapshot der Fraktionsakte, kein Live-Blick: Mitglieder, Aktenzeichen, Aktenbezug und der rohe Bedrohungs-Score bleiben drinnen. Angelegt und redigiert wird in der Fraktionsakte, freigegeben ab Senior Special Agent.
- **Zwangs-Depublizierung** - wird die Fraktionsakte als Verschlusssache eingestuft oder gelöscht, zieht der Dienst das Profil automatisch mit Begründung offline. Zusätzlich filtert der öffentliche Lesepfad in einer **zweiten Abfrage** jede gelöschte oder VS-markierte Akte heraus, sodass ein übersehener Snapshot nicht anonym erreichbar bleibt.
- **Öffentliches Führungs-Organigramm** `/fuehrung` - die einzige Stelle, an der die Behörde nach außen **Klarnamen** nennt: freigegebener Name, Dienstgradbezeichnung, Funktion und Foto der Leitung. Alle Angaben sind redaktionelle Kopien, keine Projektion aus dem Agenten-Datensatz - Beförderung, Umbenennung oder ein neues Profilbild ändern einen freigegebenen Eintrag nicht von selbst. Wählbar sind nur aktive Agenten ab Supervisory Special Agent; das Einschalten des Moduls veröffentlicht für sich genommen nichts.
- **Öffentliche Gesetzesauszüge** `/recht` - nach Gesetzbuch gruppiert mit Paragraf, Titel, Text und Strafmaß, als reiner Text ausgegeben. Freigegeben wird pro Paragraf, standardmäßig bleibt jeder intern; der Inhalt bleibt der unter `/gesetze` gepflegte Stand, eine Korrektur dort schlägt sofort nach außen durch.
- **Bürger-Vorlagen** - Textbausteine in fünf Arten (Ticket-Eingangsbestätigung und -Antwort, Hinweis-Eingangsbestätigung, -Rückfrage und -Ablehnung), je Art sortierbar und einzeln aktivierbar. Die beiden Eingangsbestätigungen gehen automatisch mit der ersten aktiven Vorlage ihrer Art hinaus - ohne aktive Vorlage wird keine Bestätigung geschrieben, einen Fallback-Text gibt es nicht. Platzhalter `BUERGER`, `AKTENZEICHEN`, `DATUM`, `UHRZEIT` werden erst beim Anwenden ersetzt, `NAME` wird immer zu `███████` geschwärzt.
- **Gemeinsamer Redaktions-Workflow** - Seiten, Presse, Warnungen und Lageberichte teilen denselben Ablauf: Rich-Text-Editor im Einstellungs-Panel, HTML-Bereinigung beim Speichern und erneut beim Veröffentlichen, Abweisen leerer Entwürfe, Zurückziehen behält den zuletzt veröffentlichten Stand, Wiederherstellung aus dem Papierkorb kommt immer als Entwurf zurück. Die anonymen Lesepfade laufen über einen 10-Sekunden-Cache, der bei jedem Schreibvorgang verworfen wird.

**Lage, Zahlen & öffentliche Suche**
- **Öffentliche Lageberichte** `/berichte`, `/berichte/2026-08` - von der Führung freigegebene Monatstexte; Anker ist der archivierte interne Monatsbericht, dessen Zahlen aber nie übernommen werden - nach außen geht allein der geschriebene Text. Die Übersicht zeigt die 24 jüngsten Berichte und benennt diesen Deckel.
- **Gefahrenlage-Ampel** `/lage` - landesweite Stufe (Niedrig/Erhöht/Hoch/Kritisch) mit kurzer Einschätzung, „seit"-Datum und der Vorgängerstufe als Trend, zusätzlich als Kachel auf der Startseite. Eine **redaktionelle Setzung** der Führung, bewusst nicht aus dem Bedrohungs-Score gerechnet; es gibt keinen Entwurf - Speichern heißt Veröffentlichen. Nur ein Stufenwechsel setzt Datum und Vorgängerstufe neu, eine Textkorrektur nicht.
- **Öffentliche Zahlen** - Zahlenband auf der Startseite (bewusst keine eigene Seite): laufende Fahndungen, abgeschlossene Ausschreibungen, eingegangene Hinweise mit bestätigten und zur Festnahme führenden sowie ausgezahlte Belohnungen. Jede Zahl hängt am Modul ihrer Quelle und entfällt lieber ganz, statt eine 0 zu behaupten; Namen, Aktenzeichen und Einzelwerte trägt die Auswertung strukturell nicht. Hinweis- und Auszahlungszahlen laufen auf einem festen Basiswert für die Behördenhistorie vor dem Livegang, der ausschließlich im öffentlichen Dienst addiert wird.
- **Gefahrenlisten** `/gefahr/personen`, `/gefahr/fraktionen` - je die 25 am höchsten eingestuften Einträge, Gefahrenstufe absteigend, jüngste Veröffentlichung als Gleichstandsregel; außen steht nur die Stufe, nie der 0–100-Wert. Beide lesen den bereits gefilterten öffentlichen Bestand statt einer eigenen Abfrage und stehen hinter zwei Modul-Schaltern.
- **Öffentliche Suche** `/suche-oeffentlich` - Volltextsuche über ausschließlich Veröffentlichtes, gruppiert in sieben Bereichen (Fahndung, Organisationen, Presse, Warnungen, Lageberichte, Information, Recht). Anonym, statisch gerendert als reines GET-Formular. Ein Treffer trägt Titel, öffentliches Kennzeichen und einen Klartext-Auszug - strukturell kein internes Aktenzeichen, kein Codename, keine Gefahrenstufe, kein Betrag, kein Relevanz-Score.
- **Absicherung der Suche** - mindestens 3 Zeichen, gekürzt auf 100; gewichtslose Zeichen fallen vor der Längenprüfung weg, damit eine Anfrage aus Zero-Width-Zeichen nicht auf jede veröffentlichte Zeile passt. Höchstens 10 Treffer je Bereich, der Deckel wird genannt statt still zu schneiden; 20 Anfragen je Minute und IP, Ergebnisseiten tragen `noindex, nofollow`. Der Dienst hat keine eigene DB-Anbindung, sondern liest die zwischengespeicherten Snapshots der Fach-Dienste und erbt so Unterdrückungsgürtel, Modul-Schalter und Not-Aus.
- **Kennzahlen des öffentlichen Bereichs (intern)** `/einstellungen?tab=oeffentliche-kennzahlen` - Führungs-Panel über den Ertrag nach außen, Fenster 7/30/90/365 Tage: Hinweis-Durchsatz mit Ergreifungsquote über die entschiedenen Hinweise, Auszahlungssummen und Kosten je bezahlter Ergreifung, Reaktionszeit der Bürger-Tickets als Median und p95 sowie das älteste unbeantwortete Ticket. Dazu die Aufrufzahlen der veröffentlichten Ausschreibungen mit Top-5-Liste, gegen die VS-Sichtbarkeit der Personenakte gefiltert. Bewusst ohne Modul-Schranke - ein abgeschaltetes Modul soll die Historie nicht verstecken, die über das Wiedereinschalten entscheidet.

**Einspruch**
- **Einspruch einlegen** `/buerger/einspruch` - nur die ausgeschriebene Person selbst über ihr Bürgerkonto: verglichen wird der Kontoname mit dem veröffentlichten Anzeigenamen (Groß-/Kleinschreibung, Umlaute und doppelte Leerzeichen toleriert, ausdrücklich **keine** Tippfehlertoleranz), Aliase und interne Klarnamen bleiben außen vor. Begründung 30–4.000 Zeichen, drei Einsprüche je Konto und rollende 24 h, höchstens einer offen je Ausschreibung, Präfix `EIN`. Einstieg ist der öffentliche Steckbrief, wo die Schaltfläche für Unbeteiligte sichtbar, aber gesperrt bleibt.
- **Einspruchs-Schreibtisch** `/fahndung` - Umschalter Offen/Entschieden mit Zählern und ausklappbarer Detailansicht. Lesen ab Senior Special Agent plus Nur-Lese-Aufsicht, entscheiden nur die Führung mit Schreibrecht. Die Statusübergänge sind fest verdrahtet, jede Entscheidung verlangt eine Begründung, die der Bürger liest, und Stattgeben setzt voraus, dass die Ausschreibung bereits nicht mehr öffentlich ist. Aus einem Einspruch lässt sich ein Vorgang anlegen.
- **Keine Doppelentscheidung** - Entscheidung und Vorgangsanlage laufen als Compare-and-swap auf dem gelesenen Stand: wer zu spät kommt, bekommt „wurde soeben entschieden" statt einer zweiten, widersprüchlichen Nachricht an den Bürger, und ein dabei überzählig entstandener Vorgang wird verworfen. Weil dieser Schreibweg am Audit-Interceptor vorbeigeht, schreibt der Dienst seine Audit-Zeilen von Hand.

---

## Tech-Stack

| Bereich | Technologie | Version |
|---------|-------------|---------|
| Runtime | .NET | `net10.0` |
| UI | Blazor Web App (nur **Interactive Server**, SignalR) | - |
| Komponenten | MudBlazor (**nur Dark-Mode**, „Anthrazit + Cyan") | 9.5.0 |
| ORM | Pomelo.EntityFrameworkCore.MySql (zieht EF Core 9 transitiv) | 9.0.0 |
| Identity | Microsoft.AspNetCore.Identity.EntityFrameworkCore | 9.0.16 |
| EF Design | Microsoft.EntityFrameworkCore.Design | 9.0.16 |
| OAuth | AspNet.Security.OAuth.Discord | 10.0.0 |
| HTML-Sanitizing | HtmlSanitizer | 9.0.892 |
| Markdown | Markdig | 1.3.2 |
| Health-Checks | Microsoft.Extensions.Diagnostics.HealthChecks.EntityFrameworkCore | 9.0.16 |
| EF-Tool | dotnet-ef (lokal gepinnt) | 9.0.17 |

**Self-hosted Frontend-Libs** (unter `wwwroot/lib`, lazy via JS-Interop): Quill 1.3.7, vis-network 9.1.9, FullCalendar 6.1.15, ECharts.
Lazy geladene JS-Module unter `wwwroot/js`: `graph.js`, `kalender.js`, `richtext.js`, `statistik-charts.js`, `pruefung.js` (Test-Countdown) - dazu `app.js` als einziges global geladenes Modul.

**DB:** lokal MariaDB/XAMPP, Produktion MariaDB 10.11 (Container) - Engine via `ServerVersion.AutoDetect()`.

> ⚠️ **EF/Identity bewusst auf der 9.0.x-Linie.** Pomelo 9.0.0 unterstützt nur EF Core 9; ein Upgrade auf 10.0.x würde EF Core 10 ziehen und mit Pomelo kollidieren. Die 9.0.x-Pakete laufen sauber auf der .NET-10-Runtime.

---

## Architektur

Schichten innerhalb von `NOOSE-Website/`:

| Ordner | Inhalt |
|--------|--------|
| `Components/` | Razor-Pages + UI (dünn), je Feature ein Ordner |
| `Data/` | `AppDbContext`, `Entities/<Domain>/`, `Migrations/` |
| `Models/` | DTOs, `Enums/`, `Abstractions/` (Marker-Interfaces) |
| `Services/` | Business-Logik + Authorization-Durchsetzung; `CounterIntel/`, `Gamification/`, `Graph/`, `Llm/`, `Public/`, `Search/`, `Statistics/`, `Threat/` |
| `Authorization/` | Policies, Requirements, Handler, `ClaimsPrincipal`-Extensions |
| `Infrastructure/` | Interceptors, Broadcaster, Background-Worker, Audit, File-Storage, CurrentUser |

**Tragende Muster**
- **Render-Mode:** alles `InteractiveServer`, außer `[ExcludeFromInteractiveRouting]` (statisch) - pro Seite via `App.razor`.
- **DbContext-Factory:** immer `IDbContextFactory<AppDbContext>` injizieren und pro Operation einen kurzlebigen Context erzeugen (entgeht der Blazor-Circuit-Lebensdauer).
- **4 SaveChanges-Interceptors, Reihenfolge zählt:** `ReadOnlyBarrierInterceptor` (zuerst, vetoed Schreibzugriffe von OnlyReadern) → `AuditSaveChangesInterceptor` (stempelt `IAuditable`, wandelt Hard- in Soft-Delete) → `WatchlistChangeInterceptor` (Fan-out an Follower) → `SearchIndexInterceptor` (baut den Such-Nebenindex aus dem Endzustand neu auf, deshalb zuletzt).
- **Soft-Delete & Audit via Marker-Interfaces** (`ISoftDelete`, `IAuditable`): globaler Query-Filter `!IsDeleted` per Reflection; neue Entität → Interface implementieren.
- **Singleton-Broadcaster für Live-Updates:** scoped Service schreibt die Row, ruft dann den Singleton für Push an verbundene Circuits - `NotificationBroadcaster`, `TaskforceChatBroadcaster`, `SharesBroadcaster`, `DocumentAccessBroadcaster`, `AcknowledgmentBroadcaster`, `FinancingBroadcaster`, `BewerbungBroadcaster`, `TipsBroadcaster`, `TicketBroadcaster` sowie der `WatchlistDispatcher`.
- **Zehn Background-Worker** (`AddHostedService`): `FollowupDueWorker`, `JobDueSoonWorker`, `MeetingReminderWorker`, `ThreatScoreSweepWorker`, `GamificationSweepWorker`, `TopAgentAwardWorker`, `SearchIndexBackfillWorker`, `SituationReportWorker`, `BewerbungTestExpiryWorker` (1-Min-Takt, gibt abgelaufene Eignungstests ab), `PublicWantedExpiryWorker` (15-Min-Takt, setzt abgelaufene öffentliche Ausschreibungen auf „Abgelaufen").
- **Middleware-Reihenfolge ist tragend:** `PublicIndexingMiddleware` setzt außerhalb der öffentlichen Routen `noindex` und sitzt bewusst **vor** dem ExceptionHandler, damit re-executete Fehlerseiten den Header behalten; der `RateLimiter` läuft **nach** Antiforgery, damit ein tokenloser POST kein Kontingent verbraucht.
- **Idempotente Start-Seeder** nach der Auto-Migration, u. a. `PublicModuleSeeder`, `PublicPageSeeder`, `WarnhinweisSeeder`, `PublicTemplateSeeder` - sie legen fehlende Zeilen an und überschreiben eine gespeicherte Wahl nie.
- **Authorization im Service-Layer:** Write-Methoden nehmen `ClaimsPrincipal actor` und rufen `Permission.Require*`-Guards als erste Anweisung; Sichtbarkeit zentral in `Visibility` bzw. `Public/PublicVisibility`. Berechtigungslogik existiert nur in `Authorization/AgentPrincipalExtensions.cs` und `Services/Permission.cs`.

---

## Datenmodell & Domäne

- **Ein** `AppDbContext : IdentityDbContext<Agent>` mit **138 DbSets**; alle Fluent-Configs in `OnModelCreating`.
- **DB-Spalten Deutsch, C#-Member Englisch** - z. B. `Person.CaseNumber` → Spalte `Aktenzeichen`, Tabelle `Personen`, `IsDeleted` → `IstGeloescht`.
- **Aktenzeichen** menschenlesbar, z. B. `NOOSE-P-2026-0001`, race-safe über `CaseNumberCounter`. Eigene Präfixe je Bereich: `FA` (öffentliche Ausschreibung), `H` (Bürgerhinweis), `T` (Ticket), `EIN` (Einspruch), `BEL` (Belohnungsbeleg), `PM` (Pressemitteilung), `KAS` (Kassenbuchung), `ASS` (Asservat), `ENT` (Entführung).
- **Polymorphe Assoziationen** (Quellen, Kommentare, Tags, Links, Followups) über `(EntityType, EntityId)` - kein echter FK.
- **Eigene Entity-Domäne `Data/Entities/Public/`** mit 20 Entitäten für den öffentlichen Bereich (Ausschreibung, Kopfgeld-Anteil, Einspruch, Warnhinweis, Hinweis + Nachricht + Belohnung, Ticket + Beteiligter + Nachricht, Pressemitteilung, Warnung, Lagebericht, Fraktions- und Führungsprofil, Seite, Modul, Vorlage, Bürgerprofil). Was von einer Tabelle nach außen geht - oder warum nie -, steht ausnahmslos in `Services/Public/PublicVisibility.cs` und wird durch einen Deckungstest über alle DbSets erzwungen.

**Kern-Akten-Typen**

| Aktentyp | Tabelle | Einstufung | Bedrohungs-Score |
|----------|---------|------------|------------------|
| Person | `Personen` | ✓ | 0–100 |
| Fraktion | `Fraktionen` | ✓ | 0–100 (null = nicht bewertet) |
| Partei | `Parteien` | ✓ | - |
| Personengruppe | `Personengruppen` | ✓ | - |
| Operation | `Operationen` | ✓ | - |
| Taskforce | `Taskforces` | - | - |

**Zwei Einstufungs-Achsen**
- **`Classification`** (Status einer Akte): `Unknown(0)` → `ReviewCase`/Prüffall `(1)` → `SuspicionCase`/Verdachtsfall `(2)` → `SecuredStateThreatening`/Gesichert staatsgefährdend `(3)`. Höchste Stufe nur durch SeniorSpecialAgent oder Admin, sonst per Antrag.
- **`DocumentClassification`** (VS-Stufe eines Dokuments): `None(0)` (alle) → `Leadership(1)` → `Tru(2)` → `Hrb(3)`. Server-seitig durchgesetzt.

**Bedrohungs-Score**
- Wertebereich 0–100, gültig für Person & Fraktion (Operationen haben keinen Score).
- Basis-Anker nach Einstufung: `SecuredStateThreatening` 75, `SuspicionCase` 50, `ReviewCase` 12, `Unknown` 0.
- Optionaler Konfidenzwert (0–100) bildet Datenlücken ab; Begründung in strukturiertem `BedrohungsDetailJson`.
- `null` = nicht bewertet/exempt (z. B. Staats-Fraktionen). Täglicher Recompute durch `ThreatScoreSweepWorker`.

---

## Rollen & Rechte

Drei orthogonale Achsen: **Rang**, **Boolean-Flags**, **Policies**.

**Ränge** (`Rank`-Enum, int-backed)

| Wert | Rang | Hinweis |
|------|------|---------|
| 1 | JuniorAgent | |
| 2 | SpecialAgent | |
| 3 | SeniorSpecialAgent | darf höchste Einstufung setzen |
| 4 | SupervisorySpecialAgent | **ab hier Führung (Leadership)** |
| 5 | DeputyDirector | entscheidet über Beförderungen |
| 6 | Director | |

Führung = `rank >= SupervisorySpecialAgent` **oder** `IsAdmin`.

**Boolean-Flags auf `Agent`** (Spalten `Ist*`)

| Flag | Bedeutung |
|------|-----------|
| `IsAdmin` | Vollzugriff; setzt Leadership, short-circuited jedes Rang-Requirement |
| `IsTRU` | Tactical Response Unit; Zugriff auf TRU-VS |
| `IsHRB` | Human Resources Branch; Zugriff auf HRB-VS + Recruiting-Verwaltung |
| `IsTeamLead` | reiner Sichtbarkeitsmarker; allein kein Zugriff |

**OnlyReader** = `IsTeamLead && !IsAdmin` (abgeleitet, kein Flag): liest alles inkl. VS, schreibt **nichts** (hart vetoed vom `ReadOnlyBarrierInterceptor`), sieht **nie** Klarnamen, kann alle Taskforces einsehen.

**Konto-Status** (`AgentStatus`) - nicht jedes Konto ist ein Agent:

| Wert | Status | Bedeutung |
|------|--------|-----------|
| 0 | Pending | angemeldet, wartet auf Freigabe |
| 1 | Active | freigegebener interner Agent (bzw. Partnerbehörde über `PartnerAgency`) |
| 2 | Blocked | ausgesperrt; laufende Sitzung endet in ≤30 s |
| 3 | Applicant | Bewerber - Zugang nur zum Bewerber-Portal `/portal` |
| 4 | Terminated | gekündigt |
| 5 | Civilian | **Bürger** - Discord-authentifiziert, ohne jedes Behördenrecht, Zugriff nur auf den öffentlichen Bereich und `/buerger` |

Insgesamt **19 Policies** (`Authorization/Policies.cs`), alle in `AuthorizationRegistration` registriert - darunter `CitizenPortal` als Zugangs-Gate des Bürgerbereichs, `ApplicantPortal`, `InternalAgent`, `DocumentAuthor` und `AiOwner` (eigene Rechteachse neben Admin, entscheidet allein über NOOSEI-Kontingente und echte Kostenanzeige).

---

## Schnellstart

**Voraussetzungen**
- .NET 10 SDK
- MariaDB (XAMPP) oder MySQL 8.0 lokal

**Secrets (lokal, User-Secrets)**

`appsettings.json` enthält nur leere Platzhalter. Echte Werte via User-Secrets (UserSecretsId `d41f8a93-2c7b-4e16-9a55-0b3e7c1f6d28`):

```powershell
# Connection-String ist ein Beispiel/Template - Server, DB und Credentials anpassen
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=localhost;Port=3306;Database=noose;User ID=root;Password=;SslMode=None;"
dotnet user-secrets set "Authentication:Discord:ClientId" "YOUR_DISCORD_CLIENT_ID"
dotnet user-secrets set "Authentication:Discord:ClientSecret" "YOUR_DISCORD_SECRET"
dotnet user-secrets set "Bootstrap:AdminDiscordId" "YOUR_DISCORD_ID"
```

`DatabaseConnectionResolver` probt zuerst `ProductionConnection` (5 s Reachability), fällt sonst auf `DefaultConnection` zurück → derselbe Build läuft lokal und auf dem Server.

**Build & Run** (alle Befehle aus dem Repo-Root, außer `dotnet ef` — siehe „Datenbank & Migrationen")

```powershell
# Build
dotnet build NOOSE-Website.slnx

# Run  → http://localhost:5174
dotnet run --project NOOSE-Website/NOOSE-Website.csproj

# Run mit HTTPS-Profil  → https://localhost:7063
dotnet run --project NOOSE-Website/NOOSE-Website.csproj --launch-profile https

# Hot Reload
dotnet watch --project NOOSE-Website/NOOSE-Website.csproj run
```

---

## Datenbank & Migrationen

`dotnet-ef` ist ein **lokales** Tool (gepinnt auf 9.0.17). Das Manifest liegt in `scripts/dotnet-tools.json` — die Befehle laufen deshalb aus `scripts/`; aus dem Repo-Root melden sie `dotnet-ef nicht vorhanden`. `--startup-project` muss mit angegeben werden, weil der Startup-Default das aktuelle Verzeichnis ist:

```powershell
cd scripts
# MUSS vor jedem dotnet-ef-Aufruf laufen
dotnet tool restore

# Dev-Server vorher stoppen (bin-Lock), dann:
dotnet ef migrations add PhaseNN_<Name> `
    --project ../NOOSE-Website/NOOSE-Website.csproj `
    --startup-project ../NOOSE-Website/NOOSE-Website.csproj
```

Der Ordner enthält derzeit **132 Migrationen** in zwei parallelen Präfix-Familien: `PhaseNN_<Name>` für den internen
Bereich (bis `Phase76_TestBearbeitungszeit`) und `OeffentlichNN_<Name>` für den öffentlichen Bereich
(`Oeffentlich02_Module` … `Oeffentlich18_Ergreifungsmeldung`). Die Nummer folgt der Phase, nicht der Reihenfolge -
sortiert wird über den Zeitstempel im Dateinamen.

`dotnet ef database update` ist i. d. R. **unnötig** - Migrationen werden beim App-Start automatisch via `db.Database.MigrateAsync()` (Program.cs) angewendet. Die Design-Time-Factory zwingt EF-Tools immer auf die lokale `DefaultConnection` → Migrationen können nie Produktion treffen.

---

## Deployment

Deploy per GitHub Actions: **Actions → Deploy → Run workflow** (`.github/workflows/deploy.yml`, Kopie des Plattform-Templates).

- `environment`: `production` (Prod, noose.info, Default) oder `demo` (demo.noose.info)
- Commit leer = aktueller `master`, sonst Commit-SHA
- Häkchen „rollback“ = zurück auf das vorherige Release

Das Image `ghcr.io/nullradix-dev/noose-website:<commit-sha>` baut vorher der Workflow „Container-Image“ (`.github/workflows/image.yml`, Push auf `master`); er muss für den Commit fertig sein. Prod (`deploy/compose.yml`) und Demo (`deploy/demo/compose.yml`) sind zwei getrennte Apps auf der Plattform, je mit eigenem Linux-User, eigenem rootless Docker, eigener MariaDB 10.11 (Zugriff über den Compose-Dienst `db`) und eigenem Speicherlimit. Der Server prüft vor dem Umschalten alles, behält bei einem Startfehler das vorherige Release und dumpt vor jedem Wechsel alle Datenbanken des laufenden Releases (ein Rollback macht EF-Migrationen nicht rückgängig). Server-Details (Pfade, Ports, Benutzer, Backups) stehen in der privaten Betriebsdoku.

**Prod-Gotchas**
- **`App_Data` beim Deploy nie löschen** - enthält Uploads **und** Data-Protection-Keys (`App_Data/keys`); Verlust loggt alle User bei jedem Restart aus. `App_Data` ist nicht im Image, sondern ein Volume auf dem Server und überlebt jeden Deploy.
- **`TZ=Europe/Berlin`** in der Server-Env nötig - sonst sind alle `ToLocalTime()`-Zeiten verschoben. `TimeZoneInfo.Local` ist prozess-gecached → Container neu erstellen (Deploy-Workflow; `restart` liest die Env-Datei nicht neu).
- **Discord-Redirect** `https://noose.info/signin-discord` muss im Developer-Portal registriert sein.
- **Prod-Secrets** liegen nur auf dem Server, nie im Repo (Doppel-Unterstrich): `ConnectionStrings__ProductionConnection`, `Authentication__Discord__ClientId`/`__ClientSecret`, `Bootstrap__AdminDiscordId`, `Llm__ApiKey` (OpenRouter) und `Llm__DeepSeek__ApiKey` (DeepSeek direkt). Prod erzwingt `Demo__AutoSetup=false` in `deploy/compose.yml`.
- **Health-Check:** `GET /health` (anonym, prüft DB-Konnektivität) → `200 Healthy`.
- **Logs/Status:** auf dem Server im rootless Docker der jeweiligen App (`docker logs`, `docker compose ps`); Details in der privaten Betriebsdoku.
- **Backup:** nächtliche DB-Backups laufen über 1Panel auf dem Server, vor jedem Deploy zusätzlich ein Dump aller Datenbanken. Uploads (`App_Data`) sind **nicht** im DB-Backup. Manuelle Dumps/Restores macht der Admin auf dem Server.

---

## Projektstruktur

```
NOOSE-Website.slnx
NOOSE-Website/
├── Components/        Razor-Pages + UI, je Feature ein Ordner (40)
│   ├── Pages/         Abductions, Absences, Account, Activities, Admin, Board,
│   │                  Calendar, Cases, Documents, Evidence, Factions, Feedback,
│   │                  Financing, Gamification, Graph, Groups, Informants, Jobs,
│   │                  Kasse, Ki, Laws, Leads, Legal, Meetings, Monitoring,
│   │                  Operations, OrgChart, Parties, People, Personnel, Portal,
│   │                  Public, Recruiting, Search, Statistics, Taskforces,
│   │                  Tickets, Tips, Wanted, Watchlist
│   ├── Layout/
│   ├── Common/Shared/
│   └── Account/
├── Data/              AppDbContext, Entities/<Domain>/ (inkl. Public/), Migrations/
├── Models/            DTOs, Enums/, Abstractions/, Public/
├── Services/          Business-Logik + Authorization (CounterIntel/, Gamification/,
│                      Graph/, Llm/, Public/, Search/, Statistics/, Threat/)
├── Authorization/     Policies, Requirements, Handler, Extensions
├── Infrastructure/    Interceptors, Broadcaster, Worker, Audit, Storage, Seeder
├── Theme/             NooseTheme.cs (Dark-Palette)
└── wwwroot/lib/       Quill, vis-network, FullCalendar, ECharts (self-hosted)
scripts/                 dotnet-tools.json
deploy/                  compose.yml (Prod), demo/compose.yml (Demo)
```

---

## Weiterführende Docs

- [`CLAUDE.md`](CLAUDE.md) - Codebase-Konventionen, Architektur, Gotchas
- [`AGENTS.md`](AGENTS.md) - Agent-/Contributor-Hinweise
- [`claude-memory/`](claude-memory/) - Detailwissen je Bereich: **warum** eine Regel existiert, nicht nur dass sie gilt
- [`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md) - Server-Setup (nginx → Container → MariaDB-Container), Docker Compose, Backup, Troubleshooting
- [`docs/CODE_REVIEW_TODO.md`](docs/CODE_REVIEW_TODO.md) - bekannte Tech-Debt-/Review-Findings
- [`docs/CachePlan.md`](docs/CachePlan.md) - Caching und Ladezeiten
- [`docs/DEPLOYMENT-DEMO.md`](docs/DEPLOYMENT-DEMO.md) - Demo-Instanz

---

## Lizenz

Privates Fan-/RP-Projekt. **Keine** Open-Source-Lizenz - kein freies Nutzungs-, Kopier- oder Verteilungsrecht. Alle Rechte vorbehalten.
