using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Investigations;

namespace NOOSE_Website.Services.Investigations;

/// <summary>What an import read from a DoJ workbook; nothing is saved until the user confirms.</summary>
public sealed class InvestigationImportDraft
{
    public string Title { get; set; } = string.Empty;
    public string? SidCaseNumber { get; set; }
    public string? FiveNetCaseNumber { get; set; }
    public InvestigationStatus Status { get; set; }
    public DateTime? OpenedAt { get; set; }
    public string? Prosecutor { get; set; }
    public string? Indictment { get; set; }
    public string? NarrativeHtml { get; set; }
    public List<InvestigationChargeInput> Charges { get; } = new();
    public List<InvestigationPersonInput> People { get; } = new();
    public List<InvestigationEvidenceInput> Evidence { get; } = new();
    public List<InvestigationInterrogationInput> Interrogations { get; } = new();
    public List<(string Text, bool Done)> Tasks { get; } = new();
    public List<string> Notes { get; } = new();
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// Reads the DoJ investigation workbook (cover sheet, witness list, evidence log, interrogation of the accused,
/// witness interrogations). Fields are found by their labels and section headings, not by fixed cells, so a moved
/// column still reads; whatever cannot be read becomes a warning instead of a guess.
/// </summary>
public static partial class InvestigationWorkbookImporter
{
    public static InvestigationImportDraft Read(Stream input)
    {
        var sheets = XlsxReader.Read(input);
        var draft = new InvestigationImportDraft();
        var cover = Find(sheets, "Deckblatt") ?? sheets.FirstOrDefault();
        if (cover is null)
        {
            throw new InvalidDataException("Die Datei enthält kein Tabellenblatt.");
        }
        ReadCover(cover, draft);
        if (Find(sheets, "Zeugenliste") is { } witnesses)
        {
            ReadWitnesses(witnesses, draft);
        }
        else
        {
            draft.Warnings.Add("Kein Blatt „Zeugenliste“ gefunden.");
        }
        if (Find(sheets, "Beweis") is { } evidence)
        {
            ReadEvidence(evidence, draft);
        }
        if (Find(sheets, "Vernehmung Beschuldigter") is { } accused)
        {
            ReadAccused(accused, draft);
        }
        if (Find(sheets, "Zeugenvernehmungen") is { } statements)
        {
            ReadStatements(statements, draft);
        }
        if (string.IsNullOrWhiteSpace(draft.Title))
        {
            draft.Title = draft.SidCaseNumber is { } sid ? $"Ermittlung {sid}" : "Übernommene Ermittlung";
        }
        return draft;
    }

    private static XlsxSheet? Find(IReadOnlyList<XlsxSheet> sheets, string name)
        => sheets.FirstOrDefault(s => s.Name.Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
           ?? sheets.FirstOrDefault(s => s.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

    // ---- cover sheet ----

    private static void ReadCover(XlsxSheet s, InvestigationImportDraft d)
    {
        d.FiveNetCaseNumber = Label(s, "Az. 5Net");
        d.SidCaseNumber = Label(s, "Az. SID");
        d.Status = ParseStatus(Label(s, "Status"));
        d.OpenedAt = ParseDate(Label(s, "Datum"));
        d.Prosecutor = Label(s, "StA");
        d.Indictment = Label(s, "Anklage");
        if (d.Prosecutor is { } sta)
        {
            d.Notes.Add($"Staatsanwalt laut Excel-Akte: {sta}. Bitte als Beteiligten mit der Rolle Staatsanwalt eintragen.");
        }

        var charges = Heading(s, "A)", "Tatvorwurf");
        if (charges is (int cRow, int cCol))
        {
            for (var r = cRow + 2; r <= s.MaxRow && !IsHeading(s[r, cCol]); r++)
            {
                var paragraph = s[r, cCol];
                if (paragraph.Length == 0)
                {
                    break;
                }
                d.Charges.Add(new InvestigationChargeInput(null, null, paragraph, s[r, cCol + 1], s[r, cCol + 2], ParseCheck(s[r, cCol + 3])));
            }
        }
        else
        {
            d.Warnings.Add("Abschnitt „A) Tatvorwurf“ nicht gefunden.");
        }

        if (Heading(s, "C)", "Tathergang") is (int nRow, int nCol))
        {
            var lines = new List<string>();
            for (var r = nRow + 1; r <= s.MaxRow && !IsHeading(s[r, nCol]); r++)
            {
                var line = string.Join(" ", Enumerable.Range(1, s.MaxCol).Select(c => s[r, c]).Where(v => v.Length > 0));
                if (line.Length > 0)
                {
                    lines.Add(line);
                }
            }
            d.NarrativeHtml = lines.Count == 0 ? null : string.Concat(lines.Select(l => $"<p>{WebUtility.HtmlEncode(l)}</p>"));
        }

        if (Heading(s, "D)", "To-Do") is (int tRow, int tCol))
        {
            for (var r = tRow + 2; r <= s.MaxRow && !IsHeading(s[r, tCol]); r++)
            {
                var text = s[r, tCol + 1];
                if (text.Length > 0)
                {
                    d.Tasks.Add((text, s[r, tCol] is "1" or "x" or "X" or "ja" or "TRUE" or "true"));
                }
            }
        }

        if (Heading(s, "E)", "Notizen") is (int eRow, int eCol))
        {
            for (var r = eRow + 1; r <= s.MaxRow; r++)
            {
                var text = s[r, eCol].TrimStart('-', ' ', '–');
                if (text.Length > 0 && !text.Equals("Notiz", StringComparison.OrdinalIgnoreCase))
                {
                    d.Notes.Add(text);
                }
            }
        }
    }

    // ---- other sheets ----

    private static void ReadWitnesses(XlsxSheet s, InvestigationImportDraft d)
    {
        if (Header(s, "Name") is not (int row, int nameCol))
        {
            d.Warnings.Add("In der Zeugenliste fehlt die Spalte „Name“.");
            return;
        }
        var phone = Column(s, row, "Telefon");
        var role = Column(s, row, "Rolle");
        var status = Column(s, row, "Status");
        for (var r = row + 1; r <= s.MaxRow; r++)
        {
            var name = s[r, nameCol];
            if (name.Length == 0)
            {
                continue;
            }
            d.People.Add(new InvestigationPersonInput(null, name, null, phone is { } p ? Null(s[r, p]) : null,
                role is { } ro && s[r, ro].Length > 0 ? s[r, ro] : "Zeuge", ParsePersonStatus(status is { } st ? s[r, st] : null), null));
        }
    }

    private static void ReadEvidence(XlsxSheet s, InvestigationImportDraft d)
    {
        if (Header(s, "Beschreibung") is not (int row, _))
        {
            return;
        }
        var name = Column(s, row, "Name");
        var description = Column(s, row, "Beschreibung");
        var owner = Column(s, row, "Besitzer");
        var officer = Column(s, row, "Beamter");
        var status = Column(s, row, "Status");
        var date = Column(s, row, "Datum");
        for (var r = row + 1; r <= s.MaxRow; r++)
        {
            var title = name is { } n ? s[r, n] : string.Empty;
            if (title.Length == 0)
            {
                continue;
            }
            d.Evidence.Add(new InvestigationEvidenceInput(null, title, description is { } de ? Null(s[r, de]) : null,
                owner is { } o ? Null(s[r, o]) : null, officer is { } of ? Null(s[r, of]) : null,
                ParseEvidenceStatus(status is { } st ? s[r, st] : null), date is { } da ? ParseDate(s[r, da]) : null));
        }
    }

    private static void ReadAccused(XlsxSheet s, InvestigationImportDraft d)
    {
        var name = Label(s, "Name");
        if (name is null)
        {
            d.Warnings.Add("In „Vernehmung Beschuldigter“ fehlt der Name.");
            return;
        }
        var entries = new List<InvestigationQa>();
        if (Header(s, "Frage") is (int row, int qCol))
        {
            var aCol = Column(s, row, "Antwort") ?? qCol + 3;
            for (var r = row + 1; r <= s.MaxRow; r++)
            {
                var q = s[r, qCol];
                var a = s[r, aCol];
                if (q.Length > 0 || a.Length > 0)
                {
                    entries.Add(new InvestigationQa(q, a));
                }
            }
        }
        var held = Combine(ParseDate(Label(s, "Datum")), Label(s, "Uhrzeit"));
        d.Interrogations.Add(new InvestigationInterrogationInput(null, null, name, Label(s, "Rolle") ?? "Beschuldigter",
            Label(s, "Vorladung"), held, Label(s, "Beamte"), Label(s, "StA"), null, null, true, entries));
    }

    private static void ReadStatements(XlsxSheet s, InvestigationImportDraft d)
    {
        if (Header(s, "Zeuge") is not (int row, int nameCol))
        {
            return;
        }
        var refCol = Column(s, row, "Protokoll") ?? nameCol + 3;
        var summons = Label(s, "Vorladung");
        var officers = Label(s, "Beamte");
        var sta = Label(s, "StA");
        for (var r = row + 1; r <= s.MaxRow; r++)
        {
            var name = s[r, nameCol];
            if (name.Length == 0)
            {
                continue;
            }
            var reference = Null(s[r, refCol]);
            var usable = reference is null || !reference.Contains("nicht verwertbar", StringComparison.OrdinalIgnoreCase);
            d.Interrogations.Add(new InvestigationInterrogationInput(null, null, name, "Zeuge", summons, null, officers, sta,
                usable ? null : reference, usable ? reference : null, usable, Array.Empty<InvestigationQa>()));
        }
    }

    // ---- lookups ----

    /// <summary>Value right of a label cell ("Az. SID:"), stopping at the next label.</summary>
    internal static string? Label(XlsxSheet s, string label)
    {
        foreach (var ((row, col), raw) in s.Cells.OrderBy(c => c.Key.Row).ThenBy(c => c.Key.Col))
        {
            if (!raw.Trim().TrimEnd(':').Trim().Equals(label, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            for (var c = col + 1; c <= s.MaxCol; c++)
            {
                var value = s[row, c];
                if (value.Length == 0)
                {
                    continue;
                }
                return value.EndsWith(':') ? null : value;
            }
            return null;
        }
        return null;
    }

    /// <summary>Position of a section heading like "A) Tatvorwurf".</summary>
    private static (int Row, int Col)? Heading(XlsxSheet s, string marker, string word)
        => s.Cells.Where(c => c.Value.Trim().StartsWith(marker, StringComparison.OrdinalIgnoreCase) && c.Value.Contains(word, StringComparison.OrdinalIgnoreCase))
            .Select(c => ((int, int)?)c.Key).OrderBy(k => k!.Value.Item1).FirstOrDefault();

    private static bool IsHeading(string value) => HeadingPattern().IsMatch(value);

    [GeneratedRegex("^[A-Z]\\)\\s")]
    private static partial Regex HeadingPattern();

    /// <summary>First cell whose text is exactly the header.</summary>
    private static (int Row, int Col)? Header(XlsxSheet s, string header)
        => s.Cells.Where(c => c.Value.Trim().Equals(header, StringComparison.OrdinalIgnoreCase))
            .Select(c => ((int, int)?)c.Key).OrderBy(k => k!.Value.Item1).ThenBy(k => k!.Value.Item2).FirstOrDefault();

    private static int? Column(XlsxSheet s, int row, string header)
    {
        for (var c = 1; c <= s.MaxCol; c++)
        {
            if (s[row, c].Equals(header, StringComparison.OrdinalIgnoreCase))
            {
                return c;
            }
        }
        return null;
    }

    private static string? Null(string value) => value.Length == 0 ? null : value;

    // ---- value parsing ----

    /// <summary>Excel day numbers and German dates.</summary>
    internal static DateTime? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Trim() == "-")
        {
            return null;
        }
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is > 20000 and < 80000)
        {
            return DateTime.SpecifyKind(DateTime.FromOADate(serial), DateTimeKind.Local).ToUniversalTime();
        }
        return DateTime.TryParse(raw, CultureInfo.GetCultureInfo("de-DE"), DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed.ToUniversalTime()
            : null;
    }

    private static DateTime? Combine(DateTime? date, string? time)
    {
        if (date is not { } d)
        {
            return null;
        }
        var match = TimePattern().Match(time ?? string.Empty);
        if (!match.Success)
        {
            return d;
        }
        var local = d.ToLocalTime().Date.AddHours(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
            .AddMinutes(int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
        return local.ToUniversalTime();
    }

    [GeneratedRegex("(\\d{1,2}):(\\d{2})")]
    private static partial Regex TimePattern();

    internal static InvestigationStatus ParseStatus(string? raw) => (raw ?? string.Empty).ToLowerInvariant() switch
    {
        var v when v.Contains("anklage") => InvestigationStatus.Indicted,
        var v when v.Contains("verhandlung") => InvestigationStatus.Trial,
        var v when v.Contains("eingestellt") => InvestigationStatus.Dismissed,
        var v when v.Contains("abgeschlossen") || v.Contains("geschlossen") => InvestigationStatus.Closed,
        _ => InvestigationStatus.Investigating,
    };

    internal static InvestigationChargeCheck ParseCheck(string? raw) => (raw ?? string.Empty).ToLowerInvariant() switch
    {
        var v when v.Contains("nicht") => InvestigationChargeCheck.DoesNotApply,
        var v when v.Contains("trifft zu") || v == "ja" => InvestigationChargeCheck.Applies,
        _ => InvestigationChargeCheck.Open,
    };

    internal static InvestigationPersonStatus ParsePersonStatus(string? raw) => (raw ?? string.Empty).ToLowerInvariant() switch
    {
        var v when v.Contains("nicht verwertbar") => InvestigationPersonStatus.NotUsable,
        var v when v.Contains("aussage") => InvestigationPersonStatus.StatementTaken,
        var v when v.Contains("belehrt") => InvestigationPersonStatus.Cautioned,
        var v when v.Contains("kontakt") => InvestigationPersonStatus.Contacted,
        _ => InvestigationPersonStatus.Listed,
    };

    private static InvestigationEvidenceStatus ParseEvidenceStatus(string? raw) => (raw ?? string.Empty).ToLowerInvariant() switch
    {
        var v when v.Contains("auswertung") => InvestigationEvidenceStatus.UnderAnalysis,
        var v when v.Contains("ausgewertet") => InvestigationEvidenceStatus.Analysed,
        var v when v.Contains("heraus") || v.Contains("zurück") => InvestigationEvidenceStatus.Returned,
        var v when v.Contains("vernichtet") => InvestigationEvidenceStatus.Destroyed,
        _ => InvestigationEvidenceStatus.Secured,
    };
}
