using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Parties;
using NOOSE_Website.Models.People;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IParteiService" />
public class PartyService(
    IDbContextFactory<AppDbContext> dbFactory, ICaseNumberService caseNumber, IProfileSuggestionService suggestion,
    IPersonService personService, IThreatScoreService threat, INotificationService notifications,
    IPartyPhotoStorageService photoStorage) : IPartyService
{
    private static string MentionScope(Party p) => MentionNotify.Scope(p.Description, p.Targets, p.Remarks);

    public async Task<List<Party>> GetListAsync(ViewerScope scope, ArchiveFilter filter = ArchiveFilter.Active,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // include members so the list count matches the detail view
        return await VisibleParties(db, scope)
            .Apply(filter)
            .Include(p => p.Members).ThenInclude(m => m.Person)
            .Include(p => p.Photos)
            .AsSplitQuery()
            .OrderByDescending(p => p.ModifiedAt ?? p.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<Party?> GetDetailAsync(string id, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // photos come along for the card's title image
        var party = await db.Parties
            .Include(p => p.Photos)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (party is null || !await Visibility.IsRecordVisibleAsync(db, nameof(Party), id, scope, cancellationToken))
        {
            return null;
        }
        return party;
    }

    public async Task<List<Party>> GetTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Parties.IgnoreQueryFilters()
            .Where(p => p.IsDeleted)
            .OrderByDescending(p => p.DeletedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<Party>> SearchAsync(string? searchText, bool isLeadership, int max = 20, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // a picker must not offer a record that was filed away
        var query = db.Parties.OnlyActive().Where(p => isLeadership || !p.IsClassified);

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

    public async Task<Party> CreateAsync(PartyInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        ClassificationHelper.CheckRankGate(input.Classification, actor);
        Permission.RequireMayAssignClassification(actor, input.SecrecyLevel);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var party = new Party
        {
            CaseNumber = await caseNumber.NextAsync(db, "PT", cancellationToken),
            Name = input.Name.Trim(),
            Description = input.Description.TrimToNull(),
            Targets = input.Targets.TrimToNull(),
            Remarks = input.Remarks.TrimToNull(),
            Classification = input.Classification,
            SecrecyLevel = input.SecrecyLevel,
        };

        if (input.Classification != Classification.Unknown)
        {
            db.ClassificationHistory.Add(ClassificationHelper.Entry(nameof(Party), party.Id, input.Classification, input.ClassificationJustification, actor));
        }

        db.Parties.Add(party);
        await db.SaveChangesAsync(cancellationToken);

        // import members from the create form, then build colleague links
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
                    // same new name within one form creates only one record
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
                db.PartyMembers.Add(new PartyMember
                {
                    PartyId = party.Id,
                    PersonId = pid,
                    Role = m.Role.TrimToNull(),
                    IsLead = m.IsLead,
                });
                added.Add(pid);
            }
            if (added.Count > 0)
            {
                await SuggestionsStageAsync(db, party.IsClassified, input.Members.Select(m => m.Role), cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                foreach (var pid in added)
                {
                    await PartyColleaguesSyncAsync(db, pid, cancellationToken);
                }
            }
        }

        // creator auto-assigned as investigation lead (ensures at least one)
        var creatorId = actor.GetAgentId();
        if (creatorId is not null)
        {
            db.PartyAgents.Add(new PartyAgent
            {
                PartyId = party.Id,
                AgentId = creatorId,
                IsInvestigationLead = true,
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);

        await MentionNotify.DeltaAsync(notifications, null, MentionScope(party), "einer Parteiakte",
            nameof(Party), party.Id, actor, cancellationToken);
        return party;
    }

    public async Task RefreshAsync(string id, PartyInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var party = await db.Parties.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Partei '{id}' nicht gefunden.");

        // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
        Permission.RequireMaySeeClassified(actor, party.SecrecyLevel);

        var oldMentions = MentionScope(party);
        party.Name = input.Name.Trim();
        party.Description = input.Description.TrimToNull();
        party.Targets = input.Targets.TrimToNull();
        party.Remarks = input.Remarks.TrimToNull();
        Permission.RequireMayAssignClassification(actor, input.SecrecyLevel);
        CriticalChange.NoteSecrecy(db, nameof(Party), id, party.SecrecyLevel, input.SecrecyLevel, input.SecrecyReason, actor);
        party.SecrecyLevel = input.SecrecyLevel;

        await db.SaveChangesAsync(cancellationToken);

        await MentionNotify.DeltaAsync(notifications, oldMentions, MentionScope(party), "einer Parteiakte",
            nameof(Party), id, actor, cancellationToken);
    }

    public async Task DeleteAsync(string id, string reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);
        var note = CriticalChange.RequireDeletionReason(reason);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var party = await db.Parties.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Partei '{id}' nicht gefunden.");
        db.Parties.Remove(party);
        CriticalChange.NoteDeletion(db, nameof(Party), id, note, actor);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var party = await db.Parties.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Partei '{id}' nicht gefunden.");

        party.IsDeleted = false;
        party.DeletedAt = null;
        party.DeletedById = null;
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
        if (!await Visibility.IsRecordVisibleAsync(db, nameof(Party), id, ViewerScope.From(actor), cancellationToken))
        {
            throw new UnauthorizedAccessException("Diese Akte ist für dich nicht zugänglich.");
        }
        if (!await RecordArchive.SetArchivedAsync<Party>(db, id, archived, reason, actor, cancellationToken))
        {
            return;
        }
        // ExecuteUpdate bypasses the audit interceptor, so the row is written by hand
        var note = archived && !string.IsNullOrWhiteSpace(reason)
            ? ManualAudit.Change("Archivgrund", null, reason.Trim())
            : null;
        db.AuditLogs.Add(ManualAudit.Row(nameof(Party), id,
            archived ? AuditAction.Archived : AuditAction.Unarchived, actor, note));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task ClassificationSetAsync(string id, Classification @new, string? justification, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        ClassificationHelper.CheckRankGate(@new, actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var party = await db.Parties.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Partei '{id}' nicht gefunden.");

        // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
        Permission.RequireMaySeeClassified(actor, party.SecrecyLevel);

        CriticalChange.NoteDowngrade(db, nameof(Party), id, party.Classification, @new, justification, actor);
        party.Classification = @new;
        db.ClassificationHistory.Add(ClassificationHelper.Entry(nameof(Party), id, @new, justification, actor));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<ClassificationHistory>> GetClassificationHistoryAsync(string id, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await ClassificationHelper.HistoryAsync(db, nameof(Party), id, scope, cancellationToken);
    }

    // scope-filtered party query
    private static IQueryable<Party> VisibleParties(AppDbContext db, ViewerScope scope)
        => scope.PartnerAgency is { } agency
            ? db.Parties.OnlyPartnerVisible(db, agency, scope.MeId)
            : db.Parties.OnlyVisible(scope);

    public async Task<List<PartyMember>> GetMembersAsync(string partyId, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var members = await db.PartyMembers
            .Where(m => m.PartyId == partyId)
            .Include(m => m.Person)
            .ToListAsync(cancellationToken);

        // Person == null → trashed; hide. Classified persons only for leadership.
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
        return visible
            .OrderByDescending(m => m.IsLead)
            .ThenBy(m => m.Person!.Name)
            .ToList();
    }

    public async Task MemberAddAsync(string partyId, PartyMemberInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var party = await db.Parties.FirstOrDefaultAsync(p => p.Id == partyId, cancellationToken)
            ?? throw new InvalidOperationException($"Partei '{partyId}' nicht gefunden.");
        // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
        Permission.RequireMaySeeClassified(actor, party.SecrecyLevel);

        var personId = await PersonIdDetermineAsync(db, input.PersonId, input.NewPersonName, actor, cancellationToken);
        if (await db.PartyMembers.AnyAsync(m => m.PartyId == partyId && m.PersonId == personId, cancellationToken))
        {
            throw new InvalidOperationException("Diese Person ist bereits Mitglied der Partei.");
        }

        // membership + colleague links in one transaction
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        db.PartyMembers.Add(new PartyMember
        {
            PartyId = partyId,
            PersonId = personId,
            Role = input.Role.TrimToNull(),
            IsLead = input.IsLead,
        });
        await SuggestionsStageAsync(db, party.IsClassified, new[] { input.Role }, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await PartyColleaguesSyncAsync(db, personId, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        await threat.NewCalculatePersonScoreAsync(personId, cancellationToken);
    }

    /// <summary>Resolves an existing person id, or creates a fresh record when only a new name is given.</summary>
    private Task<string> PersonIdDetermineAsync(AppDbContext db, string? personId, string? newName, ClaimsPrincipal actor, CancellationToken cancellationToken)
        => MemberHelper.PersonIdDetermineAsync(db, personService, personId, newName, actor, cancellationToken);

    public async Task MemberChangeAsync(string memberId, string? role, bool isLead, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var member = await db.PartyMembers.Include(m => m.Party).FirstOrDefaultAsync(m => m.Id == memberId, cancellationToken)
            ?? throw new InvalidOperationException("Mitgliedschaft nicht gefunden.");
        if (member.Party is { } memberParty)
        {
            // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
            Permission.RequireMaySeeClassified(actor, memberParty.SecrecyLevel);
        }
        member.Role = role.TrimToNull();
        member.IsLead = isLead;
        await SuggestionsStageAsync(db, member.Party?.IsClassified == true, new[] { role }, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await threat.NewCalculatePersonScoreAsync(member.PersonId, cancellationToken);
    }

    public async Task MemberRemoveAsync(string memberId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var member = await db.PartyMembers.Include(m => m.Party).FirstOrDefaultAsync(m => m.Id == memberId, cancellationToken);
        if (member is null)
        {
            return;
        }
        if (member.Party is { } memberParty)
        {
            // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
            Permission.RequireMaySeeClassified(actor, memberParty.SecrecyLevel);
        }
        var personId = member.PersonId;
        // soft-delete keeps the membership as a history entry (exit date)
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        db.PartyMembers.Remove(member);
        await db.SaveChangesAsync(cancellationToken);
        await PartyColleaguesSyncAsync(db, personId, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        await threat.NewCalculatePersonScoreAsync(personId, cancellationToken);
    }

    public async Task<List<PartyAgent>> GetAgentsAsync(string partyId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.PartyAgents
            .Where(a => a.PartyId == partyId)
            .Include(a => a.Agent)
            .OrderByDescending(a => a.IsInvestigationLead)
            .ThenBy(a => a.Agent!.Codename)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<PartyAgent>> GetInvestigationLeadAsync(string partyId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.PartyAgents
            .Where(a => a.PartyId == partyId && a.IsInvestigationLead)
            .Include(a => a.Agent)
            .OrderBy(a => a.Agent!.Codename)
            .ToListAsync(cancellationToken);
    }

    public async Task AgentAllocateAsync(string partyId, string agentId, bool asInvestigationLead, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var party = await db.Parties.FirstOrDefaultAsync(p => p.Id == partyId, cancellationToken)
            ?? throw new InvalidOperationException($"Partei '{partyId}' nicht gefunden.");
        // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
        Permission.RequireMaySeeClassified(actor, party.SecrecyLevel);
        await RequireLeadershipOrELAsync(db, partyId, actor, cancellationToken);
        // only leadership may grant the lead flag
        if (asInvestigationLead)
        {
            Permission.RequireLeadership(actor);
        }
        // same rule as the picker: a stale circuit must not allocate a team lead, partner or ex-agent
        if (!await db.Users.OnlySelectable().AnyAsync(u => u.Id == agentId, cancellationToken))
        {
            throw new InvalidOperationException("Der gewählte Agent wurde nicht gefunden oder ist nicht zuteilbar.");
        }
        if (await db.PartyAgents.AnyAsync(a => a.PartyId == partyId && a.AgentId == agentId, cancellationToken))
        {
            throw new InvalidOperationException("Dieser Agent ist der Partei bereits zugeteilt.");
        }

        db.PartyAgents.Add(new PartyAgent
        {
            PartyId = partyId,
            AgentId = agentId,
            IsInvestigationLead = asInvestigationLead,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AgentRemoveAsync(string allocationId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var allocation = await db.PartyAgents.Include(a => a.Party).FirstOrDefaultAsync(a => a.Id == allocationId, cancellationToken);
        if (allocation is null)
        {
            return;
        }
        if (allocation.Party is { } allocParty)
        {
            // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
            Permission.RequireMaySeeClassified(actor, allocParty.SecrecyLevel);
        }
        await RequireLeadershipOrELAsync(db, allocation.PartyId, actor, cancellationToken);
        db.PartyAgents.Remove(allocation);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task InvestigationLeadSetAsync(string allocationId, bool @is, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        // leadership-only
        Permission.RequireLeadership(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var allocation = await db.PartyAgents.FirstOrDefaultAsync(a => a.Id == allocationId, cancellationToken)
            ?? throw new InvalidOperationException("Zuteilung nicht gefunden.");
        allocation.IsInvestigationLead = @is;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Throws unless the actor is leadership or an investigation lead of this party.</summary>
    private static async Task RequireLeadershipOrELAsync(AppDbContext db, string partyId, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        if (actor.IsLeadership())
        {
            return;
        }
        var agentId = actor.GetAgentId();
        var isEL = agentId is not null && await db.PartyAgents
            .AnyAsync(a => a.PartyId == partyId && a.AgentId == agentId && a.IsInvestigationLead, cancellationToken);
        if (!isEL)
        {
            throw new UnauthorizedAccessException(
                "Agents zuteilen oder entfernen dürfen nur die Führung oder ein Ermittlungsleiter dieser Akte.");
        }
    }

    public async Task<List<AuditLog>> GetHistoryAsync(string partyId, bool isLeadership, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await Visibility.IsRecordVisibleAsync(db, nameof(Party), partyId, isLeadership, cancellationToken))
        {
            return new();
        }
        var memberIds = await db.PartyMembers
            .Where(m => m.PartyId == partyId)
            .Select(m => m.Id)
            .ToListAsync(cancellationToken);
        var agentAllocationIds = await db.PartyAgents
            .Where(a => a.PartyId == partyId)
            .Select(a => a.Id)
            .ToListAsync(cancellationToken);

        // manual links touching this party, including removed ones so their delete entry shows
        var relationIds = await db.Links
            .IgnoreQueryFilters()
            .Where(v => !v.Automatic
                && ((v.SourceType == nameof(Party) && v.SourceId == partyId)
                 || (v.TargetType == nameof(Party) && v.TargetId == partyId)))
            .Select(v => v.Id)
            .ToListAsync(cancellationToken);

        var ids = new HashSet<string>(memberIds) { partyId };
        ids.UnionWith(agentAllocationIds);
        ids.UnionWith(relationIds);
        var types = new[] { nameof(Party), nameof(PartyMember), nameof(PartyAgent), nameof(Link) };

        return await db.AuditLogs
            .Where(a => types.Contains(a.EntityType) && ids.Contains(a.EntityId))
            .OrderByDescending(a => a.Timestamp)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<PartyPhoto>> GetPhotosAsync(string partyId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // Title image first, then by capture time.
        return await db.PartyPhotos
            .Where(f => f.PartyId == partyId)
            .OrderByDescending(f => f.IsTitleImage)
            .ThenBy(f => f.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<PartyPhoto?> GetPhotoWithPartyAsync(string photoId, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var photo = await db.PartyPhotos.Include(f => f.Party).FirstOrDefaultAsync(f => f.Id == photoId, cancellationToken);
        if (photo?.Party is null)
        {
            return null;
        }
        if (scope.PartnerAgency is { } agency)
        {
            // partners: parent visible AND (whole-record or this photo released)
            return await PartnerVisibility.IsChildVisibleToPartnerAsync(db, nameof(Party), photo.PartyId, nameof(PartyPhoto), photoId, agency, scope.MeId, cancellationToken)
                ? photo
                : null;
        }
        // the record's own audience (TRU/HRB) reads it too, not leadership alone
        return scope.CanSee(photo.Party.SecrecyLevel) ? photo : null;
    }

    public async Task<PartyPhoto> PhotoAddAsync(string partyId, Stream content, string originalName, string contentType, long size, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
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
        var party = await db.Parties.FirstOrDefaultAsync(p => p.Id == partyId, cancellationToken)
            ?? throw new InvalidOperationException($"Partei '{partyId}' nicht gefunden.");
        // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
        Permission.RequireMaySeeClassified(actor, party.SecrecyLevel);

        // The first photo becomes the title image automatically.
        var isFirst = !await db.PartyPhotos.AnyAsync(f => f.PartyId == partyId, cancellationToken);

        var fileName = await photoStorage.SaveAsync(content, contentType, cancellationToken);
        var photo = new PartyPhoto
        {
            PartyId = partyId,
            FileNameSaved = fileName,
            OriginalName = originalName,
            ContentType = contentType,
            SizeBytes = size,
            IsTitleImage = isFirst,
            CreatedAt = DateTime.UtcNow,
            CreatedById = actor.GetAgentId(),
        };
        db.PartyPhotos.Add(photo);
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
        var photo = await db.PartyPhotos.Include(f => f.Party).FirstOrDefaultAsync(f => f.Id == photoId, cancellationToken);
        if (photo is null)
        {
            return;
        }
        if (photo.Party is { } photoParty)
        {
            // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
            Permission.RequireMaySeeClassified(actor, photoParty.SecrecyLevel);
        }
        // The title image is the record's profile picture: hand it on instead of leaving the file blank.
        if (photo.IsTitleImage)
        {
            var remaining = await db.PartyPhotos
                .Where(f => f.PartyId == photo.PartyId && f.Id != photoId)
                .ToListAsync(cancellationToken);
            if (RecordAvatar.Successor(remaining) is { } successor)
            {
                successor.IsTitleImage = true;
            }
        }
        // Remove the DB record first, then the file, so a storage error leaves no record pointing at a missing file.
        db.PartyPhotos.Remove(photo);
        await db.SaveChangesAsync(cancellationToken);
        photoStorage.Delete(photo.FileNameSaved);
    }

    public async Task AsTitleImageSetAsync(string photoId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var photo = await db.PartyPhotos.Include(f => f.Party).FirstOrDefaultAsync(f => f.Id == photoId, cancellationToken)
            ?? throw new InvalidOperationException($"Foto '{photoId}' nicht gefunden.");
        if (photo.Party is { } photoParty)
        {
            // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
            Permission.RequireMaySeeClassified(actor, photoParty.SecrecyLevel);
        }

        // Exactly one title image per party: clear all siblings, mark this one.
        var siblings = await db.PartyPhotos.Where(f => f.PartyId == photo.PartyId).ToListAsync(cancellationToken);
        foreach (var g in siblings)
        {
            g.IsTitleImage = g.Id == photoId;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Syncs the person's automatic party-colleague links: one exists iff two people share a party.</summary>
    private static async Task PartyColleaguesSyncAsync(AppDbContext db, string personId, CancellationToken cancellationToken)
    {
        var myParties = await db.PartyMembers
            .Where(m => m.PersonId == personId)
            .Select(m => m.PartyId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var should = myParties.Count == 0
            ? new List<string>()
            : await db.PartyMembers
                .Where(m => myParties.Contains(m.PartyId) && m.PersonId != personId)
                .Select(m => m.PersonId)
                .Distinct()
                .ToListAsync(cancellationToken);

        await ColleaguesSync.SyncAsync(db, personId, ColleaguesSync.PartyColleague, should, cancellationToken);
    }

    /// <summary>Stage member roles for the shared suggestion catalog; classified records excluded. Caller persists it.</summary>
    private async Task SuggestionsStageAsync(AppDbContext db, bool isClassified, IEnumerable<string?> roles, CancellationToken cancellationToken)
    {
        if (isClassified)
        {
            return;
        }
        await suggestion.StageAsync(db, SuggestionType.PartyRole, roles.Where(r => r is not null).Select(r => r!), cancellationToken);
    }
}
