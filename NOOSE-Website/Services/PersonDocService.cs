using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.People;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IPersonDokService" />
public class PersonDocService(
    IDbContextFactory<AppDbContext> dbFactory, IPersonService personService, IThreatScoreService threat,
    INotificationService notifications) : IPersonDocService
{
    private const string Mentioned = "einem Personen-Dok";

    private Task NotifyMentionsAsync(string? oldText, string? newText, string personId,
        ClaimsPrincipal actor, CancellationToken cancellationToken)
        => MentionNotify.DeltaAsync(notifications, oldText, newText, Mentioned, nameof(Person), personId,
            actor, cancellationToken, childType: nameof(PersonDoc));

    public async Task<List<PersonDocDisplay>> GetForPersonAsync(string personId, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // independently re-check parent visibility
        if (!await Visibility.IsRecordVisibleAsync(db, nameof(Person), personId, scope, cancellationToken))
        {
            return new();
        }
        var docs = await db.PersonDocs
            .Where(d => d.PersonId == personId)
            .OrderByDescending(d => d.Timestamp)
            .ToListAsync(cancellationToken);
        if (scope.PartnerAgency is { } agency)
        {
            docs = await PartnerVisibility.FilterChildrenAsync(db, nameof(Person), personId, nameof(PersonDoc), docs, d => d.Id, agency, scope.MeId, cancellationToken);
        }
        return await ToDisplayAsync(db, docs, scope.MayClassifiedRead, cancellationToken);
    }

    public async Task<List<PersonDocDisplay>> GetForOrgAsync(string orgType, string orgId, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // check org record visibility
        if (!await Visibility.IsRecordVisibleAsync(db, orgType, orgId, scope, cancellationToken))
        {
            return new();
        }
        // subquery rather than a predicate on the navigation: the one gate is an IQueryable extension
        var visiblePeople = db.People.OnlyVisible(scope).Select(p => p.Id);
        var docs = await db.PersonDocs
            .Include(d => d.Person)
            // null person trashed; the rest gate on the person's secrecy level
            .Where(d => d.OrgType == orgType && d.OrgId == orgId
                && d.Person != null && visiblePeople.Contains(d.PersonId))
            .OrderByDescending(d => d.Timestamp)
            .ToListAsync(cancellationToken);
        if (scope.PartnerAgency is { } agency)
        {
            // person, then doc
            var released = await PartnerVisibility.ReleasedParentIdsAsync(db, nameof(Person), docs.Select(d => d.PersonId).Distinct().ToList(), agency, scope.MeId, cancellationToken);
            docs = docs.Where(d => released.Contains(d.PersonId)).ToList();
            var visibleDocs = await PartnerVisibility.VisibleChildIdsAsync(db, nameof(PersonDoc),
                docs.Select(d => (nameof(Person), d.PersonId, d.Id)).ToList(), agency, scope.MeId, cancellationToken);
            docs = docs.Where(d => visibleDocs.Contains(d.Id)).ToList();
        }
        return await ToDisplayAsync(db, docs, scope.MayClassifiedRead, cancellationToken);
    }

    public async Task<List<PersonDocDisplay>> GetAllAsync(bool isLeadership, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var docs = await db.PersonDocs
            .Include(d => d.Person)
            // soft-deleted parent surfaces as null → hide those
            .Where(d => d.Person != null && (isLeadership || !d.Person.IsClassified))
            .OrderByDescending(d => d.Timestamp)
            .ToListAsync(cancellationToken);
        return await ToDisplayAsync(db, docs, isLeadership, cancellationToken);
    }

    /// <summary>Enrich docs with their linked org's name/case number/route (classification-filtered); unresolved links get empty fields.</summary>
    private static async Task<List<PersonDocDisplay>> ToDisplayAsync(AppDbContext db, List<PersonDoc> docs, bool isLeadership, CancellationToken cancellationToken)
    {
        var factionIds = docs.Where(d => d.OrgType == nameof(Faction) && d.OrgId is not null).Select(d => d.OrgId!).Distinct().ToList();
        var groupsIds = docs.Where(d => d.OrgType == nameof(PersonGroup) && d.OrgId is not null).Select(d => d.OrgId!).Distinct().ToList();

        var factions = new Dictionary<string, (string Name, string CaseNumber)>();
        if (factionIds.Count > 0)
        {
            factions = await db.Factions
                .Where(f => factionIds.Contains(f.Id) && (isLeadership || !f.IsClassified))
                .Select(f => new { f.Id, f.Name, f.CaseNumber })
                .ToDictionaryAsync(f => f.Id, f => (f.Name, f.CaseNumber), cancellationToken);
        }

        var groups = new Dictionary<string, (string Name, string CaseNumber)>();
        if (groupsIds.Count > 0)
        {
            groups = await db.PersonGroups
                .Where(g => groupsIds.Contains(g.Id) && (isLeadership || !g.IsClassified))
                .Select(g => new { g.Id, g.Name, g.CaseNumber })
                .ToDictionaryAsync(g => g.Id, g => (g.Name, g.CaseNumber), cancellationToken);
        }

        var creatorIds = docs.Where(d => d.CreatedById is not null).Select(d => d.CreatedById!).Distinct().ToList();
        var creators = new Dictionary<string, string>();
        if (creatorIds.Count > 0)
        {
            creators = await db.Users
                .Where(a => creatorIds.Contains(a.Id))
                .ToDictionaryAsync(a => a.Id, a => a.Codename, cancellationToken);
        }

        return docs.Select(d =>
        {
            var codename = d.CreatedById is not null && creators.TryGetValue(d.CreatedById, out var cn) ? cn : null;
            if (d.OrgId is not null && d.OrgType == nameof(Faction) && factions.TryGetValue(d.OrgId, out var f))
            {
                return new PersonDocDisplay(d, f.Name, f.CaseNumber, $"/fraktionen/{d.OrgId}", codename);
            }
            if (d.OrgId is not null && d.OrgType == nameof(PersonGroup) && groups.TryGetValue(d.OrgId, out var g))
            {
                return new PersonDocDisplay(d, g.Name, g.CaseNumber, $"/personengruppen/{d.OrgId}", codename);
            }
            return new PersonDocDisplay(d, null, null, null, codename);
        }).ToList();
    }

    public async Task<PersonDoc> CreateAsync(string personId, PersonDocInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var person = await db.People.FirstOrDefaultAsync(p => p.Id == personId, cancellationToken)
            ?? throw new InvalidOperationException($"Person '{personId}' nicht gefunden.");
        // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
        Permission.RequireMaySeeClassified(actor, person.SecrecyLevel);

        var doc = await CreateDocAsync(db, personId, input, cancellationToken);
        // measures feed both faction heat and the person score
        await threat.NewCalculateForPersonAsync(personId, cancellationToken);
        await threat.NewCalculatePersonScoreAsync(personId, cancellationToken);
        await NotifyMentionsAsync(null, MentionNotify.Scope(doc.Reason, doc.ReceivedInformation), personId, actor, cancellationToken);
        return doc;
    }

    public async Task<PersonDoc> CreateForNewPersonAsync(string name, PersonDocInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Für eine neue Akte ist ein Name erforderlich.");
        }

        // create the record via the person service (own context, already committed), then attach the doc
        var person = await personService.CreateAsync(new PersonInput { Name = name.Trim() }, actor, cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var doc = await CreateDocAsync(db, person.Id, input, cancellationToken);
        await threat.NewCalculateForPersonAsync(person.Id, cancellationToken);
        await threat.NewCalculatePersonScoreAsync(person.Id, cancellationToken);
        await NotifyMentionsAsync(null, MentionNotify.Scope(doc.Reason, doc.ReceivedInformation), person.Id, actor, cancellationToken);
        return doc;
    }

    public async Task<PersonDoc> RefreshAsync(string docId, PersonDocInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var doc = await db.PersonDocs
            .Include(d => d.Person)
            .FirstOrDefaultAsync(d => d.Id == docId, cancellationToken)
            ?? throw new InvalidOperationException($"Dok '{docId}' nicht gefunden.");

        if (doc.Person is { } docPerson)
        {
            // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
            Permission.RequireMaySeeClassified(actor, docPerson.SecrecyLevel);
        }

        // capture old state before overwriting; status re-evaluation needs both
        var altOutcome = doc.Outcome;
        var altTimestamp = doc.Timestamp;
        var oldText = MentionNotify.Scope(doc.Reason, doc.ReceivedInformation);
        // a re-linked doc refreshes both the old and the new faction
        var altOrg = (doc.OrgType, doc.OrgId);

        doc.Timestamp = input.Timestamp;
        doc.Reason = input.Reason.TrimToNull();
        doc.Faction = input.Faction.TrimToNull();
        var aktOrgId = input.OrgId.TrimToNull();
        doc.OrgId = aktOrgId;
        // no orphan type without id
        doc.OrgType = aktOrgId is null ? null : input.OrgType.TrimToNull();
        doc.ReceivedInformation = input.ReceivedInformation.TrimToNull();
        doc.TruthSerum = input.TruthSerum;
        doc.Outcome = input.Outcome;
        // memory loss follows the amnesty-injection outcome
        doc.MemoryDeleted = input.Outcome == MeasureOutcome.Injection;

        if (doc.Person is not null)
        {
            StatusNewEvaluate(doc.Person, altOutcome, altTimestamp, input);
        }

        await db.SaveChangesAsync(cancellationToken);
        await DocsStampAsync(db, new[] { altOrg, (doc.OrgType, doc.OrgId) }, cancellationToken);
        // changed outcome/timestamp affects faction heat and the person score
        await threat.NewCalculateForPersonAsync(doc.PersonId, cancellationToken);
        await threat.NewCalculatePersonScoreAsync(doc.PersonId, cancellationToken);
        await NotifyMentionsAsync(oldText, MentionNotify.Scope(doc.Reason, doc.ReceivedInformation), doc.PersonId, actor, cancellationToken);
        return doc;
    }

    /// <summary>Re-evaluates a doc's life-status effect; only owns the death window if it stems from the doc's own old timestamp.</summary>
    private static void StatusNewEvaluate(Person person, MeasureOutcome altOutcome, DateTime altTimestamp, PersonDocInput @new)
    {
        var altWasShot = altOutcome == MeasureOutcome.Shot;
        var newIsShot = @new.Outcome == MeasureOutcome.Shot;
        var ownsWindow = person.LifeStatus == LifeStatus.Dead
            && person.DeadUntil == LifeStatusLogic.DeadUntilFrom(altTimestamp);

        if (newIsShot)
        {
            if (!altWasShot)
            {
                // newly shot: set death at measure time
                person.LifeStatus = LifeStatus.Dead;
                person.DeadUntil = LifeStatusLogic.DeadUntilFrom(@new.Timestamp);
            }
            else if (ownsWindow)
            {
                // still shot, time shifted → move own death window
                person.DeadUntil = LifeStatusLogic.DeadUntilFrom(@new.Timestamp);
            }
        }
        else if (altWasShot && ownsWindow)
        {
            // no longer shot → undo the death window this doc set
            person.LifeStatus = LifeStatus.Alive;
            person.DeadUntil = null;
        }
    }

    private async Task<PersonDoc> CreateDocAsync(AppDbContext db, string personId, PersonDocInput input, CancellationToken cancellationToken)
    {
        var orgId = input.OrgId.TrimToNull();
        var doc = new PersonDoc
        {
            PersonId = personId,
            Timestamp = input.Timestamp,
            Reason = input.Reason.TrimToNull(),
            Faction = input.Faction.TrimToNull(),
            OrgId = orgId,
            // no orphan type without id
            OrgType = orgId is null ? null : input.OrgType.TrimToNull(),
            ReceivedInformation = input.ReceivedInformation.TrimToNull(),
            TruthSerum = input.TruthSerum,
            Outcome = input.Outcome,
        };

        // measure outcome drives the person's life status
        switch (input.Outcome)
        {
            case MeasureOutcome.Shot:
                // load person in the same context so the status change saves with the doc
                var person = await db.People.FirstOrDefaultAsync(p => p.Id == personId, cancellationToken);
                if (person is not null)
                {
                    var newDeadUntil = LifeStatusLogic.DeadUntilFrom(input.Timestamp);
                    // never shorten a later, already-running death window
                    if (person.DeadUntil is null || newDeadUntil > person.DeadUntil)
                    {
                        person.LifeStatus = LifeStatus.Dead;
                        person.DeadUntil = newDeadUntil;
                    }
                }
                break;
            case MeasureOutcome.Injection:
                // amnesty injection: person survives but loses memory
                doc.MemoryDeleted = true;
                break;
        }

        db.PersonDocs.Add(doc);
        await db.SaveChangesAsync(cancellationToken);
        await DocsStampAsync(db, new[] { (doc.OrgType, doc.OrgId) }, cancellationToken);
        return doc;
    }

    /// <summary>Docs are one of the four faction freshness facets; only faction links carry a stamp.</summary>
    private static Task DocsStampAsync(AppDbContext db, IEnumerable<(string? OrgType, string? OrgId)> links,
        CancellationToken cancellationToken)
        => FactionRecency.StampAsync(db,
            links.Where(l => l.OrgType == nameof(Faction) && !string.IsNullOrEmpty(l.OrgId)).Select(l => l.OrgId!),
            FactionRecencyFacet.Docs, cancellationToken);

    public async Task DeleteAsync(string docId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var doc = await db.PersonDocs.Include(d => d.Person).FirstOrDefaultAsync(d => d.Id == docId, cancellationToken);
        if (doc is null)
        {
            return;
        }
        if (doc.Person is { } docPerson)
        {
            // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
            Permission.RequireMaySeeClassified(actor, docPerson.SecrecyLevel);
        }

        // if this shot-doc set the current death window, undo the status on delete
        if (doc.Person is not null && doc.Outcome == MeasureOutcome.Shot
            && doc.Person.LifeStatus == LifeStatus.Dead
            && doc.Person.DeadUntil == LifeStatusLogic.DeadUntilFrom(doc.Timestamp))
        {
            doc.Person.LifeStatus = LifeStatus.Alive;
            doc.Person.DeadUntil = null;
        }

        var personId = doc.PersonId;
        var org = (doc.OrgType, doc.OrgId);
        db.PersonDocs.Remove(doc);
        await db.SaveChangesAsync(cancellationToken);
        await DocsStampAsync(db, new[] { org }, cancellationToken);
        // removed measure drops out of faction heat and the person score
        await threat.NewCalculateForPersonAsync(personId, cancellationToken);
        await threat.NewCalculatePersonScoreAsync(personId, cancellationToken);
    }
}
