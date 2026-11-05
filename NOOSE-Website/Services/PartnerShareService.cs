using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Requests;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.Shares;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IPartnerShareService" />
public class PartnerShareService(IDbContextFactory<AppDbContext> dbFactory, INotificationService notifications,
    SharesBroadcaster? broadcaster = null) : IPartnerShareService
{
    /// <summary>Target type of an inquiry no record answers yet; never a CLR type, so no visibility arm can match it.</summary>
    public const string InquiryTarget = "PartnerAnfrage";

    private Task NotifyMentionsAsync(string? text, string what, string targetType, string targetId,
        ClaimsPrincipal actor, CancellationToken cancellationToken)
        => MentionNotify.DeltaAsync(notifications, null, text, $"einer {what} zu einer Freigabe",
            targetType, targetId, actor, cancellationToken);

    public async Task<IReadOnlyList<PartnerShareState>> GetForRecordAsync(string entityType, string entityId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var shares = await PartnerVisibility.ActiveShares(db).AsNoTracking()
            .Where(s => s.EntityType == entityType && s.EntityId == entityId && s.PartnerAgentId == null)
            .ToListAsync(cancellationToken);
        return BuildStates(shares);
    }

    public async Task<IReadOnlyList<PartnerShareState>> GetForChildAsync(string childType, string childId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var shares = await PartnerVisibility.ActiveShares(db).AsNoTracking()
            .Where(s => s.EntityType == childType && s.EntityId == childId && s.PartnerAgentId == null)
            .ToListAsync(cancellationToken);
        return BuildStates(shares);
    }

    public async Task<IReadOnlyList<PartnerIndividualShareState>> GetIndividualSharesForRecordAsync(string entityType, string entityId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // order on the real column before projecting; ordering by the record's property is untranslatable
        var rows = await PartnerVisibility.ActiveShares(db).AsNoTracking()
            .Where(s => s.EntityType == entityType && s.EntityId == entityId && s.PartnerAgentId != null)
            .Join(db.Users.AsNoTracking(), s => s.PartnerAgentId, a => a.Id, (s, a) => new { s, a })
            .OrderBy(x => x.a.Codename)
            .Select(x => new PartnerIndividualShareState(x.a.Id, x.a.Codename, x.s.Agency, x.a.PartnerRank, x.s.IncludesChildren, x.s.ValidUntil))
            .ToListAsync(cancellationToken);
        return rows;
    }

    public async Task<IReadOnlyList<PartnerAccountOption>> GetSelectablePartnersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Users.AsNoTracking()
            .Where(a => a.PartnerAgency != null && a.Status == AgentStatus.Active)
            .OrderBy(a => a.PartnerAgency).ThenBy(a => a.Codename)
            .Select(a => new PartnerAccountOption(a.Id, a.Codename, a.PartnerAgency!.Value, a.PartnerRank))
            .ToListAsync(cancellationToken);
    }

    public Task SetParentAsync(string entityType, string entityId, PartnerAgency agency, bool released, bool includesChildren,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default)
        => SetParentCoreAsync(entityType, entityId, agency, released, includesChildren, ExpiryChange.Keep, actor, cancellationToken);

    public Task SetParentAsync(string entityType, string entityId, PartnerAgency agency, bool released, bool includesChildren,
        DateTime? validUntil, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
        => SetParentCoreAsync(entityType, entityId, agency, released, includesChildren, ExpiryChange.To(validUntil), actor, cancellationToken);

    private async Task SetParentCoreAsync(string entityType, string entityId, PartnerAgency agency, bool released, bool includesChildren,
        ExpiryChange expiry, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        Permission.RequireLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await UpsertAsync(db, entityType, entityId, agency, partnerAgentId: null, released, includesChildren, expiry, actor, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetChildAsync(string childType, string childId, PartnerAgency agency, bool released,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await UpsertAsync(db, childType, childId, agency, partnerAgentId: null, released, includesChildren: false, ExpiryChange.Keep, actor, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task SetIndividualParentAsync(string entityType, string entityId, string partnerAgentId, bool released, bool includesChildren,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default)
        => SetIndividualCoreAsync(entityType, entityId, partnerAgentId, released, includesChildren, ExpiryChange.Keep, actor, cancellationToken);

    public Task SetIndividualParentAsync(string entityType, string entityId, string partnerAgentId, bool released, bool includesChildren,
        DateTime? validUntil, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
        => SetIndividualCoreAsync(entityType, entityId, partnerAgentId, released, includesChildren, ExpiryChange.To(validUntil), actor, cancellationToken);

    private async Task SetIndividualCoreAsync(string entityType, string entityId, string partnerAgentId, bool released, bool includesChildren,
        ExpiryChange expiry, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        Permission.RequireLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // agency derived from the account
        var agency = await db.Users.Where(a => a.Id == partnerAgentId).Select(a => a.PartnerAgency).FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Das gewählte Konto ist kein Partner-Konto.");
        await UpsertAsync(db, entityType, entityId, agency, partnerAgentId, released, includesChildren, expiry, actor, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetValidUntilAsync(string entityType, string entityId, PartnerAgency agency, string? partnerAgentId,
        DateTime? validUntil, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        var now = DateTime.UtcNow;
        var end = PartnerShareExpiry.Require(validUntil, now);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await PartnerVisibility.ActiveShares(db)
            .FirstOrDefaultAsync(s => s.EntityType == entityType && s.EntityId == entityId && s.Agency == agency && s.PartnerAgentId == partnerAgentId, cancellationToken)
            ?? throw new InvalidOperationException("Diese Freigabe gibt es nicht (mehr).");
        if (row.ValidUntil != end)
        {
            var before = PartnerShareExpiry.Until(row.ValidUntil);
            row.ValidUntil = end;
            db.AuditLogs.Add(RecordRow(entityType, entityId, await LabelAsync(db, agency, partnerAgentId, cancellationToken),
                actor, "freigegeben, " + before, "freigegeben, " + PartnerShareExpiry.Until(end)));
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PartnerTypeShareSummary>> GetTypeSummariesAsync(PartnerAgency agency, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // agency-wide shares only; one active row per (type, record, agency) so a row count = record count
        var sharedCounts = (await PartnerVisibility.ActiveShares(db)
            .Where(s => s.Agency == agency && s.PartnerAgentId == null)
            .GroupBy(s => s.EntityType)
            .Select(g => new { Type = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken))
            .ToDictionary(x => x.Type, x => x.Count);

        var result = new List<PartnerTypeShareSummary>();
        foreach (var type in PartnerTabCatalog.All)
        {
            var total = await CountRecordsAsync(db, type.TypeKey, cancellationToken);
            result.Add(new PartnerTypeShareSummary(type.TypeKey, type.DisplayName, total, sharedCounts.GetValueOrDefault(type.TypeKey)));
        }
        return result;
    }

    public async Task<int> SetTypeAsync(string entityType, PartnerAgency agency, bool released, bool includesChildren,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        if (!PartnerVisibility.IsReleasableType(entityType))
        {
            throw new InvalidOperationException("Dieser Akten-Typ kann nicht freigegeben werden.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        if (!released)
        {
            // withdraw every agency-wide share of this type; individual-account shares stay
            var rows = await db.PartnerShares
                .Where(s => s.EntityType == entityType && s.Agency == agency && s.PartnerAgentId == null)
                .ToListAsync(cancellationToken);
            db.PartnerShares.RemoveRange(rows);
            await db.SaveChangesAsync(cancellationToken);
            return rows.Count;
        }

        var allIds = await RecordIdsAsync(db, entityType, cancellationToken);
        var existing = await db.PartnerShares
            .Where(s => s.EntityType == entityType && s.Agency == agency && s.PartnerAgentId == null)
            .ToListAsync(cancellationToken);
        var existingById = existing.ToDictionary(s => s.EntityId);

        var added = 0;
        var now = DateTime.UtcNow;
        foreach (var id in allIds)
        {
            if (existingById.TryGetValue(id, out var row))
            {
                row.IncludesChildren = includesChildren;
                // an expired row the sweep has not removed yet is released again, unlimited
                if (!PartnerShareExpiry.IsActive(row.ValidUntil, now))
                {
                    row.ValidUntil = null;
                }
            }
            else
            {
                db.PartnerShares.Add(new PartnerShare
                {
                    EntityType = entityType,
                    EntityId = id,
                    Agency = agency,
                    PartnerAgentId = null,
                    IncludesChildren = includesChildren,
                });
                added++;
            }
        }
        await db.SaveChangesAsync(cancellationToken);
        return added;
    }

    // live record ids per type (soft-deleted rows excluded by the global query filter)
    private static async Task<List<string>> RecordIdsAsync(AppDbContext db, string entityType, CancellationToken cancellationToken)
        => entityType switch
        {
            nameof(Person) => await db.People.Select(p => p.Id).ToListAsync(cancellationToken),
            nameof(Faction) => await db.Factions.Select(f => f.Id).ToListAsync(cancellationToken),
            nameof(PersonGroup) => await db.PersonGroups.Select(g => g.Id).ToListAsync(cancellationToken),
            nameof(Party) => await db.Parties.Select(p => p.Id).ToListAsync(cancellationToken),
            nameof(Operation) => await db.Operations.Select(o => o.Id).ToListAsync(cancellationToken),
            nameof(Case) => await db.Cases.Select(c => c.Id).ToListAsync(cancellationToken),
            // taskforce-internal docs are shared per-document only, never via bulk type-release
            nameof(Document) => await db.Documents.Where(d => d.OwnerTaskforceId == null).Select(d => d.Id).ToListAsync(cancellationToken),
            nameof(Law) => await db.Laws.Select(l => l.Id).ToListAsync(cancellationToken),
            _ => new List<string>(),
        };

    private static async Task<int> CountRecordsAsync(AppDbContext db, string entityType, CancellationToken cancellationToken)
        => entityType switch
        {
            nameof(Person) => await db.People.CountAsync(cancellationToken),
            nameof(Faction) => await db.Factions.CountAsync(cancellationToken),
            nameof(PersonGroup) => await db.PersonGroups.CountAsync(cancellationToken),
            nameof(Party) => await db.Parties.CountAsync(cancellationToken),
            nameof(Operation) => await db.Operations.CountAsync(cancellationToken),
            nameof(Case) => await db.Cases.CountAsync(cancellationToken),
            nameof(Document) => await db.Documents.CountAsync(d => d.OwnerTaskforceId == null, cancellationToken),
            nameof(Law) => await db.Laws.CountAsync(cancellationToken),
            _ => 0,
        };

    /// <summary>What a write does to the end date: leave it, or set it (null = unlimited).</summary>
    private readonly record struct ExpiryChange(bool Set, DateTime? Value)
    {
        public static ExpiryChange Keep => default;
        public static ExpiryChange To(DateTime? value) => new(true, value);
    }

    private static async Task UpsertAsync(AppDbContext db, string entityType, string entityId, PartnerAgency agency, string? partnerAgentId,
        bool released, bool includesChildren, ExpiryChange expiry, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var end = expiry.Set ? PartnerShareExpiry.Require(expiry.Value, now) : null;
        var row = await db.PartnerShares
            .FirstOrDefaultAsync(s => s.EntityType == entityType && s.EntityId == entityId && s.Agency == agency && s.PartnerAgentId == partnerAgentId, cancellationToken);
        if (released)
        {
            if (row is null)
            {
                db.PartnerShares.Add(new PartnerShare
                {
                    EntityType = entityType,
                    EntityId = entityId,
                    Agency = agency,
                    PartnerAgentId = partnerAgentId,
                    IncludesChildren = includesChildren,
                    ValidUntil = end,
                });
                db.AuditLogs.Add(RecordRow(entityType, entityId, await LabelAsync(db, agency, partnerAgentId, cancellationToken),
                    actor, null, "freigegeben, " + PartnerShareExpiry.Until(end)));
            }
            else
            {
                var wasActive = PartnerShareExpiry.IsActive(row.ValidUntil, now);
                var before = wasActive ? PartnerShareExpiry.Until(row.ValidUntil) : null;
                row.IncludesChildren = includesChildren;
                // an expired row the sweep has not removed yet comes back unlimited unless a date says otherwise
                if (expiry.Set)
                {
                    row.ValidUntil = end;
                }
                else if (!wasActive)
                {
                    row.ValidUntil = null;
                }
                var after = PartnerShareExpiry.Until(row.ValidUntil);
                if (before != after)
                {
                    db.AuditLogs.Add(RecordRow(entityType, entityId, await LabelAsync(db, agency, partnerAgentId, cancellationToken),
                        actor, before is null ? null : "freigegeben, " + before, "freigegeben, " + after));
                }
            }
        }
        else if (row is not null)
        {
            var wasActive = PartnerShareExpiry.IsActive(row.ValidUntil, now);
            // soft-delete via interceptor
            db.PartnerShares.Remove(row);
            db.AuditLogs.Add(RecordRow(entityType, entityId, await LabelAsync(db, agency, partnerAgentId, cancellationToken),
                actor, wasActive ? "freigegeben, " + PartnerShareExpiry.Until(row.ValidUntil) : "abgelaufen", "entzogen"));
        }
    }

    /// <summary>Audit row on the released record itself, so the release shows on its timeline next to the other changes.</summary>
    internal static AuditLog RecordRow(string entityType, string entityId, string label, ClaimsPrincipal? actor, string? before, string after)
    {
        var changes = ManualAudit.Change("Partner-Freigabe " + label, before, after);
        return actor is null
            ? ManualAudit.SystemRow(entityType, entityId, AuditAction.Modified, changes)
            : ManualAudit.Row(entityType, entityId, AuditAction.Modified, actor, changes);
    }

    /// <summary>Agency name, plus the codename for a single-account release.</summary>
    internal static async Task<string> LabelAsync(AppDbContext db, PartnerAgency agency, string? partnerAgentId, CancellationToken cancellationToken)
    {
        var name = PartnerAgencyDisplay.Name(agency);
        if (partnerAgentId is null)
        {
            return name;
        }
        var codename = await db.Users.AsNoTracking().Where(a => a.Id == partnerAgentId).Select(a => a.Codename).FirstOrDefaultAsync(cancellationToken);
        return codename is null ? name : $"{name} · {codename}";
    }

    private static IReadOnlyList<PartnerShareState> BuildStates(List<PartnerShare> shares)
        => PartnerAgencyDisplay.All
            .Select(agency =>
            {
                var row = shares.FirstOrDefault(s => s.Agency == agency);
                return new PartnerShareState(agency, row is not null, row?.IncludesChildren ?? false, row?.ValidUntil);
            })
            .ToList();

    public async Task RequestPartnerShareAsync(ClaimsPrincipal actor, string entityType, string entityId,
        PartnerAgency agency, string? partnerAgentId, bool includesChildren, string justification,
        DateTime? validUntil = null, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        if (string.IsNullOrWhiteSpace(justification))
            throw new InvalidOperationException("Bitte eine Begründung für den Freigabe-Antrag angeben.");
        var end = PartnerShareExpiry.Require(validUntil, DateTime.UtcNow);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // block if already released agency-wide (also covers an individual target)
        if (await PartnerVisibility.ActiveShares(db).AnyAsync(s =>
            s.EntityType == entityType && s.EntityId == entityId &&
            s.Agency == agency && s.PartnerAgentId == null, cancellationToken))
        {
            throw new InvalidOperationException("Diese Akte ist für diese Behörde bereits freigegeben.");
        }

        // block if this exact partner account already has it
        if (partnerAgentId != null && await PartnerVisibility.ActiveShares(db).AnyAsync(s =>
            s.EntityType == entityType && s.EntityId == entityId &&
            s.Agency == agency && s.PartnerAgentId == partnerAgentId, cancellationToken))
        {
            throw new InvalidOperationException("Diese Akte ist für diesen Partner bereits freigegeben.");
        }

        // dedup: no other pending request for the same target + agency + account
        if (await db.Requests.AnyAsync(r =>
            r.Type == RequestType.PartnerFreigabe &&
            r.TargetType == entityType && r.TargetId == entityId &&
            r.FreigabeAgency == agency && r.FreigabePartnerAgentId == partnerAgentId &&
            r.Status == RequestStatus.Requested, cancellationToken))
        {
            throw new InvalidOperationException(partnerAgentId == null
                ? "Für diese Akte läuft bereits ein Freigabe-Antrag für diese Behörde."
                : "Für diese Akte läuft bereits ein Freigabe-Antrag für diesen Partner.");
        }

        var designation = await GetDesignationAsync(db, entityType, entityId, cancellationToken);
        db.Requests.Add(new Request
        {
            Type = RequestType.PartnerFreigabe,
            TargetType = entityType,
            TargetId = entityId,
            TargetDesignation = designation,
            FreigabeAgency = agency,
            FreigabePartnerAgentId = partnerAgentId,
            FreigabeIncludesChildren = includesChildren,
            FreigabeValidUntil = end,
            Justification = justification.Trim(),
            Status = RequestStatus.Requested,
            RequesterName = actor.GetCodename(),
        });
        await db.SaveChangesAsync(cancellationToken);

        await NotifyMentionsAsync(justification, "Begründung", entityType, entityId, actor, cancellationToken);
    }

    public async Task<List<Request>> GetPendingPartnerShareRequestsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Requests
            .Where(r => r.Type == RequestType.PartnerFreigabe && r.Status == RequestStatus.Requested)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task ApprovePartnerShareRequestAsync(ClaimsPrincipal actor, string requestId, string? note,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var request = await db.Requests.FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken)
            ?? throw new InvalidOperationException("Antrag nicht gefunden.");
        if (request.Type != RequestType.PartnerFreigabe)
            throw new InvalidOperationException("Ungültiger Antragstyp.");
        if (request.Status != RequestStatus.Requested)
            throw new InvalidOperationException("Dieser Antrag wurde bereits entschieden.");

        await UpsertAsync(db, request.TargetType, request.TargetId,
            request.FreigabeAgency!.Value, request.FreigabePartnerAgentId,
            released: true, request.FreigabeIncludesChildren,
            request.FreigabeValidUntil is null ? ExpiryChange.Keep : ExpiryChange.To(request.FreigabeValidUntil), actor, cancellationToken);

        request.Status = RequestStatus.Approved;
        request.DeciderName = actor.GetCodename();
        request.DecidedAt = DateTime.UtcNow;
        request.DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        await NotifyMentionsAsync(request.DecisionNote, "Entscheidung", request.TargetType, request.TargetId,
            actor, cancellationToken);
    }

    public async Task RejectPartnerShareRequestAsync(ClaimsPrincipal actor, string requestId, string? note,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var request = await db.Requests.FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken)
            ?? throw new InvalidOperationException("Antrag nicht gefunden.");
        if (request.Type != RequestType.PartnerFreigabe)
            throw new InvalidOperationException("Ungültiger Antragstyp.");
        if (request.Status != RequestStatus.Requested)
            throw new InvalidOperationException("Dieser Antrag wurde bereits entschieden.");

        request.Status = RequestStatus.Rejected;
        request.DeciderName = actor.GetCodename();
        request.DecidedAt = DateTime.UtcNow;
        request.DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();

        await db.SaveChangesAsync(cancellationToken);

        await NotifyMentionsAsync(request.DecisionNote, "Entscheidung", request.TargetType, request.TargetId,
            actor, cancellationToken);
    }

    public async Task SubmitPartnerInquiryAsync(ClaimsPrincipal actor, string target, string justification,
        CancellationToken cancellationToken = default)
    {
        if (!actor.IsPartner() || actor.IsDemo() || actor.GetPartnerAgency() is not { } agency || actor.GetAgentId() is not { } meId)
        {
            throw new UnauthorizedAccessException("Anfragen stellen nur Partnerbehörden.");
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        Permission.RequireInternalOrPartnerFeature(actor,
            await PartnerVisibility.FeaturesAsync(db, agency, cancellationToken), PartnerFeature.ShareRequests);

        var subject = (target ?? string.Empty).Trim();
        var reason = (justification ?? string.Empty).Trim();
        if (subject.Length == 0 || reason.Length == 0)
        {
            throw new InvalidOperationException("Bitte angeben, wen oder was ihr sehen möchtet, und warum.");
        }
        if (subject.Length > 256 || reason.Length > 2000)
        {
            throw new InvalidOperationException("Die Anfrage ist zu lang.");
        }
        var open = await db.Requests
            .Where(r => r.Type == RequestType.PartnerAnfrage && r.Status == RequestStatus.Requested && r.CreatedById == meId)
            .Select(r => r.TargetDesignation)
            .ToListAsync(cancellationToken);
        if (open.Any(t => string.Equals(t, subject, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Zu dieser Anfrage wartet schon eine Antwort.");
        }
        if (open.Count >= MaxOpenInquiries)
        {
            throw new InvalidOperationException($"Es warten schon {MaxOpenInquiries} Anfragen auf eine Antwort. Bitte erst diese abwarten.");
        }

        db.Requests.Add(new Request
        {
            Type = RequestType.PartnerAnfrage,
            TargetType = InquiryTarget,
            TargetId = string.Empty,
            TargetDesignation = subject,
            FreigabeAgency = agency,
            FreigabePartnerAgentId = meId,
            Justification = reason,
            Status = RequestStatus.Requested,
            RequesterName = actor.GetCodename(),
        });
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var leadership = await db.Users.OnlySelectable()
                .Where(u => u.IsAdmin || (u.Rank != null && u.Rank >= Rank.SectionChief))
                .Select(u => u.Id)
                .ToListAsync(cancellationToken);
            await notifications.NotifyManyAsync(leadership, NotificationType.PartnerInquiry,
                $"{PartnerAgencyDisplay.Name(agency)} fragt eine Freigabe an: {Clip(subject)}", "/admin/freigaben", meId, cancellationToken);
        }
        catch { /* best effort */ }
        broadcaster?.Report();
    }

    /// <summary>Open inquiries one account may have at once.</summary>
    public const int MaxOpenInquiries = 10;

    /// <summary>Free text short enough for a notification title.</summary>
    private static string Clip(string text) => text.Length <= 80 ? text : text[..79] + "…";

    public async Task<List<Request>> GetMyPartnerInquiriesAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        if (actor.GetAgentId() is not { } meId)
        {
            return new();
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Requests
            .Where(r => r.Type == RequestType.PartnerAnfrage && r.CreatedById == meId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<Request>> GetPendingPartnerInquiriesAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Requests
            .Where(r => r.Type == RequestType.PartnerAnfrage && r.Status == RequestStatus.Requested)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task ApprovePartnerInquiryAsync(ClaimsPrincipal actor, string requestId, string entityType, string entityId,
        bool toAccountOnly, bool includesChildren, string? note, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        if (!PartnerVisibility.IsReleasableType(entityType))
        {
            throw new InvalidOperationException("Diese Aktenart lässt sich nicht an Partner freigeben.");
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var request = await OpenInquiryAsync(db, requestId, cancellationToken);
        var designation = await GetDesignationAsync(db, entityType, entityId, cancellationToken);
        if (designation == entityId)
        {
            throw new InvalidOperationException("Die gewählte Akte gibt es nicht.");
        }
        var target = toAccountOnly ? request.FreigabePartnerAgentId : null;
        // never narrows an existing release
        includesChildren |= await PartnerVisibility.ActiveShares(db).AnyAsync(s => s.EntityType == entityType && s.EntityId == entityId
            && s.Agency == request.FreigabeAgency && s.PartnerAgentId == target && s.IncludesChildren, cancellationToken);
        await UpsertAsync(db, entityType, entityId, request.FreigabeAgency!.Value, target, released: true, includesChildren, ExpiryChange.Keep, actor, cancellationToken);

        // answer names a record
        request.TargetType = entityType;
        request.TargetId = entityId;
        request.FreigabeIncludesChildren = includesChildren;
        Decide(request, RequestStatus.Approved, actor, note);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        try
        {
            await notifications.NotifyAsync(request.CreatedById, NotificationType.RequestDecided,
                $"Deine Anfrage „{Clip(request.TargetDesignation)}“ wurde beantwortet: {Clip(designation)} ist freigegeben.",
                SearchNavigation.For(entityType, entityId), cancellationToken);
        }
        catch { /* best effort */ }
        broadcaster?.Report();
    }

    public async Task RejectPartnerInquiryAsync(ClaimsPrincipal actor, string requestId, string? note,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var request = await OpenInquiryAsync(db, requestId, cancellationToken);
        Decide(request, RequestStatus.Rejected, actor, note);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            await notifications.NotifyAsync(request.CreatedById, NotificationType.RequestDecided,
                $"Deine Anfrage „{Clip(request.TargetDesignation)}“ wurde abgelehnt.", "/anfragen", cancellationToken);
        }
        catch { /* best effort */ }
        broadcaster?.Report();
    }

    private static async Task<Request> OpenInquiryAsync(AppDbContext db, string requestId, CancellationToken cancellationToken)
    {
        var request = await db.Requests.FirstOrDefaultAsync(r => r.Id == requestId, cancellationToken)
            ?? throw new InvalidOperationException("Anfrage nicht gefunden.");
        if (request.Type != RequestType.PartnerAnfrage)
        {
            throw new InvalidOperationException("Ungültiger Antragstyp.");
        }
        if (request.Status != RequestStatus.Requested)
        {
            throw new InvalidOperationException("Diese Anfrage wurde bereits entschieden.");
        }
        return request;
    }

    private static void Decide(Request request, RequestStatus status, ClaimsPrincipal actor, string? note)
    {
        request.Status = status;
        request.DeciderName = actor.GetCodename();
        request.DecidedAt = DateTime.UtcNow;
        request.DecisionNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }

    private static async Task<string> GetDesignationAsync(AppDbContext db, string entityType, string entityId, CancellationToken ct)
        => entityType switch
        {
            nameof(Person) => await db.People.Where(p => p.Id == entityId)
                .Select(p => p.Name + " (" + p.CaseNumber + ")").FirstOrDefaultAsync(ct) ?? entityId,
            nameof(Faction) => await db.Factions.Where(f => f.Id == entityId)
                .Select(f => f.Name + " (" + f.CaseNumber + ")").FirstOrDefaultAsync(ct) ?? entityId,
            nameof(PersonGroup) => await db.PersonGroups.Where(g => g.Id == entityId)
                .Select(g => g.Name + " (" + g.CaseNumber + ")").FirstOrDefaultAsync(ct) ?? entityId,
            nameof(Party) => await db.Parties.Where(p => p.Id == entityId)
                .Select(p => p.Name + " (" + p.CaseNumber + ")").FirstOrDefaultAsync(ct) ?? entityId,
            nameof(Operation) => await db.Operations.Where(o => o.Id == entityId)
                .Select(o => o.Title + " (" + o.CaseNumber + ")").FirstOrDefaultAsync(ct) ?? entityId,
            nameof(Case) => await db.Cases.Where(c => c.Id == entityId)
                .Select(c => c.Title + " (" + c.CaseNumber + ")").FirstOrDefaultAsync(ct) ?? entityId,
            nameof(Document) => await db.Documents.Where(d => d.Id == entityId)
                .Select(d => d.Title).FirstOrDefaultAsync(ct) ?? entityId,
            nameof(Law) => await db.Laws.Where(l => l.Id == entityId)
                .Select(l => l.Title).FirstOrDefaultAsync(ct) ?? entityId,
            _ => entityId,
        };
}
