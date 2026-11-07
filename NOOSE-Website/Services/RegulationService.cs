using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Regulation;
using NOOSE_Website.Infrastructure.Announcements;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Regulation;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IRegulationService" />
public class RegulationService(
    IDbContextFactory<AppDbContext> dbFactory,
    INotificationService notifications,
    AcknowledgmentBroadcaster acknowledgmentBroadcaster,
    ILogger<RegulationService> logger) : IRegulationService
{
    private const string NotFound = "Absatz nicht gefunden.";
    private const string SectionNotFound = "Abschnitt nicht gefunden.";
    private const string Stale = "Inzwischen hat jemand eine neuere Fassung veröffentlicht. Lade die Seite neu.";

    /// <summary>The cleaned, validated wording of a version.</summary>
    private sealed record Wording(string Heading, string ChangeNote, string Html);

    /// <summary>Version metadata without the wording.</summary>
    private sealed record VersionMeta(string Id, string ParagraphId, int Version, DateOnly ValidFrom, DateTime PublishedAt,
        string Heading, RegulationAudience Audience);

    // ---- reads ----

    public async Task<RegulationDocument> GetDocumentAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var scope = ViewerScope.From(actor);
        if (!RegulationRules.MayRead(scope))
        {
            return new RegulationDocument([], [], null, false, false);
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var numbers = await RegulationVisibility.SectionNumbersAsync(db, cancellationToken);
        var paragraphs = await db.RegulationParagraphs.AsNoTracking().OnlyVisible(db, scope)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Heading)
            .ToListAsync(cancellationToken);
        var ids = paragraphs.Select(p => p.Id).ToList();
        var metas = await LoadMetaAsync(db, ids, cancellationToken);
        var owed = await OwedParagraphIdsAsync(db, actor.GetAgentId(), cancellationToken);
        var today = MeetingTime.Day(DateTime.UtcNow);

        // pick the shown version first, then load only those texts
        var shown = paragraphs.ToDictionary(p => p.Id, p => ShownVersion(p, metas[p.Id], owed.Contains(p.Id), today));
        var shownIds = paragraphs.Select(p => metas[p.Id].FirstOrDefault(m => m.Version == shown[p.Id])?.Id)
            .OfType<string>().ToList();
        var html = await db.RegulationVersions.AsNoTracking()
            .Where(v => shownIds.Contains(v.Id))
            .Select(v => new { v.ParagraphId, v.ContentHtml })
            .ToDictionaryAsync(v => v.ParagraphId, v => v.ContentHtml, cancellationToken);

        var standing = RegulationStanding.Of(actor);
        var mayManage = RegulationRules.MayManage(actor);
        RegulationParagraphRow Row(RegulationParagraph p)
            => ToRow(p, metas[p.Id], shown[p.Id], html.GetValueOrDefault(p.Id) ?? string.Empty, owed.Contains(p.Id), today,
                mayManage && RegulationRules.MayEditParagraph(standing, p.Audience));

        var sections = numbers.Select(kv => new RegulationSectionView(
                kv.Key, kv.Value.Number, kv.Value.Title, kv.Value.Number is null,
                paragraphs.Where(p => p.SectionId == kv.Key && p.RepealedAt == null).Select(Row).ToList()))
            .ToList();
        var repealed = paragraphs.Where(p => p.RepealedAt != null).OrderByDescending(p => p.RepealedAt).Select(Row).ToList();

        var lastPublished = metas.Values.SelectMany(m => m).Select(m => (DateTime?)m.PublishedAt).Max();
        var lastRepealed = paragraphs.Select(p => p.RepealedAt).Max();
        var lastChanged = lastRepealed is { } r && (lastPublished is null || r > lastPublished) ? r : lastPublished;

        return new RegulationDocument(sections, repealed, lastChanged,
            mayManage && RegulationRules.MayManageSections(standing),
            mayManage && RegulationRules.EditableAudiences(standing).Count > 0);
    }

    public async Task<RegulationParagraphView?> GetParagraphAsync(string id, int? version, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        var scope = ViewerScope.From(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var head = await db.RegulationParagraphs.AsNoTracking().OnlyVisible(db, scope)
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (head is null)
        {
            return null;
        }

        var rows = await db.RegulationVersions.AsNoTracking()
            .Where(v => v.ParagraphId == id)
            .OrderByDescending(v => v.Version)
            .Select(v => new RegulationVersionView(v.Version, v.Heading, v.Audience, v.ValidFrom, v.ChangeNote,
                v.RequiresAcknowledgment, v.PublishedByName, v.PublishedAt))
            .ToListAsync(cancellationToken);
        var metas = rows.Select(v => new VersionMeta("", id, v.Version, v.ValidFrom, v.PublishedAt, v.Heading, v.Audience)).ToList();
        var today = MeetingTime.Day(DateTime.UtcNow);
        var validVersion = RegulationRules.ValidVersion(metas.Select(v => (v.Version, v.ValidFrom)).ToList(), today);
        var shown = version ?? validVersion ?? head.CurrentVersion;
        if (rows.All(v => v.Version != shown))
        {
            return null;
        }
        var shownHtml = await db.RegulationVersions.AsNoTracking()
            .Where(v => v.ParagraphId == id && v.Version == shown)
            .Select(v => v.ContentHtml)
            .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;

        var meId = actor.GetAgentId();
        var mustAcknowledge = !string.IsNullOrEmpty(meId)
            && await RegulationDebt.Owed(db).AnyAsync(r => r.AgentId == meId && r.ParagraphId == id, cancellationToken);
        var mine = string.IsNullOrEmpty(meId)
            ? null
            : await db.RegulationAcknowledgments.AsNoTracking()
                .Where(a => a.ParagraphId == id && a.AgentId == meId)
                .OrderByDescending(a => a.Version)
                .Select(a => new { a.Version, a.AcknowledgedAt })
                .FirstOrDefaultAsync(cancellationToken);
        var numbers = await RegulationVisibility.SectionNumbersAsync(db, cancellationToken);
        var section = numbers.GetValueOrDefault(head.SectionId);

        var mayEdit = RegulationRules.MayManage(actor)
            && RegulationRules.MayEditParagraph(RegulationStanding.Of(actor), head.Audience);
        return new RegulationParagraphView(
            ToRow(head, metas, shown, shownHtml, mustAcknowledge, today, mayEdit),
            section.Number,
            section.Title ?? string.Empty,
            shown,
            shownHtml,
            rows,
            mustAcknowledge,
            mine?.Version,
            mine?.AcknowledgedAt,
            head.RepealedByName,
            RegulationRules.MaySeeQuota(actor));
    }

    public async Task<RegulationComparison?> CompareAsync(string id, int fromVersion, int toVersion, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        var older = Math.Min(fromVersion, toVersion);
        var newer = Math.Max(fromVersion, toVersion);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await RegulationVisibility.IsVisibleAsync(db, id, ViewerScope.From(actor), cancellationToken))
        {
            return null;
        }
        var texts = await db.RegulationVersions.AsNoTracking()
            .Where(v => v.ParagraphId == id && (v.Version == older || v.Version == newer))
            .Select(v => new { v.Version, v.ContentHtml })
            .ToListAsync(cancellationToken);
        var oldText = texts.FirstOrDefault(t => t.Version == older);
        var newText = texts.FirstOrDefault(t => t.Version == newer);
        if (oldText is null || newText is null)
        {
            return null;
        }
        return new RegulationComparison(older, newer, HtmlDiff.Compare(oldText.ContentHtml, newText.ContentHtml));
    }

    public async Task<RegulationQuota?> GetQuotaAsync(string id, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        if (!RegulationRules.MaySeeQuota(actor))
        {
            return null;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var acknowledgeFrom = await db.RegulationParagraphs.AsNoTracking().OnlyVisible(db, ViewerScope.From(actor))
            .Where(p => p.Id == id)
            .Select(p => (int?)p.AcknowledgeFromVersion)
            .FirstOrDefaultAsync(cancellationToken);
        if (acknowledgeFrom is null)
        {
            return null;
        }

        // merged in memory
        var audience = await (from r in RegulationDebt.Audience(db).Where(r => r.ParagraphId == id)
                              join u in db.Users on r.AgentId equals u.Id
                              select new { r.AgentId, u.Codename }).ToListAsync(cancellationToken);
        var missingIds = (await RegulationDebt.Owed(db).Where(r => r.ParagraphId == id).Select(r => r.AgentId)
            .ToListAsync(cancellationToken)).ToHashSet();
        var highest = (await RegulationDebt.AcknowledgmentsOf(db, id).ToListAsync(cancellationToken))
            .GroupBy(a => a.AgentId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.Version).First());

        RegulationAudienceRow AudienceRow(string agentId, string codename)
            => highest.TryGetValue(agentId, out var ack)
                ? new RegulationAudienceRow(agentId, codename, ack.Version, ack.AcknowledgedAt)
                : new RegulationAudienceRow(agentId, codename, null, null);

        var missing = audience.Where(a => missingIds.Contains(a.AgentId)).Select(a => AudienceRow(a.AgentId, a.Codename))
            .OrderBy(a => a.Codename, StringComparer.CurrentCultureIgnoreCase).ToList();
        var done = audience.Where(a => !missingIds.Contains(a.AgentId)).Select(a => AudienceRow(a.AgentId, a.Codename))
            .OrderBy(a => a.Codename, StringComparer.CurrentCultureIgnoreCase).ToList();
        return new RegulationQuota(audience.Count, audience.Count - missing.Count, acknowledgeFrom.Value, missing, done);
    }

    public async Task<int> GetOpenCountAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var meId = actor.GetAgentId();
        if (string.IsNullOrEmpty(meId))
        {
            return 0;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await RegulationDebt.Owed(db).Where(r => r.AgentId == meId).CountAsync(cancellationToken);
    }

    public async Task<List<OpenRegulationParagraph>> GetOpenForMeAsync(ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        var meId = actor.GetAgentId();
        if (string.IsNullOrEmpty(meId))
        {
            return new();
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // oldest first
        var rows = await RegulationDebt.OpenFor(db, meId)
            .OrderBy(r => r.Since).ThenBy(r => r.Heading)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return new();
        }
        var numbers = await RegulationVisibility.SectionNumbersAsync(db, cancellationToken);
        return rows.Select(r => new OpenRegulationParagraph(r.ParagraphId, r.SectionId,
                RegulationRules.ParagraphLabel(numbers.GetValueOrDefault(r.SectionId).Number, r.Heading),
                r.Heading, r.Version, r.Since))
            .ToList();
    }

    public async Task<List<RegulationPick>> SearchAsync(string? text, ClaimsPrincipal actor, int take = 20,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await Matching(db, db.RegulationParagraphs.AsNoTracking().OnlyVisible(db, ViewerScope.From(actor)), text)
            .OrderBy(p => p.Heading)
            .Take(Math.Clamp(take, 1, 100))
            .Select(p => new { p.Id, p.Heading })
            .ToListAsync(cancellationToken);
        var labels = await RegulationVisibility.LabelsAsync(db, rows.Select(r => r.Id).ToList(), cancellationToken);
        return rows.Select(r => new RegulationPick(r.Id, labels.GetValueOrDefault(r.Id) ?? r.Heading, r.Heading)).ToList();
    }

    /// <summary>Narrows to a heading, a section title or the current wording; the one place that decides a match.</summary>
    public static IQueryable<RegulationParagraph> Matching(AppDbContext db, IQueryable<RegulationParagraph> query, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return query;
        }
        var s = text.Trim();
        return query.Where(p => p.Heading.Contains(s)
            || db.RegulationSections.Any(x => x.Id == p.SectionId && x.Title.Contains(s))
            || db.RegulationVersions.Any(v => v.ParagraphId == p.Id && v.Version == p.CurrentVersion && v.ContentHtml.Contains(s)));
    }

    public async Task<List<RegulationTrashRow>> GetTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var sections = await db.RegulationSections.AsNoTracking().IgnoreQueryFilters()
            .Where(s => s.IsDeleted)
            .Select(s => new RegulationTrashRow(s.Id, true, s.Title, "Abschnitt", s.DeletedAt, null))
            .ToListAsync(cancellationToken);
        var paragraphs = await (from p in db.RegulationParagraphs.AsNoTracking().IgnoreQueryFilters()
                                where p.IsDeleted
                                join s in db.RegulationSections.IgnoreQueryFilters() on p.SectionId equals s.Id
                                select new RegulationTrashRow(p.Id, false, p.Heading, s.Title, p.DeletedAt, (RegulationAudience?)p.Audience))
            .ToListAsync(cancellationToken);
        return sections.Concat(paragraphs).OrderByDescending(r => r.DeletedAt).ToList();
    }

    // ---- sections ----

    public async Task<string> CreateSectionAsync(string title, bool isPreamble, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        var clean = CleanSectionTitle(title);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await RequireSectionWriteAsync(db, actor, cancellationToken);
        var next = (await db.RegulationSections.MaxAsync(s => (int?)s.SortOrder, cancellationToken) ?? 0) + 10;
        var section = new RegulationSection { Title = clean, IsPreamble = isPreamble, SortOrder = next };
        db.RegulationSections.Add(section);
        await db.SaveChangesAsync(cancellationToken);
        return section.Id;
    }

    public async Task RenameSectionAsync(string id, string title, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        var clean = CleanSectionTitle(title);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await RequireSectionWriteAsync(db, actor, cancellationToken);
        var section = await db.RegulationSections.FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
            ?? throw new InvalidOperationException(SectionNotFound);
        section.Title = clean;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task MoveSectionAsync(string id, int delta, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await RequireSectionWriteAsync(db, actor, cancellationToken);
        // the foreword stays first, so only numbered sections move among themselves
        var ordered = await db.RegulationSections.Where(s => !s.IsPreamble)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Title).ToListAsync(cancellationToken);
        if (ordered.All(s => s.Id != id))
        {
            throw new InvalidOperationException(SectionNotFound);
        }
        if (Reorder(ordered, id, Math.Sign(delta), s => s.Id, (s, order) => s.SortOrder = order))
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task DeleteSectionAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await RequireSectionWriteAsync(db, actor, cancellationToken);
        var section = await db.RegulationSections.FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
            ?? throw new InvalidOperationException(SectionNotFound);
        // repealed ones count: they stay readable under their section
        if (await db.RegulationParagraphs.AnyAsync(p => p.SectionId == id, cancellationToken))
        {
            throw new InvalidOperationException(
                "Der Abschnitt enthält noch Absätze. Verschiebe oder lösche sie zuerst.");
        }
        // soft delete; a paragraph added in the meantime touched the section, so this matches no row
        db.RegulationSections.Remove(section);
        await SaveSectionAsync(db, cancellationToken);
    }

    // ---- paragraphs ----

    public async Task<string> CreateParagraphAsync(string sectionId, RegulationParagraphInput input, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireRegulationWrite(actor);
        RequireAudience(input.Audience);
        var wording = CleanWording(input.Heading, input.ChangeNote, input.ContentHtml);
        var actorId = ActorId(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await RequireParagraphWriteAsync(db, actor, [input.Audience], cancellationToken);
        var section = await db.RegulationSections.FirstOrDefaultAsync(s => s.Id == sectionId, cancellationToken)
            ?? throw new InvalidOperationException(SectionNotFound);
        // touches the section so a trash of it in the meantime matches no row
        section.ModifiedAt = DateTime.UtcNow;
        var next = (await db.RegulationParagraphs.Where(p => p.SectionId == sectionId)
            .MaxAsync(p => (int?)p.SortOrder, cancellationToken) ?? 0) + 10;
        var now = DateTime.UtcNow;
        var paragraph = new RegulationParagraph
        {
            SectionId = sectionId,
            Heading = wording.Heading,
            SortOrder = next,
            Audience = input.Audience,
            CurrentVersion = 1,
            AcknowledgeFromVersion = 1,
        };
        db.RegulationParagraphs.Add(paragraph);
        db.RegulationVersions.Add(NewVersion(paragraph.Id, 1, input.Audience, wording, input.ValidFrom, true, actor, now));
        // publisher owes nothing
        db.RegulationAcknowledgments.Add(NewAcknowledgment(paragraph.Id, 1, actorId, now));
        await SaveSectionAsync(db, cancellationToken);

        await AnnounceAsync(db, paragraph.Id, "Neuer Absatz in der Dienstverordnung", [], ringAll: true, actorId,
            cancellationToken);
        return paragraph.Id;
    }

    public async Task PublishVersionAsync(string id, RegulationVersionInput input, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireRegulationWrite(actor);
        RequireAudience(input.Audience);
        var wording = CleanWording(input.Heading, input.ChangeNote, input.ContentHtml);
        var actorId = ActorId(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var paragraph = await FindVisibleAsync(db, id, actor, cancellationToken);
        // both the old and the new audience must be the actor's
        await RequireParagraphWriteAsync(db, actor, [paragraph.Audience, input.Audience], cancellationToken);
        if (paragraph.RepealedAt is not null)
        {
            throw new InvalidOperationException("Ein aufgehobener Absatz bekommt keine neue Fassung.");
        }
        if (input.ExpectedVersion != paragraph.CurrentVersion)
        {
            throw new InvalidOperationException(Stale);
        }
        var previousFrom = await db.RegulationVersions
            .Where(v => v.ParagraphId == id && v.Version == paragraph.CurrentVersion)
            .Select(v => (DateOnly?)v.ValidFrom)
            .FirstOrDefaultAsync(cancellationToken);
        if (!RegulationRules.IsValidFromAllowed(input.ValidFrom, previousFrom))
        {
            throw new InvalidOperationException("„Gültig ab“ darf nicht vor dem Datum der vorigen Fassung liegen.");
        }

        var oldOwed = await OwedAgentsAsync(db, id, cancellationToken);
        var next = paragraph.CurrentVersion + 1;
        var now = DateTime.UtcNow;
        paragraph.Heading = wording.Heading;
        paragraph.Audience = input.Audience;
        paragraph.CurrentVersion = next;
        paragraph.AcknowledgeFromVersion = RegulationRules.NextAcknowledgeFrom(
            paragraph.AcknowledgeFromVersion, next, input.RequiresAcknowledgment);
        db.RegulationVersions.Add(NewVersion(id, next, input.Audience, wording, input.ValidFrom,
            input.RequiresAcknowledgment, actor, now));
        db.RegulationAcknowledgments.Add(NewAcknowledgment(id, next, actorId, now));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            // someone published, repealed or trashed first
            throw new InvalidOperationException(Stale, ex);
        }

        await AnnounceAsync(db, id, "Dienstverordnung geändert", oldOwed, ringAll: input.RequiresAcknowledgment, actorId,
            cancellationToken);
    }

    public async Task MoveParagraphAsync(string id, int delta, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireRegulationWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var paragraph = await FindVisibleAsync(db, id, actor, cancellationToken);
        await RequireParagraphWriteAsync(db, actor, [paragraph.Audience], cancellationToken);
        if (paragraph.RepealedAt is not null)
        {
            throw new InvalidOperationException("Ein aufgehobener Absatz wird nicht mehr verschoben.");
        }
        var ordered = await db.RegulationParagraphs
            .Where(p => p.SectionId == paragraph.SectionId && p.RepealedAt == null)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Heading)
            .ToListAsync(cancellationToken);
        if (Reorder(ordered, id, Math.Sign(delta), p => p.Id, (p, order) => p.SortOrder = order))
        {
            await SaveHeadAsync(db, cancellationToken);
        }
    }

    public async Task RepealParagraphAsync(string id, string reason, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireRegulationWrite(actor);
        var note = CriticalChange.RequireReason(reason, "Das Aufheben eines Absatzes");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var paragraph = await FindVisibleAsync(db, id, actor, cancellationToken);
        await RequireParagraphWriteAsync(db, actor, [paragraph.Audience], cancellationToken);
        if (paragraph.RepealedAt is not null)
        {
            throw new InvalidOperationException("Dieser Absatz ist schon aufgehoben.");
        }

        var owed = await OwedAgentsAsync(db, id, cancellationToken);
        paragraph.RepealedAt = DateTime.UtcNow;
        paragraph.RepealedById = actor.GetAgentId();
        paragraph.RepealedByName = actor.GetCodename();
        paragraph.RepealReason = note;
        await SaveHeadAsync(db, cancellationToken);

        Report(owed);
    }

    public async Task DeleteParagraphAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireRegulationWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var paragraph = await FindVisibleAsync(db, id, actor, cancellationToken);
        await RequireParagraphWriteAsync(db, actor, [paragraph.Audience], cancellationToken);

        var owed = await OwedAgentsAsync(db, id, cancellationToken);
        // soft delete
        db.RegulationParagraphs.Remove(paragraph);
        await SaveHeadAsync(db, cancellationToken);

        Report(owed);
    }

    public async Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireRegulationWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var paragraph = await db.RegulationParagraphs.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == id && p.IsDeleted, cancellationToken);
        if (paragraph is not null)
        {
            await RequireParagraphWriteAsync(db, actor, [paragraph.Audience], cancellationToken);
            var home = await db.RegulationSections.FirstOrDefaultAsync(s => s.Id == paragraph.SectionId, cancellationToken)
                ?? throw new InvalidOperationException("Stelle zuerst den Abschnitt des Absatzes wieder her.");
            // touches the section so a trash of it in the meantime matches no row
            home.ModifiedAt = DateTime.UtcNow;
            paragraph.IsDeleted = false;
            paragraph.DeletedAt = null;
            paragraph.DeletedById = null;
            await SaveSectionAsync(db, cancellationToken);
            Report(await OwedAgentsAsync(db, id, cancellationToken));
            return;
        }

        var section = await db.RegulationSections.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == id && s.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException(NotFound);
        await RequireSectionWriteAsync(db, actor, cancellationToken);
        section.IsDeleted = false;
        section.DeletedAt = null;
        section.DeletedById = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- acknowledgments ----

    public async Task AcknowledgeAsync(string id, int version, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        var meId = RequireAcknowledger(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var paragraph = await FindVisibleAsync(db, id, actor, cancellationToken);
        if (paragraph.RepealedAt is not null)
        {
            throw new InvalidOperationException("Dieser Absatz ist aufgehoben.");
        }
        await RequireSelectableAsync(db, meId, cancellationToken);
        if (version != paragraph.CurrentVersion)
        {
            throw new InvalidOperationException(
                "Inzwischen gibt es eine neuere Fassung. Lies sie und nimm sie dann zur Kenntnis.");
        }
        if (await AddMissingAsync(db, meId, [(id, version)], cancellationToken) > 0)
        {
            acknowledgmentBroadcaster.Report(meId);
        }
    }

    public async Task<RegulationAcknowledgeResult> AcknowledgeAllAsync(IReadOnlyList<RegulationSeen> seen, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        var meId = RequireAcknowledger(actor);
        if (seen.Count == 0)
        {
            return new RegulationAcknowledgeResult(0, 0);
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await RequireSelectableAsync(db, meId, cancellationToken);

        var wanted = seen.GroupBy(s => s.ParagraphId).Select(g => g.First()).ToList();
        var ids = wanted.Select(s => s.ParagraphId).ToList();
        var current = await RegulationDebt.Live(db).OnlyVisible(db, ViewerScope.From(actor))
            .Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.CurrentVersion })
            .ToDictionaryAsync(p => p.Id, p => p.CurrentVersion, cancellationToken);
        // a version published after the page loaded was never read
        var matching = wanted.Where(s => current.TryGetValue(s.ParagraphId, out var v) && v == s.Version).ToList();
        // repealed or trashed in the meantime is not "changed": nobody owes it any more
        var changed = wanted.Count(s => current.TryGetValue(s.ParagraphId, out var v) && v != s.Version);

        var added = await AddMissingAsync(db, meId, matching.Select(s => (s.ParagraphId, s.Version)).ToList(), cancellationToken);
        if (added > 0)
        {
            acknowledgmentBroadcaster.Report(meId);
        }
        return new RegulationAcknowledgeResult(matching.Count, changed);
    }

    // ---- helpers ----

    private static string ActorId(ClaimsPrincipal actor)
        => actor.GetAgentId() is { Length: > 0 } id ? id : throw new UnauthorizedAccessException("Kein angemeldeter Agent.");

    private static string RequireAcknowledger(ClaimsPrincipal actor)
    {
        Permission.RequireWriteAccess(actor);
        if (!RegulationRules.MayAcknowledge(actor))
        {
            throw new UnauthorizedAccessException("Die Dienstverordnung nehmen nur interne Agenten zur Kenntnis.");
        }
        return ActorId(actor);
    }

    /// <summary>Fresh DB check: the account must still be selectable staff.</summary>
    private static async Task RequireSelectableAsync(AppDbContext db, string meId, CancellationToken cancellationToken)
    {
        if (!await db.Users.OnlySelectable().AnyAsync(u => u.Id == meId, cancellationToken))
        {
            throw new UnauthorizedAccessException("Dein Konto darf die Dienstverordnung nicht zur Kenntnis nehmen.");
        }
    }

    /// <summary>Adds the acknowledgments that do not exist yet; a double click lands on the unique index and is retried once.</summary>
    private static async Task<int> AddMissingAsync(AppDbContext db, string meId, IReadOnlyList<(string ParagraphId, int Version)> rows,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var ids = rows.Select(r => r.ParagraphId).ToList();
            var existing = (await db.RegulationAcknowledgments
                    .Where(a => a.AgentId == meId && ids.Contains(a.ParagraphId))
                    .Select(a => new { a.ParagraphId, a.Version })
                    .ToListAsync(cancellationToken))
                .Select(a => (a.ParagraphId, a.Version)).ToHashSet();
            var missing = rows.Where(r => !existing.Contains(r)).ToList();
            if (missing.Count == 0)
            {
                return 0;
            }
            var now = DateTime.UtcNow;
            foreach (var (paragraphId, version) in missing)
            {
                db.RegulationAcknowledgments.Add(NewAcknowledgment(paragraphId, version, meId, now));
            }
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return missing.Count;
            }
            catch (DbUpdateException) when (attempt == 1)
            {
                // double click
                db.ChangeTracker.Clear();
            }
        }
    }

    /// <summary>Coarse gate, then the fine gate on every given audience with the actor's standing fresh from the DB.</summary>
    private static async Task RequireParagraphWriteAsync(AppDbContext db, ClaimsPrincipal actor,
        IReadOnlyList<RegulationAudience> audiences, CancellationToken cancellationToken)
    {
        Permission.RequireRegulationWrite(actor);
        var standing = await RegulationStanding.ReadAsync(db.Users, actor, cancellationToken);
        foreach (var audience in audiences)
        {
            if (!RegulationRules.MayEditParagraph(standing, audience))
            {
                throw new UnauthorizedAccessException(RegulationRules.EditRefusal(audience));
            }
        }
    }

    private static async Task RequireSectionWriteAsync(AppDbContext db, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        Permission.RequireRegulationWrite(actor);
        var standing = await RegulationStanding.ReadAsync(db.Users, actor, cancellationToken);
        if (!RegulationRules.MayManageSections(standing))
        {
            throw new UnauthorizedAccessException("Abschnitte der Dienstverordnung ändern nur das Board und Admins.");
        }
    }

    private static void RequireAudience(RegulationAudience audience)
    {
        if (!Enum.IsDefined(audience))
        {
            throw new InvalidOperationException("Unbekannte Zielgruppe.");
        }
    }

    /// <summary>Loads a tracked, visible, undeleted paragraph; missing and hidden answer alike.</summary>
    private static async Task<RegulationParagraph> FindVisibleAsync(AppDbContext db, string id, ClaimsPrincipal actor,
        CancellationToken cancellationToken)
        => await db.RegulationParagraphs.OnlyVisible(db, ViewerScope.From(actor)).FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new InvalidOperationException(NotFound);

    private static async Task<Dictionary<string, List<VersionMeta>>> LoadMetaAsync(AppDbContext db, IReadOnlyList<string> ids,
        CancellationToken cancellationToken)
    {
        // flat query
        var rows = await db.RegulationVersions.AsNoTracking()
            .Where(v => ids.Contains(v.ParagraphId))
            .Select(v => new VersionMeta(v.Id, v.ParagraphId, v.Version, v.ValidFrom, v.PublishedAt, v.Heading, v.Audience))
            .ToListAsync(cancellationToken);
        var lookup = rows.ToLookup(v => v.ParagraphId);
        return ids.ToDictionary(id => id, id => lookup[id].ToList());
    }

    /// <summary>The owed current version for a reader who owes it, else the valid one, else the current one.</summary>
    private static int ShownVersion(RegulationParagraph p, IReadOnlyList<VersionMeta> metas, bool owes, DateOnly today)
        => owes ? p.CurrentVersion
            : RegulationRules.ValidVersion(metas.Select(m => (m.Version, m.ValidFrom)).ToList(), today) ?? p.CurrentVersion;

    private static RegulationParagraphRow ToRow(RegulationParagraph p, IReadOnlyList<VersionMeta> metas, int shown,
        string html, bool mustAcknowledge, DateOnly today, bool mayEdit)
    {
        var meta = metas.FirstOrDefault(m => m.Version == shown);
        // heading and audience as published with the shown version; the edit gate keeps reading the head
        return new RegulationParagraphRow(p.Id, p.SectionId, meta?.Heading ?? p.Heading, meta?.Audience ?? p.Audience,
            p.CurrentVersion, p.AcknowledgeFromVersion,
            shown, html, meta?.ValidFrom ?? default, meta?.PublishedAt ?? default,
            RegulationRules.ValidVersion(metas.Select(m => (m.Version, m.ValidFrom)).ToList(), today),
            mustAcknowledge, p.RepealedAt is not null, p.RepealedAt, p.RepealReason, mayEdit);
    }

    private static string CleanSectionTitle(string? title)
    {
        var clean = title.TrimToNull() ?? throw new InvalidOperationException("Bitte einen Titel für den Abschnitt angeben.");
        if (clean.Length > RegulationRules.MaxSectionTitle)
        {
            throw new InvalidOperationException($"Der Titel darf höchstens {RegulationRules.MaxSectionTitle} Zeichen lang sein.");
        }
        return clean;
    }

    private static Wording CleanWording(string? heading, string? changeNote, string? contentHtml)
    {
        var cleanHeading = heading.TrimToNull() ?? throw new InvalidOperationException("Bitte eine Überschrift angeben.");
        if (cleanHeading.Length > RegulationRules.MaxHeading)
        {
            throw new InvalidOperationException($"Die Überschrift darf höchstens {RegulationRules.MaxHeading} Zeichen lang sein.");
        }
        var note = changeNote.TrimToNull() ?? throw new InvalidOperationException("Bitte eine kurze Änderungsnotiz angeben.");
        if (note.Length > RegulationRules.MaxChangeNote)
        {
            throw new InvalidOperationException($"Die Änderungsnotiz darf höchstens {RegulationRules.MaxChangeNote} Zeichen lang sein.");
        }
        var html = HtmlCleanup.Clean(contentHtml);
        if (HtmlCleanup.PlainText(html).Length == 0)
        {
            throw new InvalidOperationException("Bitte den Text des Absatzes ausfüllen.");
        }
        return new Wording(cleanHeading, note, html);
    }

    private static RegulationParagraphVersion NewVersion(string paragraphId, int version, RegulationAudience audience,
        Wording wording, DateOnly validFrom, bool requiresAcknowledgment, ClaimsPrincipal actor, DateTime now)
        => new()
        {
            ParagraphId = paragraphId,
            Version = version,
            Heading = wording.Heading,
            Audience = audience,
            ContentHtml = wording.Html,
            ValidFrom = validFrom,
            ChangeNote = wording.ChangeNote,
            RequiresAcknowledgment = requiresAcknowledgment,
            PublishedById = actor.GetAgentId(),
            PublishedByName = actor.GetCodename(),
            PublishedAt = now,
        };

    private static RegulationAcknowledgment NewAcknowledgment(string paragraphId, int version, string agentId, DateTime now)
        => new() { ParagraphId = paragraphId, Version = version, AgentId = agentId, AcknowledgedAt = now };

    /// <summary>Agents who owe this paragraph right now; the one debt predicate.</summary>
    private static Task<List<string>> OwedAgentsAsync(AppDbContext db, string paragraphId,
        CancellationToken cancellationToken)
        => RegulationDebt.Owed(db).Where(r => r.ParagraphId == paragraphId).Select(r => r.AgentId)
            .ToListAsync(cancellationToken);

    private static async Task<HashSet<string>> OwedParagraphIdsAsync(AppDbContext db, string? agentId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(agentId))
        {
            return [];
        }
        var ids = await RegulationDebt.Owed(db).Where(r => r.AgentId == agentId).Select(r => r.ParagraphId)
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

    /// <summary>Saves a write that raced a section change; the section's tokens turn it into a readable refusal.</summary>
    private static async Task SaveSectionAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            throw new InvalidOperationException(
                "Der Abschnitt wurde inzwischen geändert oder gelöscht. Lade die Seite neu.", ex);
        }
    }

    /// <summary>Moves one row by <paramref name="delta"/> and renumbers in steps of 10; false at either end.</summary>
    internal static bool Reorder<T>(List<T> ordered, string id, int delta, Func<T, string> idOf, Action<T, int> setOrder)
    {
        var from = ordered.FindIndex(x => string.Equals(idOf(x), id, StringComparison.Ordinal));
        var to = from + delta;
        if (from < 0 || delta == 0 || to < 0 || to >= ordered.Count)
        {
            return false;
        }
        var row = ordered[from];
        ordered.RemoveAt(from);
        ordered.Insert(to, row);
        for (var i = 0; i < ordered.Count; i++)
        {
            setOrder(ordered[i], (i + 1) * 10);
        }
        return true;
    }

    /// <summary>Bell and live badge after a saved version; best effort, the version is already committed.</summary>
    private async Task AnnounceAsync(AppDbContext db, string id, string what, IReadOnlyCollection<string> oldOwed,
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
                var label = (await RegulationVisibility.LabelsAsync(db, [id], cancellationToken)).GetValueOrDefault(id);
                // heading only, never the wording
                await notifications.NotifyManyInAppAsync(ring, NotificationType.RegulationChanged,
                    label is null ? what : $"{what}: {label}", $"/dienstverordnung/absatz/{id}", actorId, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Notifying about regulation paragraph {ParagraphId} failed", id);
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
