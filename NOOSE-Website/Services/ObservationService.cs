using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.People;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IObservationService" />
public class ObservationService(
    IDbContextFactory<AppDbContext> dbFactory, IThreatScoreService threat, INotificationService notifications)
    : IObservationService
{
    private const string Mentioned = "einer Beobachtung";

    private Task NotifyMentionsAsync(string? oldText, string? newText, string personId,
        ClaimsPrincipal actor, CancellationToken cancellationToken)
        => MentionNotify.DeltaAsync(notifications, oldText, newText, Mentioned, nameof(Person), personId,
            actor, cancellationToken, childType: nameof(Observation));

    public async Task<List<ObservationDisplay>> GetForPersonAsync(string personId, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // independently re-check parent visibility
        if (!await Visibility.IsRecordVisibleAsync(db, nameof(Person), personId, scope, cancellationToken))
        {
            return new();
        }
        var entries = await db.Observations
            .Where(o => o.PersonId == personId)
            .OrderByDescending(o => o.Start)
            .ToListAsync(cancellationToken);
        if (scope.PartnerAgency is { } agency)
        {
            entries = await PartnerVisibility.FilterChildrenAsync(db, nameof(Person), personId, nameof(Observation), entries, o => o.Id, agency, scope.MeId, cancellationToken);
        }
        return await ToDisplayAsync(db, entries, scope.MayClassifiedRead, cancellationToken);
    }

    public async Task<List<ObservationDisplay>> GetAllAsync(bool isLeadership, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entries = await db.Observations
            .Include(o => o.Person)
            // soft-deleted parent surfaces as null → hide those
            .Where(o => o.Person != null && (isLeadership || !o.Person.IsClassified))
            .OrderByDescending(o => o.Start)
            .ToListAsync(cancellationToken);
        return await ToDisplayAsync(db, entries, isLeadership, cancellationToken);
    }

    public async Task<List<ObservationDisplay>> GetForOrgAsync(string orgType, string orgId, bool isLeadership, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // check the org record's own visibility
        if (!await Visibility.IsRecordVisibleAsync(db, orgType, orgId, isLeadership, cancellationToken))
        {
            return new();
        }
        var entries = await db.Observations
            .Include(o => o.Person)
            // null person = trashed; classified persons leadership-only
            .Where(o => o.OrgType == orgType && o.OrgId == orgId
                && o.Person != null && (isLeadership || !o.Person.IsClassified))
            .OrderByDescending(o => o.Start)
            .ToListAsync(cancellationToken);
        return await ToDisplayAsync(db, entries, isLeadership, cancellationToken);
    }

    /// <summary>Enrich observations with observer codename and linked org name/case number/route (classification-filtered).</summary>
    private static async Task<List<ObservationDisplay>> ToDisplayAsync(AppDbContext db, List<Observation> entries, bool isLeadership, CancellationToken cancellationToken)
    {
        var agentIds = entries.Where(o => o.ObservingAgentId is not null).Select(o => o.ObservingAgentId!).Distinct().ToList();
        var agents = new Dictionary<string, string>();
        if (agentIds.Count > 0)
        {
            agents = await db.Users
                .Where(u => agentIds.Contains(u.Id))
                .Select(u => new { u.Id, u.Codename })
                .ToDictionaryAsync(u => u.Id, u => u.Codename, cancellationToken);
        }

        var factionIds = entries.Where(o => o.OrgType == nameof(Faction) && o.OrgId is not null).Select(o => o.OrgId!).Distinct().ToList();
        var groupsIds = entries.Where(o => o.OrgType == nameof(PersonGroup) && o.OrgId is not null).Select(o => o.OrgId!).Distinct().ToList();

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

        return entries.Select(o =>
        {
            string? agentName = o.ObservingAgentId is not null
                && agents.TryGetValue(o.ObservingAgentId, out var cn) && !string.IsNullOrWhiteSpace(cn)
                ? cn : null;
            if (o.OrgId is not null && o.OrgType == nameof(Faction) && factions.TryGetValue(o.OrgId, out var f))
            {
                return new ObservationDisplay(o, agentName, f.Name, f.CaseNumber, $"/fraktionen/{o.OrgId}");
            }
            if (o.OrgId is not null && o.OrgType == nameof(PersonGroup) && groups.TryGetValue(o.OrgId, out var g))
            {
                return new ObservationDisplay(o, agentName, g.Name, g.CaseNumber, $"/personengruppen/{o.OrgId}");
            }
            return new ObservationDisplay(o, agentName, null, null, null);
        }).ToList();
    }

    public async Task<Observation> CreateAsync(string personId, ObservationInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var person = await db.People.FirstOrDefaultAsync(p => p.Id == personId, cancellationToken)
            ?? throw new InvalidOperationException($"Person '{personId}' nicht gefunden.");
        // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
        Permission.RequireMaySeeClassified(actor, person.SecrecyLevel);

        var orgId = input.OrgId.TrimToNull();
        var obs = new Observation
        {
            PersonId = personId,
            Start = input.Start,
            End = input.End,
            Location = input.Location.TrimToNull(),
            Sighting = input.Sighting.TrimToNull(),
            Result = input.Result.TrimToNull(),
            ObservingAgentId = input.ObservingAgentId.TrimToNull(),
            OrgId = orgId,
            // no orphan type without id
            OrgType = orgId is null ? null : input.OrgType.TrimToNull(),
        };

        db.Observations.Add(obs);
        await db.SaveChangesAsync(cancellationToken);
        // feeds person score
        await threat.NewCalculatePersonScoreAsync(personId, cancellationToken);
        await NotifyMentionsAsync(null, MentionNotify.Scope(obs.Sighting, obs.Result), personId, actor, cancellationToken);
        return obs;
    }

    public async Task<Observation> RefreshAsync(string observationId, ObservationInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var obs = await db.Observations
            .Include(o => o.Person)
            .FirstOrDefaultAsync(o => o.Id == observationId, cancellationToken)
            ?? throw new InvalidOperationException($"Observation '{observationId}' nicht gefunden.");

        if (obs.Person is { } obsPerson)
        {
            // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
            Permission.RequireMaySeeClassified(actor, obsPerson.SecrecyLevel);
        }

        var oldText = MentionNotify.Scope(obs.Sighting, obs.Result);
        obs.Start = input.Start;
        obs.End = input.End;
        obs.Location = input.Location.TrimToNull();
        obs.Sighting = input.Sighting.TrimToNull();
        obs.Result = input.Result.TrimToNull();
        obs.ObservingAgentId = input.ObservingAgentId.TrimToNull();
        var orgId = input.OrgId.TrimToNull();
        obs.OrgId = orgId;
        obs.OrgType = orgId is null ? null : input.OrgType.TrimToNull();

        await db.SaveChangesAsync(cancellationToken);
        await threat.NewCalculatePersonScoreAsync(obs.PersonId, cancellationToken);
        await NotifyMentionsAsync(oldText, MentionNotify.Scope(obs.Sighting, obs.Result), obs.PersonId, actor, cancellationToken);
        return obs;
    }

    public async Task DeleteAsync(string observationId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var obs = await db.Observations.Include(o => o.Person).FirstOrDefaultAsync(o => o.Id == observationId, cancellationToken);
        if (obs is null)
        {
            return;
        }
        if (obs.Person is { } obsPerson)
        {
            // classified record is writable by leadership or the record's own audience (TRU/HRB), not leadership alone
            Permission.RequireMaySeeClassified(actor, obsPerson.SecrecyLevel);
        }

        var personId = obs.PersonId;
        db.Observations.Remove(obs);
        await db.SaveChangesAsync(cancellationToken);
        await threat.NewCalculatePersonScoreAsync(personId, cancellationToken);
    }
}
