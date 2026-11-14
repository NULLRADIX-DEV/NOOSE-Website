using System.Globalization;
using System.IO.Compression;
using System.Xml;

namespace NOOSE_Website.Services.Investigations;

/// <summary>One worksheet as text cells, addressed by 1-based row and column.</summary>
public sealed class XlsxSheet(string name, IReadOnlyDictionary<(int Row, int Col), string> cells)
{
    public string Name { get; } = name;
    public IReadOnlyDictionary<(int Row, int Col), string> Cells { get; } = cells;
    public int MaxRow { get; } = cells.Count == 0 ? 0 : cells.Keys.Max(k => k.Row);
    public int MaxCol { get; } = cells.Count == 0 ? 0 : cells.Keys.Max(k => k.Col);

    /// <summary>Trimmed text of a cell, empty when missing.</summary>
    public string this[int row, int col] => Cells.TryGetValue((row, col), out var v) ? v.Trim() : string.Empty;
}

/// <summary>
/// Minimal reader for the cached values of an .xlsx file: workbook, relations, shared strings, inline strings and
/// numbers. No formulas are evaluated and nothing is executed; size, entry count and unpacked size are capped so a
/// crafted archive cannot exhaust the server.
/// </summary>
public static class XlsxReader
{
    public const long MaxFileBytes = 5 * 1024 * 1024;
    private const int MaxEntries = 300;
    private const long MaxUnpackedBytes = 60L * 1024 * 1024;
    private const long MaxPartBytes = 8L * 1024 * 1024;
    private const int MaxCells = 200_000;

    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PkgRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>Reads every sheet; throws <see cref="InvalidDataException"/> for anything that is not a sane workbook.</summary>
    public static IReadOnlyList<XlsxSheet> Read(Stream input)
    {
        using var buffer = new MemoryStream();
        CopyCapped(input, buffer, MaxFileBytes, "Die Datei ist größer als 5 MB.");
        buffer.Position = 0;
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(buffer, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException)
        {
            throw new InvalidDataException("Das ist keine Excel-Datei (.xlsx).");
        }
        using (zip)
        {
            if (zip.Entries.Count > MaxEntries)
            {
                throw new InvalidDataException("Die Datei enthält zu viele Teile.");
            }
            long budget = MaxUnpackedBytes;
            var shared = ReadSharedStrings(zip, ref budget);
            var rels = ReadRelations(zip, ref budget);
            var workbook = Load(zip, "xl/workbook.xml", ref budget) ?? throw new InvalidDataException("Die Arbeitsmappe fehlt.");
            var ns = new XmlNamespaceManager(workbook.NameTable);
            ns.AddNamespace("m", MainNs);
            ns.AddNamespace("r", RelNs);
            var sheets = new List<XlsxSheet>();
            var cellCount = 0;
            foreach (XmlElement sheet in workbook.SelectNodes("/m:workbook/m:sheets/m:sheet", ns)!)
            {
                var name = sheet.GetAttribute("name");
                var rid = sheet.GetAttribute("id", RelNs);
                if (!rels.TryGetValue(rid, out var target))
                {
                    continue;
                }
                var path = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
                var doc = Load(zip, path, ref budget);
                if (doc is null)
                {
                    continue;
                }
                var cells = ReadCells(doc, shared, ref cellCount);
                sheets.Add(new XlsxSheet(name, cells));
            }
            return sheets;
        }
    }

    private static Dictionary<(int, int), string> ReadCells(XmlDocument doc, List<string> shared, ref int cellCount)
    {
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("m", MainNs);
        var cells = new Dictionary<(int, int), string>();
        foreach (XmlElement c in doc.SelectNodes("//m:sheetData/m:row/m:c", ns)!)
        {
            if (++cellCount > MaxCells)
            {
                throw new InvalidDataException("Die Datei enthält zu viele Zellen.");
            }
            var reference = c.GetAttribute("r");
            if (!TryParseReference(reference, out var row, out var col))
            {
                continue;
            }
            var type = c.GetAttribute("t");
            string? value = type switch
            {
                "s" => int.TryParse(c.SelectSingleNode("m:v", ns)?.InnerText, out var i) && i >= 0 && i < shared.Count ? shared[i] : null,
                "inlineStr" => string.Concat(c.SelectNodes(".//m:t", ns)!.Cast<XmlNode>().Select(t => t.InnerText)),
                _ => c.SelectSingleNode("m:v", ns)?.InnerText,
            };
            if (!string.IsNullOrEmpty(value))
            {
                cells[(row, col)] = value;
            }
        }
        return cells;
    }

    private static List<string> ReadSharedStrings(ZipArchive zip, ref long budget)
    {
        var doc = Load(zip, "xl/sharedStrings.xml", ref budget);
        var list = new List<string>();
        if (doc is null)
        {
            return list;
        }
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("m", MainNs);
        foreach (XmlNode si in doc.SelectNodes("/m:sst/m:si", ns)!)
        {
            // rich text runs: plain t and r/t, never phonetic hints
            list.Add(string.Concat(si.SelectNodes("m:t | m:r/m:t", ns)!.Cast<XmlNode>().Select(t => t.InnerText)));
        }
        return list;
    }

    private static Dictionary<string, string> ReadRelations(ZipArchive zip, ref long budget)
    {
        var doc = Load(zip, "xl/_rels/workbook.xml.rels", ref budget) ?? throw new InvalidDataException("Die Arbeitsmappe ist unvollständig.");
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("p", PkgRelNs);
        var map = new Dictionary<string, string>();
        foreach (XmlElement rel in doc.SelectNodes("/p:Relationships/p:Relationship", ns)!)
        {
            map[rel.GetAttribute("Id")] = rel.GetAttribute("Target");
        }
        return map;
    }

    /// <summary>Loads one XML part within the unpacked budget; DTDs and external entities are refused.</summary>
    private static XmlDocument? Load(ZipArchive zip, string path, ref long budget)
    {
        var entry = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, path, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return null;
        }
        using var unpacked = new MemoryStream();
        using (var stream = entry.Open())
        {
            // one part is parsed into memory whole, so it gets its own small cap besides the file's budget
            CopyCapped(stream, unpacked, Math.Min(budget, MaxPartBytes), "Die Datei ist entpackt zu groß.");
        }
        budget -= unpacked.Length;
        unpacked.Position = 0;
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 0 };
        using var reader = XmlReader.Create(unpacked, settings);
        var doc = new XmlDocument { XmlResolver = null };
        try
        {
            doc.Load(reader);
        }
        catch (XmlException)
        {
            throw new InvalidDataException("Die Excel-Datei ist beschädigt.");
        }
        return doc;
    }

    private static void CopyCapped(Stream from, Stream to, long cap, string message)
    {
        var chunk = new byte[81920];
        long total = 0;
        int read;
        while ((read = from.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += read;
            if (total > cap)
            {
                throw new InvalidDataException(message);
            }
            to.Write(chunk, 0, read);
        }
    }

    /// <summary>"B12" → row 12, column 2.</summary>
    public static bool TryParseReference(string reference, out int row, out int col)
    {
        row = 0;
        col = 0;
        var i = 0;
        while (i < reference.Length && char.IsLetter(reference[i]))
        {
            col = col * 26 + (char.ToUpperInvariant(reference[i]) - 'A' + 1);
            i++;
        }
        return col > 0 && int.TryParse(reference.AsSpan(i), NumberStyles.None, CultureInfo.InvariantCulture, out row) && row > 0;
    }
}
