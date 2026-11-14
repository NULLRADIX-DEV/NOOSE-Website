using System.IO.Compression;
using System.Security;
using System.Text;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Investigations;

namespace NOOSE_Website.Tests.Services;

/// <summary>The importer reads the DoJ workbook by its labels; the workbook here is built in the test, never a real file.</summary>
public class InvestigationWorkbookImporterTests
{
    /// <summary>Builds a minimal xlsx with inline strings; cells as "A1" → value per sheet.</summary>
    private static MemoryStream Workbook(params (string Sheet, (string Ref, string Value)[] Cells)[] sheets)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var wb = new StringBuilder("""<?xml version="1.0"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>""");
            var rels = new StringBuilder("""<?xml version="1.0"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""");
            for (var i = 0; i < sheets.Length; i++)
            {
                wb.Append($"""<sheet name="{SecurityElement.Escape(sheets[i].Sheet)}" sheetId="{i + 1}" r:id="rId{i + 1}"/>""");
                rels.Append($"""<Relationship Id="rId{i + 1}" Type="worksheet" Target="worksheets/sheet{i + 1}.xml"/>""");
                var sheet = new StringBuilder("""<?xml version="1.0"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
                foreach (var row in sheets[i].Cells.GroupBy(c => new string(c.Ref.SkipWhile(char.IsLetter).ToArray())))
                {
                    sheet.Append($"""<row r="{row.Key}">""");
                    foreach (var (reference, value) in row)
                    {
                        sheet.Append($"""<c r="{reference}" t="inlineStr"><is><t>{SecurityElement.Escape(value)}</t></is></c>""");
                    }
                    sheet.Append("</row>");
                }
                sheet.Append("</sheetData></worksheet>");
                Write(zip, $"xl/worksheets/sheet{i + 1}.xml", sheet.ToString());
            }
            Write(zip, "xl/workbook.xml", wb.Append("</sheets></workbook>").ToString());
            Write(zip, "xl/_rels/workbook.xml.rels", rels.Append("</Relationships>").ToString());
        }
        ms.Position = 0;
        return ms;
    }

    private static void Write(ZipArchive zip, string path, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static readonly (string, string)[] Cover =
    [
        ("A1", "Az. 5Net:"), ("B1", "5N-123"),
        ("A2", "Az. SID:"), ("B2", "IR-9-2026-999"),
        ("A3", "Status:"), ("B3", "Ermittlung"),
        ("A4", "StA:"), ("B4", "Jane Doe"),
        ("A6", "A) Tatvorwurf"),
        ("A7", "Paragraf"), ("B7", "Gesetz"), ("C7", "Beschreibung"), ("D7", "Prüfung"),
        ("A8", "§ 1"), ("B8", "StGB"), ("C8", "Testvorwurf"), ("D8", "Offen"),
        ("A10", "C) Tathergang"),
        ("A11", "Erster Satz <b>fett</b>"),
        ("A13", "D) To-Do"),
        ("A14", "Erledigt"), ("B14", "Aufgabe"),
        ("A15", "x"), ("B15", "Zeugen laden"),
        ("A16", ""), ("B16", "Beweise sichern"),
        ("A18", "E) Notizen"),
        ("A19", "- Erste Notiz"),
    ];

    [Fact]
    public void Read_FindsCoverFields_ByTheirLabels()
    {
        using var file = Workbook(("Deckblatt", Cover), ("Zeugenliste", [("A1", "Name"), ("B1", "Telefon"), ("A2", "Max Muster"), ("B2", "555-1")]));

        var draft = InvestigationWorkbookImporter.Read(file);

        Assert.Equal("5N-123", draft.FiveNetCaseNumber);
        Assert.Equal("IR-9-2026-999", draft.SidCaseNumber);
        Assert.Equal(InvestigationStatus.Investigating, draft.Status);
        Assert.Equal("Jane Doe", draft.Prosecutor);
        Assert.Single(draft.Charges);
        Assert.Equal(2, draft.Tasks.Count);
        Assert.True(draft.Tasks[0].Done);
        Assert.Contains(draft.Notes, n => n == "Erste Notiz");
        Assert.Equal("Max Muster", Assert.Single(draft.People).Name);
    }

    [Fact]
    public void Read_EncodesTheNarrative()
    {
        using var file = Workbook(("Deckblatt", Cover));

        var draft = InvestigationWorkbookImporter.Read(file);

        Assert.Contains("&lt;b&gt;", draft.NarrativeHtml);
        Assert.DoesNotContain("<b>", draft.NarrativeHtml);
    }

    [Fact]
    public void Read_MissingSheets_BecomeWarnings()
    {
        using var file = Workbook(("Deckblatt", [("A1", "Az. SID:"), ("B1", "X-1")]));

        var draft = InvestigationWorkbookImporter.Read(file);

        Assert.Contains(draft.Warnings, w => w.Contains("Zeugenliste"));
        Assert.Contains(draft.Warnings, w => w.Contains("Tatvorwurf"));
        Assert.Equal("Ermittlung X-1", draft.Title);
    }

    [Fact]
    public void Read_NotAZip_IsRejected()
    {
        using var file = new MemoryStream(Encoding.UTF8.GetBytes("keine Excel-Datei"));
        Assert.Throws<InvalidDataException>(() => InvestigationWorkbookImporter.Read(file));
    }

    [Fact]
    public void Read_TooLarge_IsRejected()
    {
        using var file = new MemoryStream(new byte[XlsxReader.MaxFileBytes + 1]);
        var ex = Assert.Throws<InvalidDataException>(() => XlsxReader.Read(file));
        Assert.Contains("5 MB", ex.Message);
    }

    [Fact]
    public void Read_TooManyEntries_IsRejected()
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var i = 0; i < 301; i++)
            {
                zip.CreateEntry($"x/{i}.txt");
            }
        }
        ms.Position = 0;
        Assert.Throws<InvalidDataException>(() => XlsxReader.Read(ms));
    }

    [Fact]
    public void Read_HighlyCompressedEntry_HitsTheUnpackedCap()
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var stream = zip.CreateEntry("xl/workbook.xml", CompressionLevel.SmallestSize).Open();
            var chunk = new byte[1024 * 1024];
            for (var i = 0; i < 61; i++)
            {
                stream.Write(chunk);
            }
        }
        ms.Position = 0;
        Assert.True(ms.Length < XlsxReader.MaxFileBytes);
        Assert.Throws<InvalidDataException>(() => XlsxReader.Read(ms));
    }

    [Fact]
    public void Read_Dtd_IsRejected()
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "xl/workbook.xml", """<?xml version="1.0"?><!DOCTYPE x [<!ENTITY a "aaaa">]><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheets/></workbook>""");
        }
        ms.Position = 0;
        Assert.ThrowsAny<Exception>(() => XlsxReader.Read(ms));
    }
}
