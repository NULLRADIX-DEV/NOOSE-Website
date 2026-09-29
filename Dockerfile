# NOOSE-Website als Container-Image (gebaut von .github/workflows/image.yml, Betrieb: deploy/compose.yml).
# Konfiguration kommt komplett aus der Env-Datei auf dem Server, App_Data wird als Volume eingehängt.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY NOOSE-Website/NOOSE-Website.csproj NOOSE-Website/
RUN dotnet restore NOOSE-Website/NOOSE-Website.csproj
COPY NOOSE-Website/ NOOSE-Website/
RUN dotnet publish NOOSE-Website/NOOSE-Website.csproj -c Release -o /app --no-restore --nologo

# Selbst gehostete Quill-/Tabellen-Assets müssen im Artefakt liegen, sonst fehlt im Editor der
# Tabellen-Button bzw. die Lese-Ansicht bricht (vorher Schritt 1b in scripts/deploy.ps1).
RUN for f in quill.min.js quill.snow.css table-module.js table-module.css quill-global.mjs; do \
        test -f "/app/wwwroot/lib/quill/$f" || { echo "Publish-Output unvollstaendig: wwwroot/lib/quill/$f fehlt"; exit 1; }; \
    done

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# www-data (33), damit die bestehenden Dateirechte von App_Data weiter passen
USER 33:33
ENTRYPOINT ["dotnet", "NOOSE-Website.dll"]
