using Microsoft.EntityFrameworkCore;
using System.Text;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Search;

namespace NOOSE_Website.Services;

/// <summary>Assembled, codename-safe dossier context for a record.</summary>
public readonly record struct DossierContext(string Title, string Text, bool IsClassified);

/// <summary>Builds a comprehensive German plain-text dossier for a record, for AI summarisation.</summary>
/// <remarks>Split across two files: the record kinds proper live here, everything the agency runs on — duty,
/// personnel, ledger, recruiting — in <c>DossierContextBuilder.Operations.cs</c>. The shared helpers at the bottom
/// of this file serve both.</remarks>
public static partial class DossierContextBuilder
{
    /// <summary>Dispatches by CLR type name; returns null for unknown types or missing ids.</summary>
    /// <param name="scope">Whose eyes the dossier is assembled for. Null uses <see cref="DossierScope.ForRecord"/>,
    /// the minimum-privilege scope a cached brief must be generated at.</param>
    public static async Task<DossierContext?> BuildAsync(
        AppDbContext db, string entityType, string entityId, ViewerScope? scope = null, CancellationToken cancellationToken = default)
        => entityType switch
        {
            nameof(Person) => await BuildPersonAsync(db, entityId, scope, cancellationToken),
            nameof(Faction) => await BuildFactionAsync(db, entityId, scope, cancellationToken),
            nameof(PersonGroup) => await BuildPersonGroupAsync(db, entityId, scope, cancellationToken),
            nameof(Party) => await BuildPartyAsync(db, entityId, scope, cancellationToken),
            nameof(Operation) => await BuildOperationAsync(db, entityId, scope, cancellationToken),
            nameof(Case) => await BuildCaseAsync(db, entityId, scope, cancellationToken),
            nameof(Taskforce) => await BuildTaskforceAsync(db, entityId, scope, cancellationToken),
            nameof(Document) => await BuildDocumentAsync(db, entityId, scope, cancellationToken),
            nameof(Law) => await BuildLawAsync(db, entityId, scope, cancellationToken),
            // everything the agency runs on, in DossierContextBuilder.Operations.cs
            _ => await BuildOperationsAsync(db, entityType, entityId, scope, cancellationToken),
        };

    // ---- per-type builders ----

    static async Task<DossierContext?> BuildPersonAsync(AppDbContext db, string id, ViewerScope? scope, CancellationToken ct)
    {
        var p = await db.People.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null)
        {
            return null;
        }
        var view = scope ?? DossierScope.ForRecord(p.SecrecyLevel);

        var sb = new StringBuilder();
        sb.AppendLine("Personenakte");
        Line(sb, "Aktenzeichen", p.CaseNumber);
        Line(sb, "Name", p.Name);
        Line(sb, "Lebensstatus", LifeStatusDisplay.Name(p.EffectiveLifeStatus));
        Line(sb, "Tot bis", Fmt(p.DeadUntil));
        Line(sb, "Einstufung", ClassificationDisplay.Name(p.Classification));
        Line(sb, "Verschlusssache", p.IsRestricted ? $"Ja ({p.SecrecyLevel})" : "Nein");
        if (p.IsWanted)
        {
            Line(sb, "Zur Fahndung", string.IsNullOrWhiteSpace(p.WantedReason) ? "Ja" : $"Ja — {p.WantedReason}");
        }
        if (p.ThreatScore.HasValue)
        {
            var conf = p.ThreatConfidence.HasValue ? $" (Konfidenz {p.ThreatConfidence}%)" : "";
            Line(sb, "Bedrohungs-Score", $"{p.ThreatScore}{conf}");
        }
        Line(sb, "Score berechnet am", Fmt(p.ScoreCalculatedAt));
        Line(sb, "Beschreibung", p.Description);

        var (aliases, aliasesTotal) = await TakeAsync(
            db.PersonAliases.AsNoTracking().Where(a => a.PersonId == id).Select(a => a.AliasName), ct);
        if (aliasesTotal > 0)
        {
            sb.AppendLine($"— Aliase ({aliasesTotal}) —");
            sb.AppendLine(string.Join(", ", aliases));
        }

        var (phones, phonesTotal) = await TakeAsync(
            db.PersonPhones.AsNoTracking().Where(x => x.PersonId == id)
                .Select(x => new { x.Number, x.Designation }), ct);
        if (phonesTotal > 0)
        {
            sb.AppendLine($"— Telefonnummern ({phonesTotal}) —");
            foreach (var x in phones)
            {
                sb.Append("• ").Append(x.Number);
                if (!string.IsNullOrWhiteSpace(x.Designation))
                {
                    sb.Append(" (").Append(x.Designation).Append(')');
                }
                sb.AppendLine();
            }
        }

        var (vehicles, vehiclesTotal) = await TakeAsync(
            db.PersonVehicles.AsNoTracking().Where(x => x.PersonId == id)
                .Select(x => new { x.Designation, x.LicensePlate }), ct);
        if (vehiclesTotal > 0)
        {
            sb.AppendLine($"— Fahrzeuge ({vehiclesTotal}) —");
            foreach (var x in vehicles)
            {
                sb.Append("• ").Append(x.Designation);
                if (!string.IsNullOrWhiteSpace(x.LicensePlate))
                {
                    sb.Append(" [").Append(x.LicensePlate).Append(']');
                }
                sb.AppendLine();
            }
        }

        var (weapons, weaponsTotal) = await TakeAsync(
            db.PersonWeapons.AsNoTracking().Where(x => x.PersonId == id).Select(x => x.Text), ct);
        if (weaponsTotal > 0)
        {
            sb.AppendLine($"— Waffen ({weaponsTotal}) —");
            foreach (var w in weapons)
            {
                sb.Append("• ").AppendLine(w);
            }
        }

        var (locations, locationsTotal) = await TakeAsync(
            db.PersonLocations.AsNoTracking().Where(x => x.PersonId == id)
                .Select(x => new { x.Text, x.Note }), ct);
        if (locationsTotal > 0)
        {
            sb.AppendLine($"— Orte ({locationsTotal}) —");
            foreach (var x in locations)
            {
                sb.Append("• ").Append(x.Text);
                if (!string.IsNullOrWhiteSpace(x.Note))
                {
                    sb.Append(" — ").Append(Free(x.Note));
                }
                sb.AppendLine();
            }
        }

        var photoCount = await db.PersonPhotos.AsNoTracking().Where(x => x.PersonId == id).CountAsync(ct);
        if (photoCount > 0)
        {
            Line(sb, "Fotos", photoCount.ToString());
        }

        var (docs, docsTotal) = await TakeAsync(
            db.PersonDocs.AsNoTracking().Where(x => x.PersonId == id)
                .OrderByDescending(x => x.Timestamp)
                .Select(x => new { x.Timestamp, x.Reason, x.Outcome, x.TruthSerum, x.MemoryDeleted, x.ReceivedInformation }), ct);
        if (docsTotal > 0)
        {
            sb.AppendLine($"— Doks ({docsTotal}) —");
            foreach (var d in docs)
            {
                sb.Append("• ").Append(Fmt(d.Timestamp)).Append(" | Ausgang: ").Append(MeasureOutcomeDisplay.Name(d.Outcome));
                if (!string.IsNullOrWhiteSpace(d.Reason))
                {
                    sb.Append(" | Grund: ").Append(Free(d.Reason));
                }
                if (d.TruthSerum)
                {
                    sb.Append(" | Wahrheitsserum");
                }
                if (d.MemoryDeleted)
                {
                    sb.Append(" | Gedächtnis gelöscht");
                }
                if (!string.IsNullOrWhiteSpace(d.ReceivedInformation))
                {
                    sb.Append(" | Infos: ").Append(Free(d.ReceivedInformation));
                }
                sb.AppendLine();
            }
        }

        var (obs, obsTotal) = await TakeAsync(
            db.Observations.AsNoTracking().Where(x => x.PersonId == id)
                .OrderByDescending(x => x.Start)
                .Select(x => new { x.Start, x.End, x.Location, x.Sighting, x.Result, x.ObservingAgentId }), ct);
        if (obsTotal > 0)
        {
            // codename-safe: never resolve the observing agent to a real name
            var codenames = await CodenamesAsync(db, obs.Select(o => o.ObservingAgentId), ct);
            sb.AppendLine($"— Observationen ({obsTotal}) —");
            foreach (var o in obs)
            {
                sb.Append("• ").Append(Fmt(o.Start));
                if (o.End.HasValue)
                {
                    sb.Append('–').Append(Fmt(o.End));
                }
                if (!string.IsNullOrWhiteSpace(o.Location))
                {
                    sb.Append(" | Ort: ").Append(Free(o.Location));
                }
                if (!string.IsNullOrWhiteSpace(o.Sighting))
                {
                    sb.Append(" | Beobachtung: ").Append(Free(o.Sighting));
                }
                if (!string.IsNullOrWhiteSpace(o.Result))
                {
                    sb.Append(" | Ergebnis: ").Append(Free(o.Result));
                }
                if (!string.IsNullOrEmpty(o.ObservingAgentId))
                {
                    sb.Append(" | Agent: ").Append(Codename(codenames, o.ObservingAgentId));
                }
                sb.AppendLine();
            }
        }

        var relBase = db.PersonRelations.AsNoTracking().Where(r => r.PersonAId == id || r.PersonBId == id);
        var relTotal = await relBase.CountAsync(ct);
        if (relTotal > 0)
        {
            var rels = await relBase.Take(50).Select(r => new
            {
                r.PersonAId,
                r.Type,
                r.Note,
                AName = r.PersonA != null ? r.PersonA.Name : null,
                AClassified = r.PersonA != null && r.PersonA.IsClassified,
                ATru = r.PersonA != null && r.PersonA.IsTRUClassified,
                AHrb = r.PersonA != null && r.PersonA.IsHRBClassified,
                BName = r.PersonB != null ? r.PersonB.Name : null,
                BClassified = r.PersonB != null && r.PersonB.IsClassified,
                BTru = r.PersonB != null && r.PersonB.IsTRUClassified,
                BHrb = r.PersonB != null && r.PersonB.IsHRBClassified,
            }).ToListAsync(ct);
            sb.AppendLine($"— Beziehungen ({relTotal}) —");
            foreach (var r in rels)
            {
                var mine = r.PersonAId == id;
                var other = mine ? r.BName : r.AName;
                var level = mine
                    ? DossierScope.LevelOf(r.BClassified, r.BTru, r.BHrb)
                    : DossierScope.LevelOf(r.AClassified, r.ATru, r.AHrb);
                var shown = string.IsNullOrWhiteSpace(other) ? "(unbekannt)"
                    : view.CanSee(level) ? other : "(Verschlusssache)";
                sb.Append("• ").Append(RelationTypeDisplay.Name(r.Type)).Append(": ").Append(shown);
                if (Free(r.Note) is { Length: > 0 } note)
                {
                    sb.Append(" — ").Append(note);
                }
                sb.AppendLine();
            }
        }

        await AppendAffiliationsAsync(sb, db, id, view, ct);

        await AppendAttachmentsAsync(sb, db, nameof(Person), id, includeClassificationHistory: true, view, ct);
        return new DossierContext($"{p.Name} ({p.CaseNumber})", sb.ToString(), p.IsRestricted);
    }

    static async Task<DossierContext?> BuildFactionAsync(AppDbContext db, string id, ViewerScope? scope, CancellationToken ct)
    {
        var f = await db.Factions.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (f is null)
        {
            return null;
        }
        var view = scope ?? DossierScope.ForRecord(f.SecrecyLevel);

        var sb = new StringBuilder();
        sb.AppendLine("Fraktionsakte");
        Line(sb, "Aktenzeichen", f.CaseNumber);
        Line(sb, "Name", f.Name);
        Line(sb, "Art", f.Kind);
        Line(sb, "Einstufung", ClassificationDisplay.Name(f.Classification));
        Line(sb, "Verschlusssache", f.IsRestricted ? $"Ja ({f.SecrecyLevel})" : "Nein");
        Line(sb, "Staatsfraktion", f.IsStateFaction);
        Line(sb, "Badfrak", f.IsBadFaction);
        if (f.ThreatScore.HasValue)
        {
            var conf = f.ThreatConfidence.HasValue ? $" (Konfidenz {f.ThreatConfidence}%)" : "";
            Line(sb, "Bedrohungs-Score", $"{f.ThreatScore}{conf}");
        }
        Line(sb, "Score berechnet am", Fmt(f.ScoreCalculatedAt));
        if (f.EstimatedMemberCount.HasValue)
        {
            Line(sb, "Geschätzte Mitgliederzahl", f.EstimatedMemberCount.Value.ToString());
        }
        Line(sb, "Funk", f.Radio);
        Line(sb, "Darkchat", f.Darkchat);
        Line(sb, "Ausstellungszeiten", f.IssuingTimes);
        Line(sb, "Anwesen", f.Estate);
        Line(sb, "Erkennungsfarbe", f.RecognitionColor);
        Line(sb, "Ziele", f.Targets);
        Line(sb, "Beschreibung", f.Description);

        var (ranks, ranksTotal) = await TakeAsync(
            db.FactionRanks.AsNoTracking().Where(x => x.FactionId == id)
                .OrderByDescending(x => x.Order).Select(x => x.Designation), ct);
        if (ranksTotal > 0)
        {
            sb.AppendLine($"— Ränge ({ranksTotal}) —");
            sb.AppendLine(string.Join(", ", ranks));
        }

        await AppendMembersAsync(sb,
            db.FactionMembers.AsNoTracking().Where(x => x.FactionId == id)
                .Select(x => new MemberProj
                {
                    Name = x.Person != null ? x.Person.Name : null,
                    Classified = x.Person != null && x.Person.IsClassified,
                    Tru = x.Person != null && x.Person.IsTRUClassified,
                    Hrb = x.Person != null && x.Person.IsHRBClassified,
                    RoleOrRank = x.Rank,
                    IsLead = x.IsLead,
                }),
            "Rang", view, ct);

        await AppendAgentsAsync(sb, db,
            db.FactionAgents.AsNoTracking().Where(x => x.FactionId == id)
                .Select(x => new AgentProj { AgentId = x.AgentId, Flag = x.IsInvestigationLead }),
            "Ermittlungsleiter", ct);

        var weaponStock = await db.FactionWeaponStocks.AsNoTracking().Where(x => x.FactionId == id).CountAsync(ct);
        if (weaponStock > 0)
        {
            Line(sb, "Waffenbestände", weaponStock.ToString());
        }
        var inventory = await db.FactionInventories.AsNoTracking().Where(x => x.FactionId == id).CountAsync(ct);
        if (inventory > 0)
        {
            Line(sb, "Lagerbestände", inventory.ToString());
        }
        var routes = await db.FactionDrugRoutes.AsNoTracking().Where(x => x.FactionId == id).CountAsync(ct);
        if (routes > 0)
        {
            Line(sb, "Drogenrouten", routes.ToString());
        }
        var photos = await db.FactionPhotos.AsNoTracking().Where(x => x.FactionId == id).CountAsync(ct);
        if (photos > 0)
        {
            Line(sb, "Fotos", photos.ToString());
        }

        await AppendAttachmentsAsync(sb, db, nameof(Faction), id, includeClassificationHistory: true, view, ct);
        return new DossierContext($"{f.Name} ({f.CaseNumber})", sb.ToString(), f.IsRestricted);
    }

    static async Task<DossierContext?> BuildPersonGroupAsync(AppDbContext db, string id, ViewerScope? scope, CancellationToken ct)
    {
        var g = await db.PersonGroups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (g is null)
        {
            return null;
        }
        var view = scope ?? DossierScope.ForRecord(g.SecrecyLevel);

        var sb = new StringBuilder();
        sb.AppendLine("Personengruppen-Akte");
        Line(sb, "Aktenzeichen", g.CaseNumber);
        Line(sb, "Name", g.Name);
        Line(sb, "Art", GroupsKindDisplay.Name(g.Kind));
        Line(sb, "Einstufung", ClassificationDisplay.Name(g.Classification));
        Line(sb, "Verschlusssache", g.IsRestricted ? $"Ja ({g.SecrecyLevel})" : "Nein");
        if (g.EstimatedMemberCount.HasValue)
        {
            Line(sb, "Geschätzte Mitgliederzahl", g.EstimatedMemberCount.Value.ToString());
        }
        Line(sb, "Ziele", g.Targets);
        Line(sb, "Beschreibung", g.Description);

        await AppendMembersAsync(sb,
            db.PersonGroupMembers.AsNoTracking().Where(x => x.PersonGroupId == id)
                .Select(x => new MemberProj
                {
                    Name = x.Person != null ? x.Person.Name : null,
                    Classified = x.Person != null && x.Person.IsClassified,
                    Tru = x.Person != null && x.Person.IsTRUClassified,
                    Hrb = x.Person != null && x.Person.IsHRBClassified,
                    RoleOrRank = x.Role,
                    IsLead = x.IsLead,
                }),
            "Rolle", view, ct);

        await AppendAgentsAsync(sb, db,
            db.PersonGroupAgents.AsNoTracking().Where(x => x.PersonGroupId == id)
                .Select(x => new AgentProj { AgentId = x.AgentId, Flag = x.IsInvestigationLead }),
            "Ermittlungsleiter", ct);

        var groupPhotos = await db.PersonGroupPhotos.AsNoTracking().Where(x => x.PersonGroupId == id).CountAsync(ct);
        if (groupPhotos > 0)
        {
            Line(sb, "Fotos", groupPhotos.ToString());
        }

        await AppendAttachmentsAsync(sb, db, nameof(PersonGroup), id, includeClassificationHistory: true, view, ct);
        return new DossierContext($"{g.Name} ({g.CaseNumber})", sb.ToString(), g.IsRestricted);
    }

    static async Task<DossierContext?> BuildPartyAsync(AppDbContext db, string id, ViewerScope? scope, CancellationToken ct)
    {
        var p = await db.Parties.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (p is null)
        {
            return null;
        }
        var view = scope ?? DossierScope.ForRecord(p.SecrecyLevel);

        var sb = new StringBuilder();
        sb.AppendLine("Parteiakte");
        Line(sb, "Aktenzeichen", p.CaseNumber);
        Line(sb, "Name", p.Name);
        Line(sb, "Einstufung", p.Classification.ToString());
        Line(sb, "Verschlusssache", p.IsRestricted ? $"Ja ({p.SecrecyLevel})" : "Nein");
        Line(sb, "Ziele", p.Targets);
        Line(sb, "Bemerkungen", p.Remarks);
        Line(sb, "Beschreibung", p.Description);

        await AppendMembersAsync(sb,
            db.PartyMembers.AsNoTracking().Where(x => x.PartyId == id)
                .Select(x => new MemberProj
                {
                    Name = x.Person != null ? x.Person.Name : null,
                    Classified = x.Person != null && x.Person.IsClassified,
                    Tru = x.Person != null && x.Person.IsTRUClassified,
                    Hrb = x.Person != null && x.Person.IsHRBClassified,
                    RoleOrRank = x.Role,
                    IsLead = x.IsLead,
                }),
            "Rolle", view, ct);

        await AppendAgentsAsync(sb, db,
            db.PartyAgents.AsNoTracking().Where(x => x.PartyId == id)
                .Select(x => new AgentProj { AgentId = x.AgentId, Flag = x.IsInvestigationLead }),
            "Ermittlungsleiter", ct);

        var partyPhotos = await db.PartyPhotos.AsNoTracking().Where(x => x.PartyId == id).CountAsync(ct);
        if (partyPhotos > 0)
        {
            Line(sb, "Fotos", partyPhotos.ToString());
        }

        await AppendAttachmentsAsync(sb, db, nameof(Party), id, includeClassificationHistory: true, view, ct);
        return new DossierContext($"{p.Name} ({p.CaseNumber})", sb.ToString(), p.IsRestricted);
    }

    static async Task<DossierContext?> BuildOperationAsync(AppDbContext db, string id, ViewerScope? scope, CancellationToken ct)
    {
        var o = await db.Operations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (o is null)
        {
            return null;
        }
        var view = scope ?? DossierScope.ForRecord(o.SecrecyLevel);

        var sb = new StringBuilder();
        sb.AppendLine("Operationsakte");
        Line(sb, "Aktenzeichen", o.CaseNumber);
        Line(sb, "Titel", o.Title);
        Line(sb, "Typ", o.Type);
        Line(sb, "Status", OperationStatusDisplay.Name(o.Status));
        Line(sb, "Einstufung", ClassificationDisplay.Name(o.Classification));
        Line(sb, "Verschlusssache", o.IsRestricted ? $"Ja ({o.SecrecyLevel})" : "Nein");
        Line(sb, "Ort", o.Location);
        Line(sb, "Beginn", Fmt(o.Start));
        Line(sb, "Ende", Fmt(o.End));
        Line(sb, "Ablauf", o.Expiry);
        Line(sb, "Ergebnis", o.Result);
        Line(sb, "Bemerkungen", o.Remarks);

        await AppendAgentsAsync(sb, db,
            db.OperationAgents.AsNoTracking().Where(x => x.OperationId == id)
                .Select(x => new AgentProj { AgentId = x.AgentId, Flag = x.IsInvestigationLead }),
            "Ermittlungsleiter", ct);

        await AppendAttachmentsAsync(sb, db, nameof(Operation), id, includeClassificationHistory: true, view, ct);
        return new DossierContext($"{o.Title} ({o.CaseNumber})", sb.ToString(), o.IsRestricted);
    }

    static async Task<DossierContext?> BuildCaseAsync(AppDbContext db, string id, ViewerScope? scope, CancellationToken ct)
    {
        var c = await db.Cases.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null)
        {
            return null;
        }
        var view = scope ?? DossierScope.ForRecord(c.SecrecyLevel);

        var sb = new StringBuilder();
        sb.AppendLine("Vorgangsakte");
        Line(sb, "Aktenzeichen", c.CaseNumber);
        Line(sb, "Titel", c.Title);
        Line(sb, "Typ", c.Type);
        Line(sb, "Status", CaseStatusDisplay.Name(c.Status));
        Line(sb, "Einstufung", ClassificationDisplay.Name(c.Classification));
        Line(sb, "Verschlusssache", c.IsRestricted ? $"Ja ({c.SecrecyLevel})" : "Nein");
        Line(sb, "Beschreibung", c.Description);
        Line(sb, "Zusammenfassung", c.Summary);
        Line(sb, "Abschlussvermerk", c.ClosingNote);
        Line(sb, "Abgeschlossen am", Fmt(c.CompletedAt));

        await AppendAgentsAsync(sb, db,
            db.CaseAgents.AsNoTracking().Where(x => x.CaseId == id)
                .Select(x => new AgentProj { AgentId = x.AgentId, Flag = x.IsCaseLead }),
            "Fallführer", ct);

        await AppendAttachmentsAsync(sb, db, nameof(Case), id, includeClassificationHistory: true, view, ct);
        return new DossierContext($"{c.Title} ({c.CaseNumber})", sb.ToString(), c.IsRestricted);
    }

    static async Task<DossierContext?> BuildTaskforceAsync(AppDbContext db, string id, ViewerScope? scope, CancellationToken ct)
    {
        var t = await db.Taskforces.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (t is null)
        {
            return null;
        }
        var view = scope ?? DossierScope.ForRecord(
            t.IsClassified ? DocumentClassification.Leadership : DocumentClassification.None);

        var sb = new StringBuilder();
        sb.AppendLine("Taskforce-Akte");
        Line(sb, "Aktenzeichen", t.CaseNumber);
        Line(sb, "Name", t.Name);
        Line(sb, "Zweck", t.Purpose);
        Line(sb, "Geltungsbereich", TaskforceScopeDisplay.Name(t.Scope));
        Line(sb, "Status", TaskforceStatusDisplay.Name(t.Status));
        Line(sb, "Verschlusssache", t.IsClassified);
        Line(sb, "Bemerkungen", t.Remarks);

        var (agents, agentsTotal) = await TakeAsync(
            db.TaskforceAgents.AsNoTracking().Where(x => x.TaskforceId == id)
                .Select(x => new { x.AgentId, x.Role }), ct);
        if (agentsTotal > 0)
        {
            // codename-safe agent roster
            var codenames = await CodenamesAsync(db, agents.Select(a => (string?)a.AgentId), ct);
            sb.AppendLine($"— Agenten ({agentsTotal}) —");
            foreach (var a in agents)
            {
                sb.Append("• ").Append(Codename(codenames, a.AgentId)).Append(" | Rolle: ").AppendLine(TaskforceRoleDisplay.Name(a.Role));
            }
        }

        var (messages, messagesTotal) = await TakeAsync(
            db.TaskforceMessages.AsNoTracking().Where(x => x.TaskforceId == id)
                .OrderBy(x => x.CreatedAt).Select(x => new { x.AuthorName, x.Text }), ct);
        if (messagesTotal > 0)
        {
            sb.AppendLine($"— Team-Chat ({messagesTotal}) —");
            foreach (var m in messages)
            {
                sb.Append("• ").Append(string.IsNullOrWhiteSpace(m.AuthorName) ? "?" : m.AuthorName)
                    .Append(": ").AppendLine(Free(m.Text));
            }
        }

        await AppendAttachmentsAsync(sb, db, nameof(Taskforce), id, includeClassificationHistory: false, view, ct);
        return new DossierContext($"{t.Name} ({t.CaseNumber})", sb.ToString(), t.IsClassified);
    }

    static async Task<DossierContext?> BuildDocumentAsync(AppDbContext db, string id, ViewerScope? scope, CancellationToken ct)
    {
        var d = await db.Documents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (d is null)
        {
            return null;
        }
        // documents use the leadership-exclusive reading of IsClassified, unlike the record types above
        var view = scope ?? DossierScope.ForRecord(
            d.IsClassified ? DocumentClassification.Leadership
                : d.IsTRUClassified ? DocumentClassification.Tru
                : d.IsHRBClassified ? DocumentClassification.Hrb
                : DocumentClassification.None);

        var sb = new StringBuilder();
        sb.AppendLine("Dokument");
        Line(sb, "Titel", d.Title);
        Line(sb, "Kategorie", d.Category);
        Line(sb, "VS-Stufe", DocumentClassificationDisplay.Label(d.Classification));
        Line(sb, "Angepinnt", d.Pinned);
        if (!string.IsNullOrEmpty(d.OwnerTaskforceId))
        {
            Line(sb, "Taskforce-intern", "Ja");
        }

        var body = StripHtml(d.ContentHtml);
        if (body.Length > 6000)
        {
            body = body[..6000] + " …";
        }
        if (body.Length > 0)
        {
            sb.AppendLine("— Inhalt —");
            sb.AppendLine(body);
        }

        await AppendAttachmentsAsync(sb, db, nameof(Document), id, includeClassificationHistory: false, view, ct);
        return new DossierContext(d.Title, sb.ToString(), d.IsRestricted);
    }

    /// <summary>A statute. The only stock with no classification at all, so the viewer scope is only ever used to
    /// mask the records linked to it.</summary>
    static async Task<DossierContext?> BuildLawAsync(AppDbContext db, string id, ViewerScope? scope, CancellationToken ct)
    {
        var l = await db.Laws.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (l is null)
        {
            return null;
        }
        var view = scope ?? DossierScope.ForRecord(DocumentClassification.None);

        var sb = new StringBuilder();
        sb.AppendLine("Gesetz");
        Line(sb, "Gesetzbuch", l.LawBook);
        Line(sb, "Paragraf", l.Paragraph);
        Line(sb, "Titel", l.Title);
        Line(sb, "Strafmaß", l.Sentence);
        Line(sb, "Abschnitt", l.Section);
        if (Free(StripHtml(l.Text)) is { Length: > 0 } text)
        {
            sb.AppendLine("— Wortlaut —");
            sb.AppendLine(text);
        }

        await AppendAttachmentsAsync(sb, db, nameof(Law), id, includeClassificationHistory: false, view, ct);
        var title = $"{l.Paragraph} {l.Title}".Trim();
        return new DossierContext(title.Length == 0 ? "Gesetz" : title, sb.ToString(), false);
    }

    // ---- shared section renderers ----

    static async Task AppendMembersAsync(StringBuilder sb, IQueryable<MemberProj> query, string roleLabel, ViewerScope view, CancellationToken ct)
    {
        var (rows, total) = await TakeAsync(query, ct);
        if (total == 0)
        {
            return;
        }
        sb.AppendLine($"— Mitglieder ({total}) —");
        foreach (var m in rows)
        {
            // a member is masked whenever this viewer may not see them at their own secrecy level
            var shown = string.IsNullOrWhiteSpace(m.Name) ? "(unbekannt)"
                : view.CanSee(DossierScope.LevelOf(m.Classified, m.Tru, m.Hrb)) ? m.Name : "(Verschlusssache)";
            sb.Append("• ").Append(shown);
            if (!string.IsNullOrWhiteSpace(m.RoleOrRank))
            {
                sb.Append(" | ").Append(roleLabel).Append(": ").Append(m.RoleOrRank);
            }
            if (m.IsLead)
            {
                sb.Append(" | Leitung");
            }
            sb.AppendLine();
        }
    }

    /// <summary>The organisations a person belongs to, from the person's side.</summary>
    /// <remarks>
    /// Only the organisation dossiers carried their roster, so a person's file never named the faction the
    /// analysis usually turns on — the strongest fact about a person was readable in one direction only.
    /// </remarks>
    static async Task AppendAffiliationsAsync(StringBuilder sb, AppDbContext db, string personId, ViewerScope view, CancellationToken ct)
    {
        var rows = new List<AffiliationProj>();
        rows.AddRange(await db.FactionMembers.AsNoTracking().Where(m => m.PersonId == personId)
            .Select(m => new AffiliationProj
            {
                Kind = "Fraktion",
                Name = m.Faction != null ? m.Faction.Name : null,
                CaseNumber = m.Faction != null ? m.Faction.CaseNumber : null,
                Classified = m.Faction != null && m.Faction.IsClassified,
                Tru = m.Faction != null && m.Faction.IsTRUClassified,
                Hrb = m.Faction != null && m.Faction.IsHRBClassified,
                RoleLabel = "Rang",
                RoleOrRank = m.Rank,
                IsLead = m.IsLead,
            }).Take(25).ToListAsync(ct));
        rows.AddRange(await db.PersonGroupMembers.AsNoTracking().Where(m => m.PersonId == personId)
            .Select(m => new AffiliationProj
            {
                Kind = "Personengruppe",
                Name = m.PersonGroup != null ? m.PersonGroup.Name : null,
                CaseNumber = m.PersonGroup != null ? m.PersonGroup.CaseNumber : null,
                Classified = m.PersonGroup != null && m.PersonGroup.IsClassified,
                Tru = m.PersonGroup != null && m.PersonGroup.IsTRUClassified,
                Hrb = m.PersonGroup != null && m.PersonGroup.IsHRBClassified,
                RoleLabel = "Rolle",
                RoleOrRank = m.Role,
                IsLead = m.IsLead,
            }).Take(25).ToListAsync(ct));
        rows.AddRange(await db.PartyMembers.AsNoTracking().Where(m => m.PersonId == personId)
            .Select(m => new AffiliationProj
            {
                Kind = "Partei",
                Name = m.Party != null ? m.Party.Name : null,
                CaseNumber = m.Party != null ? m.Party.CaseNumber : null,
                Classified = m.Party != null && m.Party.IsClassified,
                Tru = m.Party != null && m.Party.IsTRUClassified,
                Hrb = m.Party != null && m.Party.IsHRBClassified,
                RoleLabel = "Rolle",
                RoleOrRank = m.Role,
                IsLead = m.IsLead,
            }).Take(25).ToListAsync(ct));

        if (rows.Count == 0)
        {
            return;
        }
        sb.AppendLine($"— Zugehörigkeiten ({rows.Count}) —");
        foreach (var a in rows)
        {
            sb.Append("• ").Append(a.Kind).Append(": ");
            if (string.IsNullOrWhiteSpace(a.Name))
            {
                sb.AppendLine("(unbekannt)");
                continue;
            }
            // masked against the organisation's own level, exactly like a member roster in the other direction
            if (!view.CanSee(DossierScope.LevelOf(a.Classified, a.Tru, a.Hrb)))
            {
                sb.AppendLine("(Verschlusssache)");
                continue;
            }
            sb.Append(a.Name);
            if (!string.IsNullOrWhiteSpace(a.CaseNumber))
            {
                sb.Append(" (").Append(a.CaseNumber).Append(')');
            }
            if (!string.IsNullOrWhiteSpace(a.RoleOrRank))
            {
                sb.Append(" | ").Append(a.RoleLabel).Append(": ").Append(a.RoleOrRank);
            }
            if (a.IsLead)
            {
                sb.Append(" | Leitung");
            }
            sb.AppendLine();
        }
    }

    static async Task AppendAgentsAsync(StringBuilder sb, AppDbContext db, IQueryable<AgentProj> query, string flagLabel, CancellationToken ct)
    {
        var (rows, total) = await TakeAsync(query, ct);
        if (total == 0)
        {
            return;
        }
        // codename-safe: assigned NOOSE agents are always shown by codename
        var codenames = await CodenamesAsync(db, rows.Select(r => (string?)r.AgentId), ct);
        sb.AppendLine($"— Agenten ({total}) —");
        foreach (var r in rows)
        {
            sb.Append("• ").Append(Codename(codenames, r.AgentId));
            if (r.Flag)
            {
                sb.Append(" | ").Append(flagLabel);
            }
            sb.AppendLine();
        }
    }

    static async Task AppendAttachmentsAsync(
        StringBuilder sb, AppDbContext db, string type, string id, bool includeClassificationHistory, ViewerScope view, CancellationToken ct)
    {
        var tags = await db.TagMappings.AsNoTracking()
            .Where(m => m.EntityType == type && m.EntityId == id)
            .Join(db.Tags, m => m.TagId, t => t.Id, (m, t) => t.Name)
            .Take(50).ToListAsync(ct);
        if (tags.Count > 0)
        {
            sb.AppendLine($"— Tags ({tags.Count}) —");
            sb.AppendLine(string.Join(", ", tags));
        }

        var (sources, sourcesTotal) = await TakeAsync(
            db.Sources.AsNoTracking().Where(s => s.EntityType == type && s.EntityId == id)
                .OrderByDescending(s => s.Pinned).ThenBy(s => s.CreatedAt)
                .Select(s => new { s.Title, s.Type, s.Description, s.Url }), ct);
        if (sourcesTotal > 0)
        {
            sb.AppendLine($"— Quellen ({sourcesTotal}) —");
            foreach (var s in sources)
            {
                var extra = !string.IsNullOrWhiteSpace(s.Description) ? s.Description
                    : !string.IsNullOrWhiteSpace(s.Url) ? s.Url : null;
                sb.Append("• ").Append(SourceTypeDisplay.Name(s.Type)).Append(": ")
                    .Append(string.IsNullOrWhiteSpace(s.Title) ? "(ohne Titel)" : Free(s.Title));
                if (Free(extra) is { Length: > 0 } detail)
                {
                    sb.Append(" — ").Append(detail);
                }
                sb.AppendLine();
            }
        }

        var (comments, commentsTotal) = await TakeAsync(
            db.Comments.AsNoTracking().Where(c => c.EntityType == type && c.EntityId == id)
                .OrderBy(c => c.CreatedAt).Select(c => new { c.AuthorName, c.Text }), ct);
        if (commentsTotal > 0)
        {
            sb.AppendLine($"— Kommentare ({commentsTotal}) —");
            foreach (var c in comments)
            {
                sb.Append("• ").Append(string.IsNullOrWhiteSpace(c.AuthorName) ? "?" : c.AuthorName)
                    .Append(": ").AppendLine(Free(c.Text));
            }
        }

        var (followups, followupsTotal) = await TakeAsync(
            db.Followups.AsNoTracking().Where(f => f.EntityType == type && f.EntityId == id)
                .OrderBy(f => f.DueAt).Select(f => new { f.DueAt, f.Note, f.Done }), ct);
        if (followupsTotal > 0)
        {
            sb.AppendLine($"— Wiedervorlagen ({followupsTotal}) —");
            foreach (var f in followups)
            {
                sb.Append("• ").Append(Fmt(f.DueAt)).Append(f.Done ? " [erledigt] " : " [offen] ")
                    .AppendLine(Free(f.Note));
            }
        }

        var customFields = await db.CustomFieldValues.AsNoTracking()
            .Where(v => v.EntityType == type && v.EntityId == id)
            .Join(db.CustomFieldDefinitions, v => v.CustomFieldDefinitionId, def => def.Id,
                (v, def) => new { def.Name, def.Order, v.Value })
            .OrderBy(x => x.Order).Take(50).ToListAsync(ct);
        if (customFields.Count > 0)
        {
            sb.AppendLine($"— Weitere Felder ({customFields.Count}) —");
            foreach (var cf in customFields)
            {
                Line(sb, cf.Name, cf.Value);
            }
        }

        var linkBase = db.Links.AsNoTracking()
            .Where(l => (l.SourceType == type && l.SourceId == id) || (l.TargetType == type && l.TargetId == id));
        var linksTotal = await linkBase.CountAsync(ct);
        if (linksTotal > 0)
        {
            var links = await linkBase.OrderByDescending(l => l.CreatedAt).Take(50)
                .Select(l => new { l.SourceType, l.SourceId, l.TargetType, l.TargetId, l.Label }).ToListAsync(ct);
            var refs = new List<(string, string)>();
            foreach (var l in links)
            {
                var other = l.SourceType == type && l.SourceId == id
                    ? (l.TargetType, l.TargetId)
                    : (l.SourceType, l.SourceId);
                refs.Add(other);
            }
            // taskforce membership is this viewer's, not a blanket "may see all" — otherwise every linked
            // taskforce's name and case number lands in the dossier regardless of who is reading it
            var resolved = await RecordsReference.ResolveAsync(db, refs.Distinct().ToList(), ct,
                mayAllTaskforces: view.MayAllTaskforces, meId: view.MeId);
            sb.AppendLine($"— Verknüpfungen ({linksTotal}) —");
            foreach (var l in links)
            {
                var other = l.SourceType == type && l.SourceId == id
                    ? (l.TargetType, l.TargetId)
                    : (l.SourceType, l.SourceId);
                string display;
                if (resolved.TryGetValue(other, out var r))
                {
                    // the resolver only reports a bool, so this masks conservatively: a TRU-classified link
                    // stays hidden from a TRU agent too, which is never a leak, only an omission
                    display = r.Classified && !view.MayClassifiedRead ? "(Verschlusssache)" : r.Display;
                }
                else
                {
                    display = $"{GermanType(other.Item1)} (unbekannt)"; // never surface a raw type+GUID
                }
                var label = string.IsNullOrWhiteSpace(l.Label) ? "Verknüpfung" : Free(l.Label);
                sb.Append("• ").Append(label).Append(": ").AppendLine(display);
            }
        }

        if (includeClassificationHistory)
        {
            var history = await db.ClassificationHistory.AsNoTracking()
                .Where(h => h.EntityType == type && h.EntityId == id)
                .OrderByDescending(h => h.Timestamp).Take(10)
                .Select(h => new { h.Timestamp, h.Value, h.Justification }).ToListAsync(ct);
            if (history.Count > 0)
            {
                sb.AppendLine($"— Einstufungsverlauf ({history.Count}) —");
                foreach (var h in history)
                {
                    sb.Append("• ").Append(Fmt(h.Timestamp)).Append(": ").Append(ClassificationDisplay.Name(h.Value));
                    if (Free(h.Justification) is { Length: > 0 } why)
                    {
                        sb.Append(" — ").Append(why);
                    }
                    sb.AppendLine();
                }
            }
        }
    }

    // ---- helpers ----

    static async Task<Dictionary<string, string>> CodenamesAsync(AppDbContext db, IEnumerable<string?> ids, CancellationToken ct)
    {
        var list = ids.Where(x => !string.IsNullOrEmpty(x)).Select(x => x!).Distinct().ToList();
        if (list.Count == 0)
        {
            return new Dictionary<string, string>();
        }
        var rows = await db.Users.AsNoTracking().Where(u => list.Contains(u.Id))
            .Select(u => new { u.Id, u.Codename }).ToListAsync(ct);
        return rows.ToDictionary(
            r => r.Id,
            r => string.IsNullOrWhiteSpace(r.Codename) ? "(unbenannter Agent)" : r.Codename);
    }

    static string Codename(Dictionary<string, string> map, string? agentId)
        => agentId is not null && map.TryGetValue(agentId, out var cn) ? cn : "(unbekannter Agent)";

    // German display name for a record type, used when a linked record can no longer be resolved. The catalog
    // names all of them; a nine-arm copy here answered "Eintrag" for every kind a link may also point at.
    static string GermanType(string type) => SearchCatalog.German(type);

    static async Task<(List<T> Items, int Total)> TakeAsync<T>(IQueryable<T> query, CancellationToken ct)
    {
        var total = await query.CountAsync(ct);
        var items = total == 0 ? new List<T>() : await query.Take(50).ToListAsync(ct);
        return (items, total);
    }

    static void Line(StringBuilder sb, string label, string? value)
    {
        if (Free(value) is { Length: > 0 } text)
        {
            sb.Append(label).Append(": ").AppendLine(text);
        }
    }

    /// <summary>Free text on its way out. Mention tokens carry raw GUIDs of possibly classified records and would
    /// otherwise ship verbatim, so they are dropped here rather than at every append site.</summary>
    static string Free(string? text) => MentionParser.Strip(text).Trim();

    static void Line(StringBuilder sb, string label, bool value)
        => sb.Append(label).Append(": ").AppendLine(value ? "Ja" : "Nein");

    static string Fmt(DateTime dt) => dt.ToString("dd.MM.yyyy HH:mm");

    static string? Fmt(DateTime? dt) => dt.HasValue ? dt.Value.ToString("dd.MM.yyyy HH:mm") : null;

    static string StripHtml(string? html) => HtmlCleanup.PlainText(html);

    /// <summary>Projection shape for a record's member roster.</summary>
    sealed class MemberProj
    {
        public string? Name { get; set; }
        public bool Classified { get; set; }
        public bool Tru { get; set; }
        public bool Hrb { get; set; }
        public string? RoleOrRank { get; set; }
        public bool IsLead { get; set; }
    }

    /// <summary>Projection shape for one organisation a person belongs to.</summary>
    sealed class AffiliationProj
    {
        public string Kind { get; set; } = string.Empty;
        public string? Name { get; set; }
        public string? CaseNumber { get; set; }
        public bool Classified { get; set; }
        public bool Tru { get; set; }
        public bool Hrb { get; set; }
        public string RoleLabel { get; set; } = "Rolle";
        public string? RoleOrRank { get; set; }
        public bool IsLead { get; set; }
    }

    /// <summary>Projection shape for a record's assigned-agent roster.</summary>
    sealed class AgentProj
    {
        public string AgentId { get; set; } = string.Empty;
        public bool Flag { get; set; }
    }
}
