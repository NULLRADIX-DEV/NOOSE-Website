using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace NOOSE_Website.Services.Import;

/// <summary>Which Google file a shared link points at.</summary>
public enum GoogleFileKind
{
    Document,
    Presentation,
}

/// <summary>Reads the file id out of a Google Docs or Slides link.</summary>
public static partial class GoogleLinks
{
    /// <summary>The id of a docs.google.com document or presentation link; null for anything else.</summary>
    /// <remarks>Only the id leaves this method: the server builds the export URL itself, so no host, path or query
    /// from the pasted text ever reaches the outgoing request.</remarks>
    public static string? FileId(string? link, GoogleFileKind kind)
    {
        if (string.IsNullOrWhiteSpace(link) || !Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, "docs.google.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var match = PathPattern().Match(uri.AbsolutePath);
        if (!match.Success)
        {
            return null;
        }
        var segment = kind == GoogleFileKind.Document ? "document" : "presentation";
        return string.Equals(match.Groups["kind"].Value, segment, StringComparison.Ordinal)
            ? match.Groups["id"].Value
            : null;
    }

    /// <summary>Whether a string is shaped like a Google file id; the export URL is built only from such a string.</summary>
    public static bool IsFileId(string? id) => id is not null && IdPattern().IsMatch(id);

    /// <summary>Whether a string is shaped like an OAuth access token, so it can go into a header as it is.</summary>
    public static bool IsAccessToken(string? token) => token is not null && TokenPattern().IsMatch(token);

    [GeneratedRegex(@"^/(?<kind>document|presentation)/d/(?<id>[A-Za-z0-9_-]{20,128})(?:/|$)")]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{20,128}$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._~+/=-]{20,4096}$")]
    private static partial Regex TokenPattern();
}

/// <summary>A Google export that could not be fetched; the message is shown to the author.</summary>
public sealed class GoogleExportException(string message) : Exception(message);

/// <summary>Fetches a Google file's export: shared "anyone with the link", or through the agent's Google sign-in.</summary>
public interface IGoogleExportFetcher
{
    /// <summary>The document as Google's zipped HTML export.</summary>
    Task<byte[]> FetchDocumentZipAsync(string fileId, CancellationToken cancellationToken = default);

    /// <summary>The presentation as a PDF, no larger than <paramref name="maxBytes"/>.</summary>
    Task<byte[]> FetchPresentationPdfAsync(string fileId, long maxBytes, CancellationToken cancellationToken = default);

    /// <summary>A document chosen in the Picker, as zipped HTML, read with the agent's own access token.</summary>
    Task<DriveExport> FetchDriveDocumentZipAsync(GoogleDrivePick pick, CancellationToken cancellationToken = default);

    /// <summary>A presentation chosen in the Picker, as a PDF no larger than <paramref name="maxBytes"/>.</summary>
    Task<DriveExport> FetchDrivePresentationPdfAsync(GoogleDrivePick pick, long maxBytes, CancellationToken cancellationToken = default);

    /// <summary>Whether the Google sign-in is configured; without it a pick is refused before anything is counted.</summary>
    bool IsDriveConfigured { get; }
}

/// <inheritdoc cref="IGoogleExportFetcher" />
/// <remarks>
/// The HTTP client follows no redirect on its own. Google answers an export with a redirect to a
/// googleusercontent.com download host, and an unshared file with one to the sign-in page; following only the
/// first kind, a hop at a time, keeps every request on Google's own hosts whatever a response says.
/// Through the sign-in, the Drive API on www.googleapis.com is asked first. It exports at most 10 MB; a larger file
/// comes through the export link Drive names for it, on docs.google.com. The token goes to those two hosts only,
/// never along a redirect to a download host. The client keeps no cookies (see Program.cs), so nothing a response to
/// one agent's token sets travels with another agent's request.
/// </remarks>
public sealed class GoogleExportFetcher(IHttpClientFactory httpClientFactory, IOptions<GoogleDriveOptions>? driveOptions = null)
    : IGoogleExportFetcher
{
    public const string ClientName = "google-export";

    /// <summary>Largest export accepted, in bytes.</summary>
    public const long MaxBytes = 25L * 1024 * 1024;

    private const int MaxRedirects = 3;

    // a file's metadata is a few hundred bytes; anything far larger is not what was asked for
    private const long MetadataMaxBytes = 1024 * 1024;

    private const string DocumentType = "application/vnd.google-apps.document";
    private const string PresentationType = "application/vnd.google-apps.presentation";

    private const string NotShared =
        "Die Datei ist nicht per Link freigegeben. Stelle in Google „Jeder mit dem Link“ ein oder lade sie hoch.";

    private const string DriveRefused =
        "Google gibt die Datei nicht heraus. Wähle sie im Google-Fenster noch einmal aus; du brauchst dort Leserecht.";

    private const string DriveExpired = "Die Google-Anmeldung ist abgelaufen. Wähle die Datei noch einmal.";

    private const string DriveUnavailable =
        "Die Google-Anmeldung ist hier nicht eingerichtet. Nutze den Freigabe-Link oder lade die Datei hoch.";

    private const string DriveThrottled = "Google bremst gerade. Versuche es in ein paar Minuten.";

    /// <summary>Longest one fetch may take, body included: the client's own timeout ends with the response headers.</summary>
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromMinutes(2);

    private static readonly IReadOnlySet<string> NoReasons = new HashSet<string>();

    private const string DriveTooLarge =
        "Über die Google-Anmeldung gibt Google diese große Datei nicht heraus. Lade sie hoch (Datei → Herunterladen) "
        + "oder nutze den Freigabe-Link.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool IsDriveConfigured => driveOptions?.Value.IsConfigured == true;

    public Task<byte[]> FetchDocumentZipAsync(string fileId, CancellationToken cancellationToken = default)
    {
        var url = new Uri($"https://docs.google.com/document/d/{Checked(fileId)}/export?format=zip");
        return Guarded(token => FetchOnceAsync(url, ["application/zip", "application/x-zip-compressed"], MaxBytes, NotShared,
            null, token), cancellationToken);
    }

    public Task<byte[]> FetchPresentationPdfAsync(string fileId, long maxBytes, CancellationToken cancellationToken = default)
    {
        var url = new Uri($"https://docs.google.com/presentation/d/{Checked(fileId)}/export/pdf");
        return Guarded(token => FetchOnceAsync(url, ["application/pdf"], maxBytes, NotShared, null, token), cancellationToken);
    }

    public Task<DriveExport> FetchDriveDocumentZipAsync(GoogleDrivePick pick, CancellationToken cancellationToken = default)
        => FetchDriveAsync(pick, DocumentType, "Das ist kein Google-Dokument. Wähle ein Dokument aus Google Docs.",
            "application/zip", ["application/zip", "application/x-zip-compressed"], MaxBytes, cancellationToken);

    public Task<DriveExport> FetchDrivePresentationPdfAsync(GoogleDrivePick pick, long maxBytes, CancellationToken cancellationToken = default)
        => FetchDriveAsync(pick, PresentationType, "Das ist keine Google-Präsentation. Wähle eine Präsentation aus Google Slides.",
            "application/pdf", ["application/pdf"], maxBytes, cancellationToken);

    private static string Checked(string fileId)
        => GoogleLinks.IsFileId(fileId) ? fileId : throw new GoogleExportException("Das ist kein gültiger Google-Link.");

    /// <summary>Refuses a pick while the sign-in is not configured, or whose id or token could not be sent as they
    /// are. The import services call it before taking the quota, so such a pick costs nothing.</summary>
    public static void RequireUsable(GoogleDrivePick pick, bool configured)
    {
        if (!configured)
        {
            throw new GoogleExportException(DriveUnavailable);
        }
        if (!GoogleLinks.IsFileId(pick.FileId))
        {
            throw new GoogleExportException("Google hat keine gültige Datei gemeldet. Wähle sie noch einmal.");
        }
        if (!GoogleLinks.IsAccessToken(pick.AccessToken))
        {
            throw new GoogleExportException(DriveExpired);
        }
    }

    /// <summary>Hosts a download may be served from.</summary>
    public static bool IsAllowedHost(Uri uri)
        => IsDocsHost(uri)
            || (uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
                && uri.Host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase));

    private static bool IsDocsHost(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
            && string.Equals(uri.Host, "docs.google.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>A network fault or a timeout is Google's state, not a bug: it reaches the page as a message like any
    /// refusal. The deadline also covers reading the body, so a stalled download cannot hold a dialog forever.</summary>
    private static async Task<T> Guarded<T>(Func<CancellationToken, Task<T>> fetch, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(FetchTimeout);
        try
        {
            return await fetch(deadline.Token);
        }
        catch (HttpRequestException)
        {
            throw new GoogleExportException("Google ist gerade nicht erreichbar. Versuche es später oder lade die Datei hoch.");
        }
        catch (IOException)
        {
            throw new GoogleExportException("Die Verbindung zu Google ist abgebrochen. Versuche es später oder lade die Datei hoch.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GoogleExportException("Google hat zu lange gebraucht. Versuche es später oder lade die Datei hoch.");
        }
    }

    /// <summary>Follows an export to its bytes, a hop at a time. <paramref name="bearer"/> goes to docs.google.com
    /// only: a download host gets a signed link and no token.</summary>
    private async Task<byte[]> FetchOnceAsync(Uri url, string[] acceptedTypes, long maxBytes, string refused, string? bearer,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(ClientName);
        var target = url;
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            if (bearer is not null && IsDocsHost(target))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            }
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (IsRedirect(response.StatusCode))
            {
                var next = response.Headers.Location is { } location
                    ? (location.IsAbsoluteUri ? location : new Uri(target, location))
                    : null;
                if (next is null)
                {
                    throw new GoogleExportException("Google hat die Datei nicht herausgegeben.");
                }
                if (string.Equals(next.Host, "accounts.google.com", StringComparison.OrdinalIgnoreCase))
                {
                    throw new GoogleExportException(refused);
                }
                if (!IsAllowedHost(next))
                {
                    throw new GoogleExportException("Google hat auf eine unbekannte Adresse umgeleitet.");
                }
                target = next;
                continue;
            }
            if (response.StatusCode is HttpStatusCode.NotFound)
            {
                throw new GoogleExportException("Die Datei gibt es bei Google nicht (mehr).");
            }
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                throw new GoogleExportException(refused);
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new GoogleExportException($"Google antwortet gerade nicht ({(int)response.StatusCode}). Versuche es später.");
            }
            // an unshared file can also come back as a 200 sign-in page
            RequireType(response, acceptedTypes, refused);
            return await ReadBodyAsync(response, maxBytes, cancellationToken);
        }
        throw new GoogleExportException("Google hat zu oft umgeleitet.");
    }

    private async Task<DriveExport> FetchDriveAsync(GoogleDrivePick pick, string googleType, string wrongType, string exportType,
        string[] acceptedTypes, long maxBytes, CancellationToken cancellationToken)
    {
        RequireUsable(pick, IsDriveConfigured);
        return await Guarded(token => FetchDriveOnceAsync(pick, googleType, wrongType, exportType, acceptedTypes, maxBytes,
            token), cancellationToken);
    }

    private async Task<DriveExport> FetchDriveOnceAsync(GoogleDrivePick pick, string googleType, string wrongType,
        string exportType, string[] acceptedTypes, long maxBytes, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(ClientName);

        // what the file is before any of its bytes: a spreadsheet or a stored PDF is refused by name, not by a failed export
        DriveFile file;
        using (var request = DriveRequest($"files/{pick.FileId}?fields=name,mimeType,exportLinks&supportsAllDrives=true", pick))
        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            await RequireDriveSuccessAsync(response, cancellationToken);
            var json = await ReadBodyAsync(response, MetadataMaxBytes, cancellationToken);
            try
            {
                file = JsonSerializer.Deserialize<DriveFile>(json, Json) ?? throw new JsonException();
            }
            catch (JsonException)
            {
                throw new GoogleExportException("Google hat die Datei nicht beschrieben. Versuche es später.");
            }
        }
        if (!string.Equals(file.MimeType, googleType, StringComparison.Ordinal))
        {
            throw new GoogleExportException(wrongType);
        }
        var name = string.IsNullOrWhiteSpace(file.Name) ? null : file.Name.Trim();

        using (var request = DriveRequest($"files/{pick.FileId}/export?mimeType={Uri.EscapeDataString(exportType)}", pick))
        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            var reasons = response.StatusCode is HttpStatusCode.Forbidden
                ? await ReasonsAsync(response, cancellationToken)
                : NoReasons;
            if (reasons.Contains("exportSizeLimitExceeded"))
            {
                // the API exports at most 10 MB; Drive names an export link for larger files, served by docs.google.com
                if (file.ExportLinks?.GetValueOrDefault(exportType) is not { } link
                    || !Uri.TryCreate(link, UriKind.Absolute, out var linkUri) || !IsDocsHost(linkUri))
                {
                    throw new GoogleExportException(DriveTooLarge);
                }
                var bytes = await FetchOnceAsync(linkUri, acceptedTypes, maxBytes, DriveTooLarge, pick.AccessToken, cancellationToken);
                return new DriveExport(bytes, name);
            }
            RequireDriveSuccess(response, reasons);
            RequireType(response, acceptedTypes, DriveRefused);
            return new DriveExport(await ReadBodyAsync(response, maxBytes, cancellationToken), name);
        }
    }

    private static HttpRequestMessage DriveRequest(string path, GoogleDrivePick pick)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, new Uri($"https://www.googleapis.com/drive/v3/{path}"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", pick.AccessToken);
        return request;
    }

    private static async Task RequireDriveSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            RequireDriveSuccess(response, response.StatusCode is HttpStatusCode.Forbidden
                ? await ReasonsAsync(response, cancellationToken)
                : NoReasons);
        }
    }

    private static void RequireDriveSuccess(HttpResponseMessage response, IReadOnlySet<string> reasons)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        throw new GoogleExportException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => DriveExpired,
            // Drive refuses with 403 for a rate limit too
            HttpStatusCode.TooManyRequests => DriveThrottled,
            HttpStatusCode.Forbidden when reasons.Contains("rateLimitExceeded") || reasons.Contains("userRateLimitExceeded")
                => DriveThrottled,
            // drive.file reaches only files picked for this app; anything else is "not found"
            HttpStatusCode.Forbidden or HttpStatusCode.NotFound => DriveRefused,
            _ when IsRedirect(response.StatusCode) => "Google hat die Datei nicht herausgegeben.",
            _ => $"Google antwortet gerade nicht ({(int)response.StatusCode}). Versuche es später.",
        });
    }

    /// <summary>The reasons a Drive API refusal names (<c>error.errors[].reason</c>); none when the body says nothing.</summary>
    private static async Task<IReadOnlySet<string>> ReasonsAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = JsonDocument.Parse(await ReadCappedAsync(stream, MetadataMaxBytes, cancellationToken));
            if (!json.RootElement.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object
                || !error.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
            {
                return NoReasons;
            }
            return errors.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Object && e.TryGetProperty("reason", out var reason)
                    && reason.ValueKind == JsonValueKind.String)
                .Select(e => e.GetProperty("reason").GetString()!)
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is JsonException or GoogleExportException)
        {
            return NoReasons;
        }
    }

    private static void RequireType(HttpResponseMessage response, string[] acceptedTypes, string refused)
    {
        var type = response.Content.Headers.ContentType?.MediaType;
        if (type is null || !acceptedTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
        {
            throw new GoogleExportException(refused);
        }
    }

    private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, long maxBytes, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maxBytes)
        {
            throw new GoogleExportException($"Die Datei ist zu groß (höchstens {maxBytes / 1024 / 1024} MB).");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await ReadCappedAsync(stream, maxBytes, cancellationToken);
    }

    private static bool IsRedirect(HttpStatusCode code)
        => code is HttpStatusCode.Moved or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    /// <summary>Reads a stream to its end but no further than the cap; the length header is not trusted.</summary>
    public static async Task<byte[]> ReadCappedAsync(Stream stream, long cap, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > cap)
            {
                throw new GoogleExportException($"Die Datei ist zu groß (höchstens {cap / 1024 / 1024} MB).");
            }
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private sealed record DriveFile(string? Name, string? MimeType, Dictionary<string, string>? ExportLinks);
}
