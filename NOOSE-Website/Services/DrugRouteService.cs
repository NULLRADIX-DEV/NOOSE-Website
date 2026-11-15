using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.DrugRoutes;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.DrugRoutes;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IDrugRouteService" />
public class DrugRouteService(
    IDbContextFactory<AppDbContext> dbFactory, ICaseNumberService caseNumber, IPersonService personService,
    IThreatScoreService threat, INotificationService notifications, IDrugRoutePhotoStorageService photoStorage) : IDrugRouteService
{
    public const int MaxPhotosPerRoute = 60;

    // one designation per active route; serialises the check-then-write
    private static readonly SemaphoreSlim DesignationGate = new(1, 1);

    private static string MentionScope(DrugRoute r) => MentionNotify.Scope(r.Description, r.Findings, r.Remarks);

    public async Task<List<DrugRoute>> GetListAsync(ViewerScope scope, ArchiveFilter filter = ArchiveFilter.Active,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var routes = await VisibleRoutes(db, scope)
            .Apply(filter)
            .Include(r => r.Faction)
            .Include(r => r.Stations)
            .Include(r => r.Photos)
            .AsSplitQuery()
            .OrderByDescending(r => r.ModifiedAt ?? r.CreatedAt)
            .ToListAsync(cancellationToken);
        // a faction the viewer may not open stays unnamed
        var factionIds = routes.Where(r => r.FactionId is not null).Select(r => r.FactionId!).Distinct().ToList();
        var visibleFactions = new HashSet<string>();
        foreach (var fid in factionIds)
        {
            if (await Visibility.IsRecordVisibleAsync(db, nameof(Faction), fid, scope, cancellationToken))
            {
                visibleFactions.Add(fid);
            }
        }
        foreach (var r in routes.Where(r => r.FactionId is not null && !visibleFactions.Contains(r.FactionId)))
        {
            r.Faction = null;
        }
        return routes;
    }

    public async Task<DrugRoute?> GetDetailAsync(string id, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var route = await db.DrugRoutes
            .Include(r => r.Photos)
            .Include(r => r.Faction)
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (route is null || !await Visibility.IsRecordVisibleAsync(db, nameof(DrugRoute), id, scope, cancellationToken))
        {
            return null;
        }
        // a faction the viewer may not open stays unnamed
        if (route.FactionId is { } fid && !await Visibility.IsRecordVisibleAsync(db, nameof(Faction), fid, scope, cancellationToken))
        {
            route.Faction = null;
        }
        return route;
    }

    public async Task<List<DrugRoute>> GetTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.DrugRoutes.IgnoreQueryFilters()
            .Where(r => r.IsDeleted)
            .OrderByDescending(r => r.DeletedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<DrugRoute>> SearchAsync(string? searchText, bool isLeadership, int max = 20, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.DrugRoutes.OnlyActive().Where(r => isLeadership || !r.IsClassified);
        var s = searchText?.Trim();
        if (!string.IsNullOrEmpty(s))
        {
            query = query.Where(r => r.Designation.Contains(s) || r.CaseNumber.Contains(s));
        }
        return await query.OrderBy(r => r.Designation).Take(max).ToListAsync(cancellationToken);
    }

    public async Task<List<DrugRoute>> GetForFactionAsync(string factionId, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await VisibleRoutes(db, scope)
            .Where(r => r.FactionId == factionId)
            .Include(r => r.Stations)
            .OrderBy(r => r.IsArchived)
            .ThenBy(r => r.Designation)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<DrugRoute>> GetForPersonAsync(string personId, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var ids = db.DrugRoutePeople.Where(p => p.PersonId == personId).Select(p => p.DrugRouteId);
        return await VisibleRoutes(db, scope)
            .Where(r => ids.Contains(r.Id))
            .OrderBy(r => r.IsArchived)
            .ThenBy(r => r.Designation)
            .ToListAsync(cancellationToken);
    }

    public async Task<DrugRoute> CreateAsync(DrugRouteInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        ClassificationHelper.CheckRankGate(input.Classification, actor);
        Permission.RequireMayAssignClassification(actor, input.SecrecyLevel);
        var designation = RequireDesignation(input.Designation);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var factionId = await RequireVisibleFactionAsync(db, input.FactionId, actor, cancellationToken);

        DrugRoute route;
        await DesignationGate.WaitAsync(cancellationToken);
        try
        {
            await RequireFreeDesignationAsync(db, designation, null, actor, cancellationToken);
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
            route = new DrugRoute
            {
                CaseNumber = await caseNumber.NextAsync(db, "DR", cancellationToken),
                Designation = designation,
                Substance = input.Substance.TrimToNull(),
                Description = input.Description.TrimToNull(),
                Findings = input.Findings.TrimToNull(),
                Remarks = input.Remarks.TrimToNull(),
                FactionId = factionId,
                Classification = input.Classification,
                SecrecyLevel = input.SecrecyLevel,
            };
            if (input.Classification != Classification.Unknown)
            {
                db.ClassificationHistory.Add(ClassificationHelper.Entry(nameof(DrugRoute), route.Id, input.Classification, input.ClassificationJustification, actor));
            }
            db.DrugRoutes.Add(route);
            if (factionId is not null)
            {
                await NoteFactionChangeAsync(db, route, null, factionId, actor, cancellationToken);
            }
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }
        finally
        {
            DesignationGate.Release();
        }

        await FactionsTouchedAsync(db, [factionId], cancellationToken);
        await MentionNotify.DeltaAsync(notifications, null, MentionScope(route), "einer Drogenroutenakte",
            nameof(DrugRoute), route.Id, actor, cancellationToken);
        return route;
    }

    public async Task RefreshAsync(string id, DrugRouteInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        var designation = RequireDesignation(input.Designation);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var route = await db.DrugRoutes.FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Drogenroute '{id}' nicht gefunden.");
        Permission.RequireMaySeeClassified(actor, route.SecrecyLevel);
        Permission.RequireMayAssignClassification(actor, input.SecrecyLevel);

        // an unchanged faction may stay even if the actor cannot open it
        var factionId = input.FactionId == route.FactionId
            ? route.FactionId
            : await RequireVisibleFactionAsync(db, input.FactionId, actor, cancellationToken);
        var oldFactionId = route.FactionId;
        var oldMentions = MentionScope(route);

        await DesignationGate.WaitAsync(cancellationToken);
        try
        {
            if (DrugRouteRules.Key(designation) != DrugRouteRules.Key(route.Designation) && !route.IsArchived)
            {
                await RequireFreeDesignationAsync(db, designation, id, actor, cancellationToken);
            }
            route.Designation = designation;
            route.Substance = input.Substance.TrimToNull();
            route.Description = input.Description.TrimToNull();
            route.Findings = input.Findings.TrimToNull();
            route.Remarks = input.Remarks.TrimToNull();
            CriticalChange.NoteSecrecy(db, nameof(DrugRoute), id, route.SecrecyLevel, input.SecrecyLevel, input.SecrecyReason, actor);
            route.SecrecyLevel = input.SecrecyLevel;
            if (factionId != oldFactionId)
            {
                route.FactionId = factionId;
                await NoteFactionChangeAsync(db, route, oldFactionId, factionId, actor, cancellationToken);
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            DesignationGate.Release();
        }

        if (factionId != oldFactionId)
        {
            await FactionsTouchedAsync(db, [oldFactionId, factionId], cancellationToken);
        }
        await MentionNotify.DeltaAsync(notifications, oldMentions, MentionScope(route), "einer Drogenroutenakte",
            nameof(DrugRoute), id, actor, cancellationToken);
    }

    public async Task DeleteAsync(string id, string reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);
        var note = CriticalChange.RequireDeletionReason(reason);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var route = await db.DrugRoutes.FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Drogenroute '{id}' nicht gefunden.");
        db.DrugRoutes.Remove(route);
        CriticalChange.NoteDeletion(db, nameof(DrugRoute), id, note, actor);
        await db.SaveChangesAsync(cancellationToken);
        await FactionsTouchedAsync(db, [route.FactionId], cancellationToken);
    }

    public async Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var route = await db.DrugRoutes.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Drogenroute '{id}' nicht gefunden.");

        await DesignationGate.WaitAsync(cancellationToken);
        try
        {
            if (!route.IsArchived)
            {
                await RequireFreeDesignationAsync(db, route.Designation, id, actor, cancellationToken);
            }
            route.IsDeleted = false;
            route.DeletedAt = null;
            route.DeletedById = null;
            await db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            DesignationGate.Release();
        }
        await FactionsTouchedAsync(db, [route.FactionId], cancellationToken);
    }

    public Task ArchiveAsync(string id, string? reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
        => SetArchivedAsync(id, true, reason, actor, cancellationToken);

    public Task UnarchiveAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
        => SetArchivedAsync(id, false, null, actor, cancellationToken);

    private async Task SetArchivedAsync(string id, bool archived, string? reason, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await Visibility.IsRecordVisibleAsync(db, nameof(DrugRoute), id, ViewerScope.From(actor), cancellationToken))
        {
            throw new UnauthorizedAccessException("Diese Akte ist für dich nicht zugänglich.");
        }
        string? factionId;
        await DesignationGate.WaitAsync(cancellationToken);
        try
        {
            var route = await db.DrugRoutes.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
                ?? throw new InvalidOperationException($"Drogenroute '{id}' nicht gefunden.");
            factionId = route.FactionId;
            if (!archived && route.IsArchived)
            {
                await RequireFreeDesignationAsync(db, route.Designation, id, actor, cancellationToken);
            }
            if (!await RecordArchive.SetArchivedAsync<DrugRoute>(db, id, archived, reason, actor, cancellationToken))
            {
                return;
            }
        }
        finally
        {
            DesignationGate.Release();
        }
        // ExecuteUpdate bypasses the audit interceptor, so the row is written by hand
        var note = archived && !string.IsNullOrWhiteSpace(reason)
            ? ManualAudit.Change("Archivgrund", null, reason.Trim())
            : null;
        db.AuditLogs.Add(ManualAudit.Row(nameof(DrugRoute), id,
            archived ? AuditAction.Archived : AuditAction.Unarchived, actor, note));
        await db.SaveChangesAsync(cancellationToken);
        await FactionsTouchedAsync(db, [factionId], cancellationToken);
    }

    public async Task ClassificationSetAsync(string id, Classification @new, string? justification, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        ClassificationHelper.CheckRankGate(@new, actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var route = await db.DrugRoutes.FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Drogenroute '{id}' nicht gefunden.");
        Permission.RequireMaySeeClassified(actor, route.SecrecyLevel);

        CriticalChange.NoteDowngrade(db, nameof(DrugRoute), id, route.Classification, @new, justification, actor);
        route.Classification = @new;
        db.ClassificationHistory.Add(ClassificationHelper.Entry(nameof(DrugRoute), id, @new, justification, actor));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<ClassificationHistory>> GetClassificationHistoryAsync(string id, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await ClassificationHelper.HistoryAsync(db, nameof(DrugRoute), id, scope, cancellationToken);
    }

    public async Task<List<DrugRouteStation>> GetStationsAsync(string routeId, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var stations = await db.DrugRouteStations
            .Where(s => s.DrugRouteId == routeId)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.CreatedAt)
            .ToListAsync(cancellationToken);
        return await PartnerFilterAsync(db, stations, s => s.Id, nameof(DrugRouteStation), routeId, scope, cancellationToken);
    }

    /// <summary>Internal viewers get everything; partners only children of a released route that are covered by the release.</summary>
    private static async Task<List<T>> PartnerFilterAsync<T>(AppDbContext db, List<T> rows, Func<T, string> id, string childType,
        string routeId, ViewerScope scope, CancellationToken cancellationToken)
    {
        if (scope.PartnerAgency is not { } agency || rows.Count == 0)
        {
            return rows;
        }
        var visible = await PartnerVisibility.VisibleChildIdsAsync(db, childType,
            rows.Select(r => (nameof(DrugRoute), routeId, id(r))).ToList(), agency, scope.MeId, cancellationToken);
        return rows.Where(r => visible.Contains(id(r))).ToList();
    }

    public async Task<DrugRouteStation> StationAddAsync(string routeId, DrugRouteStationInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await RequireWritableRouteAsync(db, routeId, actor, cancellationToken);
        var next = (await db.DrugRouteStations.Where(s => s.DrugRouteId == routeId)
            .MaxAsync(s => (int?)s.SortOrder, cancellationToken) ?? 0) + 1;
        var station = new DrugRouteStation
        {
            DrugRouteId = routeId,
            SortOrder = next,
            Kind = input.Kind,
            Location = RequireLocation(input.Location),
            Description = input.Description.TrimToNull(),
        };
        db.DrugRouteStations.Add(station);
        await db.SaveChangesAsync(cancellationToken);
        return station;
    }

    public async Task StationChangeAsync(string stationId, DrugRouteStationInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var station = await db.DrugRouteStations.FirstOrDefaultAsync(s => s.Id == stationId, cancellationToken)
            ?? throw new InvalidOperationException("Station nicht gefunden.");
        await RequireWritableRouteAsync(db, station.DrugRouteId, actor, cancellationToken);
        station.Kind = input.Kind;
        station.Location = RequireLocation(input.Location);
        station.Description = input.Description.TrimToNull();
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task StationRemoveAsync(string stationId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var station = await db.DrugRouteStations.FirstOrDefaultAsync(s => s.Id == stationId, cancellationToken);
        if (station is null)
        {
            return;
        }
        await RequireWritableRouteAsync(db, station.DrugRouteId, actor, cancellationToken);
        // evidence outlives the station: its pictures fall back to the gallery
        foreach (var photo in await db.DrugRoutePhotos.Where(f => f.StationId == stationId).ToListAsync(cancellationToken))
        {
            photo.StationId = null;
        }
        db.DrugRouteStations.Remove(station);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task StationMoveAsync(string stationId, int direction, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var station = await db.DrugRouteStations.FirstOrDefaultAsync(s => s.Id == stationId, cancellationToken)
            ?? throw new InvalidOperationException("Station nicht gefunden.");
        await RequireWritableRouteAsync(db, station.DrugRouteId, actor, cancellationToken);
        var all = await db.DrugRouteStations.Where(s => s.DrugRouteId == station.DrugRouteId)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.CreatedAt)
            .ToListAsync(cancellationToken);
        var index = all.FindIndex(s => s.Id == stationId);
        var target = index + Math.Sign(direction);
        if (target < 0 || target >= all.Count || target == index)
        {
            return;
        }
        (all[index], all[target]) = (all[target], all[index]);
        // renumber so gaps and ties from older rows disappear
        for (var i = 0; i < all.Count; i++)
        {
            if (all[i].SortOrder != i + 1)
            {
                all[i].SortOrder = i + 1;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<DrugRoutePerson>> GetPeopleAsync(string routeId, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var people = await db.DrugRoutePeople
            .Where(p => p.DrugRouteId == routeId)
            .Include(p => p.Person)
            .ToListAsync(cancellationToken);
        var visible = people
            .Where(p => p.Person is not null && RecordVisibility.IsVisible(scope,
                p.Person.IsClassified, p.Person.IsTRUClassified, p.Person.IsHRBClassified))
            .ToList();
        if (scope.PartnerAgency is { } agency)
        {
            var released = await PartnerVisibility.ReleasedParentIdsAsync(db, nameof(Person),
                visible.Select(p => p.PersonId).Distinct().ToList(), agency, scope.MeId, cancellationToken);
            visible = visible.Where(p => released.Contains(p.PersonId)).ToList();
        }
        return visible.OrderBy(p => p.Person!.Name).ToList();
    }

    public async Task PersonAddAsync(string routeId, DrugRoutePersonInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await RequireWritableRouteAsync(db, routeId, actor, cancellationToken);
        var personId = await MemberHelper.PersonIdDetermineAsync(db, personService, input.PersonId, input.NewPersonName, actor, cancellationToken);
        if (await db.DrugRoutePeople.AnyAsync(p => p.DrugRouteId == routeId && p.PersonId == personId, cancellationToken))
        {
            throw new InvalidOperationException("Diese Person ist bereits bei der Route eingetragen.");
        }
        db.DrugRoutePeople.Add(new DrugRoutePerson
        {
            DrugRouteId = routeId,
            PersonId = personId,
            Role = input.Role.TrimToNull(),
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task PersonChangeAsync(string entryId, string? role, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.DrugRoutePeople.FirstOrDefaultAsync(p => p.Id == entryId, cancellationToken)
            ?? throw new InvalidOperationException("Eintrag nicht gefunden.");
        await RequireWritableRouteAsync(db, entry.DrugRouteId, actor, cancellationToken);
        entry.Role = role.TrimToNull();
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task PersonRemoveAsync(string entryId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.DrugRoutePeople.FirstOrDefaultAsync(p => p.Id == entryId, cancellationToken);
        if (entry is null)
        {
            return;
        }
        await RequireWritableRouteAsync(db, entry.DrugRouteId, actor, cancellationToken);
        db.DrugRoutePeople.Remove(entry);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<AuditLog>> GetHistoryAsync(string routeId, bool isLeadership, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await Visibility.IsRecordVisibleAsync(db, nameof(DrugRoute), routeId, isLeadership, cancellationToken))
        {
            return new();
        }
        var stationIds = await db.DrugRouteStations.IgnoreQueryFilters()
            .Where(s => s.DrugRouteId == routeId).Select(s => s.Id).ToListAsync(cancellationToken);
        var personIds = await db.DrugRoutePeople.IgnoreQueryFilters()
            .Where(p => p.DrugRouteId == routeId).Select(p => p.Id).ToListAsync(cancellationToken);
        var relationIds = await db.Links
            .IgnoreQueryFilters()
            .Where(v => !v.Automatic
                && ((v.SourceType == nameof(DrugRoute) && v.SourceId == routeId)
                 || (v.TargetType == nameof(DrugRoute) && v.TargetId == routeId)))
            .Select(v => v.Id)
            .ToListAsync(cancellationToken);

        var ids = new HashSet<string>(stationIds) { routeId };
        ids.UnionWith(personIds);
        ids.UnionWith(relationIds);
        var types = new[] { nameof(DrugRoute), nameof(DrugRouteStation), nameof(DrugRoutePerson), nameof(Link) };

        return await db.AuditLogs
            .Where(a => types.Contains(a.EntityType) && ids.Contains(a.EntityId))
            .OrderByDescending(a => a.Timestamp)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<DrugRoutePhoto>> GetPhotosAsync(string routeId, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var photos = await db.DrugRoutePhotos
            .Where(f => f.DrugRouteId == routeId)
            .OrderByDescending(f => f.IsTitleImage)
            .ThenBy(f => f.CreatedAt)
            .ToListAsync(cancellationToken);
        return await PartnerFilterAsync(db, photos, f => f.Id, nameof(DrugRoutePhoto), routeId, scope, cancellationToken);
    }

    public async Task<DrugRoutePhoto?> GetPhotoWithRouteAsync(string photoId, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var photo = await db.DrugRoutePhotos.Include(f => f.DrugRoute).FirstOrDefaultAsync(f => f.Id == photoId, cancellationToken);
        if (photo?.DrugRoute is null)
        {
            return null;
        }
        if (scope.PartnerAgency is { } agency)
        {
            return await PartnerVisibility.IsChildVisibleToPartnerAsync(db, nameof(DrugRoute), photo.DrugRouteId,
                nameof(DrugRoutePhoto), photoId, agency, scope.MeId, cancellationToken)
                ? photo
                : null;
        }
        return scope.CanSee(photo.DrugRoute.SecrecyLevel) ? photo : null;
    }

    public async Task<DrugRoutePhoto> PhotoAddAsync(string routeId, string? stationId, string? caption, Stream content, string originalName,
        string contentType, long size, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        if (!photoStorage.IsAllowedType(contentType))
        {
            throw new InvalidOperationException($"Dateityp '{contentType}' ist nicht erlaubt.");
        }
        if (size > photoStorage.MaxBytes)
        {
            throw new InvalidOperationException($"Datei zu groß (max. {photoStorage.MaxBytes / (1024 * 1024)} MB).");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await RequireWritableRouteAsync(db, routeId, actor, cancellationToken);
        if (stationId is not null
            && !await db.DrugRouteStations.AnyAsync(s => s.Id == stationId && s.DrugRouteId == routeId, cancellationToken))
        {
            throw new InvalidOperationException("Die Station gehört nicht zu dieser Route.");
        }
        var count = await db.DrugRoutePhotos.CountAsync(f => f.DrugRouteId == routeId, cancellationToken);
        if (count >= MaxPhotosPerRoute)
        {
            throw new InvalidOperationException($"Eine Route hat höchstens {MaxPhotosPerRoute} Fotos.");
        }

        var fileName = await photoStorage.SaveAsync(content, contentType, cancellationToken);
        var photo = new DrugRoutePhoto
        {
            DrugRouteId = routeId,
            StationId = stationId,
            Caption = caption.TrimToNull(),
            FileNameSaved = fileName,
            OriginalName = originalName,
            ContentType = contentType,
            SizeBytes = size,
            IsTitleImage = count == 0,
            CreatedAt = DateTime.UtcNow,
            CreatedById = actor.GetAgentId(),
        };
        db.DrugRoutePhotos.Add(photo);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            photoStorage.Delete(fileName);
            throw;
        }
        return photo;
    }

    public async Task PhotoCaptionSetAsync(string photoId, string? caption, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var photo = await db.DrugRoutePhotos.FirstOrDefaultAsync(f => f.Id == photoId, cancellationToken)
            ?? throw new InvalidOperationException($"Foto '{photoId}' nicht gefunden.");
        await RequireWritableRouteAsync(db, photo.DrugRouteId, actor, cancellationToken);
        photo.Caption = caption.TrimToNull();
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task PhotoRemoveAsync(string photoId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var photo = await db.DrugRoutePhotos.FirstOrDefaultAsync(f => f.Id == photoId, cancellationToken);
        if (photo is null)
        {
            return;
        }
        await RequireWritableRouteAsync(db, photo.DrugRouteId, actor, cancellationToken);
        if (photo.IsTitleImage)
        {
            var remaining = await db.DrugRoutePhotos
                .Where(f => f.DrugRouteId == photo.DrugRouteId && f.Id != photoId)
                .ToListAsync(cancellationToken);
            if (RecordAvatar.Successor(remaining) is { } successor)
            {
                successor.IsTitleImage = true;
            }
        }
        db.DrugRoutePhotos.Remove(photo);
        await db.SaveChangesAsync(cancellationToken);
        photoStorage.Delete(photo.FileNameSaved);
    }

    public async Task AsTitleImageSetAsync(string photoId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var photo = await db.DrugRoutePhotos.FirstOrDefaultAsync(f => f.Id == photoId, cancellationToken)
            ?? throw new InvalidOperationException($"Foto '{photoId}' nicht gefunden.");
        await RequireWritableRouteAsync(db, photo.DrugRouteId, actor, cancellationToken);
        var siblings = await db.DrugRoutePhotos.Where(f => f.DrugRouteId == photo.DrugRouteId).ToListAsync(cancellationToken);
        foreach (var g in siblings)
        {
            g.IsTitleImage = g.Id == photoId;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static IQueryable<DrugRoute> VisibleRoutes(AppDbContext db, ViewerScope scope)
        => scope.PartnerAgency is { } agency
            ? db.DrugRoutes.OnlyPartnerVisible(db, agency, scope.MeId)
            : db.DrugRoutes.OnlyVisible(scope);

    private static string RequireDesignation(string? designation)
        => string.Join(' ', (designation ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) is { Length: > 0 } d
            ? d
            : throw new InvalidOperationException("Die Route braucht eine Bezeichnung.");

    private static string RequireLocation(string? location)
        => location.TrimToNull() ?? throw new InvalidOperationException("Die Station braucht eine Ortsangabe.");

    /// <summary>Loads the route and checks the actor may write it.</summary>
    private static async Task<DrugRoute> RequireWritableRouteAsync(AppDbContext db, string routeId, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var route = await db.DrugRoutes.AsNoTracking().FirstOrDefaultAsync(r => r.Id == routeId, cancellationToken)
            ?? throw new InvalidOperationException($"Drogenroute '{routeId}' nicht gefunden.");
        Permission.RequireMaySeeClassified(actor, route.SecrecyLevel);
        return route;
    }

    /// <summary>Null stays null; a faction id must exist and be visible to the actor.</summary>
    private static async Task<string?> RequireVisibleFactionAsync(AppDbContext db, string? factionId, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(factionId))
        {
            return null;
        }
        if (!await db.Factions.AnyAsync(f => f.Id == factionId, cancellationToken)
            || !await Visibility.IsRecordVisibleAsync(db, nameof(Faction), factionId, ViewerScope.From(actor), cancellationToken))
        {
            throw new InvalidOperationException("Die gewählte Fraktion wurde nicht gefunden.");
        }
        return factionId;
    }

    /// <summary>Refuses a designation another active route already carries; trash and archive give a name free.</summary>
    private static async Task RequireFreeDesignationAsync(AppDbContext db, string designation, string? exceptId,
        ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var key = DrugRouteRules.Key(designation);
        var candidates = await db.DrugRoutes.OnlyActive()
            .Where(r => exceptId == null || r.Id != exceptId)
            .Select(r => new { r.Id, r.Designation, r.CaseNumber })
            .ToListAsync(cancellationToken);
        var hit = candidates.FirstOrDefault(r => DrugRouteRules.Key(r.Designation) == key);
        if (hit is null)
        {
            return;
        }
        var visible = await Visibility.IsRecordVisibleAsync(db, nameof(DrugRoute), hit.Id, ViewerScope.From(actor), cancellationToken);
        throw new DrugRouteDuplicateException(designation, visible ? hit.Id : null, visible ? hit.CaseNumber : null);
    }

    /// <summary>Both faction histories get a line when a route moves; a classified other side stays unnamed.</summary>
    private static async Task NoteFactionChangeAsync(AppDbContext db, DrugRoute route, string? fromId, string? toId,
        ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        const string Unnamed = "einer anderen Fraktion";
        var ids = new[] { fromId, toId }.Where(i => i is not null).Cast<string>().ToList();
        var factions = await db.Factions.IgnoreQueryFilters().AsNoTracking()
            .Where(f => ids.Contains(f.Id))
            .Select(f => new { f.Id, f.Name, f.IsClassified, f.IsTRUClassified, f.IsHRBClassified })
            .ToListAsync(cancellationToken);
        string Name(string? id) => factions.FirstOrDefault(f => f.Id == id) is { } f
            && !(f.IsClassified || f.IsTRUClassified || f.IsHRBClassified) ? f.Name : Unnamed;
        // the faction history is read by everyone who sees the faction
        var routeLabel = route.IsRestricted ? "eine eingestufte Route" : $"{route.Designation} ({route.CaseNumber})";

        if (fromId is not null)
        {
            var changes = new Dictionary<string, object?[]> { ["Drogenroute"] = [routeLabel, null] };
            if (toId is not null)
            {
                changes["Übergeben an"] = [null, Name(toId)];
            }
            db.AuditLogs.Add(ManualAudit.Row(nameof(Faction), fromId, AuditAction.Modified, actor, changes));
        }
        if (toId is not null)
        {
            var changes = new Dictionary<string, object?[]>
            {
                [fromId is null ? "Drogenroute" : "Drogenroute übernommen"] = [null, routeLabel],
            };
            if (fromId is not null)
            {
                changes["Von"] = [null, Name(fromId)];
            }
            db.AuditLogs.Add(ManualAudit.Row(nameof(Faction), toId, AuditAction.Modified, actor, changes));
        }
    }

    /// <summary>A faction whose route stock changed: its stock light and its score follow.</summary>
    private async Task FactionsTouchedAsync(AppDbContext db, IEnumerable<string?> factionIds, CancellationToken cancellationToken)
    {
        foreach (var id in factionIds.Where(i => i is not null).Cast<string>().Distinct())
        {
            await FactionRecency.StampAsync(db, id, FactionRecencyFacet.Stock, cancellationToken);
            await threat.NewCalculateAsync(id, cancellationToken);
        }
    }
}
