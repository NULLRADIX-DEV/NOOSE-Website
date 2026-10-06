using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Factions;
using NOOSE_Website.Models.People;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Services;

public class FactionService(
    IDbContextFactory<AppDbContext> dbFactory, ICaseNumberService caseNumber, IProfileSuggestionService suggestion,
    IPersonService personService, IFactionPhotoStorageService photoStorage, IThreatScoreService threat,
    INotificationService notifications, IPublicFactionProfileService publicProfiles) : IFactionService
{
    private static string MentionScope(Faction f) => MentionNotify.Scope(f.Description, f.Targets, f.Estate);

    public async Task<List<Faction>> GetListAsync(ViewerScope scope, ArchiveFilter filter = ArchiveFilter.Active,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // Include members+person so the list member count matches the detail view.
        return await VisibleFactions(db, scope)
            .Apply(filter)
            .Include(f => f.Members).ThenInclude(m => m.Person)
            .Include(f => f.Photos)
            .OrderByDescending(f => f.ModifiedAt ?? f.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<Faction?> GetDetailAsync(string id, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var faction = await db.Factions
            .Include(f => f.Ranks)
            .Include(f => f.WeaponStock)
            .Include(f => f.Inventory)
            .Include(f => f.DrugRoutes)
            .Include(f => f.Photos)
            .AsSplitQuery()
            .FirstOrDefaultAsync(f => f.Id == id, cancellationToken);

        if (faction is null || !await Visibility.IsRecordVisibleAsync(db, nameof(Faction), id, scope, cancellationToken))
        {
            return null;
        }
        if (scope.PartnerAgency is { } agency)
        {
            faction.ThreatDetailJson = ThreatDetailRedaction.WithoutContent(
                faction.ThreatDetailJson, await PartnerVisibility.BlockedContentAsync(db, agency, cancellationToken));
        }
        return faction;
    }

    public async Task<List<Faction>> GetTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Factions.IgnoreQueryFilters()
            .Where(f => f.IsDeleted)
            .OrderByDescending(f => f.DeletedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<Faction>> SearchAsync(string? searchText, bool isLeadership, int max = 20, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // a picker must not offer a record that was filed away
        var query = db.Factions.OnlyActive().Where(f => isLeadership || !f.IsClassified);

        var s = searchText?.Trim();
        if (!string.IsNullOrEmpty(s))
        {
            query = query.Where(f => f.Name.Contains(s) || f.CaseNumber.Contains(s));
        }

        return await query
            .OrderBy(f => f.Name)
            .Take(max)
            .ToListAsync(cancellationToken);
    }

    public async Task<Faction> CreateAsync(FactionInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        ClassificationHelper.CheckRankGate(input.Classification, actor);
        Permission.RequireMayAssignClassification(actor, input.SecrecyLevel);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // held until the commit: the new rows stay invisible to a parallel check before that
        using var routeLock = await RouteLock.EnterAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var faction = new Faction
        {
            CaseNumber = await caseNumber.NextAsync(db, "F", cancellationToken),
            Name = input.Name.Trim(),
            Kind = input.Kind.TrimToNull(),
            Radio = input.Radio.TrimToNull(),
            Darkchat = input.Darkchat.TrimToNull(),
            IssuingTimes = input.IssuingTimes.TrimToNull(),
            Estate = input.Estate.TrimToNull(),
            RecognitionColor = input.RecognitionColor.TrimToNull(),
            Targets = input.Targets.TrimToNull(),
            Description = input.Description.TrimToNull(),
            Classification = input.Classification,
            SecrecyLevel = input.SecrecyLevel,
            IsStateFaction = input.IsStateFaction,
            IsBadFaction = input.IsBadFaction && !input.IsStateFaction,
            EstimatedMemberCount = input.EstimatedMemberCount,
        };
        ChildrenMap(faction, input);
        await SuggestionsStageAsync(db, faction, cancellationToken);

        if (input.Classification != Classification.Unknown)
        {
            db.ClassificationHistory.Add(ClassificationHelper.Entry(nameof(Faction), faction.Id, input.Classification, input.ClassificationJustification, actor));
        }

        var gaveRoutes = await TakeOverRoutesAsync(db, faction, input, actor, cancellationToken);
        db.Factions.Add(faction);
        await db.SaveChangesAsync(cancellationToken);

        // Take over the members from the create form (existing + auto-created, deduplicated), then build colleague links.
        if (input.Members.Count > 0)
        {
            var existingIds = input.Members
                .Select(m => m.PersonId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .ToList();
            var existing = existingIds.Count == 0
                ? new HashSet<string>()
                : (await db.People.Where(p => existingIds.Contains(p.Id)).Select(p => p.Id)
                    .ToListAsync(cancellationToken)).ToHashSet();

            var added = new List<string>();
            var seen = new HashSet<string>();
            var seenNewNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in input.Members)
            {
                string? pid = null;
                if (!string.IsNullOrWhiteSpace(m.PersonId) && existing.Contains(m.PersonId))
                {
                    pid = m.PersonId;
                }
                else if (string.IsNullOrWhiteSpace(m.PersonId) && !string.IsNullOrWhiteSpace(m.NewPersonName))
                {
                    if (!seenNewNames.Add(m.NewPersonName.Trim()))
                    {
                        continue;
                    }
                    var person = await personService.CreateAsync(new PersonInput { Name = m.NewPersonName.Trim() }, actor, cancellationToken);
                    pid = person.Id;
                }
                if (pid is null || !seen.Add(pid))
                {
                    continue;
                }
                db.FactionMembers.Add(new FactionMember
                {
                    FactionId = faction.Id,
                    PersonId = pid,
                    Rank = m.Rank.TrimToNull(),
                    IsLead = m.IsLead,
                });
                added.Add(pid);
            }
            if (added.Count > 0)
            {
                await db.SaveChangesAsync(cancellationToken);
                foreach (var pid in added)
                {
                    await FactionColleaguesSyncAsync(db, pid, cancellationToken);
                }
            }
        }

        // Auto-assign the creator as investigation lead so at least one always exists.
        var creatorId = actor.GetAgentId();
        if (creatorId is not null)
        {
            db.FactionAgents.Add(new FactionAgent
            {
                FactionId = faction.Id,
                AgentId = creatorId,
                IsInvestigationLead = true,
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        routeLock.Dispose();
        await RoutesGivenAwayAsync(db, gaveRoutes, cancellationToken);
        // Initial threat score now that classification/members/stocks exist.
        await threat.NewCalculateAsync(faction.Id, cancellationToken);
        await MentionNotify.DeltaAsync(notifications, null, MentionScope(faction), "einer Fraktionsakte",
            nameof(Faction), faction.Id, actor, cancellationToken);
        return faction;
    }

    public async Task RefreshAsync(string id, FactionInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var faction = await db.Factions
            .Include(f => f.Ranks)
            .Include(f => f.WeaponStock)
            .Include(f => f.Inventory)
            .Include(f => f.DrugRoutes)
            .AsSplitQuery()
            .FirstOrDefaultAsync(f => f.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Fraktion '{id}' nicht gefunden.");

        // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
        Permission.RequireMaySeeClassified(actor, faction.SecrecyLevel);

        var oldMentions = MentionScope(faction);
        var wasRestricted = faction.IsRestricted;
        faction.Name = input.Name.Trim();
        faction.Kind = input.Kind.TrimToNull();
        faction.Radio = input.Radio.TrimToNull();
        faction.Darkchat = input.Darkchat.TrimToNull();
        faction.IssuingTimes = input.IssuingTimes.TrimToNull();
        faction.Estate = input.Estate.TrimToNull();
        faction.RecognitionColor = input.RecognitionColor.TrimToNull();
        faction.Targets = input.Targets.TrimToNull();
        faction.Description = input.Description.TrimToNull();
        Permission.RequireMayAssignClassification(actor, input.SecrecyLevel);
        faction.SecrecyLevel = input.SecrecyLevel;
        faction.IsStateFaction = input.IsStateFaction;
        faction.IsBadFaction = input.IsBadFaction && !input.IsStateFaction;
        faction.EstimatedMemberCount = input.EstimatedMemberCount;

        // Detect renames before replacing the old ranks, so the denormalized rank name on members can follow.
        var renames = RankRenamesDetect(faction.Ranks, input.Ranks);
        // Snapshot the stocks before they are replaced: only a real stock change refreshes the freshness light.
        var stockBefore = StockSnapshot(faction);

        // Replace the structured lists wholesale; members are untouched (own endpoints).
        db.FactionRanks.RemoveRange(faction.Ranks);
        db.FactionWeaponStocks.RemoveRange(faction.WeaponStock);
        db.FactionInventories.RemoveRange(faction.Inventory);
        db.FactionDrugRoutes.RemoveRange(faction.DrugRoutes);
        ChildrenMap(faction, input);
        await SuggestionsStageAsync(db, faction, cancellationToken);

        // Carry renamed ranks over to the member list (rank is a denormalized copy there).
        if (renames.Count > 0)
        {
            var members = await db.FactionMembers
                .Where(m => m.FactionId == id && m.Rank != null)
                .ToListAsync(cancellationToken);
            foreach (var m in members)
            {
                if (m.Rank is not null && renames.TryGetValue(m.Rank, out var newName))
                {
                    m.Rank = newName;
                }
            }
        }

        List<string> gaveRoutes;
        using (await RouteLock.EnterAsync(cancellationToken))
        {
            gaveRoutes = await TakeOverRoutesAsync(db, faction, input, actor, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        if (!StockSnapshot(faction).SequenceEqual(stockBefore, StringComparer.Ordinal))
        {
            await FactionRecency.StampAsync(db, id, FactionRecencyFacet.Stock, cancellationToken);
        }
        await RoutesGivenAwayAsync(db, gaveRoutes, cancellationToken);
        // Master data affects the score.
        await threat.NewCalculateAsync(id, cancellationToken);
        await MentionNotify.DeltaAsync(notifications, oldMentions, MentionScope(faction), "einer Fraktionsakte",
            nameof(Faction), id, actor, cancellationToken);

        // a file that just became a Verschlusssache must not stay outside; the read side hides it anyway, this closes
        // the row itself so the state is honest and audited
        if (!wasRestricted && faction.IsRestricted)
        {
            await publicProfiles.RetractForRecordAsync(id, "Akte als Verschlusssache eingestuft.", actor, cancellationToken);
        }
    }

    public async Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var faction = await db.Factions.FirstOrDefaultAsync(f => f.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Fraktion '{id}' nicht gefunden.");
        // Interceptor rewrites Remove to soft-delete.
        db.Factions.Remove(faction);
        await db.SaveChangesAsync(cancellationToken);

        // a public profile of a deleted file has nothing behind it any more
        await publicProfiles.RetractForRecordAsync(id, "Akte gelöscht.", actor, cancellationToken);
    }

    public async Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var faction = await db.Factions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(f => f.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Fraktion '{id}' nicht gefunden.");

        faction.IsDeleted = false;
        faction.DeletedAt = null;
        faction.DeletedById = null;
        bool dropped;
        using (await RouteLock.EnterAsync(cancellationToken))
        {
            // still archived: it holds nothing yet, the unarchive decides
            dropped = !faction.IsArchived && await DropTakenRoutesAsync(db, id, actor, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        if (dropped)
        {
            await RoutesGivenAwayAsync(db, [id], cancellationToken);
        }
    }

    public async Task ArchiveAsync(string id, string? reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
        => await SetArchivedAsync(id, true, reason, actor, cancellationToken);

    public async Task UnarchiveAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
        => await SetArchivedAsync(id, false, null, actor, cancellationToken);

    private async Task SetArchivedAsync(string id, bool archived, string? reason, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        // a stock decision, not a rank decision
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // filing a record away hides it from everyone, so the actor must be able to see it first
        if (!await Visibility.IsRecordVisibleAsync(db, nameof(Faction), id, ViewerScope.From(actor), cancellationToken))
        {
            throw new UnauthorizedAccessException("Diese Akte ist für dich nicht zugänglich.");
        }
        var dropped = false;
        using (await RouteLock.EnterAsync(cancellationToken))
        {
            if (!await RecordArchive.SetArchivedAsync<Faction>(db, id, archived, reason, actor, cancellationToken))
            {
                return;
            }
            // ExecuteUpdate bypasses the audit interceptor, so the row is written by hand
            var note = archived && !string.IsNullOrWhiteSpace(reason)
                ? ManualAudit.Change("Archivgrund", null, reason.Trim())
                : null;
            db.AuditLogs.Add(ManualAudit.Row(nameof(Faction), id,
                archived ? AuditAction.Archived : AuditAction.Unarchived, actor, note));
            // archiving gives the routes free; coming back cannot take one another faction holds meanwhile
            if (!archived)
            {
                dropped = await DropTakenRoutesAsync(db, id, actor, cancellationToken);
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        if (dropped)
        {
            await RoutesGivenAwayAsync(db, [id], cancellationToken);
        }
    }

    public async Task ClassificationSetAsync(string id, Classification @new, string? justification, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        ClassificationHelper.CheckRankGate(@new, actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var faction = await db.Factions.FirstOrDefaultAsync(f => f.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Fraktion '{id}' nicht gefunden.");

        // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
        Permission.RequireMaySeeClassified(actor, faction.SecrecyLevel);

        faction.Classification = @new;
        db.ClassificationHistory.Add(ClassificationHelper.Entry(nameof(Faction), id, @new, justification, actor));
        await db.SaveChangesAsync(cancellationToken);
        // Classification sets the score's minimum band.
        await threat.NewCalculateAsync(id, cancellationToken);
    }

    public async Task<List<ClassificationHistory>> GetClassificationHistoryAsync(string id, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await Visibility.IsRecordVisibleAsync(db, nameof(Faction), id, scope, cancellationToken))
        {
            return new();
        }
        return await db.ClassificationHistory
            .Where(e => e.EntityType == nameof(Faction) && e.EntityId == id)
            .OrderByDescending(e => e.Timestamp)
            .ToListAsync(cancellationToken);
    }

    private static IQueryable<Faction> VisibleFactions(AppDbContext db, ViewerScope scope)
        => scope.PartnerAgency is { } agency
            ? db.Factions.OnlyPartnerVisible(db, agency, scope.MeId)
            : db.Factions.OnlyVisible(scope);

    public async Task<List<FactionMember>> GetMembersAsync(string factionId, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var members = await db.FactionMembers
            .Where(m => m.FactionId == factionId)
            .Include(m => m.Person)
            .ToListAsync(cancellationToken);
        var rankOrder = (await db.FactionRanks
                .Where(r => r.FactionId == factionId)
                .ToListAsync(cancellationToken))
            .ToDictionary(r => r.Designation, r => r.Order, StringComparer.OrdinalIgnoreCase);

        // Person == null means the record is trashed; classified people are leadership-only.
        var visible = members
            .Where(m => m.Person is not null && RecordVisibility.IsVisible(scope,
                m.Person.IsClassified, m.Person.IsTRUClassified, m.Person.IsHRBClassified))
            .ToList();
        if (scope.PartnerAgency is { } agency)
        {
            // partners: only members whose person is released
            var released = await PartnerVisibility.ReleasedParentIdsAsync(db, nameof(Person),
                visible.Select(m => m.PersonId).Distinct().ToList(), agency, scope.MeId, cancellationToken);
            visible = visible.Where(m => released.Contains(m.PersonId)).ToList();
        }
        // hierarchy order: known ranks first (highest on top), unknown/none last, lead breaks ties
        return visible
            .OrderBy(m => m.Rank is not null && rankOrder.TryGetValue(m.Rank, out var order) ? order : int.MaxValue)
            .ThenByDescending(m => m.IsLead)
            .ThenBy(m => m.Person!.Name)
            .ToList();
    }

    public async Task<int> MemberAddAsync(string factionId, MemberInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        // fail the read-only roles before a membership moves, same as the bulk path
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var faction = await db.Factions.FirstOrDefaultAsync(f => f.Id == factionId, cancellationToken)
            ?? throw new InvalidOperationException($"Fraktion '{factionId}' nicht gefunden.");
        // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
        Permission.RequireMaySeeClassified(actor, faction.SecrecyLevel);

        var personId = await PersonIdDetermineAsync(db, input.PersonId, input.NewPersonName, actor, cancellationToken);
        if (await db.FactionMembers.AnyAsync(m => m.FactionId == factionId && m.PersonId == personId, cancellationToken))
        {
            throw new InvalidOperationException("Diese Person ist bereits Mitglied der Fraktion.");
        }

        var oldFactionIds = new List<string>();
        // Membership and colleague links in one transaction, so no intermediate state leaks.
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        db.FactionMembers.Add(new FactionMember
        {
            FactionId = factionId,
            PersonId = personId,
            Rank = input.Rank.TrimToNull(),
            IsLead = input.IsLead,
        });
        if (input.RemoveFromOtherFactions)
        {
            oldFactionIds = await OtherFactionMembershipsEndAsync(db, personId, factionId, actor, cancellationToken);
        }
        await db.SaveChangesAsync(cancellationToken);
        await FactionColleaguesSyncAsync(db, personId, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        await FactionRecency.StampAsync(db, factionId, FactionRecencyFacet.Members, cancellationToken);
        if (oldFactionIds.Count > 0)
        {
            await FactionRecency.StampAsync(db, oldFactionIds, FactionRecencyFacet.Members, cancellationToken);
        }
        // Member count/lead affect the faction score; the new member brings its measure heat.
        await threat.NewCalculateAsync(factionId, cancellationToken);
        foreach (var oldFactionId in oldFactionIds.Distinct())
        {
            await threat.NewCalculateAsync(oldFactionId, cancellationToken);
        }
        // Membership/lead role affect the person score.
        await threat.NewCalculatePersonScoreAsync(personId, cancellationToken);
        return oldFactionIds.Count;
    }

    /// <summary>Returns the person id: the existing one (checked) or a freshly created person record.</summary>
    private Task<string> PersonIdDetermineAsync(AppDbContext db, string? personId, string? newName, ClaimsPrincipal actor, CancellationToken cancellationToken)
        => MemberHelper.PersonIdDetermineAsync(db, personService, personId, newName, actor, cancellationToken);

    public async Task<BulkMemberResult> MembersBulkApplyAsync(string factionId, IReadOnlyList<MemberInput> toAdd, IReadOnlyList<string> memberIdsToRemove, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        // Fail read-only actors before any person is created, so a denied run leaves no orphan records.
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var faction = await db.Factions.FirstOrDefaultAsync(f => f.Id == factionId, cancellationToken)
            ?? throw new InvalidOperationException($"Fraktion '{factionId}' nicht gefunden.");
        Permission.RequireMaySeeClassified(actor, faction.SecrecyLevel);

        // Which requested existing ids actually exist (batch, not per row).
        var requestedIds = toAdd.Select(m => m.PersonId).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        var existingIds = requestedIds.Count == 0
            ? new HashSet<string>()
            : (await db.People.Where(p => requestedIds.Contains(p.Id)).Select(p => p.Id).ToListAsync(cancellationToken)).ToHashSet();

        // Create new persons first, each on its own transaction, so the outer transaction stays short.
        var resolved = new List<(string PersonId, string? Rank, bool IsLead, bool IsNew, bool RemoveFromOtherFactions)>();
        var seenNewNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var created = 0;
        foreach (var m in toAdd)
        {
            if (!string.IsNullOrWhiteSpace(m.PersonId))
            {
                if (existingIds.Contains(m.PersonId))
                {
                    resolved.Add((m.PersonId, m.Rank, m.IsLead, false, m.RemoveFromOtherFactions));
                }
            }
            else if (!string.IsNullOrWhiteSpace(m.NewPersonName))
            {
                var name = m.NewPersonName.Trim();
                if (!seenNewNames.Add(name))
                {
                    continue;
                }
                var person = await personService.CreateAsync(new PersonInput { Name = name }, actor, cancellationToken);
                resolved.Add((person.Id, m.Rank, m.IsLead, true, m.RemoveFromOtherFactions));
                created++;
            }
        }

        var activeMemberPersonIds = (await db.FactionMembers
            .Where(m => m.FactionId == factionId)
            .Select(m => m.PersonId)
            .ToListAsync(cancellationToken)).ToHashSet();

        var addedExisting = 0;
        var alreadyMembers = 0;
        var affected = new HashSet<string>();
        var seenPersonIds = new HashSet<string>();
        var leaveOtherFactions = new List<string>();

        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        foreach (var r in resolved)
        {
            if (!seenPersonIds.Add(r.PersonId))
            {
                continue;
            }
            if (activeMemberPersonIds.Contains(r.PersonId))
            {
                alreadyMembers++;
                continue;
            }
            db.FactionMembers.Add(new FactionMember
            {
                FactionId = factionId,
                PersonId = r.PersonId,
                Rank = r.Rank.TrimToNull(),
                IsLead = r.IsLead,
            });
            affected.Add(r.PersonId);
            if (r.RemoveFromOtherFactions)
            {
                leaveOtherFactions.Add(r.PersonId);
            }
            if (!r.IsNew)
            {
                addedExisting++;
            }
        }

        var removed = 0;
        if (memberIdsToRemove.Count > 0)
        {
            var removeIds = memberIdsToRemove.Distinct().ToList();
            // FactionId filter is a required ownership check; default filter keeps it to active members.
            var members = await db.FactionMembers
                .Where(m => removeIds.Contains(m.Id) && m.FactionId == factionId)
                .ToListAsync(cancellationToken);
            foreach (var member in members)
            {
                db.FactionMembers.Remove(member);
                affected.Add(member.PersonId);
                removed++;
            }
        }

        var oldFactionIds = new List<string>();
        foreach (var pid in leaveOtherFactions)
        {
            oldFactionIds.AddRange(await OtherFactionMembershipsEndAsync(db, pid, factionId, actor, cancellationToken));
        }

        var changed = affected.Count > 0;
        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
            foreach (var pid in affected)
            {
                await FactionColleaguesSyncAsync(db, pid, cancellationToken);
            }
        }
        await tx.CommitAsync(cancellationToken);

        if (changed)
        {
            await FactionRecency.StampAsync(db, factionId, FactionRecencyFacet.Members, cancellationToken);
            if (oldFactionIds.Count > 0)
            {
                await FactionRecency.StampAsync(db, oldFactionIds, FactionRecencyFacet.Members, cancellationToken);
            }
            // One faction recompute reflects the final membership; per-person scores for everyone touched.
            await threat.NewCalculateAsync(factionId, cancellationToken);
            foreach (var oldFactionId in oldFactionIds.Distinct())
            {
                await threat.NewCalculateAsync(oldFactionId, cancellationToken);
            }
            foreach (var pid in affected)
            {
                await threat.NewCalculatePersonScoreAsync(pid, cancellationToken);
            }
        }

        return new BulkMemberResult(created, addedExisting, alreadyMembers, removed, oldFactionIds.Count);
    }

    public async Task MemberChangeAsync(string memberId, string? rank, bool isLead, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var member = await db.FactionMembers.Include(m => m.Faction).FirstOrDefaultAsync(m => m.Id == memberId, cancellationToken)
            ?? throw new InvalidOperationException("Mitgliedschaft nicht gefunden.");
        if (member.Faction is { } memberFaction)
        {
            // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
            Permission.RequireMaySeeClassified(actor, memberFaction.SecrecyLevel);
        }
        member.Rank = rank.TrimToNull();
        member.IsLead = isLead;
        await db.SaveChangesAsync(cancellationToken);
        await FactionRecency.StampAsync(db, member.FactionId, FactionRecencyFacet.Members, cancellationToken);
        // Lead flag affects both the faction and the person score.
        await threat.NewCalculateAsync(member.FactionId, cancellationToken);
        await threat.NewCalculatePersonScoreAsync(member.PersonId, cancellationToken);
    }

    public async Task MemberRemoveAsync(string memberId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var member = await db.FactionMembers.Include(m => m.Faction).FirstOrDefaultAsync(m => m.Id == memberId, cancellationToken);
        if (member is null)
        {
            return;
        }
        if (member.Faction is { } memberFaction)
        {
            // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
            Permission.RequireMaySeeClassified(actor, memberFaction.SecrecyLevel);
        }
        var personId = member.PersonId;
        var factionId = member.FactionId;
        // Departure and colleague-link update in one transaction.
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        // Soft-delete keeps the membership as a history entry; colleague sync then sees only active members.
        db.FactionMembers.Remove(member);
        await db.SaveChangesAsync(cancellationToken);
        await FactionColleaguesSyncAsync(db, personId, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        await FactionRecency.StampAsync(db, factionId, FactionRecencyFacet.Members, cancellationToken);
        // Departure affects faction size/lead; the member's heat stays stable.
        await threat.NewCalculateAsync(factionId, cancellationToken);
        // Departure changes the person's memberships/lead roles.
        await threat.NewCalculatePersonScoreAsync(personId, cancellationToken);
    }

    public async Task<List<FactionAgent>> GetAgentsAsync(string factionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.FactionAgents
            .Where(a => a.FactionId == factionId)
            .Include(a => a.Agent)
            .OrderByDescending(a => a.IsInvestigationLead)
            .ThenBy(a => a.Agent!.Codename)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<FactionAgent>> GetInvestigationLeadAsync(string factionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.FactionAgents
            .Where(a => a.FactionId == factionId && a.IsInvestigationLead)
            .Include(a => a.Agent)
            .OrderBy(a => a.Agent!.Codename)
            .ToListAsync(cancellationToken);
    }

    public async Task AgentAllocateAsync(string factionId, string agentId, bool asInvestigationLead, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var faction = await db.Factions.FirstOrDefaultAsync(f => f.Id == factionId, cancellationToken)
            ?? throw new InvalidOperationException($"Fraktion '{factionId}' nicht gefunden.");
        // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
        Permission.RequireMaySeeClassified(actor, faction.SecrecyLevel);
        await RequireLeadershipOrELAsync(db, factionId, actor, cancellationToken);
        // Only leadership may grant the investigation-lead flag.
        if (asInvestigationLead)
        {
            Permission.RequireLeadership(actor);
        }
        // same rule as the picker: a stale circuit must not allocate a team lead, partner or ex-agent
        if (!await db.Users.OnlySelectable().AnyAsync(u => u.Id == agentId, cancellationToken))
        {
            throw new InvalidOperationException("Der gewählte Agent wurde nicht gefunden oder ist nicht zuteilbar.");
        }
        if (await db.FactionAgents.AnyAsync(a => a.FactionId == factionId && a.AgentId == agentId, cancellationToken))
        {
            throw new InvalidOperationException("Dieser Agent ist der Fraktion bereits zugeteilt.");
        }

        db.FactionAgents.Add(new FactionAgent
        {
            FactionId = factionId,
            AgentId = agentId,
            IsInvestigationLead = asInvestigationLead,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AgentRemoveAsync(string allocationId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var allocation = await db.FactionAgents.Include(a => a.Faction).FirstOrDefaultAsync(a => a.Id == allocationId, cancellationToken);
        if (allocation is null)
        {
            return;
        }
        if (allocation.Faction is { } allocFaction)
        {
            // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
            Permission.RequireMaySeeClassified(actor, allocFaction.SecrecyLevel);
        }
        await RequireLeadershipOrELAsync(db, allocation.FactionId, actor, cancellationToken);
        db.FactionAgents.Remove(allocation);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task InvestigationLeadSetAsync(string allocationId, bool @is, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        // Granting/revoking the investigation lead is leadership-only.
        Permission.RequireLeadership(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var allocation = await db.FactionAgents.FirstOrDefaultAsync(a => a.Id == allocationId, cancellationToken)
            ?? throw new InvalidOperationException("Zuteilung nicht gefunden.");
        allocation.IsInvestigationLead = @is;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Throws unless the actor is leadership or an investigation lead of this faction.</summary>
    private static async Task RequireLeadershipOrELAsync(AppDbContext db, string factionId, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        if (actor.IsLeadership())
        {
            return;
        }
        var agentId = actor.GetAgentId();
        var isEL = agentId is not null && await db.FactionAgents
            .AnyAsync(a => a.FactionId == factionId && a.AgentId == agentId && a.IsInvestigationLead, cancellationToken);
        if (!isEL)
        {
            throw new UnauthorizedAccessException(
                "Agents zuteilen oder entfernen dürfen nur die Führung oder ein Ermittlungsleiter dieser Akte.");
        }
    }

    public async Task<List<AuditLog>> GetHistoryAsync(string factionId, bool isLeadership, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await Visibility.IsRecordVisibleAsync(db, nameof(Faction), factionId, isLeadership, cancellationToken))
        {
            return new();
        }
        var memberIds = await db.FactionMembers
            .Where(m => m.FactionId == factionId)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken);
        var agentAllocationIds = await db.FactionAgents
            .Where(a => a.FactionId == factionId)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);

        // Manual relations touching this faction, including removed ones so their "removed" entry still shows.
        var relationIds = await db.Links
            .IgnoreQueryFilters()
            .Where(v => !v.Automatic
                && ((v.SourceType == nameof(Faction) && v.SourceId == factionId)
                 || (v.TargetType == nameof(Faction) && v.TargetId == factionId)))
            .Select(v => v.Id)
            .ToListAsync(cancellationToken);

        var ids = new HashSet<string>(memberIds) { factionId };
        ids.UnionWith(agentAllocationIds);
        ids.UnionWith(relationIds);
        var types = new[] { nameof(Faction), nameof(FactionMember), nameof(FactionAgent), nameof(Link) };

        return await db.AuditLogs
            .Where(a => types.Contains(a.EntityType) && ids.Contains(a.EntityId))
            .OrderByDescending(a => a.Timestamp)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<FactionPhoto>> GetPhotosAsync(string factionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // Title image first, then by capture time.
        return await db.FactionPhotos
            .Where(f => f.FactionId == factionId)
            .OrderByDescending(f => f.IsTitleImage)
            .ThenBy(f => f.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<FactionPhoto?> GetPhotoWithFactionAsync(string photoId, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var photo = await db.FactionPhotos.Include(f => f.Faction).FirstOrDefaultAsync(f => f.Id == photoId, cancellationToken);
        if (photo?.Faction is null)
        {
            return null;
        }
        if (scope.PartnerAgency is { } agency)
        {
            // partners: parent visible AND (whole-record or this photo released)
            return await PartnerVisibility.IsChildVisibleToPartnerAsync(db, nameof(Faction), photo.FactionId, nameof(FactionPhoto), photoId, agency, scope.MeId, cancellationToken)
                ? photo
                : null;
        }
        return photo.Faction.IsClassified && !scope.MayClassifiedRead ? null : photo;
    }

    public async Task<FactionPhoto> PhotoAddAsync(string factionId, Stream content, string originalName, string contentType, long size, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        if (!photoStorage.IsAllowedType(contentType))
        {
            throw new InvalidOperationException($"Dateityp '{contentType}' ist nicht erlaubt.");
        }
        // Enforce the size limit server-side, not just in the UI.
        if (size > photoStorage.MaxBytes)
        {
            throw new InvalidOperationException($"Datei zu groß (max. {photoStorage.MaxBytes / (1024 * 1024)} MB).");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // Check existence and visibility before writing a file.
        var faction = await db.Factions.FirstOrDefaultAsync(f => f.Id == factionId, cancellationToken)
            ?? throw new InvalidOperationException($"Fraktion '{factionId}' nicht gefunden.");
        // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
        Permission.RequireMaySeeClassified(actor, faction.SecrecyLevel);

        // The first photo becomes the title image automatically.
        var isFirst = !await db.FactionPhotos.AnyAsync(f => f.FactionId == factionId, cancellationToken);

        var fileName = await photoStorage.SaveAsync(content, contentType, cancellationToken);
        var photo = new FactionPhoto
        {
            FactionId = factionId,
            FileNameSaved = fileName,
            OriginalName = originalName,
            ContentType = contentType,
            SizeBytes = size,
            IsTitleImage = isFirst,
            CreatedAt = DateTime.UtcNow,
            CreatedById = actor.GetAgentId(),
        };
        db.FactionPhotos.Add(photo);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // Remove the written file if the DB insert fails, to avoid an orphaned attachment.
            photoStorage.Delete(fileName);
            throw;
        }
        return photo;
    }

    public async Task PhotoRemoveAsync(string photoId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var photo = await db.FactionPhotos.Include(f => f.Faction).FirstOrDefaultAsync(f => f.Id == photoId, cancellationToken);
        if (photo is null)
        {
            return;
        }
        if (photo.Faction is { } photoFaction)
        {
            // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
            Permission.RequireMaySeeClassified(actor, photoFaction.SecrecyLevel);
        }
        // The title image is the record's profile picture: hand it on instead of leaving the file blank.
        if (photo.IsTitleImage)
        {
            var remaining = await db.FactionPhotos
                .Where(f => f.FactionId == photo.FactionId && f.Id != photoId)
                .ToListAsync(cancellationToken);
            if (RecordAvatar.Successor(remaining) is { } successor)
            {
                successor.IsTitleImage = true;
            }
        }
        // Remove the DB record first, then the file, so a storage error leaves no record pointing at a missing file.
        db.FactionPhotos.Remove(photo);
        await db.SaveChangesAsync(cancellationToken);
        photoStorage.Delete(photo.FileNameSaved);
    }

    public async Task AsTitleImageSetAsync(string photoId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var photo = await db.FactionPhotos.Include(f => f.Faction).FirstOrDefaultAsync(f => f.Id == photoId, cancellationToken)
            ?? throw new InvalidOperationException($"Foto '{photoId}' nicht gefunden.");
        if (photo.Faction is { } photoFaction)
        {
            // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
            Permission.RequireMaySeeClassified(actor, photoFaction.SecrecyLevel);
        }

        // Exactly one title image per faction: clear all siblings, mark this one.
        var siblings = await db.FactionPhotos.Where(f => f.FactionId == photo.FactionId).ToListAsync(cancellationToken);
        foreach (var g in siblings)
        {
            g.IsTitleImage = g.Id == photoId;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Detects rank renames (old to new designation) by stable rank id; only actual changes count.</summary>
    private static Dictionary<string, string> RankRenamesDetect(IEnumerable<FactionRank> existing, IEnumerable<RankInput> input)
    {
        var oldById = existing.ToDictionary(r => r.Id, r => r.Designation);
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var ri in input)
        {
            if (string.IsNullOrWhiteSpace(ri.Id) || !oldById.TryGetValue(ri.Id, out var oldName))
            {
                continue;
            }
            var newName = ri.Designation?.Trim();
            if (!string.IsNullOrWhiteSpace(newName) && !string.Equals(oldName, newName, StringComparison.Ordinal))
            {
                renames[oldName] = newName;
            }
        }
        return renames;
    }

    /// <summary>Order-independent snapshot of the three stock lists, so an unrelated master-data edit does not refresh the stock stamp.</summary>
    private static List<string> StockSnapshot(Faction faction)
        => faction.WeaponStock.Select(w => $"W|{w.Designation}|{w.Quantity}")
            .Concat(faction.Inventory.Select(l => $"L|{l.Designation}|{l.Quantity}"))
            .Concat(faction.DrugRoutes.Select(d => $"D|{d.Designation}|{d.Note}"))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

    private static void ChildrenMap(Faction faction, FactionInput input)
    {
        faction.Ranks = input.Ranks
            .Where(r => !string.IsNullOrWhiteSpace(r.Designation))
            .Select((r, i) => new FactionRank { FactionId = faction.Id, Designation = r.Designation.Trim(), Order = i })
            .ToList();
        faction.WeaponStock = input.WeaponStock
            .Where(w => !string.IsNullOrWhiteSpace(w.Designation))
            .Select(w => new FactionWeaponStock { FactionId = faction.Id, Designation = w.Designation.Trim(), Quantity = w.Quantity.TrimToNull() })
            .ToList();
        faction.Inventory = input.Inventory
            .Where(l => !string.IsNullOrWhiteSpace(l.Designation))
            .Select(l => new FactionInventory { FactionId = faction.Id, Designation = l.Designation.Trim(), Quantity = l.Quantity.TrimToNull() })
            .ToList();
        // Drug routes share the generic stock input; its quantity field carries the note here.
        faction.DrugRoutes = DrugRouteRules.Distinct(input.DrugRoutes)
            .Select(d => new FactionDrugRoute { FactionId = faction.Id, Designation = d.Designation.Trim(), Note = d.Quantity.TrimToNull() })
            .ToList();
    }

    // one process, one gate: two saves must not both find a route free and both take it
    private static readonly SemaphoreSlim RouteGate = new(1, 1);

    /// <summary>Holds <see cref="RouteGate"/>; releasing twice is harmless, so an early release can stand beside the using.</summary>
    private sealed class RouteLock : IDisposable
    {
        private int _released;

        public static async Task<RouteLock> EnterAsync(CancellationToken cancellationToken)
        {
            await RouteGate.WaitAsync(cancellationToken);
            return new RouteLock();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                RouteGate.Release();
            }
        }
    }

    public async Task<List<DrugRouteConflict>> GetDrugRouteConflictsAsync(string? factionId, IEnumerable<string> designations,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return (await RouteConflictsAsync(db, factionId, designations, actor, cancellationToken))
            .Select(c => c.Conflict)
            .ToList();
    }

    /// <summary>One faction holding a wanted route: what the actor is told, the rows that would move, and whether
    /// the holder is open to every agent (only then may another file's history name it).</summary>
    private sealed record RouteHolding(DrugRouteConflict Conflict, List<string> RowIds, bool HolderOpen);

    /// <summary>Every holding of a wanted route by another active faction.</summary>
    private static async Task<List<RouteHolding>> RouteConflictsAsync(AppDbContext db,
        string? factionId, IEnumerable<string> designations, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var wanted = designations.Select(DrugRouteRules.Key).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0)
        {
            return [];
        }

        // trash (query filter) and archive give a route free
        var held = await (
                from d in db.FactionDrugRoutes
                join f in db.Factions.OnlyActive() on d.FactionId equals f.Id
                where factionId == null || d.FactionId != factionId
                select new { d.Id, d.FactionId, f.Name, f.SecrecyLevel, d.Designation })
            .ToListAsync(cancellationToken);

        var scope = ViewerScope.From(actor);
        var audience = DocumentViewerScope.From(actor);
        var result = new List<RouteHolding>();
        foreach (var holding in held
                     .Where(h => wanted.Contains(DrugRouteRules.Key(h.Designation)))
                     .GroupBy(h => (Key: DrugRouteRules.Key(h.Designation), h.FactionId))
                     .OrderBy(g => g.Key.Key, StringComparer.Ordinal))
        {
            var first = holding.First();
            // a holder the actor cannot see stays unnamed, and its route cannot be taken blindly
            var visible = audience.CanSee(first.SecrecyLevel)
                && await Visibility.IsRecordVisibleAsync(db, nameof(Faction), first.FactionId, scope, cancellationToken);
            result.Add(new RouteHolding(new DrugRouteConflict(holding.Key.Key, first.Designation,
                    visible ? first.FactionId : null, visible ? first.Name : null, visible),
                holding.Select(h => h.Id).ToList(),
                first.SecrecyLevel == DocumentClassification.None));
        }
        return result;
    }

    /// <summary>Moves the confirmed routes over from their holders or refuses the save; runs inside <see cref="RouteGate"/>, before the save.</summary>
    /// <returns>The factions that gave a route away.</returns>
    private static async Task<List<string>> TakeOverRoutesAsync(AppDbContext db, Faction faction, FactionInput input,
        ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var conflicts = await RouteConflictsAsync(db, faction.Id, faction.DrugRoutes.Select(d => d.Designation), actor, cancellationToken);
        if (conflicts.Count == 0)
        {
            return [];
        }

        var confirmed = input.ConfirmedRouteTakeovers.Select(DrugRouteRules.Key).ToHashSet(StringComparer.Ordinal);
        var refused = conflicts
            .Where(c => !c.Conflict.MayTakeOver || !confirmed.Contains(c.Conflict.Key))
            .Select(c => c.Conflict)
            .ToList();
        if (refused.Count > 0)
        {
            throw new DrugRouteConflictException(refused);
        }

        var rowIds = conflicts.SelectMany(c => c.RowIds).ToList();
        db.FactionDrugRoutes.RemoveRange(await db.FactionDrugRoutes.Where(d => rowIds.Contains(d.Id)).ToListAsync(cancellationToken));
        // each history is read by everyone who sees that file, so a classified other side stays unnamed
        const string Unnamed = "einer anderen Fraktion";
        var takerOpen = faction.SecrecyLevel == DocumentClassification.None;
        foreach (var (c, _, holderOpen) in conflicts)
        {
            var taken = faction.DrugRoutes.First(d => DrugRouteRules.Key(d.Designation) == c.Key).Designation;
            // route rows carry no audit of their own, so both files get a line by hand
            db.AuditLogs.Add(ManualAudit.Row(nameof(Faction), c.HolderId!, AuditAction.Modified, actor,
                new Dictionary<string, object?[]>
                {
                    ["Drogenroute"] = [c.Designation, null],
                    ["Übergeben an"] = [null, takerOpen ? faction.Name : Unnamed],
                }));
            db.AuditLogs.Add(ManualAudit.Row(nameof(Faction), faction.Id, AuditAction.Modified, actor,
                new Dictionary<string, object?[]>
                {
                    ["Drogenroute übernommen"] = [null, taken],
                    ["Von"] = [null, holderOpen ? c.HolderName : Unnamed],
                }));
        }
        return conflicts.Select(c => c.Conflict.HolderId!).Distinct().ToList();
    }

    /// <summary>A faction that lost a route: its stock light and its score follow.</summary>
    private async Task RoutesGivenAwayAsync(AppDbContext db, IEnumerable<string> factionIds, CancellationToken cancellationToken)
    {
        foreach (var id in factionIds)
        {
            await FactionRecency.StampAsync(db, id, FactionRecencyFacet.Stock, cancellationToken);
            await threat.NewCalculateAsync(id, cancellationToken);
        }
    }

    /// <summary>A faction coming back from trash or archive drops the routes another active faction holds meanwhile.</summary>
    /// <returns>Whether a route was dropped; the caller saves.</returns>
    private static async Task<bool> DropTakenRoutesAsync(AppDbContext db, string factionId, ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var own = await db.FactionDrugRoutes.Where(d => d.FactionId == factionId).ToListAsync(cancellationToken);
        if (own.Count == 0)
        {
            return false;
        }
        var taken = (await (
                    from d in db.FactionDrugRoutes
                    join f in db.Factions.OnlyActive() on d.FactionId equals f.Id
                    where d.FactionId != factionId
                    select d.Designation)
                .ToListAsync(cancellationToken))
            .Select(DrugRouteRules.Key)
            .ToHashSet(StringComparer.Ordinal);
        var lost = own.Where(d => taken.Contains(DrugRouteRules.Key(d.Designation))).ToList();
        if (lost.Count == 0)
        {
            return false;
        }

        db.FactionDrugRoutes.RemoveRange(lost);
        foreach (var d in lost)
        {
            // the other faction stays unnamed: it may be classified for whoever reads this history
            db.AuditLogs.Add(ManualAudit.Row(nameof(Faction), factionId, AuditAction.Modified, actor,
                new Dictionary<string, object?[]>
                {
                    ["Drogenroute"] = [d.Designation, null],
                    ["Grund"] = [null, "inzwischen bei einer anderen Fraktion"],
                }));
        }
        return true;
    }

    /// <summary>Stages stock designations into the shared suggestion catalog; classified factions are excluded, staged within the caller's SaveChanges.</summary>
    private async Task SuggestionsStageAsync(AppDbContext db, Faction faction, CancellationToken cancellationToken)
    {
        if (faction.IsClassified)
        {
            return;
        }
        if (!string.IsNullOrWhiteSpace(faction.Kind))
        {
            await suggestion.StageAsync(db, SuggestionType.Kind, new[] { faction.Kind }, cancellationToken);
        }
        await suggestion.StageAsync(db, SuggestionType.Weapon, faction.WeaponStock.Select(w => w.Designation), cancellationToken);
        await suggestion.StageAsync(db, SuggestionType.Inventory, faction.Inventory.Select(l => l.Designation), cancellationToken);
        await suggestion.StageAsync(db, SuggestionType.DrugRoute, faction.DrugRoutes.Select(d => d.Designation), cancellationToken);
    }

    /// <summary>Syncs the person's automatic faction-colleague links: a link exists iff two people share at least one faction. Called after every membership change.</summary>
    private static async Task FactionColleaguesSyncAsync(AppDbContext db, string personId, CancellationToken cancellationToken)
    {
        var myFactions = await db.FactionMembers
            .Where(m => m.PersonId == personId)
            .Select(m => m.FactionId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var should = myFactions.Count == 0
            ? new List<string>()
            : await db.FactionMembers
                .Where(m => myFactions.Contains(m.FactionId) && m.PersonId != personId)
                .Select(m => m.PersonId)
                .Distinct()
                .ToListAsync(cancellationToken);

        await ColleaguesSync.SyncAsync(db, personId, ColleaguesSync.FactionColleague, should, cancellationToken);
    }

    /// <summary>Ends the person's active memberships in other factions; factions the actor may not see are left alone. Returns the ended faction ids.</summary>
    private static async Task<List<string>> OtherFactionMembershipsEndAsync(
        AppDbContext db, string personId, string keepFactionId, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var scope = DocumentViewerScope.From(actor);
        var others = await db.FactionMembers
            .Include(m => m.Faction)
            .Where(m => m.PersonId == personId && m.FactionId != keepFactionId)
            .ToListAsync(cancellationToken);

        var endedFactionIds = new List<string>();
        foreach (var membership in others)
        {
            if (membership.Faction is null || membership.Faction.IsDeleted || !scope.CanSee(membership.Faction.SecrecyLevel))
            {
                continue;
            }
            // interceptor rewrites Remove to soft-delete, so the old membership stays as history
            db.FactionMembers.Remove(membership);
            endedFactionIds.Add(membership.FactionId);
        }
        return endedFactionIds;
    }
}
