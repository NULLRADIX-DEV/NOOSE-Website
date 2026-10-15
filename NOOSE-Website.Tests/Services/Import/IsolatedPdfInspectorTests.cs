using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NOOSE_Website.Services.Import;

namespace NOOSE_Website.Tests.Services.Import;

/// <summary>Reads PDFs in this test process; the service tests need no child process.</summary>
internal sealed class InProcessPdfInspector : IPdfInspector
{
    public Task<PdfInfo> InspectAsync(byte[] pdf, CancellationToken cancellationToken = default)
        => Task.FromResult(PdfInspector.Read(pdf));
}

/// <summary>Runs the real child process: the web app's own assembly with <c>--pdf-info</c>.</summary>
public sealed class IsolatedPdfInspectorTests
{
    private static IsolatedPdfInspector Build()
    {
        // the app's assembly is copied next to the tests, with its runtimeconfig and deps
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        var app = Path.Combine(AppContext.BaseDirectory, "NOOSE-Website.dll");
        return new IsolatedPdfInspector(new PdfInspectorCommand(dotnet, [app]), NullLogger<IsolatedPdfInspector>.Instance);
    }

    /// <summary>A valid PDF whose page tree is a chain of <paramref name="depth"/> nested page nodes.</summary>
    private static byte[] DeepTree(int depth)
    {
        var objects = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>" };
        for (var i = 0; i < depth; i++)
        {
            objects.Add($"<< /Type /Pages /Kids [{4 + i} 0 R] /Count 1 >>");
        }
        objects.Add("<< /Type /Page /MediaBox [0 0 10 10] >>");
        var text = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Count; i++)
        {
            offsets.Add(text.Length);
            text.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        var xref = text.Length;
        text.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
        {
            text.Append($"{offset:D10} 00000 n \n");
        }
        text.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(text.ToString());
    }

    [Fact]
    public async Task The_child_reads_page_count_and_title()
    {
        var info = await Build().InspectAsync(TestPdf.Make(3, "Lagebild Süd"));

        Assert.Equal(new PdfInfo(3, "Lagebild Süd"), info);
    }

    [Fact]
    public async Task A_page_tree_that_overflows_the_parser_takes_down_only_the_child()
    {
        // in the web process this file ends the whole app with a stack overflow
        var error = await Assert.ThrowsAsync<GoogleExportException>(() => Build().InspectAsync(DeepTree(20000)));

        Assert.Contains("lässt sich nicht lesen", error.Message);
        Assert.Equal(new PdfInfo(1, null), await Build().InspectAsync(DeepTree(3)));
    }

    [Fact]
    public async Task The_childs_own_message_reaches_the_author_and_no_pdf_starts_no_child()
    {
        var broken = TestPdf.Make(2)[..200];

        var unreadable = await Assert.ThrowsAsync<GoogleExportException>(() => Build().InspectAsync(broken));
        var html = await Assert.ThrowsAsync<GoogleExportException>(() =>
            new IsolatedPdfInspector(new PdfInspectorCommand("kein-programm-weit-und-breit", []), NullLogger<IsolatedPdfInspector>.Instance)
                .InspectAsync("<html>Anmelden</html>"u8.ToArray()));

        Assert.Contains("lässt sich nicht lesen", unreadable.Message);
        Assert.Equal(PdfInspector.NotAPdf, html.Message);
    }
}
