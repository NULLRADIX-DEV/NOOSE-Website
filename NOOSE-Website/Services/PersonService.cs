using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.People;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IPersonService" />
public class PersonService(
    IDbContextFactory<AppDbContext> dbFactory, IFileStorageService fileStorage, IProfileSuggestionService suggestion,
    ICaseNumberService caseNumber, IThreatScoreService threat, INotificationService notifications,
    IPublicWantedService publicWanted) : IPersonService
{
    public async Task<List<Person>> GetListAsync(ViewerScope scope, ArchiveFilter filter = ArchiveFilter.Active,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await VisiblePeople(db, scope)
            .Apply(filter)
            .Include(p => p.Aliases)
            .OrderByDescending(p => p.ModifiedAt ?? p.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<Person?> GetDetailAsync(string id, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var person = await db.People
            .Include(p => p.Aliases)
            .Include(p => p.PhoneNumbers)
            .Include(p => p.Vehicles)
            .Include(p => p.Locations)
            .Include(p => p.Weapons)
            .Include(p => p.Photos)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (person is null || !await Visibility.IsRecordVisibleAsync(db, nameof(Person), id, scope, cancellationToken))
        {
            return null;
        }
        if (scope.PartnerAgency is { } agency)
        {
            person.ThreatDetailJson = ThreatDetailRedaction.WithoutContent(
                person.ThreatDetailJson, await PartnerVisibility.BlockedContentAsync(db, agency, cancellationToken));
        }
        return person;
    }

    public async Task<List<Person>> GetTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.People.IgnoreQueryFilters()
            .Where(p => p.IsDeleted)
            .OrderByDescending(p => p.DeletedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<Person>> SearchAsync(string? searchText, bool isLeadership, int max = 20, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // a picker must not offer a record that was filed away
        var query = db.People.OnlyActive().Where(p => isLeadership || !p.IsClassified);

        var s = searchText?.Trim();
        if (!string.IsNullOrEmpty(s))
        {
            query = query.Where(p => p.Name.Contains(s) || p.CaseNumber.Contains(s));
        }

        return await query
            .OrderBy(p => p.Name)
            .Take(max)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<Person>> FindDuplicatesAsync(string name, IEnumerable<string> phoneNumbers, bool isLeadership, CancellationToken cancellationToken = default)
    {
        var nameLower = (name ?? string.Empty).Trim().ToLower();
        var numbers = phoneNumbers
            .Select(n => (n ?? string.Empty).Trim())
            .Where(n => n.Length > 0)
            .ToList();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // restricted records only as duplicates for leadership (else the warning dialog leaks name + case number)
        return await db.People
            .Where(p => isLeadership || !p.IsClassified)
            .Include(p => p.PhoneNumbers)
            .Where(p => p.Name.ToLower() == nameLower
                     || p.PhoneNumbers.Any(t => numbers.Contains(t.Number)))
            .ToListAsync(cancellationToken);
    }

    public async Task<List<Person>> FindByNamesAsync(IEnumerable<string> names, bool isLeadership, CancellationToken cancellationToken = default)
    {
        var set = names.Select(n => (n ?? string.Empty).Trim().ToLower())
            .Where(s => s.Length > 0)
            .Distinct()
            .ToList();
        if (set.Count == 0)
        {
            return new List<Person>();
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // classified records only for leadership; a hidden match yields a "create new" (minor duplicate), never a leak
        return await db.People
            .Where(p => isLeadership || !p.IsClassified)
            .Where(p => set.Contains(p.Name.ToLower()))
            .ToListAsync(cancellationToken);
    }

    public async Task<Person> CreateAsync(PersonInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        ClassificationHelper.CheckRankGate(input.Classification, actor);
        Permission.RequireMayAssignClassification(actor, input.SecrecyLevel);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var person = new Person
        {
            CaseNumber = await caseNumber.NextAsync(db, "P", cancellationToken),
            Name = input.Name.Trim(),
            Description = input.Description.TrimToNull(),
            LifeStatus = input.LifeStatus,
            DeadUntil = input.LifeStatus == LifeStatus.Dead ? LifeStatusLogic.DeadUntilFrom(DateTime.UtcNow) : null,
            Classification = input.Classification,
            SecrecyLevel = input.SecrecyLevel,
        };
        ChildrenMap(person, input);
        await SuggestionsStageAsync(db, person, cancellationToken);

        if (input.Classification != Classification.Unknown)
        {
            db.ClassificationHistory.Add(ClassificationHelper.Entry(nameof(Person), person.Id, input.Classification, input.ClassificationJustification, actor));
        }

        db.People.Add(person);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        // compute the initial person score (inputs now committed)
        await threat.NewCalculatePersonScoreAsync(person.Id, cancellationToken);
        await MentionNotify.DeltaAsync(notifications, null, person.Description, "einer Personenakte",
            nameof(Person), person.Id, actor, cancellationToken);
        return person;
    }

    public async Task RefreshAsync(string id, PersonInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var person = await db.People
            .Include(p => p.Aliases)
            .Include(p => p.PhoneNumbers)
            .Include(p => p.Vehicles)
            .Include(p => p.Locations)
            .Include(p => p.Weapons)
            .AsSplitQuery()
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Person '{id}' nicht gefunden.");

        // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
        Permission.RequireMaySeeClassified(actor, person.SecrecyLevel);

        var altStatus = person.LifeStatus;
        var altDeadUntil = person.DeadUntil;
        var oldMentions = person.Description;
        var wasRestricted = person.IsRestricted;

        person.Name = input.Name.Trim();
        person.Description = input.Description.TrimToNull();
        Permission.RequireMayAssignClassification(actor, input.SecrecyLevel);
        person.SecrecyLevel = input.SecrecyLevel;
        person.LifeStatus = input.LifeStatus;
        if (input.LifeStatus == LifeStatus.Dead)
        {
            // start a fresh death window only on a real transition into dead; never restart an existing one
            person.DeadUntil = altStatus != LifeStatus.Dead
                ? LifeStatusLogic.DeadUntilFrom(DateTime.UtcNow)
                : altDeadUntil;
        }
        else
        {
            person.DeadUntil = null;
        }

        // fully replace profile children
        db.PersonAliases.RemoveRange(person.Aliases);
        db.PersonPhones.RemoveRange(person.PhoneNumbers);
        db.PersonVehicles.RemoveRange(person.Vehicles);
        db.PersonLocations.RemoveRange(person.Locations);
        db.PersonWeapons.RemoveRange(person.Weapons);
        ChildrenMap(person, input);
        await SuggestionsStageAsync(db, person, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        // weapons and fugitive status affect the person score
        await threat.NewCalculatePersonScoreAsync(id, cancellationToken);
        await MentionNotify.DeltaAsync(notifications, oldMentions, person.Description, "einer Personenakte",
            nameof(Person), id, actor, cancellationToken);

        // a file that just became a Verschlusssache must not stay on the public board; the read side hides it anyway,
        // this closes the row itself so the state is honest and audited
        if (!wasRestricted && person.IsRestricted)
        {
            await publicWanted.RetractForRecordAsync(id, "Akte als Verschlusssache eingestuft.", actor, cancellationToken);
        }
    }

    public async Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        // delete/archive is leadership/admin only
        Permission.RequireLeadership(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var person = await db.People.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Person '{id}' nicht gefunden.");
        db.People.Remove(person);
        await db.SaveChangesAsync(cancellationToken);

        // a poster for a deleted file has nothing behind it any more
        await publicWanted.RetractForRecordAsync(id, "Akte gelöscht.", actor, cancellationToken);
    }

    public async Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        // restore from trash is leadership/admin only
        Permission.RequireLeadership(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var person = await db.People.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Person '{id}' nicht gefunden.");

        person.IsDeleted = false;
        person.DeletedAt = null;
        person.DeletedById = null;
        await db.SaveChangesAsync(cancellationToken);
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
        if (!await Visibility.IsRecordVisibleAsync(db, nameof(Person), id, ViewerScope.From(actor), cancellationToken))
        {
            throw new UnauthorizedAccessException("Diese Akte ist für dich nicht zugänglich.");
        }
        if (!await RecordArchive.SetArchivedAsync<Person>(db, id, archived, reason, actor, cancellationToken))
        {
            return;
        }
        // ExecuteUpdate bypasses the audit interceptor, so the row is written by hand
        var note = archived && !string.IsNullOrWhiteSpace(reason)
            ? ManualAudit.Change("Archivgrund", null, reason.Trim())
            : null;
        db.AuditLogs.Add(ManualAudit.Row(nameof(Person), id,
            archived ? AuditAction.Archived : AuditAction.Unarchived, actor, note));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ClassificationSetAsync(string id, Classification @new, string? justification, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        ClassificationHelper.CheckRankGate(@new, actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var person = await db.People.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Person '{id}' nicht gefunden.");

        // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
        Permission.RequireMaySeeClassified(actor, person.SecrecyLevel);

        person.Classification = @new;
        db.ClassificationHistory.Add(ClassificationHelper.Entry(nameof(Person), id, @new, justification, actor));
        await db.SaveChangesAsync(cancellationToken);
        // classification sets the minimum score band
        await threat.NewCalculatePersonScoreAsync(id, cancellationToken);
    }

    public async Task WantedSetAsync(string id, bool wanted, string? reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var person = await db.People.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Person '{id}' nicht gefunden.");

        // cannot flag a classified record you are not cleared for
        Permission.RequireMaySeeClassified(actor, person.SecrecyLevel);

        var oldMentions = person.WantedReason;
        person.IsWanted = wanted;
        person.WantedReason = wanted && !string.IsNullOrWhiteSpace(reason) ? reason.Trim() : null;
        await db.SaveChangesAsync(cancellationToken);

        await MentionNotify.DeltaAsync(notifications, oldMentions, person.WantedReason, "einer Fahndung",
            nameof(Person), id, actor, cancellationToken);
    }

    public async Task<List<ClassificationHistory>> GetClassificationHistoryAsync(string id, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // gate invisible records
        if (!await Visibility.IsRecordVisibleAsync(db, nameof(Person), id, scope, cancellationToken))
        {
            return new();
        }
        return await db.ClassificationHistory
            .Where(e => e.EntityType == nameof(Person) && e.EntityId == id)
            .OrderByDescending(e => e.Timestamp)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<PersonAffiliation>> GetAffiliationsAsync(string personId, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await Visibility.IsRecordVisibleAsync(db, nameof(Person), personId, scope, cancellationToken))
        {
            return new();
        }
        // soft-delete filter keeps only live parent records and active memberships
        var factions = await (
            from m in db.FactionMembers
            where m.PersonId == personId
            join f in db.Factions.OnlyVisible(scope) on m.FactionId equals f.Id
            orderby f.Name
            select new PersonAffiliation(nameof(Faction), m.Id, f.Id, f.Name, f.CaseNumber, m.Rank, m.IsLead, m.CreatedAt, (DateTime?)null))
            .ToListAsync(cancellationToken);

        var groups = await (
            from m in db.PersonGroupMembers
            where m.PersonId == personId
            join g in db.PersonGroups.OnlyVisible(scope) on m.PersonGroupId equals g.Id
            orderby g.Name
            select new PersonAffiliation(nameof(PersonGroup), m.Id, g.Id, g.Name, g.CaseNumber, m.Role, m.IsLead, m.CreatedAt, (DateTime?)null))
            .ToListAsync(cancellationToken);

        return await OnlyReleasedOrgsAsync(db, factions.Concat(groups).ToList(), scope, cancellationToken);
    }

    public async Task<Dictionary<string, List<PersonAffiliation>>> GetAffiliationsManyAsync(
        IReadOnlyList<string> personIds, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        var ids = personIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        if (ids.Count == 0)
        {
            return new();
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // same person gate as GetAffiliationsAsync, batched instead of per person
        var personQuery = db.People.Where(p => ids.Contains(p.Id));
        personQuery = scope.PartnerAgency is { } agency
            ? personQuery.OnlyPartnerVisible(db, agency, scope.MeId)
            : personQuery.OnlyVisible(scope);
        var visibleIds = (await personQuery.Select(p => p.Id).ToListAsync(cancellationToken)).ToHashSet();
        if (visibleIds.Count == 0)
        {
            return new();
        }

        // soft-delete filter keeps only live parent records and active memberships
        var factions = await (
            from m in db.FactionMembers
            where visibleIds.Contains(m.PersonId)
            join f in db.Factions.OnlyVisible(scope) on m.FactionId equals f.Id
            orderby f.Name
            select new { m.PersonId, Affiliation = new PersonAffiliation(nameof(Faction), m.Id, f.Id, f.Name, f.CaseNumber, m.Rank, m.IsLead, m.CreatedAt, (DateTime?)null) })
            .ToListAsync(cancellationToken);

        var groups = await (
            from m in db.PersonGroupMembers
            where visibleIds.Contains(m.PersonId)
            join g in db.PersonGroups.OnlyVisible(scope) on m.PersonGroupId equals g.Id
            orderby g.Name
            select new { m.PersonId, Affiliation = new PersonAffiliation(nameof(PersonGroup), m.Id, g.Id, g.Name, g.CaseNumber, m.Role, m.IsLead, m.CreatedAt, (DateTime?)null) })
            .ToListAsync(cancellationToken);

        var rows = factions.Concat(groups).ToList();
        var released = await OnlyReleasedOrgsAsync(db, rows.Select(r => r.Affiliation).ToList(), scope, cancellationToken);
        var allowed = released.Select(a => a.MemberId).ToHashSet();
        return rows.Where(r => allowed.Contains(r.Affiliation.MemberId))
            .GroupBy(r => r.PersonId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.Affiliation).ToList());
    }

    public async Task<List<PersonAffiliation>> GetFormerAffiliationsAsync(string personId, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await Visibility.IsRecordVisibleAsync(db, nameof(Person), personId, scope, cancellationToken))
        {
            return new();
        }
        var isLeadership = scope.MayClassifiedRead;
        // IgnoreQueryFilters fetches ended memberships but disables ALL filters → re-apply trash/classified manually
        var factions = await (
            from m in db.FactionMembers.IgnoreQueryFilters()
            where m.PersonId == personId && m.IsDeleted
            join f in db.Factions on m.FactionId equals f.Id
            where !f.IsDeleted && (isLeadership || !f.IsClassified)
            select new PersonAffiliation(nameof(Faction), m.Id, f.Id, f.Name, f.CaseNumber, m.Rank, m.IsLead, m.CreatedAt, m.DeletedAt))
            .ToListAsync(cancellationToken);

        var groups = await (
            from m in db.PersonGroupMembers.IgnoreQueryFilters()
            where m.PersonId == personId && m.IsDeleted
            join g in db.PersonGroups on m.PersonGroupId equals g.Id
            where !g.IsDeleted && (isLeadership || !g.IsClassified)
            select new PersonAffiliation(nameof(PersonGroup), m.Id, g.Id, g.Name, g.CaseNumber, m.Role, m.IsLead, m.CreatedAt, m.DeletedAt))
            .ToListAsync(cancellationToken);

        // most recent exit first
        return await OnlyReleasedOrgsAsync(db, factions.Concat(groups).OrderByDescending(z => z.EndedAt).ToList(), scope, cancellationToken);
    }

    // for partners, keep only affiliations whose org (faction/group) is released to the agency
    private static async Task<List<PersonAffiliation>> OnlyReleasedOrgsAsync(AppDbContext db, List<PersonAffiliation> items, ViewerScope scope, CancellationToken cancellationToken)
    {
        if (scope.PartnerAgency is not { } agency || items.Count == 0)
        {
            return items;
        }
        var relF = await PartnerVisibility.ReleasedParentIdsAsync(db, nameof(Faction),
            items.Where(z => z.Type == nameof(Faction)).Select(z => z.Id).ToList(), agency, scope.MeId, cancellationToken);
        var relG = await PartnerVisibility.ReleasedParentIdsAsync(db, nameof(PersonGroup),
            items.Where(z => z.Type == nameof(PersonGroup)).Select(z => z.Id).ToList(), agency, scope.MeId, cancellationToken);
        return items.Where(z => (z.Type == nameof(Faction) && relF.Contains(z.Id))
                             || (z.Type == nameof(PersonGroup) && relG.Contains(z.Id))).ToList();
    }

    public async Task<List<DerivedRelation>> GetDerivedRelationsAsync(string personId, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await Visibility.IsRecordVisibleAsync(db, nameof(Person), personId, scope, cancellationToken))
        {
            return new();
        }
        // partners: hide fan-out
        if (scope.IsPartner)
        {
            return new();
        }
        var isLeadership = scope.MayClassifiedRead;

        // 1. viewer-visible orgs the person belongs to (factions + groups + parties)
        var myFactions = await db.FactionMembers
            .Where(m => m.PersonId == personId)
            .Join(db.Factions, m => m.FactionId, f => f.Id, (m, f) => f)
            .Where(f => isLeadership || !f.IsClassified)
            .Select(f => new { f.Id, f.Name })
            .ToListAsync(cancellationToken);
        var myGroups = await db.PersonGroupMembers
            .Where(m => m.PersonId == personId)
            .Join(db.PersonGroups, m => m.PersonGroupId, g => g.Id, (m, g) => g)
            .Where(g => isLeadership || !g.IsClassified)
            .Select(g => new { g.Id, g.Name })
            .ToListAsync(cancellationToken);
        var myParties = await db.PartyMembers
            .Where(m => m.PersonId == personId)
            .Join(db.Parties, m => m.PartyId, p => p.Id, (m, p) => p)
            .Where(p => isLeadership || !p.IsClassified)
            .Select(p => new { p.Id, p.Name })
            .ToListAsync(cancellationToken);

        var orgNames = new Dictionary<string, string>();
        foreach (var f in myFactions) orgNames[$"{nameof(Faction)}|{f.Id}"] = f.Name;
        foreach (var g in myGroups) orgNames[$"{nameof(PersonGroup)}|{g.Id}"] = g.Name;
        foreach (var p in myParties) orgNames[$"{nameof(Party)}|{p.Id}"] = p.Name;
        if (orgNames.Count == 0)
        {
            return new();
        }

        // 2. alliance/conflict links involving one of those orgs
        var myOrgIds = myFactions.Select(f => f.Id)
            .Concat(myGroups.Select(g => g.Id))
            .Concat(myParties.Select(p => p.Id))
            .ToList();
        var raw = await db.Links
            .Where(v => (v.Kind == LinkKind.Alliance || v.Kind == LinkKind.Conflict)
                     && (myOrgIds.Contains(v.SourceId) || myOrgIds.Contains(v.TargetId)))
            .Select(v => new { v.SourceType, v.SourceId, v.TargetType, v.TargetId, v.Kind })
            .ToListAsync(cancellationToken);

        // per link, pick own side and the partner org
        var partner = new List<(string SourceKey, string PartnerType, string PartnerId, LinkKind Kind)>();
        foreach (var v in raw)
        {
            var sourceKey = $"{v.SourceType}|{v.SourceId}";
            var targetKey = $"{v.TargetType}|{v.TargetId}";
            if (orgNames.ContainsKey(sourceKey))
            {
                partner.Add((sourceKey, v.TargetType, v.TargetId, v.Kind));
            }
            else if (orgNames.ContainsKey(targetKey))
            {
                partner.Add((targetKey, v.SourceType, v.SourceId, v.Kind));
            }
        }
        if (partner.Count == 0)
        {
            return new();
        }

        // 3. resolve visible partner orgs (classified leadership-only)
        var partnerFactionIds = partner.Where(p => p.PartnerType == nameof(Faction)).Select(p => p.PartnerId).Distinct().ToList();
        var partnerGroupsIds = partner.Where(p => p.PartnerType == nameof(PersonGroup)).Select(p => p.PartnerId).Distinct().ToList();
        var partnerPartyIds = partner.Where(p => p.PartnerType == nameof(Party)).Select(p => p.PartnerId).Distinct().ToList();
        var partnerFactions = (await db.Factions
            .Where(f => partnerFactionIds.Contains(f.Id) && (isLeadership || !f.IsClassified))
            .Select(f => new { f.Id, f.Name }).ToListAsync(cancellationToken)).ToDictionary(f => f.Id, f => f.Name);
        var partnerGroups = (await db.PersonGroups
            .Where(g => partnerGroupsIds.Contains(g.Id) && (isLeadership || !g.IsClassified))
            .Select(g => new { g.Id, g.Name }).ToListAsync(cancellationToken)).ToDictionary(g => g.Id, g => g.Name);
        var partnerParties = (await db.Parties
            .Where(p => partnerPartyIds.Contains(p.Id) && (isLeadership || !p.IsClassified))
            .Select(p => new { p.Id, p.Name }).ToListAsync(cancellationToken)).ToDictionary(p => p.Id, p => p.Name);

        // 4. members of the partner orgs (person ids per partner)
        var visibleFactionIds = partnerFactions.Keys.ToList();
        var visibleGroupsIds = partnerGroups.Keys.ToList();
        var visiblePartyIds = partnerParties.Keys.ToList();
        var factionMembers = await db.FactionMembers
            .Where(m => visibleFactionIds.Contains(m.FactionId))
            .Select(m => new { m.FactionId, m.PersonId }).ToListAsync(cancellationToken);
        var groupsMembers = await db.PersonGroupMembers
            .Where(m => visibleGroupsIds.Contains(m.PersonGroupId))
            .Select(m => new { m.PersonGroupId, m.PersonId }).ToListAsync(cancellationToken);
        var partyMembers = await db.PartyMembers
            .Where(m => visiblePartyIds.Contains(m.PartyId))
            .Select(m => new { m.PartyId, m.PersonId }).ToListAsync(cancellationToken);
        var membersPerPartner = factionMembers.GroupBy(m => m.FactionId).ToDictionary(g => g.Key, g => g.Select(x => x.PersonId).ToList());
        foreach (var grp in groupsMembers.GroupBy(m => m.PersonGroupId))
        {
            membersPerPartner[grp.Key] = grp.Select(x => x.PersonId).ToList();
        }
        foreach (var grp in partyMembers.GroupBy(m => m.PartyId))
        {
            membersPerPartner[grp.Key] = grp.Select(x => x.PersonId).ToList();
        }

        // 5. build candidates (deduped per (person, kind); exclude self)
        var candidates = new Dictionary<(string PersonId, LinkKind Kind), (string SourceName, string PartnerName)>();
        foreach (var p in partner)
        {
            var partnerName = p.PartnerType switch
            {
                nameof(Faction) => partnerFactions.TryGetValue(p.PartnerId, out var fn) ? fn : null,
                nameof(PersonGroup) => partnerGroups.TryGetValue(p.PartnerId, out var gn) ? gn : null,
                nameof(Party) => partnerParties.TryGetValue(p.PartnerId, out var pn) ? pn : null,
                _ => null,
            };
            if (partnerName is null || !membersPerPartner.TryGetValue(p.PartnerId, out var mids))
            {
                continue;
            }
            var sourceName = orgNames[p.SourceKey];
            foreach (var mid in mids)
            {
                if (mid == personId)
                {
                    continue;
                }
                candidates.TryAdd((mid, p.Kind), (sourceName, partnerName));
            }
        }
        if (candidates.Count == 0)
        {
            return new();
        }

        // 6. resolve persons (classified leadership-only)
        var personIds = candidates.Keys.Select(k => k.PersonId).Distinct().ToList();
        var people = (await db.People
            .Where(p => personIds.Contains(p.Id) && (isLeadership || !p.IsClassified))
            .Select(p => new { p.Id, p.Name, p.CaseNumber }).ToListAsync(cancellationToken))
            .ToDictionary(p => p.Id);

        var result = new List<DerivedRelation>();
        foreach (var ((pid, kind), (sourceName, partnerName)) in candidates)
        {
            if (!people.TryGetValue(pid, out var person))
            {
                continue;
            }
            result.Add(new DerivedRelation(kind, person.Id, person.Name, person.CaseNumber, sourceName, partnerName));
        }
        return result
            .OrderBy(e => e.Kind)
            .ThenBy(e => e.PersonName)
            .ToList();
    }

    public async Task<PersonPhoto> PhotoAddAsync(string personId, Stream content, string originalName, string contentType, long size, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        if (!fileStorage.IsAllowedType(contentType))
        {
            throw new InvalidOperationException($"Dateityp '{contentType}' ist nicht erlaubt.");
        }
        // enforce size limit server-side against disk-filling
        if (size > fileStorage.MaxBytes)
        {
            throw new InvalidOperationException($"Datei zu groß (max. {fileStorage.MaxBytes / (1024 * 1024)} MB).");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // check existence and visibility before writing any file
        var person = await db.People.FirstOrDefaultAsync(p => p.Id == personId, cancellationToken)
            ?? throw new InvalidOperationException($"Person '{personId}' nicht gefunden.");
        // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
        Permission.RequireMaySeeClassified(actor, person.SecrecyLevel);

        var fileName = await fileStorage.SaveAsync(content, contentType, cancellationToken);
        var photo = new PersonPhoto
        {
            PersonId = personId,
            FileNameSaved = fileName,
            OriginalName = originalName,
            ContentType = contentType,
            SizeBytes = size,
            CreatedAt = DateTime.UtcNow,
            CreatedById = actor.GetAgentId(),
        };
        db.PersonPhotos.Add(photo);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // roll back the written file if the insert fails
            fileStorage.Delete(fileName);
            throw;
        }
        return photo;
    }

    public async Task PhotoRemoveAsync(string photoId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var photo = await db.PersonPhotos.Include(f => f.Person).FirstOrDefaultAsync(f => f.Id == photoId, cancellationToken);
        if (photo is null)
        {
            return;
        }
        if (photo.Person is { } photoPerson)
        {
            // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
            Permission.RequireMaySeeClassified(actor, photoPerson.SecrecyLevel);
        }
        // remove the DB row (source of truth) before the file
        db.PersonPhotos.Remove(photo);
        await db.SaveChangesAsync(cancellationToken);
        fileStorage.Delete(photo.FileNameSaved);
    }

    public async Task PhotoSetFocalPointAsync(string photoId, int x, int y, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        x = Math.Clamp(x, 0, 100);
        y = Math.Clamp(y, 0, 100);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // tracked save (not ExecuteUpdate) so the change is audited and surfaces on the timeline
        var photo = await db.PersonPhotos.FirstOrDefaultAsync(p => p.Id == photoId, cancellationToken);
        if (photo is null)
        {
            return;
        }
        photo.FocalPointX = x;
        photo.FocalPointY = y;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<PersonPhoto?> GetPhotoWithPersonAsync(string photoId, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var photo = await db.PersonPhotos.Include(f => f.Person).FirstOrDefaultAsync(f => f.Id == photoId, cancellationToken);
        if (photo?.Person is null)
        {
            return null;
        }
        if (scope.PartnerAgency is { } agency)
        {
            // partners: parent visible AND (whole-record or this photo released)
            return await PartnerVisibility.IsChildVisibleToPartnerAsync(db, nameof(Person), photo.PersonId, nameof(PersonPhoto), photoId, agency, scope.MeId, cancellationToken)
                ? photo
                : null;
        }
        return photo.Person.IsClassified && !scope.MayClassifiedRead ? null : photo;
    }

    public async Task<List<AuditLog>> GetHistoryAsync(string personId, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await Visibility.IsRecordVisibleAsync(db, nameof(Person), personId, scope, cancellationToken))
        {
            return new();
        }
        // partners: hide audit
        if (scope.IsPartner)
        {
            return new();
        }
        // collect doc ids so their audit entries appear in the record history
        var docIds = await db.PersonDocs.IgnoreQueryFilters()
            .Where(d => d.PersonId == personId)
            .Select(d => d.Id)
            .ToListAsync(cancellationToken);

        var ids = new HashSet<string>(docIds) { personId };
        var types = new[] { nameof(Person), nameof(PersonDoc) };

        return await db.AuditLogs
            .Where(a => types.Contains(a.EntityType) && ids.Contains(a.EntityId))
            .OrderByDescending(a => a.Timestamp)
            .ToListAsync(cancellationToken);
    }

    private static void ChildrenMap(Person person, PersonInput input)
    {
        person.Aliases = input.Aliases
            .Where(a => !string.IsNullOrWhiteSpace(a.AliasName))
            .Select(a => new PersonAlias { PersonId = person.Id, AliasName = a.AliasName.Trim() })
            .ToList();
        person.PhoneNumbers = input.PhoneNumbers
            .Where(t => !string.IsNullOrWhiteSpace(t.Number))
            .Select(t => new PersonPhone { PersonId = person.Id, Number = t.Number.Trim(), Designation = t.Designation.TrimToNull() })
            .ToList();
        person.Vehicles = input.Vehicles
            .Where(f => !string.IsNullOrWhiteSpace(f.Designation) || !string.IsNullOrWhiteSpace(f.LicensePlate))
            .Select(f => new PersonVehicle { PersonId = person.Id, Designation = (f.Designation ?? string.Empty).Trim(), LicensePlate = f.LicensePlate.TrimToNull() })
            .ToList();
        person.Locations = input.Locations
            .Where(o => !string.IsNullOrWhiteSpace(o.Text))
            .Select(o => new PersonLocation { PersonId = person.Id, Text = o.Text.Trim(), Note = o.Note.TrimToNull() })
            .ToList();
        person.Weapons = input.Weapons
            .Where(w => !string.IsNullOrWhiteSpace(w.Text))
            .Select(w => new PersonWeapon { PersonId = person.Id, Text = w.Text.Trim() })
            .ToList();
    }

    /// <summary>Stage profile values (weapons/vehicles/locations) for the shared suggestion catalog; classified records excluded. Caller persists it.</summary>
    private async Task SuggestionsStageAsync(AppDbContext db, Person person, CancellationToken cancellationToken)
    {
        if (person.IsClassified)
        {
            return;
        }
        await suggestion.StageAsync(db, SuggestionType.Weapon, person.Weapons.Select(w => w.Text), cancellationToken);
        await suggestion.StageAsync(db, SuggestionType.Vehicle, person.Vehicles.Select(f => f.Designation), cancellationToken);
        await suggestion.StageAsync(db, SuggestionType.Location, person.Locations.Select(o => o.Text), cancellationToken);
    }

    private static string? Empty(string? s) => s.TrimToNull();

    // scope-filtered people query
    private static IQueryable<Person> VisiblePeople(AppDbContext db, ViewerScope scope)
        => scope.PartnerAgency is { } agency
            ? db.People.OnlyPartnerVisible(db, agency, scope.MeId)
            : db.People.OnlyVisible(scope);
}
