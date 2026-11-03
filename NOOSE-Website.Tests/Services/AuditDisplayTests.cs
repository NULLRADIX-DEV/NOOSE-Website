using System.Text.RegularExpressions;
using NOOSE_Website.Data.Entities.Appointments;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

public class AuditDisplayTests
{
    private const string Dash = "—"; // em dash used for empty/unknown

    // Parses a single-field payload and returns the one produced change.
    private static AuditDisplay.FieldChange Single(string json)
    {
        var result = AuditDisplay.Parse(json);
        Assert.Single(result);
        return result[0];
    }

    // ---- empty / null / malformed input ------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    [InlineData("null")]                       // deserializes to null dictionary
    [InlineData("{")]                          // truncated -> JsonException
    [InlineData("not json at all")]            // garbage -> JsonException
    [InlineData("[\"a\",\"b\"]")]              // top-level array, not a dictionary
    [InlineData("{\"Name\":\"scalar\"}")]     // value not an array -> JsonException
    [InlineData("{\"Name\":123}")]            // value not an array -> JsonException
    [InlineData("{\"Name\":true}")]           // value not an array -> JsonException
    [InlineData("{}")]                         // empty object -> empty list
    public void Parse_EmptyOrMalformed_ReturnsEmpty(string? json)
    {
        var result = AuditDisplay.Parse(json);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public void Parse_Null_ReturnsNonNullEmptyList()
    {
        IReadOnlyList<AuditDisplay.FieldChange> result = AuditDisplay.Parse(null);

        Assert.NotNull(result);
        Assert.Equal(0, result.Count);
    }

    // ---- happy path --------------------------------------------------------

    [Fact]
    public void Parse_SimpleStringChange_ReturnsSingleFieldChange()
    {
        var change = Single("""{"Name":["Old","New"]}""");

        Assert.Equal("Name", change.Field);
        Assert.Equal("Old", change.Alt);
        Assert.Equal("New", change.New);
    }

    // ---- diff semantics: changed / added / removed / empty -----------------

    [Theory]
    [InlineData("""{"Name":["a","b"]}""", "a", "b")]     // changed: both present
    [InlineData("""{"Name":[null,"b"]}""", "—", "b")] // added: old null -> new value
    [InlineData("""{"Name":["a",null]}""", "a", "—")] // removed: old value -> new null
    [InlineData("""{"Name":[null,null]}""", "—", "—")] // both null
    [InlineData("""{"Name":["",""]}""", "—", "—")]     // both empty string
    [InlineData("""{"Name":[]}""", "—", "—")]          // no values at all
    [InlineData("""{"Name":["only"]}""", "only", "—")]      // single value -> new missing
    [InlineData("""{"Name":["a","b","c"]}""", "a", "b")]         // extra elements ignored
    public void Parse_DiffPairs_ProducesAltAndNew(string json, string expectedAlt, string expectedNew)
    {
        var change = Single(json);

        Assert.Equal(expectedAlt, change.Alt);
        Assert.Equal(expectedNew, change.New);
    }

    // ---- boolean values ----------------------------------------------------

    [Theory]
    [InlineData("""{"IstVerschlusssache":[false,true]}""", "Nein", "Ja")]
    [InlineData("""{"IstVerschlusssache":[true,false]}""", "Ja", "Nein")]
    public void Parse_BooleanValues_MapToJaNein(string json, string expectedAlt, string expectedNew)
    {
        var change = Single(json);

        Assert.Equal("Verschlusssache", change.Field);
        Assert.Equal(expectedAlt, change.Alt);
        Assert.Equal(expectedNew, change.New);
    }

    // ---- hidden / meta fields are skipped ----------------------------------

    [Theory]
    [InlineData("ErstelltAm")]
    [InlineData("ErstelltVonId")]
    [InlineData("GeaendertAm")]
    [InlineData("GeaendertVonId")]
    [InlineData("GeloeschtAm")]
    [InlineData("GeloeschtVonId")]
    [InlineData("IstGeloescht")]
    [InlineData("PersonId")]
    [InlineData("FraktionId")]
    [InlineData("PersonengruppeId")]
    [InlineData("AgentId")]
    [InlineData("ProtokollHtml")]
    [InlineData("NotizHtml")]
    [InlineData("BesprechungId")]
    [InlineData("AnwesenheitAbgeschlossenAm")]
    [InlineData("ErinnerungGesendetAm")]
    [InlineData("MinutesHtml")]
    [InlineData("NotesHtml")]
    [InlineData("ContentHtml")]
    [InlineData("SummaryHtml")]
    [InlineData("TldrHtml")]
    [InlineData("DetailJson")]
    [InlineData("LayoutJson")]
    [InlineData("ThreatDetailJson")]
    [InlineData("CreatedAt")]
    [InlineData("CreatedById")]
    [InlineData("ModifiedAt")]
    [InlineData("ModifiedById")]
    [InlineData("DeletedAt")]
    [InlineData("DeletedById")]
    [InlineData("IsDeleted")]
    [InlineData("NotifiedAt")]
    [InlineData("MeetingId")]
    [InlineData("AttendanceClosedAt")]
    [InlineData("ReminderSentAt")]
    [InlineData("PreviousMeetingId")]
    [InlineData("CarriedFromItemId")]
    [InlineData("AcknowledgedById")]
    [InlineData("DoneById")]
    [InlineData("MarkedById")]
    public void Parse_HiddenField_IsSkipped(string field)
    {
        var json = $"{{\"{field}\":[\"a\",\"b\"]}}";

        Assert.Empty(AuditDisplay.Parse(json));
    }

    [Fact]
    public void Parse_HiddenAndVisibleMixed_KeepsOnlyVisible()
    {
        var change = Single("""{"GeaendertAm":["x","y"],"Name":["a","b"]}""");

        Assert.Equal("Name", change.Field);
        Assert.Equal("a", change.Alt);
        Assert.Equal("b", change.New);
    }

    // ---- label mapping -----------------------------------------------------

    [Theory]
    [InlineData("Name", "Name")]
    [InlineData("Beschreibung", "Beschreibung")]
    [InlineData("IstVerschlusssache", "Verschlusssache")]
    [InlineData("TotBis", "Tot-Fenster")]
    [InlineData("OrgTyp", "Verknüpfte Org (Typ)")]
    [InlineData("OrgId", "Verknüpfte Org")]
    [InlineData("Ausgang", "Maßnahme-Ausgang")]
    [InlineData("BisDatum", "Bis (einschließlich)")]
    [InlineData("VonDatum", "Von")]
    [InlineData("KenntnisGenommenVonName", "Kenntnis genommen von")]
    [InlineData("Sortierung", "Reihenfolge")]
    [InlineData("AgentCodename", "Agent")]
    [InlineData("TotallyUnknownField", "TotallyUnknownField")] // fallback: field name itself
    public void Parse_FieldLabel_MapsOrFallsBack(string field, string expectedLabel)
    {
        // string values that are neither dates nor enums so label is the only thing exercised
        var json = $"{{\"{field}\":[\"foo\",\"bar\"]}}";

        var change = Single(json);

        Assert.Equal(expectedLabel, change.Field);
    }

    // ---- enum formatting: every arm + out-of-range fallback ----------------

    [Theory]
    // Einstufung -> Classification
    [InlineData("Einstufung", 0, "Unbekannt")]
    [InlineData("Einstufung", 1, "Prüffall")]
    [InlineData("Einstufung", 2, "Verdachtsfall")]
    [InlineData("Einstufung", 3, "Gesichert staatsgefährdend")]
    [InlineData("Einstufung", 99, "—")]
    [InlineData("Einstufung", -1, "—")]
    // Lebensstatus -> LifeStatus
    [InlineData("Lebensstatus", 0, "Lebend")]
    [InlineData("Lebensstatus", 1, "Tot")]
    [InlineData("Lebensstatus", 2, "Flüchtig")]
    [InlineData("Lebensstatus", 5, "—")]
    // Ausgang -> MeasureOutcome
    [InlineData("Ausgang", 0, "Läuft noch")]
    [InlineData("Ausgang", 1, "Offiziell entlassen")]
    [InlineData("Ausgang", 2, "Amnestie-Spritze")]
    [InlineData("Ausgang", 3, "Erschossen")]
    [InlineData("Ausgang", 9, "—")]
    // Abmeldegrund -> AbsenceCategory
    [InlineData("Abmeldegrund", 0, "Urlaub")]
    [InlineData("Abmeldegrund", 1, "Arbeit (RL)")]
    [InlineData("Abmeldegrund", 2, "Krank")]
    [InlineData("Abmeldegrund", 3, "RP-Pause")]
    [InlineData("Abmeldegrund", 4, "Sonstiges")]
    [InlineData("Abmeldegrund", 7, "—")]
    // Herkunft -> MeetingAbsenceOrigin
    [InlineData("Herkunft", 0, "—")]
    [InlineData("Herkunft", 1, "Abmeldung (Zeitraum)")]
    [InlineData("Herkunft", 2, "Abmeldung (Besprechung)")]
    [InlineData("Herkunft", 3, "Manuell erfasst")]
    [InlineData("Herkunft", 8, "—")]
    // non-enum numeric fields -> raw ToString()
    [InlineData("Sortierung", 3, "3")]
    [InlineData("Tage", 0, "0")]
    [InlineData("GeschaetzteMitgliederzahl", 250, "250")]
    [InlineData("SomethingUnmapped", 42, "42")]
    public void Parse_NumericValue_FormatsEnumOrPlainNumber(string field, int value, string expectedAlt)
    {
        var json = $"{{\"{field}\":[{value}]}}";

        var change = Single(json);

        Assert.Equal(expectedAlt, change.Alt);
        Assert.Equal(Dash, change.New); // single-element array -> new missing
    }

    // ---- numbers that are not int32 fall through to raw text ---------------

    [Theory]
    [InlineData("""{"Tage":[1.5]}""", "1.5")]
    [InlineData("""{"Tage":[-2.5]}""", "-2.5")]
    [InlineData("""{"Tage":[9999999999]}""", "9999999999")] // exceeds Int32
    [InlineData("""{"Einstufung":[2.7]}""", "2.7")]          // enum field but non-int -> raw
    public void Parse_NonIntegerNumber_ReturnsRawText(string json, string expectedAlt)
    {
        Assert.Equal(expectedAlt, Single(json).Alt);
    }

    // ---- DateOnly fields (timezone-independent) ----------------------------

    [Theory]
    [InlineData("""{"VonDatum":["2026-07-21"]}""", "21.07.2026")]
    [InlineData("""{"BisDatum":["2026-01-05"]}""", "05.01.2026")]
    [InlineData("""{"VonDatum":["1999-12-31"]}""", "31.12.1999")]
    public void Parse_DateOnlyField_FormatsGermanDate(string json, string expected)
    {
        Assert.Equal(expected, Single(json).Alt);
    }

    [Theory]
    [InlineData("""{"VonDatum":["not-a-date"]}""", "not-a-date")]
    [InlineData("""{"BisDatum":["nonsense"]}""", "nonsense")]
    public void Parse_DateOnlyField_InvalidValue_ReturnsRawString(string json, string expected)
    {
        Assert.Equal(expected, Single(json).Alt);
    }

    // ---- DateTime fields: assert format shape (timezone-independent) -------

    [Theory]
    [InlineData("""{"Zeitpunkt":["2026-07-21T14:30:00Z"]}""")]
    [InlineData("""{"TotBis":["2026-07-21T00:00:00Z"]}""")]
    [InlineData("""{"Beginn":["2026-07-21T09:15:00Z"]}""")]
    [InlineData("""{"Ende":["2026-07-21T18:45:00Z"]}""")]
    public void Parse_DateTimeField_FormatsAsDateAndTime(string json)
    {
        var alt = Single(json).Alt;

        // dd.MM.yyyy HH:mm regardless of local offset
        Assert.Matches(new Regex(@"^\d{2}\.\d{2}\.\d{4} \d{2}:\d{2}$"), alt);
    }

    [Theory]
    [InlineData("""{"Zeitpunkt":["garbage"]}""", "garbage")]
    [InlineData("""{"Beginn":["not-a-timestamp"]}""", "not-a-timestamp")]
    public void Parse_DateTimeField_InvalidValue_ReturnsRawString(string json, string expected)
    {
        Assert.Equal(expected, Single(json).Alt);
    }

    [Fact]
    public void Parse_DateLikeStringOnNonDateField_IsNotReformatted()
    {
        // Name is not a date field, so an ISO-looking string is returned verbatim
        var change = Single("""{"Name":["2026-07-21"]}""");

        Assert.Equal("2026-07-21", change.Alt);
    }

    // ---- default arm: array/object values render as raw JSON ---------------

    [Fact]
    public void Parse_ArrayValue_RendersRawJsonText()
    {
        // values[0] is the nested array [1,2]; values[1] is the number 3
        var change = Single("""{"Xyz":[[1,2],3]}""");

        Assert.Equal("[1,2]", change.Alt);
        Assert.Equal("3", change.New);
    }

    [Fact]
    public void Parse_ObjectValue_RendersRawJsonText()
    {
        var change = Single("""{"Xyz":[{"a":1}]}""");

        Assert.Equal("""{"a":1}""", change.Alt);
        Assert.Equal(Dash, change.New);
    }

    // ---- multiple visible fields (order-independent) -----------------------

    [Fact]
    public void Parse_MultipleVisibleFields_ReturnsAllWithLabels()
    {
        var result = AuditDisplay.Parse(
            """{"Name":["a","b"],"Beschreibung":["c","d"],"GeaendertAm":["x","y"]}""");

        Assert.Equal(2, result.Count); // hidden GeaendertAm dropped

        var name = Assert.Single(result, c => c.Field == "Name");
        Assert.Equal("a", name.Alt);
        Assert.Equal("b", name.New);

        var desc = Assert.Single(result, c => c.Field == "Beschreibung");
        Assert.Equal("c", desc.Alt);
        Assert.Equal("d", desc.New);
    }

    // ---- CLR field names (what the interceptor actually stamps) -------------

    [Theory]
    [InlineData("IsHRBClassified", "VS-Stufe Administration")]
    [InlineData("IsTRUClassified", "VS-Stufe HRT")]
    [InlineData("IsClassified", "Verschlusssache")]
    [InlineData("CaseNumber", "Aktenzeichen")]
    [InlineData("Title", "Titel")]
    [InlineData("Description", "Beschreibung")]
    public void Parse_ClrFieldName_GetsGermanLabel(string field, string expected)
    {
        var change = Single($$"""{"{{field}}":["a","b"]}""");

        Assert.Equal(expected, change.Field);
    }

    [Fact]
    public void Parse_ClrEnumField_IsNamed()
    {
        var change = Single("""{"Classification":[0,2]}""");

        Assert.Equal("Einstufung", change.Field);
        Assert.NotEqual("2", change.New);
    }

    // ---- value clipping -----------------------------------------------------

    [Fact]
    public void Parse_WithMaxValueLength_ClipsLongValues()
    {
        var longText = new string('x', 300);

        var change = AuditDisplay.Parse($$"""{"Description":["{{longText}}","b"]}""", maxValueLength: 20).Single();

        Assert.Equal(21, change.Alt.Length); // 20 chars plus the ellipsis
        Assert.EndsWith("…", change.Alt);
        Assert.Equal("b", change.New);
    }

    [Fact]
    public void Parse_WithoutMaxValueLength_KeepsFullValue()
    {
        var longText = new string('x', 300);

        var change = Single($$"""{"Description":["{{longText}}","b"]}""");

        Assert.Equal(300, change.Alt.Length);
    }

    // ---- the entity type disambiguates shared property names ---------------

    [Fact]
    public void Category_WithoutAnEntityType_StaysTheAbsenceReason()
    {
        // the per-record timelines pass none, and only an absence reaches them
        var change = Assert.Single(AuditDisplay.Parse("""{"Category":[0,2]}"""));

        Assert.Equal("Kategorie", change.Field);
        Assert.Equal("Urlaub", change.Alt);
        Assert.Equal("Krank", change.New);
    }

    [Fact]
    public void ATicketCategoryIsNotReadAsAnAbsenceReason()
    {
        var change = Assert.Single(
            AuditDisplay.Parse("""{"Category":[1,3]}""", entityType: nameof(Ticket)));

        Assert.Equal("Anzeige", change.Alt);
        Assert.Equal("Beschwerde", change.New);
    }

    [Fact]
    public void AnAppointmentCategoryIsNotReadAsAnAbsenceReason()
    {
        var change = Assert.Single(
            AuditDisplay.Parse("""{"Category":[0,2]}""", entityType: nameof(Appointment)));

        Assert.Equal("Gerichtstermin", change.Alt);
        Assert.Equal("Einsatz", change.New);
    }

    [Fact]
    public void ATicketStatusAndClosingReasonAreNamed()
    {
        var changes = AuditDisplay.Parse(
            """{"Status":[1,3],"ClosingReason":[null,2]}""", entityType: nameof(Ticket));

        var status = changes.Single(c => c.Field == "Status");
        Assert.Equal("In Bearbeitung", status.Alt);
        Assert.Equal("Geschlossen", status.New);
        var reason = changes.Single(c => c.Field == "Abschlussgrund");
        Assert.Equal(Dash, reason.Alt);
        Assert.Equal("Spam", reason.New);
    }

    [Fact]
    public void AStatusOnAnotherRecordKeepsItsRawValue()
    {
        // Status is not a single axis either; naming it for every entity would be the same mistake
        var change = Assert.Single(AuditDisplay.Parse("""{"Status":[1,3]}""", entityType: "Person"));

        Assert.Equal("1", change.Alt);
        Assert.Equal("3", change.New);
    }

    [Fact]
    public void TheFollowupStampOfATicketStaysOutOfTheProtocol()
    {
        // hourly worker bookkeeping, exactly like NotifiedAt on a followup
        Assert.Empty(AuditDisplay.Parse("""{"NudgedAt":[null,"2026-09-07T10:00:00Z"]}"""));
    }

    // ---- FieldChange record shape ------------------------------------------

    [Fact]
    public void FieldChange_ExposesPositionalMembers()
    {
        var fc = new AuditDisplay.FieldChange("F", "A", "N");

        Assert.Equal("F", fc.Field);
        Assert.Equal("A", fc.Alt);
        Assert.Equal("N", fc.New);
        Assert.Equal(new AuditDisplay.FieldChange("F", "A", "N"), fc); // value equality
    }
}
