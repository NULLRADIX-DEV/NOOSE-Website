using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Administration;
using NOOSE_Website.Models.Administration;
using NOOSE_Website.Models.Decorations;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Decorations;

namespace NOOSE_Website.Services.Administration;

/// <summary>The Administration workspace: its info page, roster, overviews, relationships and decoration proposals.</summary>
public interface IAdministrationService
{
    /// <summary>Creates the one workspace row if missing; called at startup.</summary>
    Task EnsureWorkspaceAsync(CancellationToken cancellationToken = default);

    Task<string?> GetInformationAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task SaveInformationAsync(string html, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AdministrationMemberRow>> GetMembersAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SecurityCheckRow>> GetSecurityChecksAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EvaluationOverviewRow>> GetEvaluationOverviewAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PromotionOverviewRow>> GetOpenPromotionsAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RelationshipRow>> GetRelationshipsAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<string> AddRelationshipAsync(RelationshipInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task RemoveRelationshipAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Ids related to the given agent; empty for viewers outside the workspace.</summary>
    Task<IReadOnlySet<string>> GetRelatedIdsAsync(string agentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DecorationProposalRow>> GetProposalsAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<string> ProposeDecorationAsync(DecorationProposalInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Accept awards the decoration through the decoration service; refuse only closes the proposal.</summary>
    Task DecideProposalAsync(string id, bool accept, string? citation, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IAdministrationService" />
public class AdministrationService(IDbContextFactory<AppDbContext> dbFactory, IDecorationService decorations,
    INotificationService notifications, ILogger<AdministrationService> logger) : IAdministrationService
{
    /// <summary>The regulation wants an evaluation at least this often before a promotion.</summary>
    public static readonly TimeSpan EvaluationStaleAfter = TimeSpan.FromDays(14);

    private const string Href = "/administration";

    public async Task EnsureWorkspaceAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await db.AdministrationWorkspaces.AnyAsync(cancellationToken))
        {
            return;
        }
        db.AdministrationWorkspaces.Add(new AdministrationWorkspace());
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) { /* ignore */ }
    }

    public async Task<string?> GetInformationAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireAdministrationRead(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.AdministrationWorkspaces.AsNoTracking()
            .Where(w => w.Id == AdministrationWorkspace.SingletonId)
            .Select(w => w.InformationHtml)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task SaveInformationAsync(string html, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireAdministrationWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.AdministrationWorkspaces.FirstOrDefaultAsync(w => w.Id == AdministrationWorkspace.SingletonId, cancellationToken);
        if (row is null)
        {
            row = new AdministrationWorkspace();
            db.AdministrationWorkspaces.Add(row);
        }
        var clean = HtmlCleanup.Clean(html);
        row.InformationHtml = HtmlCleanup.PlainText(clean).Length == 0 && !clean.Contains("<img", StringComparison.OrdinalIgnoreCase)
            ? null
            : clean;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AdministrationMemberRow>> GetMembersAsync(ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireAdministrationRead(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Users.AsNoTracking().OnlySelectable()
            .Where(u => u.IsHRB || u.Rank >= DivisionRules.BoardFloor)
            .ToListAsync(cancellationToken);
        return rows
            .Select(u => new AdministrationMemberRow(u.Id, u.Codename, u.Rank, DivisionRules.Of(u).Home, u.IsHRB,
                DivisionRules.IsBoard(u.Rank)))
            .OrderByDescending(r => r.IsHrb)
            .ThenByDescending(r => r.Rank)
            .ThenBy(r => r.Codename, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<SecurityCheckRow>> GetSecurityChecksAsync(ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireAdministrationRead(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var since = DateTime.UtcNow.AddDays(-30);
        return await db.Bewerbungen.AsNoTracking()
            .Where(b => b.Status == BewerbungStatus.InSicherheitspruefung
                || (b.SecurityCheckPassed != null && (b.DecidedAt ?? b.SubmittedAt) >= since))
            .OrderBy(b => b.Status == BewerbungStatus.InSicherheitspruefung ? 0 : 1)
            .ThenByDescending(b => b.SubmittedAt)
            .Take(200)
            .Select(b => new SecurityCheckRow(b.Id, b.CaseNumber, b.Name, b.Status, b.SecurityCheckPassed, b.SubmittedAt,
                b.DecidedAt, b.AssignedAgentName))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EvaluationOverviewRow>> GetEvaluationOverviewAsync(ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireAdministrationRead(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agents = await db.Users.AsNoTracking().OnlySelectable()
            .Select(u => new { u.Id, u.Codename, u.Rank })
            .ToListAsync(cancellationToken);
        // newest per agent, picked in memory: one row per agent is a few dozen
        var latest = (await db.AgentEvaluations.AsNoTracking()
                .Select(e => new { e.AgentId, e.EntryDate, e.Grade, e.CreatedAt })
                .ToListAsync(cancellationToken))
            .GroupBy(e => e.AgentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(e => e.EntryDate).ThenByDescending(e => e.CreatedAt).First());
        var staleBefore = DateTime.UtcNow - EvaluationStaleAfter;
        return agents
            .Select(a =>
            {
                var last = latest.GetValueOrDefault(a.Id);
                return new EvaluationOverviewRow(a.Id, a.Codename, a.Rank, last?.EntryDate, last?.Grade,
                    last is null || last.EntryDate < staleBefore,
                    last is not null && last.Grade >= EvaluationGrade.Poor);
            })
            .OrderByDescending(r => r.Negative)
            .ThenBy(r => r.LastDate ?? DateTime.MinValue)
            .ThenBy(r => r.Codename, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<PromotionOverviewRow>> GetOpenPromotionsAsync(ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireAdministrationRead(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var open = await db.AgentPromotionRequests.AsNoTracking()
            .Where(r => r.Status == PromotionStatus.Requested)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(cancellationToken);
        var ids = open.Select(r => r.AgentId).Distinct().ToList();
        var agents = await db.Users.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.Codename, u.Rank })
            .ToDictionaryAsync(u => u.Id, cancellationToken);
        var blocked = await db.DisciplinaryProceedings.AsNoTracking()
            .Where(d => ids.Contains(d.AgentId)
                && (d.Status == DisciplinaryStatus.Open || d.Status == DisciplinaryStatus.Suspended))
            .Select(d => d.AgentId)
            .Distinct()
            .ToListAsync(cancellationToken);
        return open
            .Select(r =>
            {
                var a = agents.GetValueOrDefault(r.AgentId);
                return new PromotionOverviewRow(r.Id, r.AgentId, a?.Codename ?? "—", a?.Rank, r.TargetRank,
                    r.RequesterName, r.CreatedAt, blocked.Contains(r.AgentId));
            })
            .ToList();
    }

    public async Task<IReadOnlyList<RelationshipRow>> GetRelationshipsAsync(ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireAdministrationRead(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.AgentRelationships.AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(cancellationToken);
        var names = await NamesAsync(db, rows.SelectMany(r => new[] { r.AgentAId, r.AgentBId }), cancellationToken);
        return rows
            .Select(r => new RelationshipRow(r.Id, r.AgentAId, names.GetValueOrDefault(r.AgentAId, "—"), r.AgentBId,
                names.GetValueOrDefault(r.AgentBId, "—"), r.Kind, r.Since, r.Note))
            .ToList();
    }

    public async Task<string> AddRelationshipAsync(RelationshipInput input, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireAdministrationWrite(actor);
        if (string.IsNullOrEmpty(input.AgentAId) || string.IsNullOrEmpty(input.AgentBId)
            || string.Equals(input.AgentAId, input.AgentBId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Bitte zwei verschiedene Agenten wählen.");
        }
        var note = Trimmed(input.Note, PersonalRelationRules.MaxNoteLength, "Die Notiz");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var found = await db.Users.OnlySelectable()
            .CountAsync(u => u.Id == input.AgentAId || u.Id == input.AgentBId, cancellationToken);
        if (found != 2)
        {
            throw new InvalidOperationException("Beziehungen werden nur zwischen aktiven Agenten erfasst.");
        }
        var (a, b) = PersonalRelationRules.Pair(input.AgentAId, input.AgentBId);
        if (await db.AgentRelationships.AnyAsync(r => r.AgentAId == a && r.AgentBId == b, cancellationToken))
        {
            throw new InvalidOperationException("Für diese beiden ist bereits eine Beziehung erfasst.");
        }
        var row = new Data.Entities.Administration.AgentRelationship
        {
            AgentAId = a,
            AgentBId = b,
            Kind = input.Kind,
            Since = input.Since,
            Note = note,
        };
        db.AgentRelationships.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return row.Id;
    }

    public async Task RemoveRelationshipAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireAdministrationWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.AgentRelationships.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (row is null)
        {
            return;
        }
        db.AgentRelationships.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlySet<string>> GetRelatedIdsAsync(string agentId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        // the bias of the viewer counts for everyone, the relationships of others only inside the workspace
        var meId = actor.GetAgentId();
        if (!actor.MayAdministrationRead() && !string.Equals(meId, agentId, StringComparison.OrdinalIgnoreCase))
        {
            await using var check = await dbFactory.CreateDbContextAsync(cancellationToken);
            return await PersonalRelationRules.AreRelatedAsync(check, meId, agentId, cancellationToken)
                ? new HashSet<string>([meId!])
                : new HashSet<string>();
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await PersonalRelationRules.RelatedIdsAsync(db, agentId, cancellationToken);
    }

    public async Task<IReadOnlyList<DecorationProposalRow>> GetProposalsAsync(ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireAdministrationRead(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.DecorationProposals.AsNoTracking()
            .OrderBy(p => p.Status == DecorationProposalStatus.Open ? 0 : 1)
            .ThenByDescending(p => p.CreatedAt)
            .Take(200)
            .ToListAsync(cancellationToken);
        var names = await NamesAsync(db, rows.Select(r => r.AgentId), cancellationToken);
        var decorationIds = rows.Select(r => r.DecorationId).Distinct().ToList();
        var decorationNames = await db.Decorations.IgnoreQueryFilters().AsNoTracking()
            .Where(d => decorationIds.Contains(d.Id))
            .ToDictionaryAsync(d => d.Id, d => d.Name, cancellationToken);
        return rows
            .Select(r => new DecorationProposalRow(r.Id, r.AgentId, names.GetValueOrDefault(r.AgentId, "—"), r.DecorationId,
                decorationNames.GetValueOrDefault(r.DecorationId, "—"), r.Reason, r.Status, r.ProposerName, r.CreatedAt,
                r.DecidedByName, r.DecidedAt))
            .ToList();
    }

    public async Task<string> ProposeDecorationAsync(DecorationProposalInput input, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        if (!DecorationProposalRules.MayPropose(actor, input.AgentId))
        {
            throw new UnauthorizedAccessException("Ehrungen schlägt man für andere vor, nicht für sich selbst.");
        }
        var reason = Trimmed(input.Reason, DecorationProposalRules.MaxReasonLength, "Die Begründung")
            ?? throw new InvalidOperationException("Bitte begründe den Vorschlag.");
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Users.OnlySelectable().AnyAsync(u => u.Id == input.AgentId, cancellationToken))
        {
            throw new InvalidOperationException("Vorschläge gehen nur an aktive Agenten des FIB.");
        }
        if (!await db.Decorations.AnyAsync(d => d.Id == input.DecorationId && d.IsActive, cancellationToken))
        {
            throw new InvalidOperationException("Dieses Ehrenzeichen kann nicht (mehr) verliehen werden.");
        }
        var row = new DecorationProposal
        {
            AgentId = input.AgentId,
            DecorationId = input.DecorationId,
            Reason = reason,
            ProposerName = actor.GetCodename(),
        };
        db.DecorationProposals.Add(row);
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            var recipients = await MemberIdsAsync(db, cancellationToken);
            await notifications.NotifyManyInAppAsync(recipients, NotificationType.DecorationProposed,
                "Neuer Vorschlag für ein Ehrenzeichen.", Href + "?tab=auszeichnungen", actor.GetAgentId(),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            /* best effort */
            logger.LogWarning(ex, "Bell for decoration proposal {Id} failed.", row.Id);
        }
        return row.Id;
    }

    public async Task DecideProposalAsync(string id, bool accept, string? citation, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireAdministrationWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.DecorationProposals.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Der Vorschlag wurde nicht gefunden.");
        if (row.Status != DecorationProposalStatus.Open)
        {
            throw new InvalidOperationException("Über diesen Vorschlag wurde bereits entschieden.");
        }
        if (!DecorationProposalRules.MayDecide(actor, row.AgentId))
        {
            throw new UnauthorizedAccessException("Über einen Vorschlag für dich selbst entscheidest du nicht.");
        }
        await PersonalRelationRules.RequireNotRelatedAsync(db, actor, row.AgentId, cancellationToken);

        if (accept)
        {
            // claim the row first, so two clicks cannot award twice
            var claimed = await db.DecorationProposals
                .Where(p => p.Id == id && p.Status == DecorationProposalStatus.Open)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(p => p.Status, DecorationProposalStatus.Accepted)
                    .SetProperty(p => p.DecidedByName, actor.GetCodename())
                    .SetProperty(p => p.DecidedAt, DateTime.UtcNow), cancellationToken);
            if (claimed == 0)
            {
                throw new InvalidOperationException("Über diesen Vorschlag wurde bereits entschieden.");
            }
            string awardId;
            try
            {
                awardId = await decorations.AwardAsync(new DecorationAwardInput
                {
                    AgentId = row.AgentId,
                    DecorationId = row.DecorationId,
                    Citation = string.IsNullOrWhiteSpace(citation) ? row.Reason : citation.Trim(),
                }, actor, cancellationToken);
            }
            catch
            {
                // award refused: the proposal stays open
                await db.DecorationProposals.Where(p => p.Id == id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(p => p.Status, DecorationProposalStatus.Open)
                        .SetProperty(p => p.DecidedByName, (string?)null)
                        .SetProperty(p => p.DecidedAt, (DateTime?)null), CancellationToken.None);
                throw;
            }
            await db.DecorationProposals.Where(p => p.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.AwardId, awardId), CancellationToken.None);
            db.AuditLogs.Add(ManualAudit.Row(nameof(DecorationProposal), id, AuditAction.Modified, actor,
                ManualAudit.Change("Status", "Offen", "Verliehen")));
            await db.SaveChangesAsync(CancellationToken.None);
            return;
        }

        row.Status = DecorationProposalStatus.Refused;
        row.DecidedByName = actor.GetCodename();
        row.DecidedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Division members who get the workspace bells: selectable and flagged.</summary>
    internal static async Task<List<string>> MemberIdsAsync(AppDbContext db, CancellationToken cancellationToken)
        => await db.Users.OnlySelectable().Where(u => u.IsHRB).Select(u => u.Id).ToListAsync(cancellationToken);

    internal static async Task<Dictionary<string, string>> NamesAsync(AppDbContext db, IEnumerable<string> ids,
        CancellationToken cancellationToken)
    {
        var list = ids.Where(i => !string.IsNullOrEmpty(i)).Distinct().ToList();
        if (list.Count == 0)
        {
            return new();
        }
        return await db.Users.AsNoTracking()
            .Where(u => list.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => string.IsNullOrWhiteSpace(u.Codename) ? "—" : u.Codename, cancellationToken);
    }

    internal static string? Trimmed(string? text, int max, string what)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }
        if (value.Length > max)
        {
            throw new InvalidOperationException($"{what} darf höchstens {max} Zeichen haben.");
        }
        return value;
    }
}
