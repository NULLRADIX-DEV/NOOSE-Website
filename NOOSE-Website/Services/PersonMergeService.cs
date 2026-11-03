using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IPersonMergeService" />
public class PersonMergeService(
    IDbContextFactory<AppDbContext> dbFactory, IPublicWantedService publicWanted) : IPersonMergeService
{
    public async Task MergeAsync(string sourceId, string targetId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        // ExecuteUpdate bypasses the SaveChanges interceptor → gate read-only here explicitly
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);

        if (string.IsNullOrWhiteSpace(sourceId) || string.IsNullOrWhiteSpace(targetId) || sourceId == targetId)
        {
            throw new InvalidOperationException("Bitte zwei unterschiedliche Akten wählen.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var source = await db.People
            .Include(p => p.Aliases).Include(p => p.PhoneNumbers).Include(p => p.Vehicles)
            .Include(p => p.Locations).Include(p => p.Weapons)
            .FirstOrDefaultAsync(p => p.Id == sourceId, cancellationToken)
            ?? throw new InvalidOperationException("Die Quell-Akte wurde nicht gefunden.");
        var target = await db.People
            .Include(p => p.Aliases).Include(p => p.PhoneNumbers).Include(p => p.Vehicles)
            .Include(p => p.Locations).Include(p => p.Weapons)
            .FirstOrDefaultAsync(p => p.Id == targetId, cancellationToken)
            ?? throw new InvalidOperationException("Die Ziel-Akte wurde nicht gefunden.");

        // ---- children with no duplicate risk: reassign wholesale (bulk) ----
        await db.PersonDocs.Where(d => d.PersonId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.PersonId, targetId), cancellationToken);
        await db.Observations.Where(o => o.PersonId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.PersonId, targetId), cancellationToken);
        await db.PersonPhotos.Where(f => f.PersonId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(f => f.PersonId, targetId), cancellationToken);
        // trashed ones too, or a restore would hang off the merged-away file
        await db.Warrants.IgnoreQueryFilters().Where(w => w.PersonId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.PersonId, targetId), cancellationToken);

        // ---- profile children with case-insensitive dedup ----
        static string Norm(string? s) => (s ?? string.Empty).Trim().ToLowerInvariant();

        var targetAliases = target.Aliases.Select(a => Norm(a.AliasName)).ToHashSet();
        targetAliases.Add(Norm(target.Name));
        foreach (var alias in source.Aliases)
        {
            if (targetAliases.Add(Norm(alias.AliasName)))
            {
                alias.PersonId = targetId;
            }
            else
            {
                db.PersonAliases.Remove(alias);
            }
        }
        // keep the source name findable as an alias
        if (targetAliases.Add(Norm(source.Name)))
        {
            db.PersonAliases.Add(new PersonAlias { PersonId = targetId, AliasName = source.Name });
        }

        var targetPhones = target.PhoneNumbers.Select(t => Norm(t.Number)).ToHashSet();
        foreach (var phone in source.PhoneNumbers)
        {
            if (targetPhones.Add(Norm(phone.Number)))
            {
                phone.PersonId = targetId;
            }
            else
            {
                db.PersonPhones.Remove(phone);
            }
        }

        var targetVehicles = target.Vehicles.Select(f => Norm(f.Designation) + "|" + Norm(f.LicensePlate)).ToHashSet();
        foreach (var vehicle in source.Vehicles)
        {
            if (targetVehicles.Add(Norm(vehicle.Designation) + "|" + Norm(vehicle.LicensePlate)))
            {
                vehicle.PersonId = targetId;
            }
            else
            {
                db.PersonVehicles.Remove(vehicle);
            }
        }

        var targetLocations = target.Locations.Select(o => Norm(o.Text)).ToHashSet();
        foreach (var location in source.Locations)
        {
            if (targetLocations.Add(Norm(location.Text)))
            {
                location.PersonId = targetId;
            }
            else
            {
                db.PersonLocations.Remove(location);
            }
        }

        var targetWeapons = target.Weapons.Select(w => Norm(w.Text)).ToHashSet();
        foreach (var weapon in source.Weapons)
        {
            if (targetWeapons.Add(Norm(weapon.Text)))
            {
                weapon.PersonId = targetId;
            }
            else
            {
                db.PersonWeapons.Remove(weapon);
            }
        }

        // ---- person-to-person relations: reassign; drop self-references ----
        var relations = await db.PersonRelations
            .Where(b => b.PersonAId == sourceId || b.PersonBId == sourceId)
            .ToListAsync(cancellationToken);
        foreach (var relation in relations)
        {
            if (relation.PersonAId == sourceId)
            {
                relation.PersonAId = targetId;
            }
            if (relation.PersonBId == sourceId)
            {
                relation.PersonBId = targetId;
            }
            if (relation.PersonAId == relation.PersonBId)
            {
                db.PersonRelations.Remove(relation);
            }
        }

        // ---- active memberships: reassign unless target already belongs ----
        var targetFactions = await db.FactionMembers.Where(m => m.PersonId == targetId)
            .Select(m => m.FactionId).ToListAsync(cancellationToken);
        foreach (var member in await db.FactionMembers.Where(m => m.PersonId == sourceId).ToListAsync(cancellationToken))
        {
            if (targetFactions.Contains(member.FactionId))
            {
                db.FactionMembers.Remove(member);
            }
            else
            {
                member.PersonId = targetId;
            }
        }

        var targetGroups = await db.PersonGroupMembers.Where(m => m.PersonId == targetId)
            .Select(m => m.PersonGroupId).ToListAsync(cancellationToken);
        foreach (var member in await db.PersonGroupMembers.Where(m => m.PersonId == sourceId).ToListAsync(cancellationToken))
        {
            if (targetGroups.Contains(member.PersonGroupId))
            {
                db.PersonGroupMembers.Remove(member);
            }
            else
            {
                member.PersonId = targetId;
            }
        }

        var targetParties = await db.PartyMembers.Where(m => m.PersonId == targetId)
            .Select(m => m.PartyId).ToListAsync(cancellationToken);
        foreach (var member in await db.PartyMembers.Where(m => m.PersonId == sourceId).ToListAsync(cancellationToken))
        {
            if (targetParties.Contains(member.PartyId))
            {
                db.PartyMembers.Remove(member);
            }
            else
            {
                member.PersonId = targetId;
            }
        }

        // ---- polymorphic references (EntityType/Id == Person/sourceId): reassign ----
        const string type = nameof(Person);

        // classification history is append-only; merge both records' history
        await db.ClassificationHistory.Where(e => e.EntityType == type && e.EntityId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.EntityId, targetId), cancellationToken);

        // comments, sources, followups: conflict-free reassign
        await db.Comments.Where(k => k.EntityType == type && k.EntityId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.EntityId, targetId), cancellationToken);
        await db.Sources.Where(q => q.EntityType == type && q.EntityId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(q => q.EntityId, targetId), cancellationToken);
        await db.Sources.Where(q => q.TargetType == type && q.TargetId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(q => q.TargetId, targetId), cancellationToken);
        await db.Followups.Where(w => w.EntityType == type && w.EntityId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.EntityId, targetId), cancellationToken);

        // tags have a unique index → only reassign ones the target lacks
        var targetTagIds = await db.TagMappings.Where(z => z.EntityType == type && z.EntityId == targetId)
            .Select(z => z.TagId).ToListAsync(cancellationToken);
        foreach (var mapping in await db.TagMappings.Where(z => z.EntityType == type && z.EntityId == sourceId).ToListAsync(cancellationToken))
        {
            if (targetTagIds.Contains(mapping.TagId))
            {
                db.TagMappings.Remove(mapping);
            }
            else
            {
                mapping.EntityId = targetId;
            }
        }

        // custom fields have a unique index per definition → existing target values win
        var targetFieldIds = await db.CustomFieldValues.Where(w => w.EntityType == type && w.EntityId == targetId)
            .Select(w => w.CustomFieldDefinitionId).ToListAsync(cancellationToken);
        foreach (var value in await db.CustomFieldValues.Where(w => w.EntityType == type && w.EntityId == sourceId).ToListAsync(cancellationToken))
        {
            if (targetFieldIds.Contains(value.CustomFieldDefinitionId))
            {
                db.CustomFieldValues.Remove(value);
            }
            else
            {
                value.EntityId = targetId;
            }
        }

        // watchlist: one active entry per agent per record
        var targetFollower = await db.Watchlists.Where(w => w.EntityType == type && w.EntityId == targetId)
            .Select(w => w.AgentId).ToListAsync(cancellationToken);
        foreach (var entry in await db.Watchlists.Where(w => w.EntityType == type && w.EntityId == sourceId).ToListAsync(cancellationToken))
        {
            if (targetFollower.Contains(entry.AgentId))
            {
                db.Watchlists.Remove(entry);
            }
            else
            {
                entry.EntityId = targetId;
            }
        }

        // links: reassign both sides; drop resulting self-links
        foreach (var link in await db.Links
                     .Where(v => (v.SourceType == type && v.SourceId == sourceId) || (v.TargetType == type && v.TargetId == sourceId))
                     .ToListAsync(cancellationToken))
        {
            if (link.SourceType == type && link.SourceId == sourceId)
            {
                link.SourceId = targetId;
            }
            if (link.TargetType == type && link.TargetId == sourceId)
            {
                link.TargetId = targetId;
            }
            if (link.SourceType == link.TargetType && link.SourceId == link.TargetId)
            {
                db.Links.Remove(link);
            }
        }

        // open requests: point to the target record, refresh the designation. A publication request is left behind on
        // purpose — its snapshot still names the source, so following the target would approve a notice for a file the
        // request was never about; it is closed with the source's notices below instead.
        foreach (var request in await db.Requests
                     .Where(a => a.TargetType == type && a.TargetId == sourceId
                         && a.Type != RequestType.Veroeffentlichung)
                     .ToListAsync(cancellationToken))
        {
            request.TargetId = targetId;
            request.TargetDesignation = $"{target.Name} ({target.CaseNumber})";
        }

        // ---- profile: fill the target's missing fields from the source ----
        if (string.IsNullOrWhiteSpace(target.Description) && !string.IsNullOrWhiteSpace(source.Description))
        {
            target.Description = source.Description;
        }
        // classified status carries over to the merged record
        var targetBecomesRestricted = !target.IsRestricted && source.IsClassified;
        target.IsClassified = target.IsClassified || source.IsClassified;
        // wanted flag survives, else the merge lifts it with no reason
        if (source.IsWanted)
        {
            target.IsWanted = true;
            if (string.IsNullOrWhiteSpace(target.WantedReason))
            {
                target.WantedReason = source.WantedReason;
            }
        }
        // target's classification/life status left untouched (rank-gated)

        // leave a trail on the target record beyond the audit log
        db.Comments.Add(new Comment
        {
            EntityType = type,
            EntityId = targetId,
            Text = $"Akte „{source.Name}“ ({source.CaseNumber}) wurde in diese Akte überführt (Duplikat-Zusammenführung).",
            AuthorName = actor.GetCodename(),
        });

        // explicit merge trail on the target (the FK re-pointing above bypassed the interceptor)
        db.AuditLogs.Add(ManualAudit.Row(nameof(Person), targetId, AuditAction.Modified, actor,
            ManualAudit.Change("Zusammengeführt aus", null, $"{source.Name} ({source.CaseNumber})")));

        // the citizen link is a pointer like the others: left on the source it would take the whole tipster
        // history off the surviving file, because GetForLinkedPersonAsync keys on exactly this column.
        // IgnoreQueryFilters so a deactivated profile moves too.
        await db.BuergerProfile.IgnoreQueryFilters()
            .Where(p => p.LinkedPersonId == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.LinkedPersonId, targetId), cancellationToken);

        // ---- send source record to the trash (interceptor soft-deletes) ----
        db.People.Remove(source);
        // merge is the reason
        CriticalChange.NoteDeletion(db, nameof(Person), sourceId, MergeReason(target), actor);

        await db.SaveChangesAsync(cancellationToken);

        // the source file is gone, and the merge may have classified the target without going through PersonService —
        // both leave a public notice standing that nothing backs any more
        await publicWanted.RetractForRecordAsync(sourceId, "Akte zusammengeführt.", actor, cancellationToken);
        if (targetBecomesRestricted)
        {
            await publicWanted.RetractForRecordAsync(targetId, "Akte als Verschlusssache eingestuft.", actor, cancellationToken);
        }
    }

    /// <summary>Deletion reason on the source; names the target only when every agent may read it.</summary>
    private static string MergeReason(Person target)
        => target.IsRestricted
            ? "Zusammengeführt in eine andere Akte (Duplikat)."
            : $"Zusammengeführt in „{target.Name}“ ({target.CaseNumber}).";
}
