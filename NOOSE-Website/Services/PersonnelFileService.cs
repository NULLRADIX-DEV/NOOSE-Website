using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IPersonalakteService" />
public class PersonnelFileService(IDbContextFactory<AppDbContext> dbFactory, IDiscordWebhookService discord)
    : IPersonnelFileService
{
    public async Task<List<AgentRankHistory>> GetRankHistoryAsync(string agentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.AgentRankHistories
            .Where(v => v.AgentId == agentId)
            .OrderByDescending(v => v.Timestamp)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<AgentNote>> GetNotesAsync(string agentId, AgentNoteKind? kind = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.AgentNotes
            .Where(v => v.AgentId == agentId && (kind == null || v.Kind == kind))
            .OrderByDescending(v => v.EntryDate)
            .ThenByDescending(v => v.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<AgentNote> NoteCreateAsync(string agentId, AgentNoteKind kind, string? artFreetext, DateTime entryDate,
        IReadOnlyCollection<string> executorAgentIds, string text, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        // write guard ahead of the rank guard: the read-only supervision and the demo visitor carry Director,
        // so the rank alone let them fill in the whole form and fail at the save
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);
        var content = NormalizeHtml(text);
        if (string.IsNullOrEmpty(content))
        {
            throw new InvalidOperationException("Der Vermerk darf nicht leer sein.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var subject = await db.Users
            .Where(u => u.Id == agentId)
            .Select(u => new { u.RealName, u.Codename })
            .FirstOrDefaultAsync(cancellationToken);
        if (subject is null)
        {
            throw new InvalidOperationException("Der gewählte Agent wurde nicht gefunden.");
        }

        // keep only executors that resolve to an existing agent, preserving picked order
        var executors = await db.Users
            .Where(u => executorAgentIds.Contains(u.Id))
            .Select(u => new { u.Id, u.RealName, u.Codename })
            .ToListAsync(cancellationToken);
        var executorIds = executorAgentIds.Where(id => executors.Any(e => e.Id == id)).Distinct().ToList();

        var freetext = string.IsNullOrWhiteSpace(artFreetext) ? null : artFreetext.Trim();
        var note = new AgentNote
        {
            AgentId = agentId,
            Kind = kind,
            ArtFreetext = freetext,
            EntryDate = entryDate,
            Text = content,
            AuthorName = actor.GetCodename(),
            Ausfuehrende = executorIds.Count > 0 ? JsonSerializer.Serialize(executorIds) : null,
        };
        db.AgentNotes.Add(note);
        await db.SaveChangesAsync(cancellationToken);

        var artLabel = freetext ?? AgentNoteKindDisplay.Name(kind);
        var subjectDisplay = string.IsNullOrWhiteSpace(subject.RealName)
            ? subject.Codename
            : $"{subject.RealName} - {subject.Codename}";
        var executorDisplays = executorIds
            .Select(id => executors.First(e => e.Id == id))
            .Select(e => string.IsNullOrWhiteSpace(e.RealName) ? e.Codename : $"{e.RealName} - {e.Codename}")
            .ToList();
        await discord.PushPersonnelEntryAsync(agentId, subjectDisplay, artLabel, entryDate,
            PlainText(content), executorDisplays, $"/personal/{agentId}", cancellationToken);

        return note;
    }

    public async Task NoteDeleteAsync(string noteId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var note = await db.AgentNotes.FirstOrDefaultAsync(v => v.Id == noteId, cancellationToken);
        if (note is null)
        {
            return;
        }
        // only author or leadership may delete
        if (!actor.IsLeadership() && note.CreatedById != actor.GetAgentId())
        {
            throw new UnauthorizedAccessException("Diesen Vermerk darf nur der Verfasser oder die Führung löschen.");
        }
        db.AgentNotes.Remove(note);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<AgentPromotionRequest>> GetPromotionRequestsAsync(string agentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.AgentPromotionRequests
            .Where(a => a.AgentId == agentId)
            .OrderByDescending(a => a.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<AgentPromotionRequest>> GetOpenPromotionRequestsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.AgentPromotionRequests
            .Where(a => a.Status == PromotionStatus.Requested)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<AgentPromotionRequest> PromotionRequestAsync(string agentId, Rank targetRank, string? justification, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // TargetRank is a NOOSE Rank; approving one for a partner would write it onto an external account
        if (!await db.Users.OnlySelectable().AnyAsync(u => u.Id == agentId, cancellationToken))
        {
            throw new InvalidOperationException("Beförderungen können nur für aktive FIB-Agents beantragt werden.");
        }
        if (await db.AgentPromotionRequests.AnyAsync(a => a.AgentId == agentId && a.Status == PromotionStatus.Requested, cancellationToken))
        {
            throw new InvalidOperationException("Für diesen Agenten ist bereits ein Beförderungsantrag offen.");
        }
        var justificationHtml = NormalizeHtml(justification);
        var request = new AgentPromotionRequest
        {
            AgentId = agentId,
            TargetRank = targetRank,
            Justification = string.IsNullOrEmpty(justificationHtml) ? null : justificationHtml,
            Status = PromotionStatus.Requested,
            RequesterName = actor.GetCodename(),
        };
        db.AgentPromotionRequests.Add(request);
        await db.SaveChangesAsync(cancellationToken);
        return request;
    }

    // Sanitize WYSIWYG HTML; an empty editor (Quill emits <p><br></p>) collapses to "".
    private static string NormalizeHtml(string? html)
    {
        var clean = HtmlCleanup.Clean(html);
        return HtmlCleanup.PlainText(clean).Length == 0 ? string.Empty : clean;
    }

    // Strip tags to plain text for the Discord embed (HTML has no place in an embed field).
    private static string PlainText(string? html) => HtmlCleanup.PlainText(HtmlCleanup.Clean(html));
}
