# NOOSE-Website

Die zentrale Akten- und Intelligence-Datenbank der NOOSE (National Office of Security Enforcement), einer fiktiven Sicherheitsbehörde auf einem FiveM-/GTA-RP-Server. Sie ersetzt verstreute Discord-Threads durch eine durchsuchbare Datenbank, in der alles in beide Richtungen verlinkt ist. Für jede Person und jede Fraktion gibt es genau eine Akte, in der Beobachtungen, Dokumente, Beziehungen, Einstufungen und Bedrohungsbewertungen zusammenlaufen.

Unter derselben Domain liegt ein öffentlicher Bereich mit Fahndungen, Presse und Warnungen, dazu ein Bürgerkonto, über das Bürger Hinweise geben und mit der Behörde schreiben. Was nach außen geht, entscheidet immer ein ausdrücklicher Schritt der Veröffentlichung. Ab Werk ist fast jedes öffentliche Modul aus, und ein Not-Aus nimmt den ganzen Außenauftritt vom Netz.

Live-Demo: **https://demo.noose.info**, dort sind nicht alle Funktionen verfügbar.

## Was NOOSE kann

| Bereich | Was es gibt |
|---|---|
| Akten | Personen, Fraktionen, Personengruppen und Parteien mit Steckbrief, Fotos, Verknüpfungen, Einstufung, Zeitstrahl und Änderungshistorie |
| Vorgänge und Einsätze | Vorgänge, Operationen, Observationen, Taskforces mit eigenem Chat, Aufgaben-Board, Kalender, Dienstbesprechungen |
| Personal | Personalakten, Beförderungen, Abmeldungen, Dienst-Aktivitäten, Recruiting mit Bewerber-Portal und Eignungstest, Bestenliste |
| Geld und Material | Kasse, Asservatenkammer, Finanzierungsanträge |
| Wissen | Dokumente mit VS-Stufen, Datei-Bibliothek, Gesetzbuch, Schwarzes Brett, Informanten |
| Suche und Analyse | Volltextsuche über alles Sichtbare, Beziehungsgraph mit Pfadsuche, Bedrohungs-Score, Dashboard, Statistik, Lageberichte |
| NOOSEI | KI-Assistent, der über Lesewerkzeuge in der Aktendatenbank nachsieht, Kurzbriefe je Akte, Schreibhilfe im Editor |
| Partnerbehörden | Lesezugriff für DoJ, LSPD und LSMD auf ausdrücklich freigegebene Akten |
| Öffentlicher Bereich | Fahndungsboard mit Steckbriefen und Kopfgeld, Presse, Warnungen, Gefahrenlage, Organisationsprofile, Gesetzesauszüge, öffentliche Suche |
| Bürgerkonto | Hinweise geben, eine Ergreifung melden, Belohnungen erhalten, Tickets an die Führung, Einspruch gegen eine Fahndung |
| Betrieb | Änderungsprotokoll, Papierkorb, Benachrichtigungen mit Discord-Webhooks, Wartungsmodus, Einstellungen |

Änderungen an den Akten werden protokolliert, gelöschte Akten landen im Papierkorb, und was jemand sieht, hängt an Dienstgrad und Rolle. Die Bezeichner im Code sind englisch, Fachbegriffe und Oberfläche deutsch. Wie das gebaut ist, steht in [`docs/ARCHITEKTUR.md`](docs/ARCHITEKTUR.md).

## Lokal starten

Du brauchst das .NET 10 SDK und eine lokale MariaDB oder MySQL 8.0, etwa aus XAMPP. Kopier zuerst `NOOSE-Website/appsettings.json.template` nach `NOOSE-Website/appsettings.json`. Die Datei ist gitignored und enthält nur leere Platzhalter, die echten Werte kommen in die User Secrets des Projekts. Alle Befehle laufen im Repo-Root:

```powershell
# Beispiel, Server, Datenbank und Zugang an die eigene Umgebung anpassen
dotnet user-secrets set --project NOOSE-Website "ConnectionStrings:DefaultConnection" "Server=localhost;Port=3306;Database=noose;User ID=root;Password=;SslMode=None;"
dotnet user-secrets set --project NOOSE-Website "Authentication:Discord:ClientId" "<Client ID deiner Discord-App>"
dotnet user-secrets set --project NOOSE-Website "Authentication:Discord:ClientSecret" "<Client Secret>"
dotnet user-secrets set --project NOOSE-Website "Bootstrap:AdminDiscordId" "<deine Discord-ID>"
```

Dann bauen und starten:

```powershell
dotnet build NOOSE-Website.slnx
dotnet run --project NOOSE-Website/NOOSE-Website.csproj                          # http://localhost:5174
dotnet run --project NOOSE-Website/NOOSE-Website.csproj --launch-profile https   # https://localhost:7063
dotnet watch --project NOOSE-Website/NOOSE-Website.csproj run                    # mit Hot Reload
```

`DatabaseConnectionResolver` versucht zuerst `ProductionConnection` und nimmt sonst `DefaultConnection`. So läuft derselbe Build lokal und auf dem Server. Die Tests stehen in [`NOOSE-Website.Tests`](NOOSE-Website.Tests/README.md).

## Datenbank und Migrationen

Das Schema ändert sich nur per EF-Migration, und die App wendet ausstehende Migrationen beim Start selbst an. `dotnet ef database update` brauchst du deshalb fast nie. Die Design-Time-Factory zwingt die EF-Tools immer auf die lokale `DefaultConnection`, eine Migration kann also nie die Produktion treffen.

`dotnet-ef` ist ein lokales Tool, auf 9.0.17 gepinnt, und sein Manifest liegt in `scripts/dotnet-tools.json`. Die Befehle laufen deshalb aus `scripts/`, aus dem Repo-Root meldet das Tool, es sei nicht vorhanden. `--startup-project` gehört dazu, weil das Startprojekt sonst das aktuelle Verzeichnis wäre. Stoppe vorher den laufenden Dev-Server, sonst sind die DLLs gesperrt:

```powershell
cd scripts
dotnet tool restore
dotnet ef migrations add PhaseNN_<Name> `
    --project ../NOOSE-Website/NOOSE-Website.csproj `
    --startup-project ../NOOSE-Website/NOOSE-Website.csproj
```

Migrationen für den internen Bereich heißen `PhaseNN_<Name>`, die für den öffentlichen Bereich `OeffentlichNN_<Name>`. Die Nummer ist der Ausbauschritt, in dem die Migration entstand, nicht ihre Reihenfolge. Sortiert wird über den Zeitstempel im Dateinamen.

## Deployment

Prod (`noose.info`) und Demo (`demo.noose.info`) rollst du über GitHub Actions aus, mit **Actions → Deploy → Run workflow** und `environment` = `production` oder `demo`. Vorher muss der Workflow „Container-Image“ für den Commit fertig sein. Wie der Deploy abläuft, welche Konfiguration die App braucht und was bei Fehlern hilft, steht in [`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md), die Demo in [`docs/DEPLOYMENT-DEMO.md`](docs/DEPLOYMENT-DEMO.md).

## Weiterführende Doku

- [`docs/ARCHITEKTUR.md`](docs/ARCHITEKTUR.md): Tech-Stack, Aufbau, tragende Muster, Datenmodell, Rollen und Rechte
- [`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md) und [`docs/DEPLOYMENT-DEMO.md`](docs/DEPLOYMENT-DEMO.md): Ausrollen, Konfiguration, Fehlersuche
- [`NOOSE-Website.Tests/README.md`](NOOSE-Website.Tests/README.md): Aufbau der Tests
- [`CLAUDE.md`](CLAUDE.md) und [`AGENTS.md`](AGENTS.md): Konventionen und Stolperfallen für die Arbeit mit KI-Agenten

## Lizenz

Privates Fan- und RP-Projekt ohne Open-Source-Lizenz. Es gibt kein Recht, den Code zu nutzen, zu kopieren oder weiterzugeben. Alle Rechte vorbehalten.
