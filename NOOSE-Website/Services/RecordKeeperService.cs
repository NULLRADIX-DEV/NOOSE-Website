using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Who keeps a person record, and what the viewer may do about it.</summary>
public sealed record PersonKeepers(
    string PersonId,
    string? KeeperId,
    string? KeeperCodename,
    string? DeputyId,
    string? DeputyCodename,
    bool MayAssign,
    bool MayClaim,
    bool MayStepDown);

/// <summary>One record the viewer is in charge of, with how current it is.</summary>
public sealed record KeptRecord(
    string RecordType,
    string Id,
    string Name,
    string CaseNumber,
    string Href,
    string Role,
    RecencyLevel Level,
    DateTime ReferenceUtc);

/// <summary>Keeper and deputy of person records, and the viewer's own records across all record types.</summary>
public interface IRecordKeeperService
{
    /// <summary>Keeper and deputy of a person; null when the viewer may not see the record or is no internal agent.</summary>
    Task<PersonKeepers?> GetAsync(string personId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Sets both roles; leadership or the current keeper.</summary>
    Task AssignAsync(string personId, string? keeperId, string? deputyId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    /// <summary>The actor becomes keeper of a record that has none.</summary>
    Task ClaimAsync(string personId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>The actor gives up their role; a leaving keeper is followed by the deputy.</summary>
    Task StepDownAsync(string personId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Records the actor keeps or leads, stalest first.</summary>
    Task<List<KeptRecord>> GetMineAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}

/// <summary>Who may change the keeper of a record.</summary>
public static class RecordKeeperRules
{
    public const string Keeper = "Aktenführer";
    public const string Deputy = "Vertretung";
    public const string InvestigationLead = "Ermittlungsleiter";
    public const string CaseLead = "Fallführer";

    /// <summary>Leadership, or the keeper handing on their own record.</summary>
    public static bool MayAssign(ClaimsPrincipal actor, string? keeperId)
        => actor.MayWrite() && actor.IsInternalAgent()
            && (actor.IsLeadership() || (keeperId is not null && keeperId == actor.GetAgentId()));

    /// <summary>Any writing agent may take a record nobody keeps.</summary>
    public static bool MayClaim(ClaimsPrincipal actor, string? keeperId)
        => keeperId is null && actor.MayWrite() && actor.IsInternalAgent();

    /// <summary>Keeper and deputy may leave their role.</summary>
    public static bool MayStepDown(ClaimsPrincipal actor, string? keeperId, string? deputyId)
        => actor.MayWrite() && actor.GetAgentId() is { } me && (me == keeperId || me == deputyId);

    /// <summary>A deputy needs a keeper and is never the keeper.</summary>
    public static void RequireValidPair(string? keeperId, string? deputyId)
    {
        if (deputyId is not null && keeperId is null)
        {
            throw new InvalidOperationException("Eine Vertretung braucht einen Aktenführer.");
        }
        if (deputyId is not null && deputyId == keeperId)
        {
            throw new InvalidOperationException("Aktenführer und Vertretung müssen verschiedene Agents sein.");
        }
    }
}

/// <inheritdoc cref="IRecordKeeperService" />
/// <remarks>
/// Writes go through ExecuteUpdate so ModifiedAt stays: handing a record on must not make it look current. The audit
/// row is written by hand, and the write guard is checked by hand because the read-only barrier is bypassed too.
/// </remarks>
public sealed class RecordKeeperService(
    IDbContextFactory<AppDbContext> dbFactory,
    IAgentPrincipalService principals,
    IRecencyService recency,
    INotificationService notifications) : IRecordKeeperService
{
    public async Task<PersonKeepers?> GetAsync(string personId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        if (!actor.IsInternalAgent())
        {
            return null;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await Visibility.IsRecordVisibleAsync(db, nameof(Person), personId, ViewerScope.From(actor), cancellationToken))
        {
            return null;
        }
        var row = await db.People.AsNoTracking().IgnoreQueryFilters()
            .Where(p => p.Id == personId)
            .Select(p => new { p.KeeperId, p.DeputyId })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }
        var names = await CodenamesAsync(db, [row.KeeperId, row.DeputyId], cancellationToken);
        return new PersonKeepers(personId,
            row.KeeperId, row.KeeperId is null ? null : names.GetValueOrDefault(row.KeeperId),
            row.DeputyId, row.DeputyId is null ? null : names.GetValueOrDefault(row.DeputyId),
            RecordKeeperRules.MayAssign(actor, row.KeeperId),
            RecordKeeperRules.MayClaim(actor, row.KeeperId),
            RecordKeeperRules.MayStepDown(actor, row.KeeperId, row.DeputyId));
    }

    public async Task AssignAsync(string personId, string? keeperId, string? deputyId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        keeperId = string.IsNullOrWhiteSpace(keeperId) ? null : keeperId;
        deputyId = string.IsNullOrWhiteSpace(deputyId) ? null : deputyId;
        RecordKeeperRules.RequireValidPair(keeperId, deputyId);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var person = await LoadAsync(db, personId, actor, cancellationToken);
        if (!RecordKeeperRules.MayAssign(actor, person.KeeperId))
        {
            throw new UnauthorizedAccessException("Aktenführer und Vertretung setzen nur die Führung oder der Aktenführer selbst.");
        }

        // a stored agent who is no longer selectable may stay; only a new pick has to be selectable
        foreach (var id in new[] { keeperId, deputyId }.OfType<string>()
                     .Where(id => id != person.KeeperId && id != person.DeputyId))
        {
            await RequireMayKeepAsync(db, id, person, cancellationToken);
        }

        await WriteAsync(db, person, keeperId, deputyId, actor, cancellationToken);
    }

    public async Task ClaimAsync(string personId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var person = await LoadAsync(db, personId, actor, cancellationToken);
        if (!RecordKeeperRules.MayClaim(actor, person.KeeperId))
        {
            throw new InvalidOperationException("Die Akte hat schon einen Aktenführer.");
        }
        var me = actor.GetAgentId()!;
        if (!await db.Users.OnlySelectable().AnyAsync(u => u.Id == me, cancellationToken))
        {
            throw new InvalidOperationException("Du kannst gerade keine Akte übernehmen.");
        }
        await WriteAsync(db, person, me, person.DeputyId == me ? null : person.DeputyId, actor, cancellationToken);
    }

    public async Task StepDownAsync(string personId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var person = await LoadAsync(db, personId, actor, cancellationToken);
        if (!RecordKeeperRules.MayStepDown(actor, person.KeeperId, person.DeputyId))
        {
            throw new InvalidOperationException("Du bist weder Aktenführer noch Vertretung dieser Akte.");
        }
        var me = actor.GetAgentId();
        var (keeper, deputy) = person.KeeperId == me ? (person.DeputyId, (string?)null) : (person.KeeperId, (string?)null);
        await WriteAsync(db, person, keeper, deputy, actor, cancellationToken);
    }

    public async Task<List<KeptRecord>> GetMineAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        if (!actor.IsInternalAgent() || actor.GetAgentId() is not { } me)
        {
            return [];
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var settings = await recency.GetAllSettingsAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var found = new List<KeptRecord>();

        RecencyLevel Level(string type, bool exempt, DateTime reference)
        {
            var s = settings[type];
            return RecencyAssessment.For(s.AgingDisabled, exempt, false, s.WarningDays, s.StaleDays, reference, now);
        }

        var people = await db.People.AsNoTracking().OnlyActive()
            .Where(p => p.KeeperId == me || p.DeputyId == me)
            .Select(p => new { p.Id, p.Name, p.CaseNumber, p.KeeperId, p.AgingDisabled, Reference = p.ModifiedAt ?? p.CreatedAt })
            .ToListAsync(cancellationToken);
        found.AddRange(people.Select(p => new KeptRecord(nameof(Person), p.Id, p.Name, p.CaseNumber, $"/personen/{p.Id}",
            p.KeeperId == me ? RecordKeeperRules.Keeper : RecordKeeperRules.Deputy,
            Level(nameof(Person), p.AgingDisabled, p.Reference), p.Reference)));

        var factions = await db.FactionAgents.AsNoTracking()
            .Where(a => a.AgentId == me && a.IsInvestigationLead)
            .Join(db.Factions.OnlyActive(), a => a.FactionId, f => f.Id, (a, f) => new
            {
                f.Id, f.Name, f.CaseNumber, f.AgingDisabled, f.IsStateFaction, f.CreatedAt,
                f.MembersRefreshedAt, f.StockRefreshedAt, f.ActivitiesRefreshedAt, f.DocsRefreshedAt,
            })
            .ToListAsync(cancellationToken);
        found.AddRange(factions.Select(f =>
        {
            var reference = FactionRecency.Reference(f.CreatedAt, f.MembersRefreshedAt, f.StockRefreshedAt,
                f.ActivitiesRefreshedAt, f.DocsRefreshedAt);
            return new KeptRecord(nameof(Faction), f.Id, f.Name, f.CaseNumber, $"/fraktionen/{f.Id}",
                RecordKeeperRules.InvestigationLead, Level(nameof(Faction), f.AgingDisabled || f.IsStateFaction, reference),
                reference);
        }));

        var groups = await db.PersonGroupAgents.AsNoTracking()
            .Where(a => a.AgentId == me && a.IsInvestigationLead)
            .Join(db.PersonGroups.OnlyActive(), a => a.PersonGroupId, g => g.Id,
                (a, g) => new { g.Id, g.Name, g.CaseNumber, g.AgingDisabled, Reference = g.ModifiedAt ?? g.CreatedAt })
            .ToListAsync(cancellationToken);
        found.AddRange(groups.Select(g => new KeptRecord(nameof(PersonGroup), g.Id, g.Name, g.CaseNumber,
            $"/personengruppen/{g.Id}", RecordKeeperRules.InvestigationLead,
            Level(nameof(PersonGroup), g.AgingDisabled, g.Reference), g.Reference)));

        var parties = await db.PartyAgents.AsNoTracking()
            .Where(a => a.AgentId == me && a.IsInvestigationLead)
            .Join(db.Parties.OnlyActive(), a => a.PartyId, p => p.Id,
                (a, p) => new { p.Id, p.Name, p.CaseNumber, p.AgingDisabled, Reference = p.ModifiedAt ?? p.CreatedAt })
            .ToListAsync(cancellationToken);
        found.AddRange(parties.Select(p => new KeptRecord(nameof(Party), p.Id, p.Name, p.CaseNumber,
            $"/parteien/{p.Id}", RecordKeeperRules.InvestigationLead,
            Level(nameof(Party), p.AgingDisabled, p.Reference), p.Reference)));

        var cases = await db.CaseAgents.AsNoTracking()
            .Where(a => a.AgentId == me && a.IsCaseLead)
            .Join(db.Cases.OnlyActive().Where(c => c.Status == CaseStatus.Open || c.Status == CaseStatus.InProcessing
                    || c.Status == CaseStatus.Dormant),
                a => a.CaseId, c => c.Id,
                (a, c) => new { c.Id, c.Title, c.CaseNumber, c.AgingDisabled, Reference = c.ModifiedAt ?? c.CreatedAt })
            .ToListAsync(cancellationToken);
        found.AddRange(cases.Select(c => new KeptRecord(nameof(Case), c.Id, c.Title, c.CaseNumber,
            $"/vorgaenge/{c.Id}", RecordKeeperRules.CaseLead,
            Level(nameof(Case), c.AgingDisabled, c.Reference), c.Reference)));

        // a role outlives a reclassification the agent is not cleared for
        var scope = ViewerScope.From(actor);
        var visible = new List<KeptRecord>();
        foreach (var record in found)
        {
            if (await Visibility.IsRecordVisibleAsync(db, record.RecordType, record.Id, scope, cancellationToken))
            {
                visible.Add(record);
            }
        }
        return visible
            .OrderByDescending(r => r.Level)
            .ThenBy(r => r.ReferenceUtc)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static async Task<Person> LoadAsync(AppDbContext db, string personId, ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (!actor.IsInternalAgent()
            || !await Visibility.IsRecordVisibleAsync(db, nameof(Person), personId, ViewerScope.From(actor), cancellationToken))
        {
            throw new InvalidOperationException("Akte nicht gefunden oder nicht zugänglich.");
        }
        var person = await db.People.AsNoTracking().FirstOrDefaultAsync(p => p.Id == personId, cancellationToken)
            ?? throw new InvalidOperationException("Akte nicht gefunden oder nicht zugänglich.");
        Permission.RequireMaySeeClassified(actor, person.SecrecyLevel);
        return person;
    }

    /// <summary>A new keeper or deputy must be pickable and cleared to open the record.</summary>
    private async Task RequireMayKeepAsync(AppDbContext db, string agentId, Person person,
        CancellationToken cancellationToken)
    {
        if (!await db.Users.OnlySelectable().AnyAsync(u => u.Id == agentId, cancellationToken))
        {
            throw new InvalidOperationException("Der gewählte Agent wurde nicht gefunden oder ist nicht zuteilbar.");
        }
        var principal = await principals.BuildAsync(agentId, cancellationToken);
        if (principal is null || !await Visibility.IsRecordVisibleAsync(db, nameof(Person), person.Id,
                ViewerScope.From(principal), cancellationToken))
        {
            throw new InvalidOperationException("Der gewählte Agent darf diese Akte nicht sehen.");
        }
    }

    private async Task WriteAsync(AppDbContext db, Person person, string? keeperId, string? deputyId,
        ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        if (keeperId == person.KeeperId && deputyId == person.DeputyId)
        {
            return;
        }
        // only the row as it was read is changed, so a concurrent change is not silently overwritten
        var affected = await db.People
            .Where(p => p.Id == person.Id && p.KeeperId == person.KeeperId && p.DeputyId == person.DeputyId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.KeeperId, keeperId)
                .SetProperty(p => p.DeputyId, deputyId), cancellationToken);
        if (affected == 0)
        {
            throw new InvalidOperationException("Die Aktenführung wurde gerade von jemand anderem geändert. Lade die Akte neu.");
        }

        var names = await CodenamesAsync(db, [person.KeeperId, person.DeputyId, keeperId, deputyId], cancellationToken);
        string? Name(string? id) => id is null ? null : names.GetValueOrDefault(id, id);
        var changes = new Dictionary<string, object?[]>();
        if (keeperId != person.KeeperId)
        {
            changes[RecordKeeperRules.Keeper] = [Name(person.KeeperId), Name(keeperId)];
        }
        if (deputyId != person.DeputyId)
        {
            changes[RecordKeeperRules.Deputy] = [Name(person.DeputyId), Name(deputyId)];
        }
        db.AuditLogs.Add(ManualAudit.Row(nameof(Person), person.Id, AuditAction.Modified, actor, changes));
        await db.SaveChangesAsync(cancellationToken);

        var me = actor.GetAgentId();
        var href = $"/personen/{person.Id}";
        if (keeperId is not null && keeperId != person.KeeperId && keeperId != me)
        {
            await notifications.NotifyAsync(keeperId, NotificationType.RecordKeeperAssigned,
                $"Du bist jetzt Aktenführer von {person.Name} ({person.CaseNumber}).", href, cancellationToken);
        }
        if (deputyId is not null && deputyId != person.DeputyId && deputyId != me)
        {
            await notifications.NotifyAsync(deputyId, NotificationType.RecordKeeperAssigned,
                $"Du vertrittst jetzt den Aktenführer von {person.Name} ({person.CaseNumber}).", href, cancellationToken);
        }
    }

    private static async Task<Dictionary<string, string>> CodenamesAsync(AppDbContext db, IEnumerable<string?> ids,
        CancellationToken cancellationToken)
    {
        var wanted = ids.OfType<string>().Distinct().ToList();
        if (wanted.Count == 0)
        {
            return [];
        }
        return await db.Users.AsNoTracking()
            .Where(u => wanted.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Codename, cancellationToken);
    }
}
