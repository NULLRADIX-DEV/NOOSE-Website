using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Administration;
using NOOSE_Website.Models.Administration;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services.Administration;

/// <summary>Parliamentary inquiries: filed by parliament or recorded by the Administration, answered by the Administration.</summary>
public interface IParliamentService
{
    Task<IReadOnlyList<ParliamentRow>> GetListAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<ParliamentView?> GetAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<string> FileAsync(ParliamentInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task AssignAsync(string id, string? agentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Saves the answer; <paramref name="publish"/> releases it to parliament and closes the inquiry.</summary>
    Task AnswerAsync(string id, string answer, bool publish, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task WithdrawAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<List<ParliamentaryInquiry>> GetTrashAsync(CancellationToken cancellationToken = default);

    Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IParliamentService" />
public class ParliamentService(IDbContextFactory<AppDbContext> dbFactory, ICaseNumberService caseNumbers,
    INotificationService notifications, ILogger<ParliamentService> logger) : IParliamentService
{
    public const string CaseNumberPrefix = "PA";

    public static string InternalHref(string id) => $"/administration/parlament/{id}";

    public static string PartnerHref(string id) => $"/parlament/{id}";

    public async Task<IReadOnlyList<ParliamentRow>> GetListAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var scope = ViewerScope.From(actor);
        if (!ParliamentRules.MaySee(scope))
        {
            return [];
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.ParliamentaryInquiries.AsNoTracking()
            .OrderBy(p => p.Status == ParliamentaryInquiryStatus.Answered || p.Status == ParliamentaryInquiryStatus.Withdrawn ? 1 : 0)
            .ThenBy(p => p.DueAt)
            .Take(300)
            .ToListAsync(cancellationToken);
        var names = scope.IsPartner
            ? new Dictionary<string, string>()
            : await AdministrationService.NamesAsync(db, rows.Select(r => r.AssignedToId ?? string.Empty), cancellationToken);
        var now = DateTime.UtcNow;
        return rows
            .Select(p => new ParliamentRow(p.Id, p.CaseNumber, p.Title, p.Status, p.ReceivedAt, p.DueAt, p.SubmitterName,
                p.AssignedToId is { } a ? names.GetValueOrDefault(a) : null, p.AnsweredAt,
                p.DueAt < now && p.Status is ParliamentaryInquiryStatus.Received or ParliamentaryInquiryStatus.InProgress))
            .ToList();
    }

    public async Task<ParliamentView?> GetAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var scope = ViewerScope.From(actor);
        if (!ParliamentRules.MaySee(scope))
        {
            return null;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.ParliamentaryInquiries.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (row is null)
        {
            return null;
        }
        if (!ParliamentRules.MaySeeAnswer(scope, row.Status))
        {
            row.Answer = null;
            row.AnsweredByName = null;
        }
        string? assigned = null;
        if (!scope.IsPartner && row.AssignedToId is { } a)
        {
            assigned = (await AdministrationService.NamesAsync(db, [a], cancellationToken)).GetValueOrDefault(a);
        }
        if (scope.IsPartner)
        {
            row.AssignedToId = null;
        }
        return new ParliamentView(row, assigned, actor.MayAdministrationWrite());
    }

    public async Task<string> FileAsync(ParliamentInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        if (!ParliamentRules.MayFile(actor))
        {
            throw new UnauthorizedAccessException("Anfragen stellt das Parlament, erfasst die Administration.");
        }
        var title = AdministrationService.Trimmed(input.Title, ParliamentRules.MaxTitleLength, "Der Titel")
            ?? throw new InvalidOperationException("Bitte einen Titel angeben.");
        var question = AdministrationService.Trimmed(input.Question, ParliamentRules.MaxTextLength, "Die Anfrage")
            ?? throw new InvalidOperationException("Bitte die Anfrage formulieren.");
        var fromParliament = ParliamentRules.IsParliament(actor);
        var now = DateTime.UtcNow;
        var due = fromParliament || input.DueAt is null
            ? now.AddDays(ParliamentRules.DefaultDueDays)
            : DateTime.SpecifyKind(input.DueAt.Value, DateTimeKind.Utc);
        if (due < now.AddHours(-1))
        {
            throw new InvalidOperationException("Die Frist liegt in der Vergangenheit.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = new ParliamentaryInquiry
        {
            Title = title,
            Question = question,
            SubmitterName = actor.GetCodename(),
            FromParliament = fromParliament,
            ReceivedAt = now,
            DueAt = due,
        };
        await using (var tx = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            row.CaseNumber = await caseNumbers.NextAsync(db, CaseNumberPrefix, cancellationToken);
            db.ParliamentaryInquiries.Add(row);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
        }

        try
        {
            var recipients = await AdministrationService.MemberIdsAsync(db, cancellationToken);
            await notifications.NotifyManyInAppAsync(recipients, NotificationType.ParliamentInquiryReceived,
                $"Neue parlamentarische Anfrage {row.CaseNumber}.", InternalHref(row.Id), actor.GetAgentId(),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            /* best effort */
            logger.LogWarning(ex, "Bell for inquiry {Id} failed.", row.Id);
        }
        return row.Id;
    }

    public async Task AssignAsync(string id, string? agentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireAdministrationWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await OpenAsync(db, id, cancellationToken);
        if (!string.IsNullOrEmpty(agentId)
            && !await db.Users.OnlySelectable().AnyAsync(u => u.Id == agentId && (u.IsHRB || u.Rank >= DivisionRules.BoardFloor), cancellationToken))
        {
            throw new InvalidOperationException("Bearbeiten kann nur ein Mitglied der Administration oder des Boards.");
        }
        row.AssignedToId = string.IsNullOrEmpty(agentId) ? null : agentId;
        if (row.Status == ParliamentaryInquiryStatus.Received && row.AssignedToId is not null)
        {
            row.Status = ParliamentaryInquiryStatus.InProgress;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AnswerAsync(string id, string answer, bool publish, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireAdministrationWrite(actor);
        var text = AdministrationService.Trimmed(answer, ParliamentRules.MaxTextLength, "Die Antwort");
        if (publish && text is null)
        {
            throw new InvalidOperationException("Eine leere Antwort lässt sich nicht freigeben.");
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await OpenAsync(db, id, cancellationToken);
        row.Answer = text;
        if (row.Status == ParliamentaryInquiryStatus.Received)
        {
            row.Status = ParliamentaryInquiryStatus.InProgress;
        }
        if (publish)
        {
            row.Status = ParliamentaryInquiryStatus.Answered;
            row.AnsweredAt = DateTime.UtcNow;
            row.AnsweredByName = actor.GetCodename();
        }
        await db.SaveChangesAsync(cancellationToken);

        if (!publish)
        {
            return;
        }
        try
        {
            var parliament = await db.Users.AsNoTracking()
                .Where(u => u.PartnerAgency == PartnerAgency.Parlament && u.Status == AgentStatus.Active)
                .Select(u => u.Id)
                .ToListAsync(cancellationToken);
            await notifications.NotifyManyInAppAsync(parliament, NotificationType.ParliamentInquiryAnswered,
                $"Die Anfrage {row.CaseNumber} ist beantwortet.", PartnerHref(row.Id), actor.GetAgentId(),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            /* best effort */
            logger.LogWarning(ex, "Bell for answered inquiry {Id} failed.", row.Id);
        }
    }

    public async Task WithdrawAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireAdministrationWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await OpenAsync(db, id, cancellationToken);
        row.Status = ParliamentaryInquiryStatus.Withdrawn;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireAdministrationWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.ParliamentaryInquiries.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (row is null)
        {
            return;
        }
        db.ParliamentaryInquiries.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<ParliamentaryInquiry>> GetTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.ParliamentaryInquiries.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.IsDeleted)
            .OrderByDescending(p => p.DeletedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireAdministrationWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.ParliamentaryInquiries.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == id && p.IsDeleted, cancellationToken);
        if (row is null)
        {
            return;
        }
        row.IsDeleted = false;
        row.DeletedAt = null;
        row.DeletedById = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<ParliamentaryInquiry> OpenAsync(AppDbContext db, string id, CancellationToken cancellationToken)
    {
        var row = await db.ParliamentaryInquiries.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Die Anfrage wurde nicht gefunden.");
        if (row.Status is ParliamentaryInquiryStatus.Answered or ParliamentaryInquiryStatus.Withdrawn)
        {
            throw new InvalidOperationException("Diese Anfrage ist bereits abgeschlossen.");
        }
        return row;
    }
}
