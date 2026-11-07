using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Directives;
using NOOSE_Website.Infrastructure.Announcements;
using NOOSE_Website.Models.Directives;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IDirectiveService" />
public class DirectiveService(
    IDbContextFactory<AppDbContext> dbFactory,
    INotificationService notifications,
    AcknowledgmentBroadcaster acknowledgmentBroadcaster,
    ILogger<DirectiveService> logger) : IDirectiveService
{
    private const string NotFound = "Dienstanweisung nicht gefunden.";
    private const string Stale = "Inzwischen hat jemand eine neuere Fassung veröffentlicht. Lade die Seite neu.";

    /// <summary>The cleaned, validated wording of a version.</summary>
    private sealed record Wording(string Title, string? Subject, string ChangeNote, string Html);

    // ---- reads ----

    public async Task<List<DirectiveRow>> GetListAsync(ClaimsPrincipal actor, bool repealed,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Directives.AsNoTracking().OnlyVisible(ViewerScope.From(actor));
        query = repealed ? query.Where(d => d.RepealedAt != null) : query.Where(d => d.RepealedAt == null);
        var heads = await query.OrderBy(d => d.Number).ToListAsync(cancellationToken);
        if (heads.Count == 0)
        {
            return new();
        }

        var ids = heads.Select(d => d.Id).ToList();
        // flat query
        var versions = await db.DirectiveVersions.AsNoTracking()
            .Where(v => ids.Contains(v.DirectiveId))
            .Select(v => new { v.DirectiveId, v.Version, v.ValidFrom })
            .ToListAsync(cancellationToken);
        var byDirective = versions.ToLookup(v => v.DirectiveId);
        var owed = await OwedDirectiveIdsAsync(db, actor.GetAgentId(), cancellationToken);
        var today = MeetingTime.Day(DateTime.UtcNow);

        return heads.Select(d =>
        {
            var own = byDirective[d.Id].Select(v => (v.Version, v.ValidFrom)).ToList();
            var validFrom = own.Where(v => v.Version == d.CurrentVersion).Select(v => v.ValidFrom).FirstOrDefault();
            return ToRow(d, validFrom, DirectiveRules.ValidVersion(own, today), owed.Contains(d.Id));
        }).ToList();
    }

    public async Task<DirectiveView?> GetDetailAsync(string id, int? version, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var head = await db.Directives.AsNoTracking().OnlyVisible(ViewerScope.From(actor))
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (head is null)
        {
            return null;
        }

        var rows = await db.DirectiveVersions.AsNoTracking()
            .Where(v => v.DirectiveId == id)
            .OrderByDescending(v => v.Version)
            .Select(v => new { v.Version, v.ValidFrom, v.ChangeNote, v.RequiresAcknowledgment, v.PublishedByName, v.PublishedAt })
            .ToListAsync(cancellationToken);
        var today = MeetingTime.Day(DateTime.UtcNow);
        var validVersion = DirectiveRules.ValidVersion(rows.Select(v => (v.Version, v.ValidFrom)).ToList(), today);
        var shown = version ?? validVersion ?? head.CurrentVersion;
        if (rows.All(v => v.Version != shown))
        {
            return null;
        }
        var shownHtml = await db.DirectiveVersions.AsNoTracking()
            .Where(v => v.DirectiveId == id && v.Version == shown)
            .Select(v => v.ContentHtml)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        var meId = actor.GetAgentId();
        var mustAcknowledge = !string.IsNullOrEmpty(meId)
            && await DirectiveDebt.Owed(db).AnyAsync(r => r.AgentId == meId && r.DirectiveId == id, cancellationToken);
        var mine = string.IsNullOrEmpty(meId)
            ? null
            : await db.DirectiveAcknowledgments.AsNoTracking()
                .Where(a => a.DirectiveId == id && a.AgentId == meId)
                .OrderByDescending(a => a.Version)
                .Select(a => new { a.Version, a.AcknowledgedAt })
                .FirstOrDefaultAsync(cancellationToken);

        var currentFrom = rows.Where(v => v.Version == head.CurrentVersion).Select(v => v.ValidFrom).FirstOrDefault();
        return new DirectiveView(
            ToRow(head, currentFrom, validVersion, mustAcknowledge),
            shown,
            shownHtml,
            rows.Select(v => new DirectiveVersionView(v.Version, v.ValidFrom, v.ChangeNote, v.RequiresAcknowledgment,
                v.PublishedByName, v.PublishedAt)).ToList(),
            mustAcknowledge,
            mine?.Version,
            mine?.AcknowledgedAt,
            head.RepealedByName,
            head.RepealReason,
            DirectiveRules.MayManage(actor),
            DirectiveRules.MaySeeQuota(actor));
    }

    public async Task<List<Directive>> GetTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Directives.AsNoTracking().IgnoreQueryFilters()
            .Where(d => d.IsDeleted)
            .OrderByDescending(d => d.DeletedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<int> GetOpenCountAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var meId = actor.GetAgentId();
        if (string.IsNullOrEmpty(meId))
        {
            return 0;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await DirectiveDebt.Owed(db).Where(r => r.AgentId == meId).CountAsync(cancellationToken);
    }

    public async Task<List<OpenDirective>> GetOpenForMeAsync(ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        var meId = actor.GetAgentId();
        if (string.IsNullOrEmpty(meId))
        {
            return new();
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // oldest first
        var rows = await DirectiveDebt.OpenFor(db, meId)
            .OrderBy(r => r.Since).ThenBy(r => r.Number)
            .ToListAsync(cancellationToken);
        return rows.Select(r => new OpenDirective(r.DirectiveId, DirectiveRules.Label(r.Number), r.Title, r.Version, r.Since))
            .ToList();
    }

    public async Task<DirectiveQuota?> GetQuotaAsync(string id, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        if (!DirectiveRules.MaySeeQuota(actor))
        {
            return null;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var acknowledgeFrom = await db.Directives.AsNoTracking().OnlyVisible(ViewerScope.From(actor))
            .Where(d => d.Id == id)
            .Select(d => (int?)d.AcknowledgeFromVersion)
            .FirstOrDefaultAsync(cancellationToken);
        if (acknowledgeFrom is null)
        {
            return null;
        }

        // merged in memory
        var audience = await (from r in DirectiveDebt.Audience(db).Where(r => r.DirectiveId == id)
                              join u in db.Users on r.AgentId equals u.Id
                              select new { r.AgentId, u.Codename }).ToListAsync(cancellationToken);
        var missingIds = (await DirectiveDebt.Owed(db).Where(r => r.DirectiveId == id).Select(r => r.AgentId)
            .ToListAsync(cancellationToken)).ToHashSet();
        var highest = (await DirectiveDebt.AcknowledgmentsOf(db, id).ToListAsync(cancellationToken))
            .GroupBy(a => a.AgentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.Version).First());

        DirectiveAudienceRow AudienceRow(string agentId, string codename)
            => highest.TryGetValue(agentId, out var ack)
                ? new DirectiveAudienceRow(agentId, codename, ack.Version, ack.AcknowledgedAt)
                : new DirectiveAudienceRow(agentId, codename, null, null);

        var missing = audience.Where(a => missingIds.Contains(a.AgentId)).Select(a => AudienceRow(a.AgentId, a.Codename))
            .OrderBy(a => a.Codename, StringComparer.CurrentCultureIgnoreCase).ToList();
        var done = audience.Where(a => !missingIds.Contains(a.AgentId)).Select(a => AudienceRow(a.AgentId, a.Codename))
            .OrderBy(a => a.Codename, StringComparer.CurrentCultureIgnoreCase).ToList();
        return new DirectiveQuota(audience.Count, audience.Count - missing.Count, acknowledgeFrom.Value, missing, done);
    }

    public async Task<DirectiveComparison?> CompareAsync(string id, int fromVersion, int toVersion, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        var older = Math.Min(fromVersion, toVersion);
        var newer = Math.Max(fromVersion, toVersion);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await DirectiveVisibility.IsVisibleAsync(db, id, ViewerScope.From(actor), cancellationToken))
        {
            return null;
        }
        var texts = await db.DirectiveVersions.AsNoTracking()
            .Where(v => v.DirectiveId == id && (v.Version == older || v.Version == newer))
            .Select(v => new { v.Version, v.ContentHtml })
            .ToListAsync(cancellationToken);
        var oldText = texts.FirstOrDefault(t => t.Version == older);
        var newText = texts.FirstOrDefault(t => t.Version == newer);
        if (oldText is null || newText is null)
        {
            return null;
        }
        return new DirectiveComparison(older, newer, HtmlDiff.Compare(oldText.ContentHtml, newText.ContentHtml));
    }

    public async Task<List<DirectivePick>> SearchAsync(string? text, ClaimsPrincipal actor, int take = 20,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Directives.AsNoTracking().OnlyVisible(ViewerScope.From(actor)).Matching(text)
            .OrderBy(d => d.Number)
            .Take(Math.Clamp(take, 1, 100))
            .Select(d => new { d.Id, d.Number, d.Title })
            .ToListAsync(cancellationToken);
        return rows.Select(d => new DirectivePick(d.Id, DirectiveRules.Label(d.Number), d.Title)).ToList();
    }

    // ---- writes ----

    public async Task<string> CreateAsync(DirectiveInput input, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireDirectiveWrite(actor);
        Permission.RequireMayAssignClassification(actor, input.Classification);
        var wording = CleanWording(input.Title, input.Subject, input.ChangeNote, input.ContentHtml);
        var actorId = ActorId(actor);

        for (var attempt = 1; ; attempt++)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            // never reissued
            var number = (await db.Directives.IgnoreQueryFilters().MaxAsync(d => (int?)d.Number, cancellationToken) ?? 0) + 1;
            var now = DateTime.UtcNow;
            var directive = new Directive
            {
                Number = number,
                Title = wording.Title,
                Subject = wording.Subject,
                Classification = input.Classification,
                CurrentVersion = 1,
                AcknowledgeFromVersion = 1,
            };
            db.Directives.Add(directive);
            db.DirectiveVersions.Add(NewVersion(directive.Id, 1, wording, input.ValidFrom, true, actor, now));
            // publisher owes nothing
            db.DirectiveAcknowledgments.Add(NewAcknowledgment(directive.Id, 1, actorId, now));
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException) when (attempt == 1)
            {
                // number taken
                continue;
            }

            await AnnounceAsync(db, directive.Id, $"Neue Dienstanweisung {DirectiveRules.Label(number)}",
                [], ringAll: true, actorId, cancellationToken);
            return directive.Id;
        }
    }

    public async Task PublishVersionAsync(string id, DirectiveVersionInput input, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireDirectiveWrite(actor);
        Permission.RequireMayAssignClassification(actor, input.Classification);
        var wording = CleanWording(input.Title, input.Subject, input.ChangeNote, input.ContentHtml);
        var actorId = ActorId(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var directive = await FindVisibleAsync(db, id, actor, cancellationToken);
        if (directive.RepealedAt is not null)
        {
            throw new InvalidOperationException("Eine aufgehobene Dienstanweisung bekommt keine neue Fassung.");
        }
        if (input.ExpectedVersion != directive.CurrentVersion)
        {
            throw new InvalidOperationException(Stale);
        }
        var previousFrom = await db.DirectiveVersions
            .Where(v => v.DirectiveId == id && v.Version == directive.CurrentVersion)
            .Select(v => (DateOnly?)v.ValidFrom)
            .FirstOrDefaultAsync(cancellationToken);
        if (!DirectiveRules.IsValidFromAllowed(input.ValidFrom, previousFrom))
        {
            throw new InvalidOperationException("„Gültig ab“ darf nicht vor dem Datum der vorigen Fassung liegen.");
        }

        var oldOwed = await OwedAgentsAsync(db, id, cancellationToken);
        // reason row, same save
        CriticalChange.NoteSecrecy(db, nameof(Directive), id, directive.Classification, input.Classification,
            input.SecrecyReason, actor);

        var next = directive.CurrentVersion + 1;
        var now = DateTime.UtcNow;
        directive.Title = wording.Title;
        directive.Subject = wording.Subject;
        directive.Classification = input.Classification;
        directive.CurrentVersion = next;
        directive.AcknowledgeFromVersion = DirectiveRules.NextAcknowledgeFrom(
            directive.AcknowledgeFromVersion, next, input.RequiresAcknowledgment);
        db.DirectiveVersions.Add(NewVersion(id, next, wording, input.ValidFrom, input.RequiresAcknowledgment, actor, now));
        db.DirectiveAcknowledgments.Add(NewAcknowledgment(id, next, actorId, now));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // someone published first
            throw new InvalidOperationException(Stale, ex);
        }

        await AnnounceAsync(db, id, $"Neue Fassung von {DirectiveRules.Label(directive.Number)}",
            oldOwed, ringAll: input.RequiresAcknowledgment, actorId, cancellationToken);
    }

    public async Task AcknowledgeAsync(string id, int version, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        if (!DirectiveRules.MayAcknowledge(actor))
        {
            throw new UnauthorizedAccessException("Dienstanweisungen nehmen nur interne Agenten zur Kenntnis.");
        }
        var meId = ActorId(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var directive = await FindVisibleAsync(db, id, actor, cancellationToken);
        if (directive.RepealedAt is not null)
        {
            throw new InvalidOperationException("Diese Dienstanweisung ist aufgehoben.");
        }
        // fresh DB check
        if (!await db.Users.OnlySelectable().AnyAsync(u => u.Id == meId, cancellationToken))
        {
            throw new UnauthorizedAccessException("Dein Konto darf Dienstanweisungen nicht zur Kenntnis nehmen.");
        }
        if (version != directive.CurrentVersion)
        {
            throw new InvalidOperationException(
                "Inzwischen gibt es eine neuere Fassung. Lies sie und nimm sie dann zur Kenntnis.");
        }

        if (await HasAcknowledgedAsync(db, id, meId, version, cancellationToken))
        {
            return;
        }
        db.DirectiveAcknowledgments.Add(NewAcknowledgment(id, version, meId, DateTime.UtcNow));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // double click
            db.ChangeTracker.Clear();
            if (!await HasAcknowledgedAsync(db, id, meId, version, cancellationToken))
            {
                throw;
            }
            return;
        }

        acknowledgmentBroadcaster.Report(meId);
    }

    public async Task RepealAsync(string id, string reason, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireDirectiveWrite(actor);
        var note = CriticalChange.RequireReason(reason, "Das Aufheben einer Dienstanweisung");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var directive = await FindVisibleAsync(db, id, actor, cancellationToken);
        if (directive.RepealedAt is not null)
        {
            throw new InvalidOperationException("Diese Dienstanweisung ist schon aufgehoben.");
        }

        var owed = await OwedAgentsAsync(db, id, cancellationToken);
        directive.RepealedAt = DateTime.UtcNow;
        directive.RepealedById = actor.GetAgentId();
        directive.RepealedByName = actor.GetCodename();
        directive.RepealReason = note;
        await SaveHeadAsync(db, cancellationToken);

        Report(owed);
    }

    public async Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireDirectiveWrite(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var directive = await FindVisibleAsync(db, id, actor, cancellationToken);

        var owed = await OwedAgentsAsync(db, id, cancellationToken);
        // soft delete
        db.Directives.Remove(directive);
        await SaveHeadAsync(db, cancellationToken);

        Report(owed);
    }

    public async Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireDirectiveWrite(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var directive = await db.Directives.IgnoreQueryFilters().OnlyVisible(ViewerScope.From(actor))
            .FirstOrDefaultAsync(d => d.Id == id && d.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException(NotFound);

        directive.IsDeleted = false;
        directive.DeletedAt = null;
        directive.DeletedById = null;
        await SaveHeadAsync(db, cancellationToken);

        Report(await OwedAgentsAsync(db, id, cancellationToken));
    }

    // ---- helpers ----

    private static string ActorId(ClaimsPrincipal actor)
        => actor.GetAgentId() is { Length: > 0 } id ? id : throw new UnauthorizedAccessException("Kein angemeldeter Agent.");

    /// <summary>Loads a tracked, visible, undeleted directive; missing and hidden answer alike.</summary>
    private static async Task<Directive> FindVisibleAsync(AppDbContext db, string id, ClaimsPrincipal actor,
        CancellationToken cancellationToken)
        => await db.Directives.OnlyVisible(ViewerScope.From(actor)).FirstOrDefaultAsync(d => d.Id == id, cancellationToken)
            ?? throw new InvalidOperationException(NotFound);

    private static DirectiveRow ToRow(Directive d, DateOnly validFrom, int? validVersion, bool mustAcknowledge)
        => new(d.Id, d.Number, DirectiveRules.Label(d.Number), d.Title, d.Subject, d.Classification, d.CurrentVersion,
            d.AcknowledgeFromVersion, validFrom, validVersion, d.RepealedAt is not null, d.RepealedAt, mustAcknowledge);

    private static Wording CleanWording(string? title, string? subject, string? changeNote, string? contentHtml)
    {
        var cleanTitle = title.TrimToNull() ?? throw new InvalidOperationException("Bitte einen Titel angeben.");
        if (cleanTitle.Length > DirectiveRules.MaxTitle)
        {
            throw new InvalidOperationException($"Der Titel darf höchstens {DirectiveRules.MaxTitle} Zeichen lang sein.");
        }
        var cleanSubject = subject.TrimToNull();
        if (cleanSubject is { Length: > DirectiveRules.MaxSubject })
        {
            throw new InvalidOperationException($"Das Sachgebiet darf höchstens {DirectiveRules.MaxSubject} Zeichen lang sein.");
        }
        var note = changeNote.TrimToNull() ?? throw new InvalidOperationException("Bitte eine kurze Änderungsnotiz angeben.");
        if (note.Length > DirectiveRules.MaxChangeNote)
        {
            throw new InvalidOperationException($"Die Änderungsnotiz darf höchstens {DirectiveRules.MaxChangeNote} Zeichen lang sein.");
        }
        var html = HtmlCleanup.Clean(contentHtml);
        if (HtmlCleanup.PlainText(html).Length == 0)
        {
            throw new InvalidOperationException("Bitte den Text der Dienstanweisung ausfüllen.");
        }
        return new Wording(cleanTitle, cleanSubject, note, html);
    }

    private static DirectiveVersion NewVersion(string directiveId, int version, Wording wording, DateOnly validFrom,
        bool requiresAcknowledgment, ClaimsPrincipal actor, DateTime now)
        => new()
        {
            DirectiveId = directiveId,
            Version = version,
            ContentHtml = wording.Html,
            ValidFrom = validFrom,
            ChangeNote = wording.ChangeNote,
            RequiresAcknowledgment = requiresAcknowledgment,
            PublishedById = actor.GetAgentId(),
            PublishedByName = actor.GetCodename(),
            PublishedAt = now,
        };

    private static DirectiveAcknowledgment NewAcknowledgment(string directiveId, int version, string agentId, DateTime now)
        => new() { DirectiveId = directiveId, Version = version, AgentId = agentId, AcknowledgedAt = now };

    private static Task<bool> HasAcknowledgedAsync(AppDbContext db, string directiveId, string agentId, int version,
        CancellationToken cancellationToken)
        => db.DirectiveAcknowledgments.AnyAsync(
            a => a.DirectiveId == directiveId && a.AgentId == agentId && a.Version == version, cancellationToken);

    /// <summary>Agents who owe this directive right now; the one debt predicate.</summary>
    private static Task<List<string>> OwedAgentsAsync(AppDbContext db, string directiveId,
        CancellationToken cancellationToken)
        => DirectiveDebt.Owed(db).Where(r => r.DirectiveId == directiveId).Select(r => r.AgentId)
            .ToListAsync(cancellationToken);

    private static async Task<HashSet<string>> OwedDirectiveIdsAsync(AppDbContext db, string? agentId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(agentId))
        {
            return [];
        }
        var ids = await DirectiveDebt.Owed(db).Where(r => r.AgentId == agentId).Select(r => r.DirectiveId)
            .ToListAsync(cancellationToken);
        return ids.ToHashSet();
    }

    private static async Task SaveHeadAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // newer version published
            throw new InvalidOperationException(Stale, ex);
        }
    }

    /// <summary>Bell and live badge after a saved version; best effort, the version is already committed.</summary>
    private async Task AnnounceAsync(AppDbContext db, string id, string title, IReadOnlyCollection<string> oldOwed,
        bool ringAll, string actorId, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<string> newOwed = [];
        try
        {
            newOwed = await OwedAgentsAsync(db, id, cancellationToken);
            // corrections ring newcomers only
            var ring = (ringAll ? newOwed : newOwed.Except(oldOwed)).Where(a => a != actorId).ToList();
            if (ring.Count > 0)
            {
                // title without subject
                await notifications.NotifyManyInAppAsync(ring, NotificationType.DirectiveIssued, title,
                    $"/dienstanweisungen/{id}", actorId, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Notifying about directive {DirectiveId} failed", id);
        }

        // bell before badge
        Report(oldOwed.Union(newOwed));
    }

    private void Report(IEnumerable<string> agentIds)
    {
        foreach (var agentId in agentIds.Distinct())
        {
            acknowledgmentBroadcaster.Report(agentId);
        }
    }
}
