using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Abductions;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Abductions;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Abductions are shared incident records; any writer may file one and everyone reads them.</summary>
public class AbductionService(
    IDbContextFactory<AppDbContext> dbFactory,
    ICaseNumberService caseNumber,
    IThreatScoreService threat,
    INotificationService notifications) : IAbductionService
{
    private const string CasePrefix = "ENT";

    /// <summary>Perpetrator record types a caller may link.</summary>
    private static readonly string[] PerpetratorTypes = { nameof(Faction), nameof(PersonGroup), nameof(Person) };

    public async Task<List<AbductionDisplay>> GetListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var list = await db.AgentAbductions
            .OrderByDescending(a => a.Timestamp)
            .ToListAsync(cancellationToken);
        return await ToDisplayAsync(db, list, cancellationToken);
    }

    public async Task<AgentAbduction?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.AgentAbductions
            .Include(a => a.Compromises)
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
    }

    public async Task<AbductionDisplay?> GetDisplayAsync(string id, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var a = await db.AgentAbductions.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (a is null)
        {
            return null;
        }
        return (await ToDisplayAsync(db, new List<AgentAbduction> { a }, cancellationToken)).FirstOrDefault();
    }

    public async Task<List<AbductionDisplay>> GetForVictimAsync(string agentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var list = await db.AgentAbductions
            .Where(a => a.VictimAgentId == agentId)
            .OrderByDescending(a => a.Timestamp)
            .ToListAsync(cancellationToken);
        return await ToDisplayAsync(db, list, cancellationToken);
    }

    public async Task<List<AbductionDisplay>> GetForPerpetratorAsync(string perpetratorType, string perpetratorId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var list = await db.AgentAbductions
            .Where(a => a.PerpetratorType == perpetratorType && a.PerpetratorId == perpetratorId)
            .OrderByDescending(a => a.Timestamp)
            .ToListAsync(cancellationToken);
        return await ToDisplayAsync(db, list, cancellationToken);
    }

    public async Task<List<AgentAbduction>> GetTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.AgentAbductions.AsNoTracking()
            .IgnoreQueryFilters()
            .Include(a => a.VictimAgent)
            .Where(a => a.IsDeleted)
            .OrderByDescending(a => a.DeletedAt)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Enrich abductions with victim codename, resolved perpetrator name/route and the active compromise count.</summary>
    private static async Task<List<AbductionDisplay>> ToDisplayAsync(AppDbContext db, List<AgentAbduction> list, CancellationToken cancellationToken)
    {
        if (list.Count == 0)
        {
            return new();
        }

        var victimIds = list.Select(a => a.VictimAgentId).Distinct().ToList();
        var codenames = await db.Users
            .Where(u => victimIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Codename, cancellationToken);

        var refs = list.Select(a => (a.PerpetratorType, a.PerpetratorId)).Distinct().ToList();
        var resolved = await RecordsReference.ResolveAsync(db, refs, cancellationToken);

        var ids = list.Select(a => a.Id).ToList();
        var activeCounts = (await db.AbductionCompromises
            .Where(c => ids.Contains(c.AbductionId) && c.Status == CompromiseStatus.Compromised)
            .GroupBy(c => c.AbductionId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken))
            .ToDictionary(x => x.Key, x => x.Count);

        return list.Select(a =>
        {
            var codename = codenames.TryGetValue(a.VictimAgentId ?? string.Empty, out var cn) && !string.IsNullOrWhiteSpace(cn)
                ? cn : "(unbekannter Agent)";
            resolved.TryGetValue((a.PerpetratorType, a.PerpetratorId), out var perp);
            activeCounts.TryGetValue(a.Id, out var count);
            return new AbductionDisplay(a, codename, perp.Display, perp.Href, count);
        }).ToList();
    }

    public async Task<AgentAbduction> CreateAsync(AbductionInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Validate(input);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // case-number allocation needs the caller's transaction so counter + record commit together
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        var abduction = new AgentAbduction
        {
            CaseNumber = await caseNumber.NextAsync(db, CasePrefix, cancellationToken),
            VictimAgentId = input.VictimAgentId,
            PerpetratorType = input.PerpetratorType,
            PerpetratorId = input.PerpetratorId,
            Timestamp = input.Timestamp,
            ReleasedAt = input.ReleasedAt,
            Location = input.Location.TrimToNull(),
            TruthSerum = input.TruthSerum,
            Outcome = input.Outcome,
            Notes = input.Notes.TrimToNull(),
        };
        ApplyLeak(abduction, input);

        db.AgentAbductions.Add(abduction);
        await db.SaveChangesAsync(cancellationToken);

        foreach (var c in DesiredCompromises(input))
        {
            db.AbductionCompromises.Add(new AbductionCompromise
            {
                AbductionId = abduction.Id,
                TargetType = c.TargetType,
                TargetId = c.TargetId,
                Status = CompromiseStatus.Compromised,
            });
        }
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        await RecomputeThreatAsync(input.PerpetratorType, input.PerpetratorId, cancellationToken);
        await LeadershipNotifyAsync(db, abduction, actor, cancellationToken);
        return abduction;
    }

    public async Task UpdateAsync(string id, AbductionInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Validate(input);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var a = await db.AgentAbductions.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Entführung '{id}' nicht gefunden.");

        var oldPerp = (a.PerpetratorType, a.PerpetratorId);
        a.VictimAgentId = input.VictimAgentId;
        a.PerpetratorType = input.PerpetratorType;
        a.PerpetratorId = input.PerpetratorId;
        a.Timestamp = input.Timestamp;
        a.ReleasedAt = input.ReleasedAt;
        a.Location = input.Location.TrimToNull();
        a.TruthSerum = input.TruthSerum;
        a.Outcome = input.Outcome;
        a.Notes = input.Notes.TrimToNull();
        ApplyLeak(a, input);

        // reconcile compromises against the editor's list: add new, drop removed, keep the rest (status intact)
        var desired = DesiredCompromises(input);
        var existing = await db.AbductionCompromises
            .Where(c => c.AbductionId == id)
            .ToListAsync(cancellationToken);
        var desiredKeys = desired.Select(c => (c.TargetType, c.TargetId)).ToHashSet();
        foreach (var gone in existing.Where(c => !desiredKeys.Contains((c.TargetType, c.TargetId))))
        {
            db.AbductionCompromises.Remove(gone);
        }
        var existingKeys = existing.Select(c => (c.TargetType, c.TargetId)).ToHashSet();
        foreach (var add in desired.Where(c => !existingKeys.Contains((c.TargetType, c.TargetId))))
        {
            db.AbductionCompromises.Add(new AbductionCompromise
            {
                AbductionId = id,
                TargetType = add.TargetType,
                TargetId = add.TargetId,
                Status = CompromiseStatus.Compromised,
            });
        }
        await db.SaveChangesAsync(cancellationToken);

        // a re-linked incident refreshes both the old and the new perpetrator's score
        await RecomputeThreatAsync(oldPerp.PerpetratorType, oldPerp.PerpetratorId, cancellationToken);
        if (oldPerp != (a.PerpetratorType, a.PerpetratorId))
        {
            await RecomputeThreatAsync(a.PerpetratorType, a.PerpetratorId, cancellationToken);
        }
    }

    public async Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var a = await db.AgentAbductions.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (a is null)
        {
            return;
        }
        var perp = (a.PerpetratorType, a.PerpetratorId);
        db.AgentAbductions.Remove(a);
        await db.SaveChangesAsync(cancellationToken);
        await RecomputeThreatAsync(perp.PerpetratorType, perp.PerpetratorId, cancellationToken);
    }

    public async Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var a = await db.AgentAbductions.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Entführung '{id}' nicht gefunden.");

        a.IsDeleted = false;
        a.DeletedAt = null;
        a.DeletedById = null;
        await db.SaveChangesAsync(cancellationToken);
        await RecomputeThreatAsync(a.PerpetratorType, a.PerpetratorId, cancellationToken);
    }

    public async Task<AbductionCompromise> AddCompromiseAsync(string abductionId, string targetType, string targetId, string? note, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        if (string.IsNullOrWhiteSpace(targetType) || string.IsNullOrWhiteSpace(targetId))
        {
            throw new InvalidOperationException("Bitte eine Akte zum Verknüpfen auswählen.");
        }
        // filtering the picker is not enough, the socket takes whatever it is sent: a type that resolves to no
        // reference would be stored and then read back as a deleted record on every load, with no way to remove it
        if (!LinkService.ReferenceTypes.Contains(targetType, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Dieser Aktentyp kann nicht als kompromittiert vermerkt werden.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.AgentAbductions.AnyAsync(a => a.Id == abductionId, cancellationToken))
        {
            throw new InvalidOperationException($"Entführung '{abductionId}' nicht gefunden.");
        }

        // reactivate an existing entry rather than stacking duplicates
        var existing = await db.AbductionCompromises
            .FirstOrDefaultAsync(c => c.AbductionId == abductionId && c.TargetType == targetType && c.TargetId == targetId, cancellationToken);
        if (existing is not null)
        {
            existing.Status = CompromiseStatus.Compromised;
            existing.ClearedAt = null;
            existing.ClearedById = null;
            existing.Note = note.TrimToNull() ?? existing.Note;
            await db.SaveChangesAsync(cancellationToken);
            return existing;
        }

        var compromise = new AbductionCompromise
        {
            AbductionId = abductionId,
            TargetType = targetType,
            TargetId = targetId,
            Note = note.TrimToNull(),
            Status = CompromiseStatus.Compromised,
        };
        db.AbductionCompromises.Add(compromise);
        await db.SaveChangesAsync(cancellationToken);
        return compromise;
    }

    public async Task SetCompromiseStatusAsync(string compromiseId, CompromiseStatus status, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var c = await db.AbductionCompromises.FirstOrDefaultAsync(x => x.Id == compromiseId, cancellationToken)
            ?? throw new InvalidOperationException($"Kompromittierung '{compromiseId}' nicht gefunden.");

        c.Status = status;
        if (status == CompromiseStatus.Cleared)
        {
            c.ClearedAt = DateTime.UtcNow;
            c.ClearedById = actor.GetAgentId();
        }
        else
        {
            c.ClearedAt = null;
            c.ClearedById = null;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveCompromiseAsync(string compromiseId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var c = await db.AbductionCompromises.FirstOrDefaultAsync(x => x.Id == compromiseId, cancellationToken);
        if (c is null)
        {
            return;
        }
        db.AbductionCompromises.Remove(c);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<CompromisedRecord>> GetCompromisedRecordsAsync(bool activeOnly = true, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.AbductionCompromises
            .Include(c => c.Abduction)
            // soft-deleted parent surfaces as null → hide those
            .Where(c => c.Abduction != null);
        if (activeOnly)
        {
            query = query.Where(c => c.Status == CompromiseStatus.Compromised);
        }
        var rows = await query
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(cancellationToken);
        return await ResolveCompromisedAsync(db, rows, cancellationToken);
    }

    public async Task<List<CompromisedRecord>> GetForTargetAsync(string targetType, string targetId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.AbductionCompromises
            .Include(c => c.Abduction)
            .Where(c => c.TargetType == targetType && c.TargetId == targetId && c.Abduction != null)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(cancellationToken);
        return await ResolveCompromisedAsync(db, rows, cancellationToken);
    }

    public async Task<List<CompromisedRecord>> GetCompromisesForAbductionAsync(string abductionId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.AbductionCompromises
            .Include(c => c.Abduction)
            .Where(c => c.AbductionId == abductionId && c.Abduction != null)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(cancellationToken);
        return await ResolveCompromisedAsync(db, rows, cancellationToken);
    }

    public async Task<HashSet<string>> GetCompromisedTargetIdsAsync(string targetType, IReadOnlyCollection<string> targetIds, CancellationToken cancellationToken = default)
    {
        if (targetIds.Count == 0)
        {
            return new();
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var hits = await db.AbductionCompromises
            .Include(c => c.Abduction)
            .Where(c => c.TargetType == targetType && targetIds.Contains(c.TargetId)
                && c.Status == CompromiseStatus.Compromised && c.Abduction != null)
            .Select(c => c.TargetId)
            .Distinct()
            .ToListAsync(cancellationToken);
        return hits.ToHashSet();
    }

    private static async Task<List<CompromisedRecord>> ResolveCompromisedAsync(AppDbContext db, List<AbductionCompromise> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return new();
        }
        var refs = rows.Select(c => (c.TargetType, c.TargetId)).Distinct().ToList();
        var resolved = await RecordsReference.ResolveAsync(db, refs, cancellationToken);
        return rows.Select(c =>
        {
            resolved.TryGetValue((c.TargetType, c.TargetId), out var target);
            var display = string.IsNullOrWhiteSpace(target.Display) ? "(gelöschte Akte)" : target.Display;
            return new CompromisedRecord(
                c.Id, c.AbductionId, c.Abduction!.CaseNumber, c.TargetType, c.TargetId,
                display, target.Href, c.Status, c.Note, c.CreatedAt);
        }).ToList();
    }

    private async Task RecomputeThreatAsync(string perpetratorType, string perpetratorId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(perpetratorId))
        {
            return;
        }
        if (perpetratorType == nameof(Faction))
        {
            await threat.NewCalculateAsync(perpetratorId, cancellationToken);
        }
        else if (perpetratorType == nameof(Person))
        {
            await threat.NewCalculatePersonScoreAsync(perpetratorId, cancellationToken);
        }
        // PersonGroup carries no threat score
    }

    /// <summary>Best effort; alerts leadership to a new abduction.</summary>
    private async Task LeadershipNotifyAsync(AppDbContext db, AgentAbduction abduction, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        try
        {
            var codename = await db.Users.AsNoTracking()
                .Where(u => u.Id == abduction.VictimAgentId).Select(u => u.Codename)
                .FirstOrDefaultAsync(cancellationToken) ?? "Unbekannt";

            var recipients = await db.Users.AsNoTracking().OnlySelectable()
                .Where(u => u.IsAdmin || u.Rank >= Rank.SupervisorySpecialAgent)
                .Select(u => u.Id)
                .ToListAsync(cancellationToken);

            var title = $"Agenten-Entführung {abduction.CaseNumber}: {codename} entführt "
                      + $"({AbductionOutcomeDisplay.Name(abduction.Outcome)}).";
            await notifications.NotifyManyAsync(recipients, NotificationType.AbductionFiled,
                title.Length > 300 ? title[..297] + "…" : title, $"/entfuehrungen/{abduction.Id}",
                actor.GetAgentId(), cancellationToken);
        }
        catch { /* best effort */ }
    }

    private static void ApplyLeak(AgentAbduction abduction, AbductionInput input)
    {
        abduction.InformationLeaked = input.InformationLeaked;
        if (input.InformationLeaked)
        {
            abduction.LeakCategories = input.LeakCategories;
            abduction.LeakSeverity = input.LeakSeverity;
        }
        else
        {
            // no leak → keep the record clean and the threat weight honest
            abduction.LeakCategories = LeakCategory.None;
            abduction.LeakSeverity = LeakSeverity.None;
        }
    }

    /// <summary>Valid, de-duplicated compromise targets from the editor; empty when no leak occurred.</summary>
    /// <summary>The compromise list of an editor round, deduplicated and checked.</summary>
    /// <remarks>
    /// The type check belongs here and not only in <c>AddCompromiseAsync</c>: create and update write the same
    /// table through this method, and filtering the picker is not enough - the socket takes whatever it is sent.
    /// A type that resolves to no reference would be stored and then read back as a deleted record on every load.
    /// </remarks>
    private static List<CompromiseTargetInput> DesiredCompromises(AbductionInput input)
    {
        if (!input.InformationLeaked)
        {
            return new();
        }
        var desired = input.Compromises
            .Where(c => !string.IsNullOrWhiteSpace(c.TargetType) && !string.IsNullOrWhiteSpace(c.TargetId))
            .GroupBy(c => (c.TargetType, c.TargetId))
            .Select(g => g.First())
            .ToList();
        foreach (var target in desired)
        {
            if (!LinkService.ReferenceTypes.Contains(target.TargetType, StringComparer.Ordinal))
            {
                throw new InvalidOperationException("Dieser Aktentyp kann nicht als kompromittiert vermerkt werden.");
            }
        }
        return desired;
    }

    private static void Validate(AbductionInput input)
    {
        if (string.IsNullOrWhiteSpace(input.VictimAgentId))
        {
            throw new InvalidOperationException("Bitte den entführten Agenten angeben.");
        }
        if (string.IsNullOrWhiteSpace(input.PerpetratorId) || !PerpetratorTypes.Contains(input.PerpetratorType))
        {
            throw new InvalidOperationException("Bitte einen Täter (Fraktion, Personengruppe oder Person) verknüpfen.");
        }
        if (input.ReleasedAt is { } released && released < input.Timestamp)
        {
            throw new InvalidOperationException("Die Freilassung darf nicht vor der Entführung liegen.");
        }
    }
}
