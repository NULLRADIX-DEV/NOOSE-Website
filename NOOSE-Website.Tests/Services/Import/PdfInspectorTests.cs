using NOOSE_Website.Services.Import;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Writer;

namespace NOOSE_Website.Tests.Services.Import;

/// <summary>Builds small PDFs the way a Google Slides export looks to the server: pages and a title.</summary>
internal static class TestPdf
{
    public static byte[] Make(int pages, string? title = null)
    {
        var builder = new PdfDocumentBuilder();
        if (title is not null)
        {
            builder.DocumentInformation.Title = title;
        }
        for (var i = 0; i < pages; i++)
        {
            builder.AddPage(PageSize.A4, isPortrait: false);
        }
        return builder.Build();
    }
}

/// <summary>Unit tests for <see cref="PdfInspector"/>.</summary>
public sealed class PdfInspectorTests
{
    [Fact]
    public void Reads_the_page_count_and_the_title()
    {
        var info = PdfInspector.Read(TestPdf.Make(4, "Einsatzbesprechung"));

        Assert.Equal(4, info.PageCount);
        Assert.Equal("Einsatzbesprechung", info.Title);
    }

    [Fact]
    public void A_blank_title_reads_as_none()
    {
        Assert.Null(PdfInspector.Read(TestPdf.Make(1, "   ")).Title);
        Assert.Null(PdfInspector.Read(TestPdf.Make(1)).Title);
    }

    [Fact]
    public void Bytes_that_are_no_pdf_are_refused_with_a_message()
    {
        var html = "<html><body>Anmelden</body></html>"u8.ToArray();

        var error = Assert.Throws<GoogleExportException>(() => PdfInspector.Read(html));
        Assert.Contains("keine PDF-Datei", error.Message);
    }

    [Fact]
    public void A_broken_pdf_reads_as_unreadable_not_as_a_crash()
    {
        var broken = TestPdf.Make(3)[..200];

        var error = Assert.Throws<GoogleExportException>(() => PdfInspector.Read(broken));
        Assert.Contains("lässt sich nicht lesen", error.Message);
    }
}
