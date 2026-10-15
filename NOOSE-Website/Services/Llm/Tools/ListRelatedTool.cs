using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Presentations;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Llm;

namespace NOOSE_Website.Services.Llm.Tools;

/// <summary>Lists everything one record is connected to, masked to the asking agent's scope.</summary>
/// <remarks>
/// Connections live in three separate places, and only the third is a link row: memberships
/// (person ↔ faction/group/party), typed person-to-person relations, and hand-made links between any two
/// records. Reading only the link table made NOOSEI blind to exactly the connection agents ask about most.
/// </remarks>
public sealed class ListRelatedTool(IDbContextFactory<AppDbContext> dbFactory) : INooseiTool
{
    public string Name => "zeige_verbindungen";

    public string Description =>
        "Listet alle Verbindungen einer Akte auf: Mitgliedschaften (bei einer Person ihre Fraktionen, "
        + "Personengruppen und Parteien mit Rang; bei einer Fraktion, Personengruppe oder Partei ihre "
        + "Mitglieder), Beziehungen zwischen Personen (Feind, Verbündeter, Familie, …) und manuell gesetzte "
        + "Verknüpfungen zu Vorgängen, Operationen und anderen Akten. "
        + "Nutze dies, um zu klären, wer zu wem gehört. Für die Frage, wie zwei bestimmte Akten "
        + "zusammenhängen, nimm stattdessen finde_verbindungsweg.";

    public JsonElement ParameterSchema { get; } = NooseiLimits.Schema($$"""
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["typ", "id"],
          "properties": {
            "typ": { "type": "string", "enum": {{NooseiRecordTypes.EnumJson}} },
            "id": { "type": "string" },
            "max": { "type": "integer", "minimum": 1, "maximum": 40 }
          }
        }
        """);

    /// <summary>One connected record with the wording of the connection.</summary>
    private sealed record Related(
        string Section, string Relation, string Type, string Id,
        string? Name, string? CaseNumber, bool Classified, bool Tru, bool Hrb,
        string? RoleLabel = null, string? Role = null, bool IsLead = false);

    public async Task<NooseiToolResult> InvokeAsync(JsonElement arguments, NooseiToolContext context, CancellationToken cancellationToken = default)
    {
        var type = NooseiRecordTypes.Clr(NooseiLimits.Text(arguments, "typ"), NooseiUse.Read);
        var id = NooseiLimits.Text(arguments, "id");
        if (type is null || id is null)
        {
            return NooseiToolResult.NotFound();
        }
        var max = NooseiLimits.Count(arguments, "max", 25);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await Visibility.IsRecordVisibleAsync(db, type, id, context.Scope, cancellationToken))
        {
            return NooseiToolResult.NotFound();
        }

        var rows = new List<Related>();
        if (type == nameof(Person))
        {
            rows.AddRange(await AffiliationsAsync(db, id, max, cancellationToken));
            rows.AddRange(await RelationsAsync(db, id, max, cancellationToken));
        }
        else
        {
            rows.AddRange(await MembersAsync(db, type, id, max, cancellationToken));
        }

        var sb = new StringBuilder();
        var refs = new List<LlmContextRef>();
        var written = Render(sb, rows, context.Scope, refs);
        written += await AppendLinksAsync(sb, db, type, id, max, context.Scope, refs, cancellationToken);

        return written == 0
            ? NooseiToolResult.Empty("Verbindungen")
            : new NooseiToolResult(NooseiLimits.Clip(sb.ToString()), refs);
    }

    // ---- sources ----

    /// <summary>Person → the organisations they belong to.</summary>
    private static async Task<List<Related>> AffiliationsAsync(AppDbContext db, string personId, int max, CancellationToken ct)
    {
        var rows = new List<Related>();
        rows.AddRange(await db.FactionMembers.AsNoTracking().Where(m => m.PersonId == personId)
            .Select(m => new Related(
                "Zugehörigkeiten", m.IsLead ? "Leitung" : "Mitglied", nameof(Faction), m.FactionId,
                m.Faction != null ? m.Faction.Name : null,
                m.Faction != null ? m.Faction.CaseNumber : null,
                m.Faction != null && m.Faction.IsClassified,
                m.Faction != null && m.Faction.IsTRUClassified,
                m.Faction != null && m.Faction.IsHRBClassified,
                "Rang", m.Rank, m.IsLead))
            .Take(max).ToListAsync(ct));
        rows.AddRange(await db.PersonGroupMembers.AsNoTracking().Where(m => m.PersonId == personId)
            .Select(m => new Related(
                "Zugehörigkeiten", m.IsLead ? "Leitung" : "Mitglied", nameof(PersonGroup), m.PersonGroupId,
                m.PersonGroup != null ? m.PersonGroup.Name : null,
                m.PersonGroup != null ? m.PersonGroup.CaseNumber : null,
                m.PersonGroup != null && m.PersonGroup.IsClassified,
                m.PersonGroup != null && m.PersonGroup.IsTRUClassified,
                m.PersonGroup != null && m.PersonGroup.IsHRBClassified,
                "Rolle", m.Role, m.IsLead))
            .Take(max).ToListAsync(ct));
        rows.AddRange(await db.PartyMembers.AsNoTracking().Where(m => m.PersonId == personId)
            .Select(m => new Related(
                "Zugehörigkeiten", m.IsLead ? "Leitung" : "Mitglied", nameof(Party), m.PartyId,
                m.Party != null ? m.Party.Name : null,
                m.Party != null ? m.Party.CaseNumber : null,
                m.Party != null && m.Party.IsClassified,
                m.Party != null && m.Party.IsTRUClassified,
                m.Party != null && m.Party.IsHRBClassified,
                "Rolle", m.Role, m.IsLead))
            .Take(max).ToListAsync(ct));
        return rows;
    }

    /// <summary>Organisation → its members.</summary>
    private static async Task<List<Related>> MembersAsync(AppDbContext db, string type, string id, int max, CancellationToken ct)
        => type switch
        {
            // ordering happens on the entity: a projected record's property has no SQL translation
            nameof(Faction) => await db.FactionMembers.AsNoTracking().Where(m => m.FactionId == id)
                .OrderByDescending(m => m.IsLead)
                .Select(m => new Related(
                    "Mitglieder", m.IsLead ? "Leitung" : "Mitglied", nameof(Person), m.PersonId,
                    m.Person != null ? m.Person.Name : null,
                    m.Person != null ? m.Person.CaseNumber : null,
                    m.Person != null && m.Person.IsClassified,
                    m.Person != null && m.Person.IsTRUClassified,
                    m.Person != null && m.Person.IsHRBClassified,
                    "Rang", m.Rank, m.IsLead))
                .Take(max).ToListAsync(ct),
            nameof(PersonGroup) => await db.PersonGroupMembers.AsNoTracking().Where(m => m.PersonGroupId == id)
                .OrderByDescending(m => m.IsLead)
                .Select(m => new Related(
                    "Mitglieder", m.IsLead ? "Leitung" : "Mitglied", nameof(Person), m.PersonId,
                    m.Person != null ? m.Person.Name : null,
                    m.Person != null ? m.Person.CaseNumber : null,
                    m.Person != null && m.Person.IsClassified,
                    m.Person != null && m.Person.IsTRUClassified,
                    m.Person != null && m.Person.IsHRBClassified,
                    "Rolle", m.Role, m.IsLead))
                .Take(max).ToListAsync(ct),
            nameof(Party) => await db.PartyMembers.AsNoTracking().Where(m => m.PartyId == id)
                .OrderByDescending(m => m.IsLead)
                .Select(m => new Related(
                    "Mitglieder", m.IsLead ? "Leitung" : "Mitglied", nameof(Person), m.PersonId,
                    m.Person != null ? m.Person.Name : null,
                    m.Person != null ? m.Person.CaseNumber : null,
                    m.Person != null && m.Person.IsClassified,
                    m.Person != null && m.Person.IsTRUClassified,
                    m.Person != null && m.Person.IsHRBClassified,
                    "Rolle", m.Role, m.IsLead))
                .Take(max).ToListAsync(ct),
            _ => [],
        };

    /// <summary>Typed person-to-person relations, in whichever column this person sits.</summary>
    private static async Task<List<Related>> RelationsAsync(AppDbContext db, string personId, int max, CancellationToken ct)
    {
        var raw = await db.PersonRelations.AsNoTracking()
            .Where(r => r.PersonAId == personId || r.PersonBId == personId)
            .Select(r => new
            {
                r.PersonAId,
                r.PersonBId,
                r.Type,
                r.Note,
                AName = r.PersonA != null ? r.PersonA.Name : null,
                ACase = r.PersonA != null ? r.PersonA.CaseNumber : null,
                AClassified = r.PersonA != null && r.PersonA.IsClassified,
                ATru = r.PersonA != null && r.PersonA.IsTRUClassified,
                AHrb = r.PersonA != null && r.PersonA.IsHRBClassified,
                BName = r.PersonB != null ? r.PersonB.Name : null,
                BCase = r.PersonB != null ? r.PersonB.CaseNumber : null,
                BClassified = r.PersonB != null && r.PersonB.IsClassified,
                BTru = r.PersonB != null && r.PersonB.IsTRUClassified,
                BHrb = r.PersonB != null && r.PersonB.IsHRBClassified,
            })
            .Take(max).ToListAsync(ct);

        return raw.Select(r =>
        {
            var mine = r.PersonAId == personId;
            return new Related(
                "Beziehungen", RelationTypeDisplay.Name(r.Type), nameof(Person),
                mine ? r.PersonBId : r.PersonAId,
                mine ? r.BName : r.AName,
                mine ? r.BCase : r.ACase,
                mine ? r.BClassified : r.AClassified,
                mine ? r.BTru : r.ATru,
                mine ? r.BHrb : r.AHrb,
                "Notiz", MentionParser.Strip(r.Note ?? string.Empty).Trim());
        }).ToList();
    }

    // ---- rendering ----

    private static int Render(StringBuilder sb, List<Related> rows, ViewerScope scope, List<LlmContextRef> refs)
    {
        var written = 0;
        foreach (var group in rows.GroupBy(r => r.Section))
        {
            var items = group.ToList();
            sb.Append("— ").Append(group.Key).Append(" (").Append(items.Count).AppendLine(") —");
            foreach (var r in items)
            {
                sb.Append("• ").Append(r.Relation).Append(": ").Append(NooseiRecordTypes.German(r.Type)).Append(" | ");
                written++;
                if (string.IsNullOrWhiteSpace(r.Name))
                {
                    sb.AppendLine("(unbekannt)");
                    continue;
                }
                // masked at the connected record's own level, never at the root record's
                if (!scope.CanSee(DossierScope.LevelOf(r.Classified, r.Tru, r.Hrb)))
                {
                    sb.AppendLine("(Verschlusssache)");
                    continue;
                }
                sb.Append(r.Name);
                if (!string.IsNullOrWhiteSpace(r.CaseNumber))
                {
                    sb.Append(" (").Append(r.CaseNumber).Append(')');
                }
                if (!string.IsNullOrWhiteSpace(r.Role))
                {
                    sb.Append(" | ").Append(r.RoleLabel).Append(": ").Append(r.Role);
                }
                sb.Append(" | id=").AppendLine(r.Id);
                refs.Add(new LlmContextRef(r.Type, r.Id, r.Name));
            }
        }
        return written;
    }

    private static async Task<int> AppendLinksAsync(
        StringBuilder sb, AppDbContext db, string type, string id, int max,
        ViewerScope scope, List<LlmContextRef> refs, CancellationToken ct)
    {
        var links = await db.Links.AsNoTracking()
            .Where(l => (l.SourceType == type && l.SourceId == id) || (l.TargetType == type && l.TargetId == id))
            .OrderByDescending(l => l.CreatedAt)
            .Take(max)
            .Select(l => new { l.SourceType, l.SourceId, l.TargetType, l.TargetId, l.Label })
            .ToListAsync(ct);
        if (links.Count == 0)
        {
            return 0;
        }

        var others = links
            .Select(l => l.SourceType == type && l.SourceId == id ? (l.TargetType, l.TargetId) : (l.SourceType, l.SourceId))
            .Distinct()
            .ToList();

        // taskforce membership is this viewer's, and the classified flag is post-filtered below:
        // the resolver reports it but does not withhold anything on its own
        var resolved = await RecordsReference.ResolveAsync(db, others, ct,
            mayAllTaskforces: scope.MayAllTaskforces, meId: scope.MeId);
        // groups and invites decide; a hidden one is left out like on the link list, label and all
        var presentations = await PresentationVisibility.VisibleTitlesAsync(db,
            others.Where(o => o.Item1 == nameof(Presentation)).Select(o => o.Item2).ToList(), scope, ct);
        foreach (var (presentationId, title) in presentations)
        {
            resolved[(nameof(Presentation), presentationId)] = new(title, false, null);
        }
        links = links.Where(l =>
        {
            var (otherType, otherId) = l.SourceType == type && l.SourceId == id
                ? (l.TargetType, l.TargetId)
                : (l.SourceType, l.SourceId);
            return otherType != nameof(Presentation) || presentations.ContainsKey(otherId);
        }).ToList();
        if (links.Count == 0)
        {
            return 0;
        }

        sb.Append("— Verknüpfungen (").Append(links.Count).AppendLine(") —");
        foreach (var link in links)
        {
            var other = link.SourceType == type && link.SourceId == id
                ? (Type: link.TargetType, Id: link.TargetId)
                : (Type: link.SourceType, Id: link.SourceId);
            var label = string.IsNullOrWhiteSpace(link.Label) ? "Verknüpfung" : MentionParser.Strip(link.Label).Trim();

            sb.Append("• ").Append(label).Append(": ").Append(NooseiRecordTypes.German(other.Type)).Append(" | ");
            if (!resolved.TryGetValue(other, out var resolution))
            {
                sb.AppendLine("(nicht auflösbar)");
                continue;
            }
            if (resolution.Classified && !scope.MayClassifiedRead)
            {
                sb.AppendLine("(Verschlusssache)");
                continue;
            }
            sb.Append(resolution.Display).Append(" | id=").AppendLine(other.Id);
            refs.Add(new LlmContextRef(other.Type, other.Id, resolution.Display));
        }
        return links.Count;
    }
}
