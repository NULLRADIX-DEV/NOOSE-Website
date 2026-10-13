using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Announcements;
using NOOSE_Website.Infrastructure.Announcements;
using NOOSE_Website.Models.Announcements;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

public class AnnouncementService(
    IDbContextFactory<AppDbContext> dbFactory,
    ICaseNumberService caseNumber,
    INotificationService notifications,
    AcknowledgmentBroadcaster acknowledgmentBroadcaster,
    ILogger<AnnouncementService> logger) : IAnnouncementService
{
    public async Task<List<AnnouncementRow>> GetBoardAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var meId = actor.GetAgentId();
        var isLeadership = actor.IsLeadership();
        var myTaskforces = await AnnouncementVisibility.MyTaskforceIdsAsync(db, meId, cancellationToken);

        var rows = await db.Announcements
            .OnlyVisible(actor, myTaskforces)
            .OrderByDescending(a => a.Important)
            .ThenByDescending(a => a.CreatedAt)
            .Select(a => new
            {
                a.Id, a.CaseNumber, a.Title, a.Content, a.Important, a.Audience, a.TargetId, a.MinRank,
                a.AsBroadcast, a.AcknowledgmentRequired, a.AcknowledgmentDueAt, a.CreatedAt, a.CreatedById,
            })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return new();
        }

        var ids = rows.Select(r => r.Id).ToList();

        // Hide team-lead/partner rows so counts and lists never show them.
        var ack = await db.AnnouncementAcknowledgments
            .Where(q => ids.Contains(q.AnnouncementId) && !q.Agent!.IsTeamLead && q.Agent!.PartnerAgency == null)
            .Select(q => new { q.AnnouncementId, q.AgentId, q.AcknowledgedAt })
            .ToListAsync(cancellationToken);
        var ackPerId = ack.GroupBy(q => q.AnnouncementId).ToDictionary(g => g.Key, g => g.ToList());

        // Never the real name.
        var creatorIds = rows.Select(r => r.CreatedById).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
        var creatorNames = creatorIds.Count == 0
            ? new Dictionary<string, string>()
            : await db.Users.Where(u => creatorIds.Contains(u.Id))
                .Select(u => new { u.Id, u.Codename }).ToDictionaryAsync(u => u.Id, u => u.Codename, cancellationToken);

        var tfIds = rows.Where(r => r.Audience == AnnouncementAudience.Taskforce && r.TargetId != null)
            .Select(r => r.TargetId!).Distinct().ToList();
        var tfNames = tfIds.Count == 0
            ? new Dictionary<string, string>()
            : await db.Taskforces.IgnoreQueryFilters().Where(t => tfIds.Contains(t.Id))
                .Select(t => new { t.Id, t.Name }).ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);

        return rows.Select(r =>
        {
            var all = ackPerId.TryGetValue(r.Id, out var qs) ? qs : new();
            var my = string.IsNullOrEmpty(meId) ? null : all.FirstOrDefault(x => x.AgentId == meId);
            return new AnnouncementRow
            {
                Id = r.Id,
                CaseNumber = r.CaseNumber,
                Title = r.Title,
                Content = r.Content,
                Important = r.Important,
                Audience = r.Audience,
                TargetDisplay = TargetDisplay(r.Audience, r.TargetId, r.MinRank, tfNames),
                AsBroadcast = r.AsBroadcast,
                AcknowledgmentRequired = r.AcknowledgmentRequired,
                AcknowledgmentDueAt = r.AcknowledgmentDueAt,
                CreatedAt = r.CreatedAt,
                CreatorCodename = r.CreatedById is not null && creatorNames.TryGetValue(r.CreatedById, out var cn) ? cn : null,
                MustAcknowledge = my is { AcknowledgedAt: null },
                AlreadyAcknowledged = my is { AcknowledgedAt: not null },
                AcknowledgedCount = all.Count(x => x.AcknowledgedAt != null),
                TotalCount = all.Count,
                MayManage = isLeadership || r.CreatedById == meId,
            };
        }).ToList();
    }

    public async Task<AnnouncementView?> GetDetailAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var a = await db.Announcements.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (a is null)
        {
            return null;
        }

        var meId = actor.GetAgentId();
        var isLeadership = actor.IsLeadership();
        var mayManage = isLeadership || a.CreatedById == meId;

        if (!mayManage
            && !await AnnouncementVisibility.IsRecipientAsync(db, a, meId, actor.IsTRU(), actor.IsHRB(), actor.GetRank(), cancellationToken))
        {
            return null;
        }

        var allAck = a.AcknowledgmentRequired
            ? await db.AnnouncementAcknowledgments
                .Where(q => q.AnnouncementId == a.Id && !q.Agent!.IsTeamLead && q.Agent!.PartnerAgency == null)
                .Select(q => new { q.AgentId, q.AcknowledgedAt, Codename = q.Agent!.Codename })
                .ToListAsync(cancellationToken)
            : new();
        var my = string.IsNullOrEmpty(meId) ? null : allAck.FirstOrDefault(x => x.AgentId == meId);

        string? creatorCodename = null;
        if (!string.IsNullOrEmpty(a.CreatedById))
        {
            creatorCodename = await db.Users.Where(u => u.Id == a.CreatedById)
                .Select(u => u.Codename).FirstOrDefaultAsync(cancellationToken);
        }

        string targetDisplay;
        if (a.Audience == AnnouncementAudience.Taskforce && a.TargetId != null)
        {
            var tfName = await db.Taskforces.IgnoreQueryFilters()
                .Where(t => t.Id == a.TargetId).Select(t => t.Name).FirstOrDefaultAsync(cancellationToken);
            targetDisplay = tfName != null ? $"Taskforce: {tfName}" : "Taskforce";
        }
        else
        {
            targetDisplay = TargetDisplay(a.Audience, a.TargetId, a.MinRank, new Dictionary<string, string>());
        }

        var row = new AnnouncementRow
        {
            Id = a.Id,
            CaseNumber = a.CaseNumber,
            Title = a.Title,
            Content = a.Content,
            Important = a.Important,
            Audience = a.Audience,
            TargetDisplay = targetDisplay,
            AsBroadcast = a.AsBroadcast,
            AcknowledgmentRequired = a.AcknowledgmentRequired,
            AcknowledgmentDueAt = a.AcknowledgmentDueAt,
            CreatedAt = a.CreatedAt,
            CreatorCodename = creatorCodename,
            MustAcknowledge = my is { AcknowledgedAt: null },
            AlreadyAcknowledged = my is { AcknowledgedAt: not null },
            AcknowledgedCount = allAck.Count(x => x.AcknowledgedAt != null),
            TotalCount = allAck.Count,
            MayManage = mayManage,
        };

        return new AnnouncementView
        {
            Row = row,
            Acknowledgments = mayManage
                ? allAck
                    .OrderBy(x => x.AcknowledgedAt == null ? 0 : 1)
                    .ThenBy(x => x.Codename)
                    .Select(x => new AcknowledgmentRow(x.Codename, x.AcknowledgedAt))
                    .ToList()
                : Array.Empty<AcknowledgmentRow>(),
        };
    }

    public async Task<List<Announcement>> GetTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Announcements.IgnoreQueryFilters()
            .Where(a => a.IsDeleted)
            .OrderByDescending(a => a.DeletedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<Announcement> CreateAsync(AnnouncementInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        // Broadcast features are leadership-only.
        var isBroadcastFeature = input.AsBroadcast
            || input.Audience != AnnouncementAudience.AllActive
            || input.AcknowledgmentRequired;
        if (isBroadcastFeature)
        {
            Permission.RequireLeadership(actor);
        }

        if (input.Audience == AnnouncementAudience.Taskforce && string.IsNullOrWhiteSpace(input.TargetId))
        {
            throw new InvalidOperationException("Bitte eine Taskforce als Zielgruppe wählen.");
        }
        if (input.Audience == AnnouncementAudience.FromRank && input.MinRank is null)
        {
            throw new InvalidOperationException("Bitte einen Mindest-Dienstgrad als Zielgruppe wählen.");
        }
        if (input.AcknowledgmentRequired && !AcknowledgmentRules.IsValid(input.AcknowledgmentDays))
        {
            throw new InvalidOperationException("Bitte eine gültige Frist für die Erinnerung wählen.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var announcement = new Announcement
        {
            CaseNumber = await caseNumber.NextAsync(db, "N", cancellationToken),
            Title = input.Title.Trim(),
            Content = NormalizeContent(input.Content),
            Important = input.Important,
            Audience = input.Audience,
            TargetId = input.Audience == AnnouncementAudience.Taskforce ? input.TargetId : null,
            MinRank = input.Audience == AnnouncementAudience.FromRank ? input.MinRank : null,
            AsBroadcast = input.AsBroadcast,
            AcknowledgmentRequired = input.AcknowledgmentRequired,
            AcknowledgmentDueAt = input.AcknowledgmentRequired
                ? AcknowledgmentRules.DueAt(DateTime.UtcNow, input.AcknowledgmentDays)
                : null,
        };
        db.Announcements.Add(announcement);
        await db.SaveChangesAsync(cancellationToken);

        // Resolve recipients only when needed.
        var creatorId = actor.GetAgentId();
        var recipient = announcement.AcknowledgmentRequired || announcement.AsBroadcast
            ? await RecipientIdsAsync(db, announcement, cancellationToken)
            : new List<string>();

        if (announcement.AcknowledgmentRequired)
        {
            // Snapshot recipients, excluding the author.
            foreach (var eid in recipient.Distinct().Where(x => x != creatorId))
            {
                db.AnnouncementAcknowledgments.Add(new AnnouncementAcknowledgment
                {
                    AnnouncementId = announcement.Id,
                    AgentId = eid,
                });
            }
            await db.SaveChangesAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);

        // Notify after commit, author excluded.
        if (announcement.AsBroadcast)
        {
            await notifications.NotifyManyAsync(recipient, NotificationType.Announcement,
                $"Neue Ankündigung: „{announcement.Title}“.", $"/brett/{announcement.Id}", creatorId, cancellationToken);
        }

        return announcement;
    }

    public async Task RefreshAsync(string id, AnnouncementInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var a = await db.Announcements.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Ankündigung '{id}' nicht gefunden.");
        RequireCreatorOrLeadership(a, actor);

        // Only content is editable; audience/push/acknowledgment are fixed after creation.
        a.Title = input.Title.Trim();
        a.Content = NormalizeContent(input.Content);
        a.Important = input.Important;
        await db.SaveChangesAsync(cancellationToken);
    }

    // Sanitize WYSIWYG HTML; an empty editor (Quill emits <p><br></p>) collapses to "" so title-only notes still read as no content.
    private static string NormalizeContent(string? html)
    {
        var clean = HtmlCleanup.Clean(html);
        var textOnly = Regex.Replace(clean, "<.*?>", string.Empty).Replace("&nbsp;", " ");
        return string.IsNullOrWhiteSpace(textOnly) ? string.Empty : clean;
    }

    public async Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var a = await db.Announcements.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Ankündigung '{id}' nicht gefunden.");
        RequireCreatorOrLeadership(a, actor);
        db.Announcements.Remove(a); // interceptor rewrites to soft-delete
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var a = await db.Announcements.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Ankündigung '{id}' nicht gefunden.");

        a.IsDeleted = false;
        a.DeletedAt = null;
        a.DeletedById = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task AcknowledgeAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var meId = actor.GetAgentId();
        if (string.IsNullOrEmpty(meId))
        {
            throw new UnauthorizedAccessException("Kein angemeldeter Agent.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.AnnouncementAcknowledgments
            .FirstOrDefaultAsync(q => q.AnnouncementId == id && q.AgentId == meId, cancellationToken)
            ?? throw new UnauthorizedAccessException("Diese Ankündigung erfordert keine Quittierung von dir.");

        if (row.AcknowledgedAt is not null)
        {
            return; // idempotent
        }
        row.AcknowledgedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        // Live-refresh the acknowledger's own NavMenu badge.
        acknowledgmentBroadcaster.Report(meId);
    }

    public async Task<int> GetOpenAcknowledgmentsCountAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var meId = actor.GetAgentId();
        if (string.IsNullOrEmpty(meId))
        {
            return 0;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await Owed(db).Where(q => q.AgentId == meId).CountAsync(cancellationToken);
    }

    public async Task<List<OpenAcknowledgment>> GetOpenAcknowledgmentsAsync(ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        var meId = actor.GetAgentId();
        if (string.IsNullOrEmpty(meId))
        {
            return new();
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // important first, then the one waiting longest
        return await Owed(db).Where(q => q.AgentId == meId)
            .OrderByDescending(q => q.Announcement!.Important)
            .ThenBy(q => q.Announcement!.CreatedAt)
            .Select(q => new OpenAcknowledgment(q.AnnouncementId, q.Announcement!.Title, q.Announcement.Important,
                q.Announcement.CreatedAt, q.Announcement.AcknowledgmentDueAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<List<OverdueAcknowledgment>> GetOverdueAcknowledgmentsAsync(ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        var meId = actor.GetAgentId();
        if (string.IsNullOrEmpty(meId))
        {
            return new();
        }
        // same gate as MayManage: leadership sees every list, an author their own
        var isLeadership = actor.IsLeadership();
        var now = DateTime.UtcNow;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await Owed(db)
            .Where(q => q.Announcement!.AcknowledgmentDueAt != null && q.Announcement.AcknowledgmentDueAt <= now
                && (isLeadership || q.Announcement.CreatedById == meId))
            .Select(q => new { q.AnnouncementId, q.Announcement!.Title, q.Announcement.AcknowledgmentDueAt, q.Agent!.Codename })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(r => r.AnnouncementId)
            .Select(g => new OverdueAcknowledgment(g.Key, g.First().Title, g.First().AcknowledgmentDueAt!.Value,
                g.Select(r => r.Codename).Order(StringComparer.CurrentCulture).ToList()))
            .OrderByDescending(o => o.DueAt)
            .ToList();
    }

    public async Task<int> RemindOverdueAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var due = await db.Announcements
            .Where(a => a.AcknowledgmentRequired && a.AcknowledgmentDueAt != null && a.AcknowledgmentDueAt <= nowUtc
                && a.AcknowledgmentRemindedAt == null)
            .OrderBy(a => a.AcknowledgmentDueAt)
            .Select(a => new { a.Id, a.Title, a.Audience })
            .ToListAsync(cancellationToken);

        var sent = 0;
        foreach (var a in due)
        {
            // a stopping host leaves the rest unclaimed for the next run
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var title = $"Bitte quittieren: „{a.Title}“ – die Frist ist abgelaufen.";
            try
            {
                // claim first, so a second instance or a retry never reminds twice; once claimed, finish it
                var claimed = await db.Announcements
                    .Where(x => x.Id == a.Id && x.AcknowledgmentRemindedAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(x => x.AcknowledgmentRemindedAt, nowUtc), CancellationToken.None);
                if (claimed == 0)
                {
                    continue;
                }
                var owing = await Owed(db).Where(q => q.AnnouncementId == a.Id)
                    .Select(q => q.AgentId)
                    .ToListAsync(CancellationToken.None);
                if (owing.Count == 0)
                {
                    continue;
                }
                var text = title.Length > 300 ? title[..297] + "…" : title;
                if (a.Audience == AnnouncementAudience.Taskforce)
                {
                    // a shared channel would show who sits in the taskforce together
                    await notifications.NotifyManyInAppAsync(owing, NotificationType.AcknowledgmentReminder, text,
                        $"/brett/{a.Id}", triggerId: null, CancellationToken.None);
                }
                else
                {
                    await notifications.NotifyManyAsync(owing, NotificationType.AcknowledgmentReminder, text,
                        $"/brett/{a.Id}", triggerId: null, CancellationToken.None);
                }
                sent++;
            }
            catch (Exception ex)
            {
                // a spent claim is not retried; log this one and keep the batch going
                logger.LogError(ex, "Acknowledgment reminder for {Id} failed.", a.Id);
            }
        }
        return sent;
    }

    /// <summary>Open acknowledgments someone still owes: a selectable agent who can still read the announcement.</summary>
    /// <remarks>Badge, open list, reminder and overdue list all read this, so they never disagree. A recipient who left
    /// the audience (another taskforce, lost TRU) no longer owes it: the board hides the entry from them.</remarks>
    private static IQueryable<AnnouncementAcknowledgment> Owed(AppDbContext db)
    {
        var leadershipFloor = Rank.SupervisorySpecialAgent;
        // the reference nav forces the join, so the announcement's soft-delete filter hides trashed ones
        return db.AnnouncementAcknowledgments
            .Where(q => q.AcknowledgedAt == null && q.Announcement!.AcknowledgmentRequired)
            .Join(db.Users.OnlySelectable(), q => q.AgentId, u => u.Id, (q, u) => new { q, u })
            // mirrors AnnouncementVisibility.OnlyVisible for that agent; the author never holds a row
            .Where(x => x.u.IsAdmin || x.u.Rank >= leadershipFloor
                || x.q.Announcement!.Audience == AnnouncementAudience.AllActive
                || (x.q.Announcement.Audience == AnnouncementAudience.TruUnit && x.u.IsTRU)
                || (x.q.Announcement.Audience == AnnouncementAudience.HrbUnit && x.u.IsHRB)
                || (x.q.Announcement.Audience == AnnouncementAudience.FromRank && x.u.Rank != null
                    && x.q.Announcement.MinRank != null && x.u.Rank >= x.q.Announcement.MinRank)
                || (x.q.Announcement.Audience == AnnouncementAudience.Taskforce && x.q.Announcement.TargetId != null
                    && db.TaskforceAgents.Any(ta => ta.TaskforceId == x.q.Announcement.TargetId && ta.AgentId == x.u.Id)))
            .Select(x => x.q);
    }

    /// <summary>Active agent ids in an announcement's audience.</summary>
    private static async Task<List<string>> RecipientIdsAsync(AppDbContext db, Announcement a, CancellationToken cancellationToken)
    {
        // Team leads (read-only supervision) and external partners are never acknowledgment recipients.
        var query = db.Users.OnlySelectable();
        query = a.Audience switch
        {
            AnnouncementAudience.TruUnit => query.Where(u => u.IsTRU),
            AnnouncementAudience.HrbUnit => query.Where(u => u.IsHRB),
            AnnouncementAudience.FromRank => query.Where(u => u.Rank != null && u.Rank >= a.MinRank),
            AnnouncementAudience.Taskforce => query.Where(u => db.TaskforceAgents.Any(ta => ta.TaskforceId == a.TargetId && ta.AgentId == u.Id)),
            _ => query,
        };
        return await query.Select(u => u.Id).ToListAsync(cancellationToken);
    }

    private static string TargetDisplay(AnnouncementAudience audience, string? targetId, Rank? minRank,
        IReadOnlyDictionary<string, string> taskforceNames) => audience switch
    {
        AnnouncementAudience.AllActive => "Alle aktiven Agenten",
        AnnouncementAudience.Taskforce => targetId != null && taskforceNames.TryGetValue(targetId, out var n)
            ? $"Taskforce: {n}" : "Taskforce",
        AnnouncementAudience.TruUnit => "TRU-Einheit",
        AnnouncementAudience.HrbUnit => "HRB-Einheit",
        AnnouncementAudience.FromRank => $"Ab {RankDisplay.Name(minRank)}",
        _ => "—",
    };

    private static void RequireCreatorOrLeadership(Announcement a, ClaimsPrincipal actor)
    {
        if (actor.IsLeadership())
        {
            return;
        }
        var meId = actor.GetAgentId();
        if (!string.IsNullOrEmpty(meId) && a.CreatedById == meId)
        {
            return;
        }
        throw new UnauthorizedAccessException("Diese Ankündigung darf nur ihr Verfasser oder die Führung bearbeiten.");
    }
}
