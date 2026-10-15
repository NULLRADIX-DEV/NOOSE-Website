using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Presentations;
using NOOSE_Website.Infrastructure.Presentations;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Presentations;

namespace NOOSE_Website.Services;

/// <summary>Presentations: decks with layout slides, visible to all internal agents, one group, or the invited.</summary>
public interface IPresentationService
{
    Task<List<PresentationListItem>> GetListAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task<PresentationView?> GetAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task<Presentation> CreateAsync(PresentationInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task UpdateAsync(string id, PresentationInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<PresentationSlide> AddSlideAsync(string presentationId, SlideLayout layout, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task UpdateSlideAsync(string slideId, SlideInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task DeleteSlideAsync(string slideId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task MoveSlideAsync(string slideId, int newIndex, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task<PresentationSlide> DuplicateSlideAsync(string slideId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task SetSlideImageAsync(string slideId, Stream content, string contentType, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task RemoveSlideImageAsync(string slideId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Stored name of an imported deck's PDF for the file endpoint; null when the file is gone or its
    /// presentation is hidden from the actor.</summary>
    Task<string?> GetPdfFileNameAsync(string fileId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<List<Presentation>> GetTrashAsync(CancellationToken cancellationToken = default);
    Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IPresentationService" />
public sealed partial class PresentationService(
    IDbContextFactory<AppDbContext> dbFactory,
    ITextImageStorageService imageStorage,
    PresentationLiveRegistry live,
    PresentationBroadcaster broadcaster) : IPresentationService
{
    /// <summary>Slides one deck may hold; a Google import of a long deck stays below it.</summary>
    public const int MaxSlides = 300;

    public async Task<List<PresentationListItem>> GetListAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireInternalAgent(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await PresentationVisibility.OnlyVisible(db.Presentations.AsNoTracking(), db, ViewerScope.From(actor))
            .OrderByDescending(p => p.ModifiedAt ?? p.CreatedAt)
            .Select(p => new
            {
                p.Id, p.Title, p.Description, p.Access, p.Audience, p.TargetId, p.MinRank, p.OwnerId,
                Updated = p.ModifiedAt ?? p.CreatedAt,
                Slides = p.Slides.Count(s => !s.IsDeleted),
            })
            .ToListAsync(cancellationToken);
        var owners = await CodenamesAsync(db, rows.Select(r => r.OwnerId), cancellationToken);
        var taskforces = await TaskforceNamesAsync(db, rows.Select(r => r.TargetId), cancellationToken);
        return rows.Select(r => new PresentationListItem(r.Id, r.Title, r.Description, r.Access,
            AccessLabel(r.Access, r.Audience, r.TargetId, r.MinRank, taskforces),
            r.OwnerId, owners.GetValueOrDefault(r.OwnerId) ?? "—", r.Slides, r.Updated)).ToList();
    }

    public async Task<PresentationView?> GetAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireInternalAgent(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var presentation = await PresentationVisibility.OnlyVisible(db.Presentations.AsNoTracking(), db, ViewerScope.From(actor))
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (presentation is null)
        {
            return null;
        }
        var slides = await db.PresentationSlides.AsNoTracking().Include(s => s.PdfFile)
            .Where(s => s.PresentationId == id).OrderBy(s => s.Sorting).ToListAsync(cancellationToken);
        var files = await db.PresentationFiles.AsNoTracking().Where(f => f.PresentationId == id)
            .OrderBy(f => f.CreatedAt).ToListAsync(cancellationToken);
        var invites = await db.PresentationInvites.AsNoTracking()
            .Where(i => i.PresentationId == id).Select(i => i.AgentId).ToListAsync(cancellationToken);
        var names = await CodenamesAsync(db, files.Select(f => f.ImportedById ?? string.Empty).Append(presentation.OwnerId),
            cancellationToken);
        var taskforces = await TaskforceNamesAsync(db, [presentation.TargetId], cancellationToken);
        var imports = files.Select(f => new PresentationFileItem(f.Id, f.Title, f.PageCount, f.SourceKind, f.ImportedAt,
            names.GetValueOrDefault(f.ImportedById ?? string.Empty) ?? "—",
            slides.Count(s => s.PdfFileId == f.Id))).ToList();
        return new PresentationView(presentation, slides, invites, names.GetValueOrDefault(presentation.OwnerId) ?? "—",
            AccessLabel(presentation.Access, presentation.Audience, presentation.TargetId, presentation.MinRank, taskforces),
            actor.MayWrite() && PresentationVisibility.MayManage(actor, presentation), imports);
    }

    public async Task<Presentation> CreateAsync(PresentationInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequirePresentationWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var presentation = new Presentation { OwnerId = actor.GetAgentId() ?? string.Empty };
        await ApplyAsync(db, presentation, input, actor, cancellationToken);
        // a deck starts with its title slide, so the editor never opens on nothing
        presentation.Slides.Add(new PresentationSlide { Layout = SlideLayout.Title, Title = presentation.Title, Sorting = 1 });
        db.Presentations.Add(presentation);
        await SyncInvitesAsync(db, presentation, input, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return presentation;
    }

    public async Task UpdateAsync(string id, PresentationInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequirePresentationWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var presentation = await LoadManagedAsync(db, id, actor, cancellationToken);
        await ApplyAsync(db, presentation, input, actor, cancellationToken);
        await SyncInvitesAsync(db, presentation, input, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        // a narrowed audience takes effect for joining at once; the banner re-reads who may see it
        if (live.Get(id) is not null)
        {
            live.SetTitle(id, presentation.Title);
            broadcaster.Report(id, PresentationLiveEvent.Started);
        }
    }

    public async Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequirePresentationWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var presentation = await LoadManagedAsync(db, id, actor, cancellationToken);
        db.Presentations.Remove(presentation); // soft-delete
        await db.SaveChangesAsync(cancellationToken);
        if (live.End(id))
        {
            broadcaster.Report(id, PresentationLiveEvent.Ended);
        }
    }

    public async Task<PresentationSlide> AddSlideAsync(string presentationId, SlideLayout layout, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequirePresentationWrite(actor);
        if (layout == SlideLayout.PdfPage)
        {
            throw new InvalidOperationException("Google-Folien kommen nur über den Import.");
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var presentation = await LoadManagedAsync(db, presentationId, actor, cancellationToken);
        var count = await db.PresentationSlides.CountAsync(s => s.PresentationId == presentation.Id, cancellationToken);
        if (count >= MaxSlides)
        {
            throw new InvalidOperationException($"Eine Präsentation hat höchstens {MaxSlides} Folien.");
        }
        var max = await db.PresentationSlides.Where(s => s.PresentationId == presentation.Id)
            .MaxAsync(s => (int?)s.Sorting, cancellationToken) ?? 0;
        var slide = new PresentationSlide { PresentationId = presentation.Id, Layout = layout, Sorting = max + 1 };
        db.PresentationSlides.Add(slide);
        presentation.ModifiedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        ReportSlides(presentation.Id);
        return slide;
    }

    public async Task UpdateSlideAsync(string slideId, SlideInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequirePresentationWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var slide = await LoadManagedSlideAsync(db, slideId, actor, cancellationToken);
        if (slide.Layout == SlideLayout.PdfPage || input.Layout == SlideLayout.PdfPage)
        {
            // a Google page carries no text of its own; only the notes are the author's
            slide.Notes = TestEngine.Trim(input.Notes);
        }
        else
        {
            slide.Layout = input.Layout;
            slide.Title = TestEngine.Trim(input.Title);
            slide.BodyHtml = PresentationDisplay.HasText(input.Layout) ? HtmlCleanup.Clean(input.BodyHtml) : null;
            slide.Notes = TestEngine.Trim(input.Notes);
        }
        await db.SaveChangesAsync(cancellationToken);
        ReportSlides(slide.PresentationId);
    }

    public async Task DeleteSlideAsync(string slideId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequirePresentationWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var slide = await LoadManagedSlideAsync(db, slideId, actor, cancellationToken);
        db.PresentationSlides.Remove(slide); // soft-delete
        await db.SaveChangesAsync(cancellationToken);
        await RenumberAsync(db, slide.PresentationId, cancellationToken);
        ReportSlides(slide.PresentationId);
        // a single slide has no restore, so its pictures (the slide picture and those in its text) have no further use
        await DeleteSlideImagesAsync(slide.Id, cancellationToken);
    }

    public async Task MoveSlideAsync(string slideId, int newIndex, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequirePresentationWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var slide = await LoadManagedSlideAsync(db, slideId, actor, cancellationToken);
        var slides = await db.PresentationSlides.Where(s => s.PresentationId == slide.PresentationId)
            .OrderBy(s => s.Sorting).ToListAsync(cancellationToken);
        var moving = slides.First(s => s.Id == slideId);
        slides.Remove(moving);
        slides.Insert(Math.Clamp(newIndex, 0, slides.Count), moving);
        for (var i = 0; i < slides.Count; i++)
        {
            slides[i].Sorting = i + 1;
        }
        await db.SaveChangesAsync(cancellationToken);
        ReportSlides(slide.PresentationId);
    }

    public async Task<PresentationSlide> DuplicateSlideAsync(string slideId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequirePresentationWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var source = await LoadManagedSlideAsync(db, slideId, actor, cancellationToken);
        if (await db.PresentationSlides.CountAsync(s => s.PresentationId == source.PresentationId, cancellationToken) >= MaxSlides)
        {
            throw new InvalidOperationException($"Eine Präsentation hat höchstens {MaxSlides} Folien.");
        }
        var copy = new PresentationSlide
        {
            PresentationId = source.PresentationId,
            Layout = source.Layout,
            Title = source.Title,
            BodyHtml = source.BodyHtml,
            Notes = source.Notes,
            PdfFileId = source.PdfFileId,
            PageNumber = source.PageNumber,
        };
        // pictures are copied, not shared: each is filed under one slide, and replacing, removing or deleting it there
        // must not break the other - the slide picture as well as those pasted into the text
        if (source.ImageId is { } imageId
            && await db.TextImages.AsNoTracking().FirstOrDefaultAsync(t => t.Id == imageId, cancellationToken) is { } image)
        {
            await using var bytes = imageStorage.OpenRead(image.FileNameSaved);
            copy.ImageId = await StoreImageAsync(db, copy.Id, bytes, image.ContentType, cancellationToken);
        }
        if (copy.BodyHtml is { } body)
        {
            var ids = ImageUrl().Matches(body).Select(m => m.Groups["id"].Value).Distinct().ToList();
            var inline = await db.TextImages.AsNoTracking()
                .Where(t => ids.Contains(t.Id) && t.EntityType == nameof(PresentationSlide) && t.EntityId == source.Id)
                .ToListAsync(cancellationToken);
            foreach (var picture in inline)
            {
                await using var bytes = imageStorage.OpenRead(picture.FileNameSaved);
                var newId = await StoreImageAsync(db, copy.Id, bytes, picture.ContentType, cancellationToken);
                body = body.Replace($"/dateien/textbilder/{picture.Id}", $"/dateien/textbilder/{newId}", StringComparison.Ordinal);
            }
            copy.BodyHtml = body;
        }
        var later = await db.PresentationSlides
            .Where(s => s.PresentationId == source.PresentationId && s.Sorting > source.Sorting)
            .ToListAsync(cancellationToken);
        foreach (var slide in later)
        {
            slide.Sorting++;
        }
        copy.Sorting = source.Sorting + 1;
        db.PresentationSlides.Add(copy);
        await db.SaveChangesAsync(cancellationToken);
        ReportSlides(source.PresentationId);
        return copy;
    }

    public async Task SetSlideImageAsync(string slideId, Stream content, string contentType, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequirePresentationWrite(actor);
        if (!imageStorage.IsAllowedType(contentType))
        {
            throw new InvalidOperationException("Erlaubt sind JPG, PNG, WebP und GIF.");
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var slide = await LoadManagedSlideAsync(db, slideId, actor, cancellationToken);
        var previous = slide.ImageId;
        slide.ImageId = await StoreImageAsync(db, slide.Id, content, contentType, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        ReportSlides(slide.PresentationId);
        await DeleteImageAsync(previous, cancellationToken);
    }

    public async Task RemoveSlideImageAsync(string slideId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequirePresentationWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var slide = await LoadManagedSlideAsync(db, slideId, actor, cancellationToken);
        var previous = slide.ImageId;
        slide.ImageId = null;
        await db.SaveChangesAsync(cancellationToken);
        ReportSlides(slide.PresentationId);
        await DeleteImageAsync(previous, cancellationToken);
    }

    public async Task<string?> GetPdfFileNameAsync(string fileId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireInternalAgent(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // the deck decides, exactly like for its slides: one 404 for "gone" and "not yours"
        return await db.PresentationFiles.AsNoTracking().Where(f => f.Id == fileId)
            .Join(PresentationVisibility.OnlyVisible(db.Presentations.AsNoTracking(), db, ViewerScope.From(actor)),
                f => f.PresentationId, p => p.Id, (f, p) => f.FileNameSaved)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<List<Presentation>> GetTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Presentations.IgnoreQueryFilters().AsNoTracking()
            .Where(p => p.IsDeleted).OrderByDescending(p => p.DeletedAt).ToListAsync(cancellationToken);
    }

    public async Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var presentation = await db.Presentations.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == id && p.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException("Präsentation nicht im Papierkorb.");
        presentation.IsDeleted = false;
        presentation.DeletedAt = null;
        presentation.DeletedById = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Validates and writes title, description and audience; the guard is the caller's.</summary>
    internal static async Task ApplyAsync(AppDbContext db, Presentation presentation, PresentationInput input,
        ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var title = TestEngine.Trim(input.Title) ?? throw new InvalidOperationException("Ein Titel ist erforderlich.");
        presentation.Title = title.Length > 200 ? title[..200] : title;
        presentation.Description = TestEngine.Trim(input.Description);
        presentation.Access = input.Access;
        presentation.Audience = null;
        presentation.TargetId = null;
        presentation.MinRank = null;
        if (input.Access != PresentationAccess.Closed)
        {
            return;
        }
        if (input.Audience is not { } audience || !PresentationDisplay.ClosedAudiences.Contains(audience))
        {
            throw new InvalidOperationException("Bitte wähle die Gruppe, für die die Präsentation sichtbar ist.");
        }
        if (audience == AnnouncementAudience.Taskforce
            && (string.IsNullOrEmpty(input.TargetId) || !await db.Taskforces.AnyAsync(t => t.Id == input.TargetId, cancellationToken)))
        {
            throw new InvalidOperationException("Bitte wähle eine Taskforce.");
        }
        if (audience == AnnouncementAudience.FromRank && input.MinRank is null)
        {
            throw new InvalidOperationException("Bitte wähle einen Mindest-Dienstgrad.");
        }
        if (!await PresentationVisibility.MayTargetAsync(db, actor, audience, input.TargetId, input.MinRank, cancellationToken))
        {
            throw new UnauthorizedAccessException("Du kannst eine Präsentation nur für eine Gruppe schließen, zu der du selbst gehörst.");
        }
        presentation.Audience = audience;
        presentation.TargetId = audience == AnnouncementAudience.Taskforce ? input.TargetId : null;
        presentation.MinRank = audience == AnnouncementAudience.FromRank ? input.MinRank : null;
    }

    /// <summary>Makes the invite rows match the input; selectable agents only, and only for a private deck.</summary>
    internal static async Task SyncInvitesAsync(AppDbContext db, Presentation presentation, PresentationInput input,
        CancellationToken cancellationToken)
    {
        var wanted = input.Access == PresentationAccess.Private
            ? input.InviteIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList()
            : [];
        // the write path checks with the picker's own predicate, or the SignalR path stays open
        var allowed = wanted.Count == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : (await db.Users.OnlySelectable().Where(u => wanted.Contains(u.Id)).Select(u => u.Id)
                .ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var existing = await db.PresentationInvites.Where(i => i.PresentationId == presentation.Id).ToListAsync(cancellationToken);
        db.PresentationInvites.RemoveRange(existing.Where(i => !allowed.Contains(i.AgentId)));
        foreach (var agentId in allowed.Where(id => existing.All(i => i.AgentId != id)))
        {
            db.PresentationInvites.Add(new PresentationInvite { PresentationId = presentation.Id, AgentId = agentId });
        }
    }

    internal static async Task<Presentation> LoadManagedAsync(AppDbContext db, string id, ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var presentation = await db.Presentations.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Präsentation nicht gefunden.");
        if (!PresentationVisibility.MayManage(actor, presentation))
        {
            throw new UnauthorizedAccessException("Bearbeiten darf nur, wer die Präsentation angelegt hat, oder die Führung.");
        }
        return presentation;
    }

    private static async Task<PresentationSlide> LoadManagedSlideAsync(AppDbContext db, string slideId, ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var slide = await db.PresentationSlides.FirstOrDefaultAsync(s => s.Id == slideId, cancellationToken)
            ?? throw new InvalidOperationException("Folie nicht gefunden.");
        await LoadManagedAsync(db, slide.PresentationId, actor, cancellationToken);
        return slide;
    }

    private async Task<string> StoreImageAsync(AppDbContext db, string slideId, Stream content, string contentType,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken);
        if (buffer.Length == 0 || buffer.Length > imageStorage.MaxBytes)
        {
            throw new InvalidOperationException($"Das Bild ist zu groß (höchstens {imageStorage.MaxBytes / 1024 / 1024} MB).");
        }
        buffer.Position = 0;
        var fileName = await imageStorage.SaveAsync(buffer, contentType, cancellationToken);
        var row = new TextImage
        {
            EntityType = nameof(PresentationSlide),
            EntityId = slideId,
            FileNameSaved = fileName,
            ContentType = contentType,
            SizeBytes = buffer.Length,
        };
        db.TextImages.Add(row);
        return row.Id;
    }

    /// <summary>Removes a replaced picture, row and file, after the commit; best effort.</summary>
    private async Task DeleteImageAsync(string? imageId, CancellationToken cancellationToken)
    {
        if (imageId is null)
        {
            return;
        }
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var image = await db.TextImages.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(t => t.Id == imageId && t.EntityType == nameof(PresentationSlide), cancellationToken);
            if (image is null)
            {
                return;
            }
            await db.TextImages.IgnoreQueryFilters().Where(t => t.Id == imageId).ExecuteDeleteAsync(cancellationToken);
            imageStorage.Delete(image.FileNameSaved);
        }
        catch
        {
            /* best effort */
        }
    }

    /// <summary>Removes every picture filed under a deleted slide, rows and files, after the commit; best effort.</summary>
    private async Task DeleteSlideImagesAsync(string slideId, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var images = await db.TextImages.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.EntityType == nameof(PresentationSlide) && t.EntityId == slideId)
                .ToListAsync(cancellationToken);
            if (images.Count == 0)
            {
                return;
            }
            var ids = images.Select(t => t.Id).ToList();
            await db.TextImages.IgnoreQueryFilters().Where(t => ids.Contains(t.Id)).ExecuteDeleteAsync(cancellationToken);
            foreach (var image in images)
            {
                imageStorage.Delete(image.FileNameSaved);
            }
        }
        catch
        {
            /* best effort */
        }
    }

    /// <summary>Tells the live pages of a running deck to load its slides again.</summary>
    private void ReportSlides(string presentationId)
    {
        if (live.Get(presentationId) is not null)
        {
            broadcaster.Report(presentationId, PresentationLiveEvent.SlidesChanged);
        }
    }

    [GeneratedRegex("/dateien/textbilder/(?<id>[0-9a-fA-F-]{36})")]
    private static partial Regex ImageUrl();

    internal static async Task RenumberAsync(AppDbContext db, string presentationId, CancellationToken cancellationToken)
    {
        var slides = await db.PresentationSlides.Where(s => s.PresentationId == presentationId)
            .OrderBy(s => s.Sorting).ToListAsync(cancellationToken);
        for (var i = 0; i < slides.Count; i++)
        {
            slides[i].Sorting = i + 1;
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<Dictionary<string, string>> CodenamesAsync(AppDbContext db, IEnumerable<string> ids,
        CancellationToken cancellationToken)
    {
        var list = ids.Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => list.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Codename, cancellationToken);
    }

    private static async Task<Dictionary<string, string>> TaskforceNamesAsync(AppDbContext db, IEnumerable<string?> ids,
        CancellationToken cancellationToken)
    {
        var list = ids.Where(id => id is not null).Select(id => id!).Distinct().ToList();
        return list.Count == 0
            ? []
            : await db.Taskforces.AsNoTracking().Where(t => list.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);
    }

    /// <summary>"Öffentlich", "Nur HRB", "Taskforce Kobra", "Ab Senior Special Agent", "Privat".</summary>
    public static string AccessLabel(PresentationAccess access, AnnouncementAudience? audience, string? targetId, Rank? minRank,
        IReadOnlyDictionary<string, string> taskforces)
        => access switch
        {
            PresentationAccess.Public => "Öffentlich",
            PresentationAccess.Private => "Privat",
            _ => audience switch
            {
                AnnouncementAudience.TruUnit => "Nur TRU",
                AnnouncementAudience.HrbUnit => "Nur HRB",
                AnnouncementAudience.Taskforce => targetId is not null && taskforces.TryGetValue(targetId, out var name)
                    ? $"Taskforce {name}" : "Taskforce",
                AnnouncementAudience.FromRank => minRank is { } rank ? $"Ab {RankDisplay.Name(rank)}" : "Ab Dienstgrad",
                _ => "Geschlossen",
            },
        };
}
