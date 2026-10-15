using System.Net;
using System.Text.RegularExpressions;

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

    [GeneratedRegex(@"^/(?<kind>document|presentation)/d/(?<id>[A-Za-z0-9_-]{20,128})(?:/|$)")]
    private static partial Regex PathPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{20,128}$")]
    private static partial Regex IdPattern();
}

/// <summary>A Google export that could not be fetched; the message is shown to the author.</summary>
public sealed class GoogleExportException(string message) : Exception(message);

/// <summary>Fetches the export of a file shared "anyone with the link" from Google.</summary>
public interface IGoogleExportFetcher
{
    /// <summary>The document as Google's zipped HTML export.</summary>
    Task<byte[]> FetchDocumentZipAsync(string fileId, CancellationToken cancellationToken = default);

    /// <summary>The presentation as a PDF, no larger than <paramref name="maxBytes"/>.</summary>
    Task<byte[]> FetchPresentationPdfAsync(string fileId, long maxBytes, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IGoogleExportFetcher" />
/// <remarks>
/// The HTTP client follows no redirect on its own. Google answers an export with a redirect to a
/// googleusercontent.com download host, and an unshared file with one to the sign-in page; following only the
/// first kind, a hop at a time, keeps every request on Google's own hosts whatever a response says.
/// </remarks>
public sealed class GoogleExportFetcher(IHttpClientFactory httpClientFactory) : IGoogleExportFetcher
{
    public const string ClientName = "google-export";

    /// <summary>Largest export accepted, in bytes.</summary>
    public const long MaxBytes = 25L * 1024 * 1024;

    private const int MaxRedirects = 3;

    public Task<byte[]> FetchDocumentZipAsync(string fileId, CancellationToken cancellationToken = default)
        => FetchAsync($"https://docs.google.com/document/d/{Checked(fileId)}/export?format=zip",
            ["application/zip", "application/x-zip-compressed"], MaxBytes, cancellationToken);

    public Task<byte[]> FetchPresentationPdfAsync(string fileId, long maxBytes, CancellationToken cancellationToken = default)
        => FetchAsync($"https://docs.google.com/presentation/d/{Checked(fileId)}/export/pdf",
            ["application/pdf"], maxBytes, cancellationToken);

    private static string Checked(string fileId)
        => GoogleLinks.IsFileId(fileId) ? fileId : throw new GoogleExportException("Das ist kein gültiger Google-Link.");

    /// <summary>Hosts a download may be served from.</summary>
    public static bool IsAllowedHost(Uri uri)
        => uri.Scheme == Uri.UriSchemeHttps
            && (string.Equals(uri.Host, "docs.google.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase));

    private async Task<byte[]> FetchAsync(string url, string[] acceptedTypes, long maxBytes, CancellationToken cancellationToken)
    {
        // a network fault or a timeout is Google's state, not a bug: it reaches the page as a message like any refusal
        try
        {
            return await FetchOnceAsync(url, acceptedTypes, maxBytes, cancellationToken);
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

    private async Task<byte[]> FetchOnceAsync(string url, string[] acceptedTypes, long maxBytes, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(ClientName);
        var target = new Uri(url);
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            using var response = await client.GetAsync(target, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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
                    throw new GoogleExportException(
                        "Die Datei ist nicht per Link freigegeben. Stelle in Google „Jeder mit dem Link“ ein oder lade sie hoch.");
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
                throw new GoogleExportException(
                    "Die Datei ist nicht per Link freigegeben. Stelle in Google „Jeder mit dem Link“ ein oder lade sie hoch.");
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new GoogleExportException($"Google antwortet gerade nicht ({(int)response.StatusCode}). Versuche es später.");
            }
            var type = response.Content.Headers.ContentType?.MediaType;
            // an unshared file can also come back as a 200 sign-in page
            if (type is null || !acceptedTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
            {
                throw new GoogleExportException(
                    "Die Datei ist nicht per Link freigegeben. Stelle in Google „Jeder mit dem Link“ ein oder lade sie hoch.");
            }
            if (response.Content.Headers.ContentLength > maxBytes)
            {
                throw new GoogleExportException($"Die Datei ist zu groß (höchstens {maxBytes / 1024 / 1024} MB).");
            }
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await ReadCappedAsync(stream, maxBytes, cancellationToken);
        }
        throw new GoogleExportException("Google hat zu oft umgeleitet.");
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
}
