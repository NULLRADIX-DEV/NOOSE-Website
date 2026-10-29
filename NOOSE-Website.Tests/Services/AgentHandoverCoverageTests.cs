using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;

namespace NOOSE_Website.Tests.Services;

/// <summary>
/// The handover in <c>AgentHandoverService</c> is a hand-maintained list of tables, like the account delete. A new
/// table that ties work to an agent would otherwise stay on the leaving account without anyone noticing, which is
/// exactly the silent loss the handover exists to stop.
/// </summary>
/// <remarks>
/// A new foreign key into the user table turns this red until someone decides whether a successor takes it over.
/// <see cref="NotHandedOver"/> records the ones that knowingly stay, each with its reason.
/// </remarks>
public class AgentHandoverCoverageTests
{
    private const string Own =
        "gehört dem Agenten selbst (eigener Arbeitsstand, eigene Akte, eigenes Konto) und ist nichts für einen Nachfolger";

    private const string History = "hält fest, wer etwas getan hat; die Zeile ist Geschichte, kein offener Auftrag";

    private const string CitizenContact = "Bürgerkontakt: Bearbeiter und Beteiligte haben eigene Sicht- und "
        + "Anonymitätsregeln; ob und wie sie übergeben werden, ist offen und nicht Teil von #25";

    private const string Recruiting = "Recruiting: die Zuweisung einer Bewerbung läuft über HRB mit eigenen Rechten; "
        + "ob und wie sie übergeben wird, ist offen und nicht Teil von #25";

    /// <summary>Agent pointers the handover leaves where they are, with the reason.</summary>
    private static readonly Dictionary<string, string> NotHandedOver = new(StringComparer.Ordinal)
    {
        ["Absence.AgentId"] = Own,
        ["Absence.AcknowledgedById"] = History,
        ["AgentAbduction.VictimAgentId"] = History,
        ["AgentBadge.AgentId"] = Own,
        ["AgentInvite.UsedByUserId"] = History,
        ["AgentModuleCompletion.AgentId"] = Own,
        ["AgentNote.AgentId"] = Own,
        ["AgentPromotionRequest.AgentId"] = Own,
        ["AgentRankHistory.AgentId"] = Own,
        ["AgentTestAssignment.AgentId"] = Own,
        ["AnnouncementAcknowledgment.AgentId"] = Own,
        ["Bewerbung.ApplicantUserId"] = Own,
        ["Bewerbung.AssignedAgentId"] = Recruiting,
        ["Bewerbungssperre.AgentId"] = Own,
        ["BuergerProfil.BlockedById"] = History,
        ["BuergerProfil.UserId"] = Own,
        ["DecorationAward.AgentId"] = Own,
        ["DocumentAccessExclusion.AgentId"] = Own,
        ["EvidenceEntry.HandlerAgentId"] = History,
        ["FahndungEinspruch.DecidedById"] = History,
        ["FahndungKopfgeldAnteil.DonorAgentId"] = History,
        ["Feedback.AgentId"] = Own,
        ["FinancingBudgetPeriod.AgentId"] = Own,
        ["Hinweis.HandlerId"] = CitizenContact,
        ["HinweisNachricht.AuthorAgentId"] = History,
        ["IdentityUserClaim`1.UserId"] = Own,
        ["IdentityUserLogin`1.UserId"] = Own,
        ["IdentityUserRole`1.UserId"] = Own,
        ["IdentityUserToken`1.UserId"] = Own,
        ["KassenBuchung.BookedById"] = History,
        ["LlmQuotaAdjustment.AgentId"] = Own,
        ["LlmQuotaPeriod.AgentId"] = Own,
        ["LlmRequestLog.AgentId"] = Own,
        ["MeetingAttendance.AgentId"] = History,
        ["MeetingSignOff.AgentId"] = Own,
        ["NooseiConversation.AgentId"] = Own,
        ["Observation.ObservingAgentId"] = History,
        ["OeffentlicheFahndung.PublishedById"] = History,
        ["OeffentlicheSeite.PublishedById"] = History,
        ["OeffentlicheTelefonnummer.AgentId"] = Own,
        ["OeffentlicheWarnung.PublishedById"] = History,
        ["OeffentlicherLagebericht.PublishedById"] = History,
        ["OeffentlichesFraktionsprofil.PublishedById"] = History,
        ["OeffentlichesFuehrungsprofil.AgentId"] = Own,
        ["PresentationInvite.AgentId"] = Own,
        ["Pressemitteilung.PublishedById"] = History,
        ["SavedSearch.AgentId"] = Own,
        ["TextSnippet.AgentId"] = Own,
        ["Ticket.HandlerId"] = CitizenContact,
        ["Ticket.OpenedByAgentId"] = History,
        ["TicketNachricht.AuthorAgentId"] = History,
        ["TicketParticipant.AgentId"] = CitizenContact,
        ["WatchlistEntry.AgentId"] = Own,
    };

    private static string ServiceSource([CallerFilePath] string here = "")
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..",
            "NOOSE-Website", "Services", "AgentHandoverService.cs"));
        Assert.True(File.Exists(path), $"AgentHandoverService nicht gefunden: {path}");
        return File.ReadAllText(path);
    }

    /// <summary>Foreign keys into the user table, as (declaring entity, property) pairs.</summary>
    private static List<(string Entity, string Property)> AgentKeys()
    {
        using var ctx = new SqliteTestContext();
        using var db = ctx.NewContext();
        var userType = typeof(NOOSE_Website.Data.Entities.Agent);
        return db.Model.GetEntityTypes()
            .SelectMany(e => e.GetForeignKeys().Where(fk => fk.PrincipalEntityType.ClrType == userType)
                .SelectMany(fk => fk.Properties.Select(p => (e.ClrType.Name, p.Name))))
            .Distinct()
            .ToList();
    }

    /// <summary>CLR entity name to the DbSet property the service addresses it by.</summary>
    private static Dictionary<string, string> DbSetNames()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in typeof(AppDbContext).GetProperties())
        {
            var type = property.PropertyType;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(DbSet<>))
            {
                map[type.GetGenericArguments()[0].Name] = property.Name;
            }
        }
        return map;
    }

    [Fact]
    public void EveryAgentPointer_IsHandedOverOrListedAsStaying()
    {
        var source = ServiceSource();
        var dbSets = DbSetNames();
        var keys = AgentKeys();
        // a wrong model or a renamed entity would otherwise leave this green forever
        Assert.NotEmpty(keys);

        var offenders = keys
            .Where(k => !NotHandedOver.ContainsKey($"{k.Entity}.{k.Property}"))
            // the service reads and writes a table through its own DbSet and the pointer property
            .Where(k => !dbSets.TryGetValue(k.Entity, out var set)
                        || !source.Contains("db." + set + ".", StringComparison.Ordinal)
                        || !source.Contains("." + k.Property + " == agentId", StringComparison.Ordinal))
            .Select(k => $"{k.Entity}.{k.Property}")
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Zeiger auf einen Agenten, über die die Dienstübergabe nicht entschieden hat. Entweder in "
            + "AgentHandoverService übergeben oder mit Begründung in NotHandedOver eintragen: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void TheStayingList_NamesOnlyPointersThatStillExist()
    {
        var keys = AgentKeys().Select(k => $"{k.Entity}.{k.Property}").ToHashSet(StringComparer.Ordinal);

        var stale = NotHandedOver.Keys.Where(k => !keys.Contains(k)).Order(StringComparer.Ordinal).ToArray();

        Assert.True(stale.Length == 0,
            "Diese Zeiger gibt es nicht mehr; Eintrag aus NotHandedOver entfernen: " + string.Join(", ", stale));
    }

    [Fact]
    public void EveryStayingPointer_CarriesAReason()
    {
        var blank = NotHandedOver.Where(e => string.IsNullOrWhiteSpace(e.Value)).Select(e => e.Key)
            .Order(StringComparer.Ordinal).ToArray();

        Assert.True(blank.Length == 0, "Ein bleibender Zeiger braucht eine Begründung: " + string.Join(", ", blank));
    }
}
