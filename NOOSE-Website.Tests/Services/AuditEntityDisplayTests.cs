using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

public class AuditEntityDisplayTests
{
    [Theory]
    [InlineData("Person", "Person")]
    [InlineData("PersonDoc", "Personen-Dok")]
    [InlineData("Observation", "Observation")]
    [InlineData("PersonRelation", "Personen-Beziehung")]
    [InlineData("Faction", "Fraktion")]
    [InlineData("PersonGroup", "Personengruppe")]
    [InlineData("Party", "Partei")]
    [InlineData("Operation", "Operation")]
    [InlineData("AgentActivity", "Aktivität")]
    [InlineData("Taskforce", "Taskforce")]
    [InlineData("Case", "Vorgang")]
    [InlineData("Job", "Aufgabe")]
    [InlineData("Appointment", "Termin")]
    [InlineData("Document", "Dokument")]
    [InlineData("Law", "Gesetz")]
    [InlineData("Announcement", "Ankündigung")]
    [InlineData("Agent", "Agent")]
    [InlineData("Meeting", "Besprechung")]
    [InlineData("MeetingAgendaItem", "Tagesordnungspunkt")]
    [InlineData("MeetingAttendance", "Anwesenheit")]
    [InlineData("MeetingSignOff", "Abmeldung (Besprechung)")]
    [InlineData("Absence", "Abmeldung")]
    [InlineData("FirearmUse", "Schusswaffengebrauch")]
    [InlineData("SystemSetting", "Systemeinstellung")]
    [InlineData("BuergerProfil", "Bürgerkonto")]
    [InlineData("OeffentlichesModul", "Öffentliches Modul")]
    [InlineData("OeffentlicheSeite", "Öffentliche Seite")]
    [InlineData("OeffentlicheFahndung", "Öffentliche Ausschreibung")]
    [InlineData("PublicArea", "Öffentlicher Bereich")]
    [InlineData("Directive", "Dienstanweisung")]
    [InlineData("DirectiveVersion", "Fassung (Dienstanweisung)")]
    [InlineData("DirectiveAcknowledgment", "Kenntnisnahme (Dienstanweisung)")]
    public void Label_KnownType_ReturnsGermanLabel(string type, string expected)
    {
        Assert.Equal(expected, AuditEntityDisplay.Label(type));
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("person")]
    [InlineData("PERSON")]
    [InlineData("Foo")]
    [InlineData("SomeOtherEntity")]
    public void Label_UnknownType_ReturnsInputVerbatim(string type)
    {
        Assert.Equal(type, AuditEntityDisplay.Label(type));
    }

    [Fact]
    public void Label_EmptyString_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, AuditEntityDisplay.Label(string.Empty));
    }

    [Fact]
    public void Label_Whitespace_ReturnsWhitespaceVerbatim()
    {
        Assert.Equal("   ", AuditEntityDisplay.Label("   "));
    }

    [Fact]
    public void Label_LookupIsCaseSensitive()
    {
        // exact "Person" maps, differently-cased does not
        Assert.Equal("Person", AuditEntityDisplay.Label("Person"));
        Assert.Equal("person", AuditEntityDisplay.Label("person"));
    }

    [Theory]
    [InlineData("Person", "abc", "/personen/abc")]
    [InlineData("Faction", "abc", "/fraktionen/abc")]
    [InlineData("PersonGroup", "abc", "/personengruppen/abc")]
    [InlineData("Party", "abc", "/parteien/abc")]
    [InlineData("Operation", "abc", "/operationen/abc")]
    [InlineData("AgentActivity", "abc", "/aktivitaeten/abc")]
    [InlineData("Taskforce", "abc", "/taskforces/abc")]
    [InlineData("Case", "abc", "/vorgaenge/abc")]
    [InlineData("Job", "abc", "/aufgaben/abc")]
    [InlineData("Appointment", "abc", "/kalender/abc")]
    [InlineData("Document", "abc", "/dokumente/abc")]
    [InlineData("Law", "abc", "/gesetze/abc")]
    [InlineData("Agent", "abc", "/personal/abc")]
    [InlineData("Meeting", "abc", "/besprechungen/abc")]
    [InlineData("Directive", "abc", "/dienstanweisungen/abc")]
    public void Route_KnownRoutableType_ReturnsDeepLinkWithId(string type, string id, string expected)
    {
        Assert.Equal(expected, AuditEntityDisplay.Route(type, id));
    }

    [Fact]
    public void Route_Absence_ReturnsStaticOverviewIgnoringId()
    {
        Assert.Equal("/abmeldungen/uebersicht", AuditEntityDisplay.Route("Absence", "any-id"));
        Assert.Equal("/abmeldungen/uebersicht", AuditEntityDisplay.Route("Absence", string.Empty));
    }

    [Fact]
    public void Route_FirearmUse_ReturnsTheOverviewIgnoringId()
    {
        Assert.Equal("/schusswaffengebrauch", AuditEntityDisplay.Route("FirearmUse", "any-id"));
        Assert.Equal("/schusswaffengebrauch", AuditEntityDisplay.Route("FirearmUse", string.Empty));
    }

    [Theory]
    [InlineData("PersonDoc")]
    [InlineData("Observation")]
    [InlineData("PersonRelation")]
    [InlineData("AgentActivity_wrong")]
    [InlineData("Announcement")]
    [InlineData("MeetingAgendaItem")]
    [InlineData("MeetingAttendance")]
    [InlineData("MeetingSignOff")]
    [InlineData("DirectiveVersion")]
    [InlineData("DirectiveAcknowledgment")]
    [InlineData("Unknown")]
    [InlineData("")]
    [InlineData("person")]
    public void Route_ChildOrUnknownType_ReturnsNull(string type)
    {
        Assert.Null(AuditEntityDisplay.Route(type, "abc"));
    }

    [Fact]
    public void Route_RoutableTypeWithEmptyId_EmbedsEmptyId()
    {
        Assert.Equal("/personen/", AuditEntityDisplay.Route("Person", string.Empty));
    }

    [Fact]
    public void Route_LabelableButNonRoutableType_ReturnsNull()
    {
        // types that have a Label but no detail page
        Assert.Null(AuditEntityDisplay.Route("Announcement", "x"));
        Assert.Null(AuditEntityDisplay.Route("PersonDoc", "x"));
        // a system setting spans several settings tabs, so there is no single place to point at
        Assert.Null(AuditEntityDisplay.Route("SystemSetting", "x"));
    }

    [Theory]
    [InlineData("BuergerProfil", "/einstellungen?tab=buerger")]
    [InlineData("OeffentlichesModul", "/einstellungen?tab=oeffentliche-module")]
    [InlineData("PublicArea", "/einstellungen?tab=oeffentliche-module")]
    [InlineData("OeffentlicheSeite", "/einstellungen?tab=oeffentliche-seiten")]
    [InlineData("OeffentlicheFahndung", "/fahndung?tab=oeffentlich")]
    public void Route_PublicAreaConfig_PointsAtTheEditingSection(string type, string expected)
    {
        // these have no detail page; the audit row should still be followable to where it was changed
        Assert.Equal(expected, AuditEntityDisplay.Route(type, "any-id"));
    }

    [Fact]
    public void Tips_carry_a_German_label_and_a_route()
    {
        Assert.Equal("Bürgerhinweis", AuditEntityDisplay.Label("Hinweis"));
        Assert.Equal("Hinweis-Nachricht", AuditEntityDisplay.Label("HinweisNachricht"));
        Assert.Equal("/hinweise/h1", AuditEntityDisplay.Route("Hinweis", "h1"));
        // a message has no page of its own; the inbox is where it is read
        Assert.Equal("/hinweise", AuditEntityDisplay.Route("HinweisNachricht", "m1"));
    }

    [Fact]
    public void Route_IsCaseSensitive_MismatchedCaseYieldsNull()
    {
        Assert.Equal("/personen/1", AuditEntityDisplay.Route("Person", "1"));
        Assert.Null(AuditEntityDisplay.Route("person", "1"));
    }
}
