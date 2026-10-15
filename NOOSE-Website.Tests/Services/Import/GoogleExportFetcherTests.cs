using System.Net;
using System.Net.Http.Headers;
using NOOSE_Website.Services.Import;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Import;

/// <summary>The export fetch stays on Google's hosts and tells an unshared file apart.</summary>
public class GoogleExportFetcherTests
{
    private const string Id = "1AbCdEfGhIjKlMnOpQrStUvWxYz0123456789";

    /// <summary>Answers each request from a script and records where the fetcher went.</summary>
    private sealed class ScriptedHandler(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Visited { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Visited.Add(request.RequestUri!);
            return Task.FromResult(respond(request.RequestUri!));
        }
    }

    private static (GoogleExportFetcher Fetcher, ScriptedHandler Handler) Build(Func<Uri, HttpResponseMessage> respond)
    {
        var handler = new ScriptedHandler(respond);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(GoogleExportFetcher.ClientName).Returns(_ => new HttpClient(handler, disposeHandler: false));
        return (new GoogleExportFetcher(factory), handler);
    }

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
    public async Task The_cap_counts_the_bytes_actually_read()
    {
        using var stream = new MemoryStream(new byte[2048]);

        await Assert.ThrowsAsync<GoogleExportException>(() => GoogleExportFetcher.ReadCappedAsync(stream, 1024, default));
    }
}
