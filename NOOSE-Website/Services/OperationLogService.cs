using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Infrastructure.Chat;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IOperationLogService" />
public class OperationLogService(IDbContextFactory<AppDbContext> dbFactory, OperationLogBroadcaster broadcaster,
    INotificationService notifications) : IOperationLogService
{
    public async Task<List<OperationLogEntry>> GetEntriesAsync(string operationId, ViewerScope scope,
        CancellationToken cancellationToken = default)
    {
        // internal working log: never released to partners
        if (scope.PartnerAgency is not null)
        {
            return new();
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await Visibility.IsRecordVisibleAsync(db, nameof(Operation), operationId, scope, cancellationToken))
        {
            return new();
        }
        return await db.OperationLogEntries.AsNoTracking()
            .Where(e => e.OperationId == operationId)
            .OrderBy(e => e.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<OperationLogAccess> GetAccessAsync(string operationId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        if (!actor.MayWrite() || !actor.IsInternalAgent())
        {
            return OperationLogAccess.None;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var operation = await LoadVisibleAsync(db, operationId, actor, cancellationToken);
        if (operation is null)
        {
            return OperationLogAccess.None;
        }
        var may = OperationLogRules.IsOpen(operation.Status)
            && OperationLogRules.MayWrite(actor, await IsAssignedAsync(db, operationId, actor, cancellationToken));
        return new OperationLogAccess(may, OperationLogRules.IsAddendum(operation.Status), operation.Status);
    }

    public async Task<OperationLogEntry> AddAsync(string operationId, OperationLogKind kind, string text, string? location,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        var content = text?.Trim();
        if (string.IsNullOrEmpty(content))
        {
            throw new InvalidOperationException("Die Meldung darf nicht leer sein.");
        }
        if (content.Length > OperationLogRules.MaxText)
        {
            throw new InvalidOperationException($"Eine Meldung darf höchstens {OperationLogRules.MaxText} Zeichen lang sein.");
        }
        if (!Enum.IsDefined(kind))
        {
            throw new InvalidOperationException("Unbekannte Art der Meldung.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var operation = await LoadVisibleAsync(db, operationId, actor, cancellationToken)
            ?? throw new UnauthorizedAccessException("Diese Operation ist für dich nicht zugänglich.");
        Permission.RequireMaySeeClassified(actor, operation.SecrecyLevel);
        if (!OperationLogRules.IsOpen(operation.Status))
        {
            throw new InvalidOperationException("Das Einsatztagebuch öffnet, sobald die Operation läuft.");
        }
        if (!OperationLogRules.MayWrite(actor, await IsAssignedAsync(db, operationId, actor, cancellationToken)))
        {
            throw new UnauthorizedAccessException("Ins Einsatztagebuch schreiben Beteiligte, das HRT und die Führung.");
        }

        var entry = new OperationLogEntry
        {
            OperationId = operationId,
            Kind = kind,
            Text = content,
            Location = Truncate(location.TrimToNull(), OperationLogRules.MaxLocation),
            IsAddendum = OperationLogRules.IsAddendum(operation.Status),
            AuthorName = actor.GetCodename(),
        };
        db.OperationLogEntries.Add(entry);
        await db.SaveChangesAsync(cancellationToken);

        broadcaster.Report(operationId);

        try
        {
            var who = string.IsNullOrWhiteSpace(actor.GetCodename()) ? "Ein Agent" : actor.GetCodename();
            await notifications.NotifyMentionedAsync(content, $"{who} hat dich im Einsatztagebuch erwähnt.",
                $"/operationen/{operationId}", nameof(Operation), operationId, actor, cancellationToken);
        }
        catch { /* best effort */ }

        return entry;
    }

    public async Task DeleteAsync(string entryId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.OperationLogEntries.FirstOrDefaultAsync(e => e.Id == entryId, cancellationToken);
        if (entry is null)
        {
            return;
        }
        if (!actor.IsLeadership() && entry.CreatedById != actor.GetAgentId())
        {
            throw new UnauthorizedAccessException("Nur der Autor oder die Führung kann eine Meldung zurückziehen.");
        }
        var operation = await LoadVisibleAsync(db, entry.OperationId, actor, cancellationToken)
            ?? throw new UnauthorizedAccessException("Diese Operation ist für dich nicht zugänglich.");
        Permission.RequireMaySeeClassified(actor, operation.SecrecyLevel);

        db.OperationLogEntries.Remove(entry);
        await db.SaveChangesAsync(cancellationToken);

        broadcaster.Report(entry.OperationId);
    }

    public async Task ApplyToExpiryAsync(string operationId, bool append, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        // same gate as editing the operation, since that is what this does
        Permission.RequireWriteAccess(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var operation = await LoadVisibleAsync(db, operationId, actor, cancellationToken)
            ?? throw new UnauthorizedAccessException("Diese Operation ist für dich nicht zugänglich.");
        Permission.RequireMaySeeClassified(actor, operation.SecrecyLevel);

        var entries = await db.OperationLogEntries.AsNoTracking()
            .Where(e => e.OperationId == operationId)
            .ToListAsync(cancellationToken);
        if (entries.Count == 0)
        {
            throw new InvalidOperationException("Das Einsatztagebuch ist leer.");
        }
        var merged = OperationLogRules.Merge(operation.Expiry, OperationLogRules.ToExpiryText(entries), append);
        if (merged.Length > OperationLogRules.MaxExpiry)
        {
            throw new InvalidOperationException(
                $"Der Ablauf würde {merged.Length} Zeichen lang, erlaubt sind {OperationLogRules.MaxExpiry}. Ersetze ihn statt anzuhängen oder kürze das Tagebuch.");
        }
        operation.Expiry = merged;
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<Operation?> LoadVisibleAsync(AppDbContext db, string operationId, ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var operation = await db.Operations.FirstOrDefaultAsync(o => o.Id == operationId, cancellationToken);
        if (operation is null
            || !await Visibility.IsRecordVisibleAsync(db, nameof(Operation), operationId, ViewerScope.From(actor), cancellationToken))
        {
            return null;
        }
        return operation;
    }

    private static Task<bool> IsAssignedAsync(AppDbContext db, string operationId, ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var meId = actor.GetAgentId();
        return string.IsNullOrEmpty(meId)
            ? Task.FromResult(false)
            : db.OperationAgents.AnyAsync(a => a.OperationId == operationId && a.AgentId == meId, cancellationToken);
    }

    private static string? Truncate(string? value, int max)
        => value is { Length: var n } && n > max ? value[..max] : value;
}
