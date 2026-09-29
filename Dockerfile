# NOOSE-Website als Container-Image (gebaut von .github/workflows/image.yml, Betrieb: deploy/compose.yml).
# Konfiguration kommt komplett aus der Env-Datei auf dem Server, App_Data wird als Volume eingehängt.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Restore erst zusammen mit allen Quellen, NICHT vorab nur mit der .csproj: Das Web-SDK entscheidet
# anhand der .razor-Dateien, ob die Blazor-Framework-Skripte (_framework/blazor.web.js) dazukommen.
# Ohne sie startet Blazor im Browser nicht (Navbar & Co. reagieren nicht).
COPY NOOSE-Website/ NOOSE-Website/
# BuildNumber.txt ist gitignored; die Version (Einstellungen -> Status) setzt deshalb die GitHub Action.
ARG BUILD_VERSION=
RUN dotnet publish NOOSE-Website/NOOSE-Website.csproj -c Release -o /app --nologo ${BUILD_VERSION:+-p:Version=$BUILD_VERSION}

# Pflicht-Assets im Artefakt pruefen: Blazor-Skript und die selbst gehosteten Quill-/Tabellen-Assets
# (sonst fehlt im Editor der Tabellen-Button bzw. die Lese-Ansicht bricht).
RUN test -f /app/wwwroot/_framework/blazor.web.js || { echo "Publish-Output unvollstaendig: wwwroot/_framework/blazor.web.js fehlt"; exit 1; } \
 && for f in quill.min.js quill.snow.css table-module.js table-module.css quill-global.mjs; do \
        test -f "/app/wwwroot/lib/quill/$f" || { echo "Publish-Output unvollstaendig: wwwroot/lib/quill/$f fehlt"; exit 1; }; \
    done

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
# www-data (33), damit die bestehenden Dateirechte von App_Data weiter passen
USER 33:33
ENTRYPOINT ["dotnet", "NOOSE-Website.dll"]
