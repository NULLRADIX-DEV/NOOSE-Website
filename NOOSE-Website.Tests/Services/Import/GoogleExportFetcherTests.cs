using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;
using NOOSE_Website.Services.Import;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Import;

/// <summary>The export fetch stays on Google's hosts and tells an unshared file apart.</summary>
public class GoogleExportFetcherTests
{
    private const string Id = "1AbCdEfGhIjKlMnOpQrStUvWxYz0123456789";

    /// <summary>Answers each request from a script and records where the fetcher went, with what credentials.</summary>
    private sealed class ScriptedHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Visited { get; } = [];
        public List<string?> Credentials { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Visited.Add(request.RequestUri!);
            Credentials.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(respond(request.RequestUri!));
        }
    }

    private static (GoogleExportFetcher Fetcher, ScriptedHandler Handler) Build(Func<Uri, HttpResponseMessage> respond,
        bool signIn = false)
    {
        var handler = new ScriptedHandler(respond);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(GoogleExportFetcher.ClientName).Returns(_ => new HttpClient(handler, disposeHandler: false));
        var options = signIn
            ? Options.Create(new GoogleDriveOptions { ClientId = "client.apps.googleusercontent.com", ApiKey = "key", AppId = "123" })
            : null;
        return (new GoogleExportFetcher(factory, options), handler);
    }

    private const string Token = "ya29.a0-test-token-for-one-fetch";
    private static readonly GoogleDrivePick Pick = new(Id, Token);

    private static HttpResponseMessage JsonAnswer(HttpStatusCode status, string json)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Metadata(string mimeType, string? exportLink = null)
    {
        var links = exportLink is null ? string.Empty : ",\"exportLinks\":{\"application/pdf\":\"" + exportLink + "\"}";
        return JsonAnswer(HttpStatusCode.OK, "{\"name\":\"Lagebild Süd\",\"mimeType\":\"" + mimeType + "\"" + links + "}");
    }

    private static bool IsMetadata(Uri uri) => uri.Host == "www.googleapis.com" && !uri.AbsolutePath.EndsWith("/export");

    private static HttpResponseMessage Redirect(string to)
    {
        var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
        response.Headers.Location = new Uri(to);
        return response;
    }

    private static HttpResponseMessage Content(string type, byte[] body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return response;
    }

    [Theory]
    [InlineData("https://docs.google.com/document/d/" + Id + "/edit?usp=sharing", GoogleFileKind.Document, Id)]
    [InlineData("https://docs.google.com/document/d/" + Id, GoogleFileKind.Document, Id)]
    [InlineData("https://docs.google.com/presentation/d/" + Id + "/edit#slide=id.p", GoogleFileKind.Presentation, Id)]
    [InlineData("https://docs.google.com/presentation/d/" + Id + "/edit", GoogleFileKind.Document, null)]
    [InlineData("http://docs.google.com/document/d/" + Id + "/edit", GoogleFileKind.Document, null)]
    [InlineData("https://docs.google.com.evil.example/document/d/" + Id, GoogleFileKind.Document, null)]
    [InlineData("https://evil.example/document/d/" + Id, GoogleFileKind.Document, null)]
    [InlineData("https://docs.google.com/document/d/short/edit", GoogleFileKind.Document, null)]
    [InlineData("kein Link", GoogleFileKind.Document, null)]
    public void FileId_reads_only_docs_google_com_links_of_the_right_kind(string link, GoogleFileKind kind, string? expected)
        => Assert.Equal(expected, GoogleLinks.FileId(link, kind));

    [Fact]
    public async Task Follows_the_download_redirect_and_returns_the_zip()
    {
        var (fetcher, handler) = Build(uri => uri.Host == "docs.google.com"
            ? Redirect("https://doc-0s-1a-docs.googleusercontent.com/export/abc")
            : Content("application/zip", [1, 2, 3]));

        var bytes = await fetcher.FetchDocumentZipAsync(Id);

        Assert.Equal([1, 2, 3], bytes);
        Assert.Equal($"https://docs.google.com/document/d/{Id}/export?format=zip", handler.Visited[0].ToString());
        Assert.Equal("doc-0s-1a-docs.googleusercontent.com", handler.Visited[1].Host);
    }

    [Fact]
    public async Task A_sign_in_redirect_reads_as_not_shared()
    {
        var (fetcher, _) = Build(_ => Redirect("https://accounts.google.com/ServiceLogin?continue=x"));

        var error = await Assert.ThrowsAsync<GoogleExportException>(() => fetcher.FetchDocumentZipAsync(Id));
        Assert.Contains("nicht per Link freigegeben", error.Message);
    }

    [Fact]
    public async Task A_redirect_off_google_is_never_followed()
    {
        var (fetcher, handler) = Build(_ => Redirect("https://evil.example/steal"));

        await Assert.ThrowsAsync<GoogleExportException>(() => fetcher.FetchDocumentZipAsync(Id));
        Assert.Single(handler.Visited);
    }

    [Fact]
    public async Task A_sign_in_page_with_200_is_not_taken_for_the_export()
    {
        var (fetcher, _) = Build(_ => Content("text/html", "<html>Anmelden</html>"u8.ToArray()));

        var error = await Assert.ThrowsAsync<GoogleExportException>(() => fetcher.FetchDocumentZipAsync(Id));
        Assert.Contains("nicht per Link freigegeben", error.Message);
    }

    [Fact]
    public async Task An_invalid_id_never_reaches_the_network()
    {
        var (fetcher, handler) = Build(_ => Content("application/zip", [1]));

        await Assert.ThrowsAsync<GoogleExportException>(() => fetcher.FetchDocumentZipAsync("../../etc"));
        Assert.Empty(handler.Visited);
    }

    [Fact]
    public async Task A_network_fault_or_a_timeout_reads_as_a_message_not_a_crash()
    {
        var (unreachable, _) = Build(_ => throw new HttpRequestException("no route"));
        var (slow, _) = Build(_ => throw new TaskCanceledException("timeout"));

        await Assert.ThrowsAsync<GoogleExportException>(
            () => unreachable.FetchDocumentZipAsync("1AbCdEfGhIjKlMnOpQrStUvWxYz0123456789"));
        await Assert.ThrowsAsync<GoogleExportException>(
            () => slow.FetchDocumentZipAsync("1AbCdEfGhIjKlMnOpQrStUvWxYz0123456789"));
    }

    [Fact]
    public async Task A_presentation_comes_as_pdf_under_the_cap_its_caller_sets()
    {
        var (fetcher, handler) = Build(_ => Content("application/pdf", new byte[2048]));

        var bytes = await fetcher.FetchPresentationPdfAsync(Id, 4096);

        Assert.Equal(2048, bytes.Length);
        Assert.Equal($"https://docs.google.com/presentation/d/{Id}/export/pdf", handler.Visited[0].ToString());
        await Assert.ThrowsAsync<GoogleExportException>(() => fetcher.FetchPresentationPdfAsync(Id, 1024));
    }

    [Fact]
    public async Task The_cap_counts_the_bytes_actually_read()
    {
        using var stream = new MemoryStream(new byte[2048]);

        await Assert.ThrowsAsync<GoogleExportException>(() => GoogleExportFetcher.ReadCappedAsync(stream, 1024, default));
    }

    // ---------- Google sign-in ----------

    [Fact]
    public async Task A_picked_document_is_read_through_the_drive_api_with_its_token()
    {
        var (fetcher, handler) = Build(uri => IsMetadata(uri)
            ? Metadata("application/vnd.google-apps.document")
            : Content("application/zip", [7, 8, 9]), signIn: true);

        var export = await fetcher.FetchDriveDocumentZipAsync(Pick);

        Assert.Equal(new byte[] { 7, 8, 9 }, export.Bytes);
        Assert.Equal("Lagebild Süd", export.Name);
        Assert.Equal($"https://www.googleapis.com/drive/v3/files/{Id}/export?mimeType=application%2Fzip", handler.Visited[1].ToString());
        Assert.All(handler.Visited, uri => Assert.Equal("www.googleapis.com", uri.Host));
        Assert.All(handler.Credentials, credential => Assert.Equal($"Bearer {Token}", credential));
    }

    [Theory]
    [InlineData("application/vnd.google-apps.spreadsheet")]
    [InlineData("application/pdf")]
    public async Task Anything_but_a_google_document_is_refused_before_its_bytes(string mimeType)
    {
        var (fetcher, handler) = Build(_ => Metadata(mimeType), signIn: true);

        var error = await Assert.ThrowsAsync<GoogleExportException>(() => fetcher.FetchDriveDocumentZipAsync(Pick));

        Assert.Contains("kein Google-Dokument", error.Message);
        Assert.Single(handler.Visited);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "abgelaufen")]
    [InlineData(HttpStatusCode.Forbidden, "nicht heraus")]
    [InlineData(HttpStatusCode.NotFound, "nicht heraus")]
    [InlineData(HttpStatusCode.TooManyRequests, "bremst")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    public async Task Each_refusal_of_the_drive_api_reads_as_its_own_message(HttpStatusCode status, string message)
    {
        var (fetcher, _) = Build(_ => JsonAnswer(status, "{}"), signIn: true);

        var error = await Assert.ThrowsAsync<GoogleExportException>(() => fetcher.FetchDrivePresentationPdfAsync(Pick, 4096));

        Assert.Contains(message, error.Message);
    }

    [Fact]
    public async Task A_deck_over_ten_megabytes_comes_through_its_export_link_and_the_token_stops_at_docs_google_com()
    {
        const string link = "https://docs.google.com/feeds/download/presentations/Export?id=x&exportFormat=pdf";
        var (fetcher, handler) = Build(uri => uri.Host switch
        {
            "www.googleapis.com" when IsMetadata(uri) => Metadata("application/vnd.google-apps.presentation", link),
            "www.googleapis.com" => JsonAnswer(HttpStatusCode.Forbidden,
                """{"error":{"code":403,"errors":[{"reason":"exportSizeLimitExceeded","message":"This file is too large to be exported."}]}}"""),
            "docs.google.com" => Redirect("https://doc-04-docs.googleusercontent.com/export/signed"),
            _ => Content("application/pdf", new byte[2048]),
        }, signIn: true);

        var export = await fetcher.FetchDrivePresentationPdfAsync(Pick, 4096);

        Assert.Equal(2048, export.Bytes.Length);
        Assert.Equal(["www.googleapis.com", "www.googleapis.com", "docs.google.com", "doc-04-docs.googleusercontent.com"],
            handler.Visited.Select(uri => uri.Host));
        Assert.Equal([$"Bearer {Token}", $"Bearer {Token}", $"Bearer {Token}", null], handler.Credentials);
    }

    [Theory]
    [InlineData("https://evil.example/export?id=x")]
    [InlineData("https://docs.google.com:8443/export?id=x")]
    [InlineData(null)]
    public async Task An_export_link_off_docs_google_com_never_gets_the_token(string? link)
    {
        var (fetcher, handler) = Build(uri => IsMetadata(uri)
            ? Metadata("application/vnd.google-apps.presentation", link)
            : JsonAnswer(HttpStatusCode.Forbidden, """{"error":{"errors":[{"reason":"exportSizeLimitExceeded"}]}}"""),
            signIn: true);

        var error = await Assert.ThrowsAsync<GoogleExportException>(() => fetcher.FetchDrivePresentationPdfAsync(Pick, 4096));

        Assert.Contains("Lade sie hoch", error.Message);
        Assert.All(handler.Visited, uri => Assert.Equal("www.googleapis.com", uri.Host));
    }

    [Theory]
    [InlineData("userRateLimitExceeded")]
    [InlineData("rateLimitExceeded")]
    public async Task A_rate_limit_refused_with_403_reads_as_throttling(string reason)
    {
        var (fetcher, _) = Build(_ => JsonAnswer(HttpStatusCode.Forbidden, "{\"error\":{\"errors\":[{\"reason\":\"" + reason + "\"}]}}"),
            signIn: true);

        var error = await Assert.ThrowsAsync<GoogleExportException>(() => fetcher.FetchDriveDocumentZipAsync(Pick));

        Assert.Contains("bremst", error.Message);
    }

    [Fact]
    public async Task Metadata_that_is_not_json_reads_as_a_message()
    {
        var (fetcher, handler) = Build(_ => JsonAnswer(HttpStatusCode.OK, "<html>kein JSON</html>"), signIn: true);

        var error = await Assert.ThrowsAsync<GoogleExportException>(() => fetcher.FetchDriveDocumentZipAsync(Pick));

        Assert.Contains("nicht beschrieben", error.Message);
        Assert.Single(handler.Visited);
    }

    [Fact]
    public async Task A_plain_refusal_of_the_export_is_not_taken_for_a_size_limit()
    {
        var (fetcher, handler) = Build(uri => IsMetadata(uri)
            ? Metadata("application/vnd.google-apps.presentation", "https://docs.google.com/export?id=x")
            : JsonAnswer(HttpStatusCode.Forbidden, """{"error":{"errors":[{"reason":"insufficientFilePermissions"}]}}"""),
            signIn: true);

        var error = await Assert.ThrowsAsync<GoogleExportException>(() => fetcher.FetchDrivePresentationPdfAsync(Pick, 4096));

        Assert.Contains("nicht heraus", error.Message);
        Assert.Equal(2, handler.Visited.Count);
    }

    [Fact]
    public async Task Without_configuration_or_with_a_malformed_pick_nothing_leaves_the_server()
    {
        var (unconfigured, first) = Build(_ => Content("application/zip", [1]));
        var (configured, second) = Build(_ => Content("application/zip", [1]), signIn: true);

        var off = await Assert.ThrowsAsync<GoogleExportException>(() => unconfigured.FetchDriveDocumentZipAsync(Pick));
        var header = await Assert.ThrowsAsync<GoogleExportException>(() =>
            configured.FetchDriveDocumentZipAsync(new GoogleDrivePick(Id, "ya29.token\r\nX-Injected: 1")));
        await Assert.ThrowsAsync<GoogleExportException>(() =>
            configured.FetchDriveDocumentZipAsync(new GoogleDrivePick("../../about?fields=*", Token)));

        Assert.Contains("nicht eingerichtet", off.Message);
        Assert.Contains("abgelaufen", header.Message);
        Assert.Empty(first.Visited);
        Assert.Empty(second.Visited);
    }
}
