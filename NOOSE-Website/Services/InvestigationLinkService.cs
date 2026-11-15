using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Appointments;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.DrugRoutes;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Investigations;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Meetings;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Presentations;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Data.Entities.Regulation;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Data.Entities.Warrants;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Investigations;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IInvestigationLinkService" />
/// <remarks>
/// Never through <c>db.Links</c>: about fifteen readers of that table know no viewer (graph, leads, threat score,
/// timeline, dossier …) and would hand a file's title to anyone. Only this service reads <c>ErmittlungVerknuepfungen</c>,
/// and every row it returns has passed the viewer's check on both ends.
/// </remarks>
public class InvestigationLinkService(IDbContextFactory<AppDbContext> dbFactory) : IInvestigationLinkService
{
    public const int LabelMaxLength = 200;

    // a file with more links than this shows the newest; nothing is lost, it is only not listed
    private const int MaxRows = 200;
    private const int PickerHits = 10;
    // files resolved in one batch when only editable ones count, so an older file one leads is still offered
    private const int PickerScan = 200;

    /// <summary>Records a file can be linked to: every type whose page shows a links area, plus another file.</summary>
    /// <remarks>Document, personnel file, ticket, doc and observation are known link types but list no links on
    /// their own page, so a link there would be visible from the file only.</remarks>
    public static readonly IReadOnlyList<string> RecordTypes =
    [
        nameof(Person), nameof(Faction), nameof(PersonGroup), nameof(Party), nameof(DrugRoute), nameof(Operation), nameof(Taskforce),
        nameof(Case), nameof(Job), nameof(Appointment), nameof(Meeting), nameof(MeetingAgendaItem), nameof(Law),
        nameof(Bewerbung), nameof(Warrant), nameof(Hinweis), nameof(RegulationParagraph), nameof(Presentation),
        nameof(Investigation),
    ];

    /// <summary>True for a type a file may be linked to.</summary>
    public static bool IsLinkable(string? type) => type is not null && RecordTypes.Contains(type, StringComparer.Ordinal);

    // ---- reading ----

    public async Task<List<InvestigationLinkView>> GetForRecordAsync(string recordType, string recordId, ClaimsPrincipal viewer,
        CancellationToken cancellationToken = default)
    {
        // a file's own links are read through GetForInvestigationAsync, with its tab rights
        if (!IsLinkable(recordType) || recordType == nameof(Investigation))
        {
            return [];
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await InvestigationReferences.ViewerAsync(db, ViewerScope.From(viewer), cancellationToken) is not { } scope
            || !await Visibility.IsRecordVisibleAsync(db, recordType, recordId, scope, cancellationToken))
        {
            return [];
        }
        var visible = db.Investigations.AsNoTracking().OnlyVisible(db, scope);
        var rows = await db.InvestigationLinks.AsNoTracking()
            .Where(l => l.RecordType == recordType && l.RecordId == recordId)
            .Join(visible, l => l.InvestigationId, i => i.Id, (l, i) => new { l.Id, l.InvestigationId, l.Label, l.CreatedAt, i.CaseNumber, i.Title })
            .OrderByDescending(r => r.CreatedAt)
            .Take(MaxRows)
            .ToListAsync(cancellationToken);
        var mayRemove = viewer.MayWrite();
        return rows.Select(r => new InvestigationLinkView(r.Id, r.InvestigationId, $"{r.CaseNumber} · {r.Title}",
                InvestigationReferences.Href(nameof(Investigation), r.InvestigationId, r.InvestigationId), r.Label, mayRemove))
            .ToList();
    }

    public async Task<List<InvestigationLinkedRecord>> GetForInvestigationAsync(string investigationId, ClaimsPrincipal viewer,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var access = await InvestigationAccess.ResolveAsync(db, investigationId, viewer, cancellationToken);
        if (!access.CanRead(InvestigationTabs.Overview)
            || await InvestigationReferences.ViewerAsync(db, ViewerScope.From(viewer), cancellationToken) is not { } scope)
        {
            return [];
        }
        var mayEdit = access.CanEdit(InvestigationTabs.Overview);

        // a file to file link is one row, read from both files
        var rows = (await db.InvestigationLinks.AsNoTracking()
                .Where(l => l.InvestigationId == investigationId
                    || (l.RecordType == nameof(Investigation) && l.RecordId == investigationId))
                .OrderByDescending(l => l.CreatedAt)
                .Take(MaxRows)
                .Select(l => new { l.Id, l.InvestigationId, l.RecordType, l.RecordId, l.Label })
                .ToListAsync(cancellationToken))
            .Select(l => l.InvestigationId == investigationId
                ? (l.Id, Own: true, Type: l.RecordType, OtherId: l.RecordId, l.Label)
                : (l.Id, Own: false, Type: nameof(Investigation), OtherId: l.InvestigationId, l.Label))
            .Where(l => IsLinkable(l.Type) && !(l.Type == nameof(Investigation) && l.OtherId == investigationId))
            .ToList();

        var labels = await VisibleLabelsAsync(db, rows.Select(r => (r.Type, r.OtherId)).Distinct().ToList(), scope, cancellationToken);
        return rows
            .Where(r => labels.ContainsKey((r.Type, r.OtherId)))
            .Select(r =>
            {
                var (display, href) = labels[(r.Type, r.OtherId)];
                return new InvestigationLinkedRecord(r.Id, r.Type, r.OtherId, display, href, r.Label,
                    MayEditLabel: mayEdit && r.Own, MayRemove: mayEdit);
            })
            .ToList();
    }

    public async Task<List<InvestigationPick>> FindInvestigationsAsync(string? text, ClaimsPrincipal viewer, bool editableOnly,
        IReadOnlyCollection<string>? exclude = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await InvestigationReferences.ViewerAsync(db, ViewerScope.From(viewer), cancellationToken) is not { } scope)
        {
            return [];
        }
        var s = text?.Trim();
        var query = db.Investigations.AsNoTracking().OnlyVisible(db, scope);
        if (!string.IsNullOrEmpty(s))
        {
            query = query.Where(i => i.Title.Contains(s) || i.CaseNumber.Contains(s) || (i.SidCaseNumber != null && i.SidCaseNumber.Contains(s)));
        }
        // before the Take: already linked files would otherwise fill the slots and hide older ones
        var skip = exclude?.Where(e => !string.IsNullOrEmpty(e)).Distinct().ToList() ?? [];
        if (skip.Count > 0)
        {
            query = query.Where(i => !skip.Contains(i.Id));
        }
        var rows = await query.OrderByDescending(i => i.OpenedAt)
            .Take(editableOnly ? PickerScan : PickerHits)
            .Select(i => new InvestigationPick(i.Id, i.CaseNumber, i.Title))
            .ToListAsync(cancellationToken);
        if (!editableOnly)
        {
            return rows;
        }
        // one batch for the whole window: per keystroke, a resolution per file would be a query burst
        var access = await InvestigationAccess.ResolveManyAsync(db, rows.Select(r => r.Id).ToList(), viewer, cancellationToken);
        return rows
            .Where(r => access.GetValueOrDefault(r.Id)?.CanEdit(InvestigationTabs.Overview) == true)
            .Take(PickerHits)
            .ToList();
    }

    // ---- writing ----

    public async Task<string> CreateAsync(string investigationId, string recordType, string recordId, string? label, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        if (!IsLinkable(recordType))
        {
            throw new InvalidOperationException("Dieser Aktentyp lässt sich nicht mit einer Ermittlung verknüpfen.");
        }
        if (recordType == nameof(Investigation) && recordId == investigationId)
        {
            throw new InvalidOperationException("Eine Ermittlung kann nicht mit sich selbst verknüpft werden.");
        }
        var trimmed = CleanLabel(label);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var access = await InvestigationAccess.ResolveAsync(db, investigationId, actor, cancellationToken);
        InvestigationAccess.RequireEdit(access, InvestigationTabs.Overview);
        if (await InvestigationReferences.ViewerAsync(db, ViewerScope.From(actor), cancellationToken) is not { } scope
            || !await IsVisibleAsync(db, recordType, recordId, scope, cancellationToken))
        {
            throw new UnauthorizedAccessException("Auf diese Akte darfst du nicht verlinken (Verschlusssache oder nicht vorhanden).");
        }

        // one live row per pair; a file to file pair counts in either direction
        var exists = await db.InvestigationLinks.AnyAsync(l =>
            (l.InvestigationId == investigationId && l.RecordType == recordType && l.RecordId == recordId)
            || (recordType == nameof(Investigation) && l.InvestigationId == recordId
                && l.RecordType == nameof(Investigation) && l.RecordId == investigationId),
            cancellationToken);
        if (exists)
        {
            throw new InvalidOperationException("Diese Akte ist mit der Ermittlung schon verknüpft.");
        }

        var row = new InvestigationLink
        {
            InvestigationId = investigationId,
            RecordType = recordType,
            RecordId = recordId,
            Label = trimmed,
        };
        db.InvestigationLinks.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return row.Id;
    }

    public async Task UpdateLabelAsync(string linkId, string? label, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var trimmed = CleanLabel(label);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.InvestigationLinks.FirstOrDefaultAsync(l => l.Id == linkId, cancellationToken)
            ?? throw new KeyNotFoundException("Die Verknüpfung wurde nicht gefunden.");
        var access = await InvestigationAccess.ResolveAsync(db, row.InvestigationId, actor, cancellationToken);
        InvestigationAccess.RequireEdit(access, InvestigationTabs.Overview);
        row.Label = trimmed;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveAsync(string linkId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.InvestigationLinks.FirstOrDefaultAsync(l => l.Id == linkId, cancellationToken)
            ?? throw new KeyNotFoundException("Die Verknüpfung wurde nicht gefunden.");
        if (!await MayRemoveAsync(db, row, actor, cancellationToken))
        {
            throw new UnauthorizedAccessException("Keine Berechtigung, diese Verknüpfung zu entfernen.");
        }
        db.InvestigationLinks.Remove(row); // soft-delete via interceptor
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>The file's cover sheet right, or the record end: write on the record and sight of the file.</summary>
    private static async Task<bool> MayRemoveAsync(AppDbContext db, InvestigationLink row, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        if ((await InvestigationAccess.ResolveAsync(db, row.InvestigationId, actor, cancellationToken)).CanEdit(InvestigationTabs.Overview))
        {
            return true;
        }
        if (row.RecordType == nameof(Investigation))
        {
            // the other file's cover sheet is the write right on that end; seeing this one comes with the link
            return (await InvestigationAccess.ResolveAsync(db, row.RecordId, actor, cancellationToken)).CanEdit(InvestigationTabs.Overview)
                && await InvestigationAccess.IsVisibleAsync(db, row.InvestigationId, ViewerScope.From(actor), cancellationToken);
        }
        if (!actor.MayWrite() || await InvestigationReferences.ViewerAsync(db, ViewerScope.From(actor), cancellationToken) is not { } scope)
        {
            return false;
        }
        return await InvestigationAccess.IsVisibleAsync(db, row.InvestigationId, scope, cancellationToken)
            && await Visibility.IsRecordVisibleAsync(db, row.RecordType, row.RecordId, scope, cancellationToken);
    }

    // ---- helpers ----

    private static string? CleanLabel(string? label)
    {
        var trimmed = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        if (trimmed is { Length: > LabelMaxLength })
        {
            throw new InvalidOperationException($"Die Bezeichnung darf höchstens {LabelMaxLength} Zeichen lang sein.");
        }
        return trimmed;
    }

    /// <summary>Exists, is not trashed and the viewer may open it; a warrant of a file lives in that file's own tab.</summary>
    private static async Task<bool> IsVisibleAsync(AppDbContext db, string type, string id, ViewerScope scope, CancellationToken cancellationToken)
    {
        if (type == nameof(Investigation))
        {
            return await InvestigationAccess.IsVisibleAsync(db, id, scope, cancellationToken);
        }
        if (type == nameof(Warrant) && await db.Warrants.AnyAsync(w => w.Id == id && w.InvestigationId != null, cancellationToken))
        {
            return false;
        }
        return await RecordBatch.ExistsAndVisibleAsync(db, type, id, scope, cancellationToken);
    }

    /// <summary>Label and route of every linked record the viewer may open; the rest is left out of the map.</summary>
    private static async Task<Dictionary<(string Type, string Id), (string Display, string? Href)>> VisibleLabelsAsync(
        AppDbContext db, IReadOnlyCollection<(string Type, string Id)> refs, ViewerScope scope, CancellationToken cancellationToken)
    {
        var map = new Dictionary<(string Type, string Id), (string Display, string? Href)>();
        if (refs.Count == 0)
        {
            return map;
        }
        List<string> Ids(string type) => refs.Where(r => r.Type == type).Select(r => r.Id).Distinct().ToList();

        // files: one query through the participant predicate
        var fileIds = Ids(nameof(Investigation));
        if (fileIds.Count > 0)
        {
            foreach (var f in await db.Investigations.AsNoTracking().Where(i => fileIds.Contains(i.Id)).OnlyVisible(db, scope)
                         .Select(i => new { i.Id, i.CaseNumber, i.Title }).ToListAsync(cancellationToken))
            {
                map[(nameof(Investigation), f.Id)] = ($"{f.CaseNumber} · {f.Title}", InvestigationReferences.Href(nameof(Investigation), f.Id, f.Id));
            }
        }

        // labels first, then the viewer's own check per record
        var labels = new Dictionary<(string Type, string Id), (string Display, string? Href)>();
        foreach (var (key, res) in await RecordsReference.ResolveAsync(db,
                     refs.Where(r => r.Type is not (nameof(Investigation) or nameof(Law) or nameof(Bewerbung) or nameof(Warrant)
                         or nameof(Presentation) or nameof(MeetingAgendaItem))).ToList(),
                     cancellationToken, scope.MayAllTaskforces, scope.MeId))
        {
            labels[key] = (res.Display, res.Href);
        }

        var lawIds = Ids(nameof(Law));
        if (lawIds.Count > 0)
        {
            foreach (var x in await db.Laws.AsNoTracking().Where(l => lawIds.Contains(l.Id))
                         .Select(l => new { l.Id, l.Paragraph, l.Title, l.LawBook }).ToListAsync(cancellationToken))
            {
                labels[(nameof(Law), x.Id)] = ($"{x.Paragraph} {x.Title} ({x.LawBook})", $"/gesetze/{x.Id}");
            }
        }
        var bewerbungIds = Ids(nameof(Bewerbung));
        if (bewerbungIds.Count > 0)
        {
            foreach (var x in await db.Bewerbungen.AsNoTracking().Where(b => bewerbungIds.Contains(b.Id))
                         .Select(b => new { b.Id, b.Name, b.CaseNumber }).ToListAsync(cancellationToken))
            {
                labels[(nameof(Bewerbung), x.Id)] = ($"{x.Name} ({x.CaseNumber})", $"/bewerbungen/{x.Id}");
            }
        }
        var warrantIds = Ids(nameof(Warrant));
        if (warrantIds.Count > 0)
        {
            foreach (var x in await db.Warrants.AsNoTracking().Where(w => warrantIds.Contains(w.Id) && w.InvestigationId == null)
                         .Select(w => new { w.Id, w.CaseNumber, w.Type }).ToListAsync(cancellationToken))
            {
                labels[(nameof(Warrant), x.Id)] = ($"{WarrantTypeDisplay.Name(x.Type)} {x.CaseNumber}", $"/anordnungen/{x.Id}");
            }
        }
        var presentationIds = Ids(nameof(Presentation));
        if (presentationIds.Count > 0)
        {
            foreach (var x in await db.Presentations.AsNoTracking().Where(p => presentationIds.Contains(p.Id))
                         .Select(p => new { p.Id, p.Title }).ToListAsync(cancellationToken))
            {
                labels[(nameof(Presentation), x.Id)] = (x.Title, $"/praesentationen/{x.Id}");
            }
        }
        var agendaIds = Ids(nameof(MeetingAgendaItem));
        if (agendaIds.Count > 0)
        {
            foreach (var x in await db.MeetingAgendaItems.AsNoTracking().Where(p => agendaIds.Contains(p.Id))
                         .Join(db.Meetings, p => p.MeetingId, m => m.Id, (p, m) => new { p.Id, p.Title, MeetingId = m.Id, MeetingTitle = m.Title })
                         .ToListAsync(cancellationToken))
            {
                labels[(nameof(MeetingAgendaItem), x.Id)] = ($"{x.MeetingTitle} – {x.Title}", $"/besprechungen/{x.MeetingId}?tab=agenda#top-{x.Id}");
            }
        }

        // the resolver above knows no secrecy scope; the viewer's gate decides, a partner through the releases
        var visible = await VisibleRecordsAsync(db, labels.Keys.ToList(), scope, cancellationToken);
        foreach (var (key, value) in labels.Where(l => visible.Contains(l.Key)))
        {
            map[key] = value;
        }
        return map;
    }

    /// <summary>Of the given records, those the viewer may open: one query per type where a list predicate exists,
    /// the point check of <see cref="IsVisibleAsync"/> for the rare rest.</summary>
    /// <remarks>Each list predicate is the one the point check runs for a single id, so both answer alike.</remarks>
    private static async Task<HashSet<(string Type, string Id)>> VisibleRecordsAsync(AppDbContext db,
        IReadOnlyCollection<(string Type, string Id)> keys, ViewerScope scope, CancellationToken cancellationToken)
    {
        var result = new HashSet<(string Type, string Id)>();
        foreach (var group in keys.GroupBy(k => k.Type))
        {
            var ids = group.Select(k => k.Id).Distinct().ToList();
            if (await VisibleIdsAsync(db, group.Key, ids, scope, cancellationToken) is { } batch)
            {
                result.UnionWith(batch.Select(id => (group.Key, id)));
                continue;
            }
            foreach (var id in ids)
            {
                if (await IsVisibleAsync(db, group.Key, id, scope, cancellationToken))
                {
                    result.Add((group.Key, id));
                }
            }
        }
        return result;
    }

    /// <summary>The visible ids of one type in one query; null for a type without a list predicate here.</summary>
    private static async Task<List<string>?> VisibleIdsAsync(AppDbContext db, string type, List<string> ids, ViewerScope scope,
        CancellationToken cancellationToken)
    {
        if (scope.PartnerAgency is { } agency)
        {
            var me = scope.MeId;
            return type switch
            {
                nameof(Person) => await db.People.Where(x => ids.Contains(x.Id)).OnlyPartnerVisible(db, agency, me).Select(x => x.Id).ToListAsync(cancellationToken),
                nameof(Faction) => await db.Factions.Where(x => ids.Contains(x.Id)).OnlyPartnerVisible(db, agency, me).Select(x => x.Id).ToListAsync(cancellationToken),
                nameof(PersonGroup) => await db.PersonGroups.Where(x => ids.Contains(x.Id)).OnlyPartnerVisible(db, agency, me).Select(x => x.Id).ToListAsync(cancellationToken),
                nameof(Party) => await db.Parties.Where(x => ids.Contains(x.Id)).OnlyPartnerVisible(db, agency, me).Select(x => x.Id).ToListAsync(cancellationToken),
                nameof(DrugRoute) => await db.DrugRoutes.Where(x => ids.Contains(x.Id)).OnlyPartnerVisible(db, agency, me).Select(x => x.Id).ToListAsync(cancellationToken),
                nameof(Operation) => await db.Operations.Where(x => ids.Contains(x.Id)).OnlyPartnerVisible(db, agency, me).Select(x => x.Id).ToListAsync(cancellationToken),
                nameof(Case) => await db.Cases.Where(x => ids.Contains(x.Id)).OnlyPartnerVisible(db, agency, me).Select(x => x.Id).ToListAsync(cancellationToken),
                nameof(Taskforce) => await db.Taskforces.Where(x => ids.Contains(x.Id)).OnlyPartnerVisible(db, agency, me).Select(x => x.Id).ToListAsync(cancellationToken),
                nameof(Law) => await db.Laws.Where(x => ids.Contains(x.Id)).OnlyPartnerVisible(db, agency, me).Select(x => x.Id).ToListAsync(cancellationToken),
                _ => null,
            };
        }
        return type switch
        {
            nameof(Person) => await db.People.Where(x => ids.Contains(x.Id)).OnlyVisible(scope).Select(x => x.Id).ToListAsync(cancellationToken),
            nameof(Faction) => await db.Factions.Where(x => ids.Contains(x.Id)).OnlyVisible(scope).Select(x => x.Id).ToListAsync(cancellationToken),
            nameof(PersonGroup) => await db.PersonGroups.Where(x => ids.Contains(x.Id)).OnlyVisible(scope).Select(x => x.Id).ToListAsync(cancellationToken),
            nameof(Party) => await db.Parties.Where(x => ids.Contains(x.Id)).OnlyVisible(scope).Select(x => x.Id).ToListAsync(cancellationToken),
            nameof(DrugRoute) => await db.DrugRoutes.Where(x => ids.Contains(x.Id)).OnlyVisible(scope).Select(x => x.Id).ToListAsync(cancellationToken),
            nameof(Operation) => await db.Operations.Where(x => ids.Contains(x.Id)).OnlyVisible(scope).Select(x => x.Id).ToListAsync(cancellationToken),
            nameof(Case) => await db.Cases.Where(x => ids.Contains(x.Id)).OnlyVisible(scope).Select(x => x.Id).ToListAsync(cancellationToken),
            nameof(Law) => await db.Laws.Where(x => ids.Contains(x.Id)).Select(x => x.Id).ToListAsync(cancellationToken),
            nameof(Meeting) => await db.Meetings.Where(x => ids.Contains(x.Id)).Select(x => x.Id).ToListAsync(cancellationToken),
            nameof(Job) => await db.Jobs.OnlyVisible(db, scope.MayClassifiedRead, scope.MeId).Where(x => ids.Contains(x.Id)).Select(x => x.Id).ToListAsync(cancellationToken),
            nameof(Appointment) => await db.Appointments.OnlyVisible(db, scope.MayClassifiedRead, scope.MeId).Where(x => ids.Contains(x.Id)).Select(x => x.Id).ToListAsync(cancellationToken),
            nameof(Presentation) => await PresentationVisibility.OnlyVisible(db.Presentations, db, scope).Where(x => ids.Contains(x.Id)).Select(x => x.Id).ToListAsync(cancellationToken),
            _ => null,
        };
    }
}
