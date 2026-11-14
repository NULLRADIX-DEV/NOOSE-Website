using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Warrants;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Warrants;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IWarrantService" />
public class WarrantService(
    IDbContextFactory<AppDbContext> dbFactory,
    ICaseNumberService caseNumber,
    INotificationService notifications) : IWarrantService
{
    private const string NotFound = "Die Anordnung wurde nicht gefunden.";
    private const string Unknown = "(unbekannt)";

    // ---- reads ----

    public async Task<List<WarrantView>> GetListAsync(WarrantState? state, ClaimsPrincipal viewer,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var query = db.Warrants.AsNoTracking().OnlyVisible(db, ViewerScope.From(viewer));
        if (state is { } wanted)
        {
            query = query.WhereState(wanted, now);
        }
        var rows = await query.OrderByDescending(w => w.CreatedAt).ToListAsync(cancellationToken);
        return await ForViewerAsync(db, await ProjectAsync(db, rows, now, ignoreFilters: false, cancellationToken), viewer, cancellationToken);
    }

    public async Task<List<WarrantView>> GetForInvestigationAsync(string investigationId, ClaimsPrincipal viewer,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Warrants.AsNoTracking().OnlyVisible(db, ViewerScope.From(viewer))
            .Where(w => w.InvestigationId == investigationId)
            .OrderByDescending(w => w.CreatedAt)
            .ToListAsync(cancellationToken);
        return await ForViewerAsync(db, await ProjectAsync(db, rows, DateTime.UtcNow, ignoreFilters: false, cancellationToken), viewer, cancellationToken);
    }

    public async Task<List<WarrantView>> GetToSignAsync(ClaimsPrincipal viewer, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await ProsecutorQueue(db, viewer).OrderBy(w => w.CreatedAt).ToListAsync(cancellationToken);
        var views = await ForViewerAsync(db, await ProjectAsync(db, rows, DateTime.UtcNow, ignoreFilters: false, cancellationToken), viewer, cancellationToken);
        return views.Where(v => v.MaySign).ToList();
    }

    public async Task<int> GetToSignCountAsync(ClaimsPrincipal viewer, CancellationToken cancellationToken = default)
        => (await GetToSignAsync(viewer, cancellationToken)).Count;

    /// <summary>Open requests of investigations where the viewer is the prosecutor; empty for internal accounts.</summary>
    private static IQueryable<Warrant> ProsecutorQueue(AppDbContext db, ClaimsPrincipal viewer)
    {
        var me = viewer.GetAgentId();
        if (!viewer.IsPartner() || me is null)
        {
            return db.Warrants.Where(_ => false);
        }
        return db.Warrants.AsNoTracking().Where(w => w.Status == WarrantStatus.Requested && w.InvestigationId != null
            && db.InvestigationParticipants.Any(p => p.InvestigationId == w.InvestigationId && p.AgentId == me
                && p.Role == InvestigationRole.Prosecutor));
    }

    /// <summary>
    /// Drops warrants of investigations whose warrants tab the viewer may not read, and marks what the viewer may
    /// decide: FIB leadership everywhere, the prosecutor of the investigation for its own warrants; never the requester.
    /// </summary>
    private static async Task<List<WarrantView>> ForViewerAsync(AppDbContext db, List<WarrantView> views, ClaimsPrincipal viewer,
        CancellationToken cancellationToken)
    {
        var me = viewer.GetAgentId();
        var cache = new Dictionary<string, InvestigationAccessView>();
        var result = new List<WarrantView>(views.Count);
        foreach (var view in views)
        {
            var w = view.Warrant;
            var maySign = viewer.MayWarrantSign();
            if (w.InvestigationId is { } invId)
            {
                if (!cache.TryGetValue(invId, out var access))
                {
                    access = await InvestigationAccess.ResolveAsync(db, invId, viewer, cancellationToken);
                    cache[invId] = access;
                }
                if (!access.CanRead(InvestigationTabs.Warrants))
                {
                    continue;
                }
                maySign = maySign || access.MaySignWarrants;
            }
            var own = !string.IsNullOrEmpty(w.CreatedById) && w.CreatedById == me;
            result.Add(view with
            {
                MaySign = maySign && view.State == WarrantState.Requested && !own,
                MayCancel = maySign && view.State == WarrantState.Valid,
                MayDelete = maySign || (own && view.State == WarrantState.Requested),
            });
        }
        return result;
    }

    /// <summary>FIB leadership signs every warrant; the prosecutor of an investigation those filed under it. Read fresh.</summary>
    private static async Task RequireMayDecideAsync(AppDbContext db, Warrant warrant, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        if (actor.MayWarrantSign())
        {
            return;
        }
        if (warrant.InvestigationId is { } invId
            && (await InvestigationAccess.ResolveAsync(db, invId, actor, cancellationToken)).MaySignWarrants)
        {
            return;
        }
        throw new UnauthorizedAccessException("Zeichnen darf die FIB-Führung oder der zuständige Staatsanwalt der Ermittlung.");
    }

    public async Task<List<WarrantView>> GetForPersonAsync(string personId, ClaimsPrincipal viewer,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Warrants.AsNoTracking().OnlyVisible(db, ViewerScope.From(viewer))
            .Where(w => w.PersonId == personId)
            .OrderByDescending(w => w.CreatedAt)
            .ToListAsync(cancellationToken);
        return await ForViewerAsync(db, await ProjectAsync(db, rows, DateTime.UtcNow, ignoreFilters: false, cancellationToken), viewer, cancellationToken);
    }

    public async Task<List<WarrantView>> GetForCaseAsync(string caseId, ClaimsPrincipal viewer,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Warrants.AsNoTracking().OnlyVisible(db, ViewerScope.From(viewer))
            .Where(w => w.CaseId == caseId)
            .OrderByDescending(w => w.CreatedAt)
            .ToListAsync(cancellationToken);
        return await ForViewerAsync(db, await ProjectAsync(db, rows, DateTime.UtcNow, ignoreFilters: false, cancellationToken), viewer, cancellationToken);
    }

    public async Task<List<WarrantView>> GetPendingAsync(ClaimsPrincipal viewer, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Warrants.AsNoTracking().OnlyVisible(db, ViewerScope.From(viewer))
            .Where(w => w.Status == WarrantStatus.Requested)
            .OrderBy(w => w.CreatedAt)
            .ToListAsync(cancellationToken);
        return await ForViewerAsync(db, await ProjectAsync(db, rows, DateTime.UtcNow, ignoreFilters: false, cancellationToken), viewer, cancellationToken);
    }

    public async Task<int> GetOpenCountAsync(ClaimsPrincipal viewer, CancellationToken cancellationToken = default)
    {
        if (!viewer.MayWarrantSign())
        {
            return 0;
        }
        var me = viewer.GetAgentId();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // the own request is not the viewer's to sign, so it is no work for them either
        return await db.Warrants.OnlyVisible(db, ViewerScope.From(viewer))
            .CountAsync(w => w.Status == WarrantStatus.Requested && (w.CreatedById == null || w.CreatedById != me),
                cancellationToken);
    }

    public async Task<WarrantView?> GetAsync(string id, ClaimsPrincipal viewer, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var warrant = await db.Warrants.AsNoTracking().OnlyVisible(db, ViewerScope.From(viewer))
            .FirstOrDefaultAsync(w => w.Id == id, cancellationToken);
        if (warrant is null)
        {
            return null;
        }

        var ids = WarrantRules.LawIdsOf(warrant).ToList();
        var laws = ids.Count == 0
            ? new Dictionary<string, string>()
            : (await db.Laws.AsNoTracking().Where(l => ids.Contains(l.Id)).ToListAsync(cancellationToken))
                .ToDictionary(l => l.Id, WarrantRules.LawLabel);
        // a deleted paragraph drops out of the links; the snapshot on the paper keeps its wording
        var cited = ids.Where(laws.ContainsKey).Select(i => new WarrantLaw(i, laws[i])).ToList();

        var view = (await ForViewerAsync(db, await ProjectAsync(db, [warrant], DateTime.UtcNow, ignoreFilters: false, cancellationToken),
            viewer, cancellationToken)).SingleOrDefault();
        return view is null ? null : view with { Laws = cited };
    }

    public async Task<WarrantTarget?> GetTargetAsync(string type, string id, ClaimsPrincipal viewer,
        CancellationToken cancellationToken = default)
    {
        var scope = ViewerScope.From(viewer);
        if (!scope.IsInternalAgent || scope.IsPartner || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return type switch
        {
            nameof(Person) => await db.People.AsNoTracking().OnlyVisible(scope)
                .Where(p => p.Id == id)
                .Select(p => new WarrantTarget(p.Id, p.Name, p.CaseNumber))
                .FirstOrDefaultAsync(cancellationToken),
            nameof(Case) => await db.Cases.AsNoTracking().OnlyVisible(scope)
                .Where(c => c.Id == id)
                .Select(c => new WarrantTarget(c.Id, c.Title, c.CaseNumber))
                .FirstOrDefaultAsync(cancellationToken),
            _ => null,
        };
    }

    public async Task<List<WarrantTarget>> GetInvestigationTargetsAsync(string investigationId, ClaimsPrincipal viewer,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var access = await InvestigationAccess.ResolveAsync(db, investigationId, viewer, cancellationToken);
        if (!access.CanEdit(InvestigationTabs.Warrants))
        {
            return new();
        }
        return await db.InvestigationPeople.AsNoTracking()
            .Where(p => p.InvestigationId == investigationId && p.PersonId != null)
            .Join(db.People.AsNoTracking(), p => p.PersonId, x => x.Id, (p, x) => new WarrantTarget(x.Id, x.Name, x.CaseNumber))
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    // ---- writes ----

    public async Task<Warrant> CreateAsync(WarrantInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var investigationId = input.InvestigationId.TrimToNull();
        if (investigationId is null)
        {
            Permission.RequireWarrantRequest(actor);
        }
        else if (actor.IsTeamLead() || !actor.MayContribute())
        {
            throw new UnauthorizedAccessException("Du darfst keine Anordnung beantragen.");
        }
        if (!Enum.IsDefined(input.Type))
        {
            throw new InvalidOperationException("Bitte eine Art der Anordnung wählen.");
        }
        var subject = input.Subject.TrimToNull()
            ?? throw new InvalidOperationException(
                "Bitte angeben, was durchsucht oder beschlagnahmt oder wer festgenommen werden soll.");
        if (subject.Length > WarrantRules.SubjectMaxLength)
        {
            throw new InvalidOperationException(
                $"Der Gegenstand darf höchstens {WarrantRules.SubjectMaxLength} Zeichen lang sein.");
        }
        var justification = input.Justification.TrimToNull()
            ?? throw new InvalidOperationException("Bitte eine Begründung angeben.");
        if (justification.Length > WarrantRules.JustificationMaxLength)
        {
            throw new InvalidOperationException(
                $"Die Begründung darf höchstens {WarrantRules.JustificationMaxLength} Zeichen lang sein.");
        }
        if (!WarrantRules.IsValidityChoice(input.ValidityDays))
        {
            throw new InvalidOperationException("Bitte eine der angebotenen Gültigkeitsdauern wählen.");
        }
        var lawIds = input.LawIds
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Select(i => i.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (lawIds.Count == 0)
        {
            throw new InvalidOperationException("Bitte mindestens eine Rechtsgrundlage aus dem Gesetzbuch wählen.");
        }
        if (lawIds.Count > WarrantRules.MaxLaws)
        {
            throw new InvalidOperationException($"Höchstens {WarrantRules.MaxLaws} Rechtsgrundlagen je Anordnung.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var scope = ViewerScope.From(actor);
        var personId = input.PersonId.TrimToNull();
        if (investigationId is not null)
        {
            var access = await InvestigationAccess.ResolveAsync(db, investigationId, actor, cancellationToken);
            InvestigationAccess.RequireEdit(access, InvestigationTabs.Warrants);
            // from an investigation the target is one of its people with a person file
            if (personId is null || !await db.InvestigationPeople.AnyAsync(
                    p => p.InvestigationId == investigationId && p.PersonId == personId, cancellationToken))
            {
                throw new InvalidOperationException("Die Person steht nicht mit Personenakte in dieser Ermittlung.");
            }
        }
        // same answer for "missing" and "not yours to see"
        if (personId is null || (!scope.IsPartner && !await db.People.OnlyVisible(scope).AnyAsync(p => p.Id == personId, cancellationToken)))
        {
            throw new InvalidOperationException("Die Person wurde nicht gefunden.");
        }
        var caseId = scope.IsPartner ? null : input.CaseId.TrimToNull();
        if (caseId is not null && !await db.Cases.OnlyVisible(scope).AnyAsync(c => c.Id == caseId, cancellationToken))
        {
            throw new InvalidOperationException("Der Vorgang wurde nicht gefunden.");
        }
        var laws = await db.Laws.AsNoTracking().Where(l => lawIds.Contains(l.Id)).ToListAsync(cancellationToken);
        if (laws.Count != lawIds.Count)
        {
            throw new InvalidOperationException("Ein gewählter Paragraf existiert nicht mehr.");
        }
        var byId = laws.ToDictionary(l => l.Id);

        var warrant = new Warrant
        {
            Type = input.Type,
            Status = WarrantStatus.Requested,
            PersonId = personId,
            CaseId = caseId,
            Subject = subject,
            Justification = justification,
            LegalBasis = string.Join("\n", lawIds.Select(i => WarrantRules.LawLabel(byId[i]))),
            LawIds = string.Join(",", lawIds),
            ValidityDays = input.ValidityDays,
            RequesterName = actor.GetCodename(),
            InvestigationId = investigationId,
        };

        // case-number allocation needs an enclosing transaction so counter and record commit together
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        warrant.CaseNumber = await caseNumber.NextAsync(db, WarrantRules.CaseNumberPrefix, cancellationToken);
        db.Warrants.Add(warrant);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        if (investigationId is not null)
        {
            await NotifyProsecutorsAsync(db, warrant, actor, cancellationToken);
        }
        return warrant;
    }

    /// <summary>Tells the prosecutors of the investigation a request waits for them; bell only, case number only.</summary>
    private async Task NotifyProsecutorsAsync(AppDbContext db, Warrant warrant, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        try
        {
            var prosecutors = await db.InvestigationParticipants.AsNoTracking()
                .Where(p => p.InvestigationId == warrant.InvestigationId && p.Role == InvestigationRole.Prosecutor && p.AgentId != null)
                .Select(p => p.AgentId!)
                .ToListAsync(cancellationToken);
            await notifications.NotifyManyInAppAsync(prosecutors, NotificationType.WarrantToSign,
                $"Anordnung {warrant.CaseNumber} wartet auf deine Zeichnung.", $"/anordnungen/{warrant.Id}", actor.GetAgentId(), cancellationToken);
        }
        catch { /* best effort */ }
    }

    public async Task SignAsync(string id, int validityDays, string? note, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        if (!WarrantRules.IsValidityChoice(validityDays))
        {
            throw new InvalidOperationException("Bitte eine der angebotenen Gültigkeitsdauern wählen.");
        }
        var trimmed = Note(note);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var warrant = await OpenRequestAsync(db, id, actor, cancellationToken);
        await RequireMayDecideAsync(db, warrant, actor, cancellationToken);
        if (!string.IsNullOrEmpty(warrant.CreatedById) && warrant.CreatedById == actor.GetAgentId())
        {
            throw new InvalidOperationException(
                "Die eigene Anordnung unterzeichnet jemand anderes (Vier-Augen-Prinzip).");
        }

        var now = DateTime.UtcNow;
        warrant.Status = WarrantStatus.Signed;
        warrant.ValidityDays = validityDays;
        warrant.ValidFrom = now;
        warrant.ValidUntil = now.AddDays(validityDays);
        await StampAsync(db, warrant, actor, now, trimmed, cancellationToken);
        await SaveAsync(db, cancellationToken);

        await NotifyRequesterAsync(warrant, actor, $"Deine Anordnung {warrant.CaseNumber} wurde unterzeichnet.", cancellationToken);
    }

    public async Task RejectAsync(string id, string? note, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var trimmed = Note(note);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var warrant = await OpenRequestAsync(db, id, actor, cancellationToken);
        await RequireMayDecideAsync(db, warrant, actor, cancellationToken);
        warrant.Status = WarrantStatus.Rejected;
        await StampAsync(db, warrant, actor, DateTime.UtcNow, trimmed, cancellationToken);
        await SaveAsync(db, cancellationToken);

        await NotifyRequesterAsync(warrant, actor, $"Deine Anordnung {warrant.CaseNumber} wurde abgelehnt.", cancellationToken);
    }

    public async Task CancelAsync(string id, string reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var trimmed = reason.TrimToNull()
            ?? throw new InvalidOperationException("Bitte einen Grund für die Aufhebung angeben.");
        if (trimmed.Length > WarrantRules.NoteMaxLength)
        {
            throw new InvalidOperationException($"Der Grund darf höchstens {WarrantRules.NoteMaxLength} Zeichen lang sein.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var warrant = await db.Warrants.OnlyVisible(db, ViewerScope.From(actor))
            .FirstOrDefaultAsync(w => w.Id == id, cancellationToken)
            ?? throw new InvalidOperationException(NotFound);
        await RequireMayDecideAsync(db, warrant, actor, cancellationToken);
        var now = DateTime.UtcNow;
        if (WarrantRules.StateAt(warrant, now) != WarrantState.Valid)
        {
            throw new InvalidOperationException("Aufheben lässt sich nur eine gültige Anordnung.");
        }

        warrant.Status = WarrantStatus.Cancelled;
        warrant.CancelledAt = now;
        warrant.CancelledByName = actor.GetCodename();
        warrant.CancellationReason = trimmed;
        await SaveAsync(db, cancellationToken);

        await NotifyRequesterAsync(warrant, actor, $"Deine Anordnung {warrant.CaseNumber} wurde aufgehoben.", cancellationToken);
    }

    public async Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        if (!actor.IsPartner())
        {
            Permission.RequireWarrantRequest(actor);
        }
        else if (!actor.MayContribute())
        {
            throw new UnauthorizedAccessException("Du darfst keine Anordnung löschen.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var warrant = await db.Warrants.OnlyVisible(db, ViewerScope.From(actor))
            .FirstOrDefaultAsync(w => w.Id == id, cancellationToken);
        if (warrant is null)
        {
            return;
        }
        var ownAndOpen = warrant.Status == WarrantStatus.Requested
            && !string.IsNullOrEmpty(warrant.CreatedById) && warrant.CreatedById == actor.GetAgentId();
        if (!actor.MayWarrantSign() && !ownAndOpen)
        {
            throw new UnauthorizedAccessException(
                "Löschen darf die Führung oder der Antragsteller, solange sein Antrag noch offen ist.");
        }
        // interceptor rewrites Remove to a soft delete; the status token stops a requester deleting a fresh signature
        db.Warrants.Remove(warrant);
        await SaveAsync(db, cancellationToken);
    }

    public async Task<List<WarrantView>> GetTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Warrants.AsNoTracking().IgnoreQueryFilters()
            .Where(w => w.IsDeleted)
            .OrderByDescending(w => w.DeletedAt)
            .ToListAsync(cancellationToken);
        return await ProjectAsync(db, rows, DateTime.UtcNow, ignoreFilters: true, cancellationToken);
    }

    public async Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWarrantSign(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var warrant = await db.Warrants.IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.Id == id && w.IsDeleted, cancellationToken)
            ?? throw new InvalidOperationException(NotFound);
        warrant.IsDeleted = false;
        warrant.DeletedAt = null;
        warrant.DeletedById = null;
        await SaveAsync(db, cancellationToken);
    }

    // ---- helpers ----

    /// <summary>Loads a visible warrant that still waits for a decision, tracked for the update.</summary>
    private static async Task<Warrant> OpenRequestAsync(AppDbContext db, string id, ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var warrant = await db.Warrants.OnlyVisible(db, ViewerScope.From(actor))
            .FirstOrDefaultAsync(w => w.Id == id, cancellationToken)
            ?? throw new InvalidOperationException(NotFound);
        if (warrant.Status != WarrantStatus.Requested)
        {
            throw new InvalidOperationException("Über diese Anordnung wurde bereits entschieden.");
        }
        return warrant;
    }

    private static string? Note(string? note)
    {
        var trimmed = note.TrimToNull();
        if (trimmed is { Length: > WarrantRules.NoteMaxLength })
        {
            throw new InvalidOperationException($"Die Notiz darf höchstens {WarrantRules.NoteMaxLength} Zeichen lang sein.");
        }
        return trimmed;
    }

    /// <summary>Who decided; a partner signer gets the unit as function instead of a FIB rank, read fresh.</summary>
    private static async Task StampAsync(AppDbContext db, Warrant warrant, ClaimsPrincipal actor, DateTime now, string? note,
        CancellationToken cancellationToken)
    {
        warrant.DeciderId = actor.GetAgentId();
        warrant.DeciderName = actor.GetCodename();
        warrant.DeciderRank = actor.GetRank();
        warrant.DeciderFunction = null;
        if (actor.IsPartner())
        {
            var me = await db.Users.AsNoTracking().Where(u => u.Id == warrant.DeciderId)
                .Select(u => new { u.PartnerAgency, u.PartnerUnitId }).FirstOrDefaultAsync(cancellationToken);
            var unit = me?.PartnerUnitId is null ? null
                : await db.PartnerUnits.AsNoTracking().Where(u => u.Id == me.PartnerUnitId).Select(u => u.Name).FirstOrDefaultAsync(cancellationToken);
            warrant.DeciderRank = null;
            warrant.DeciderFunction = PartnerUnitDisplay.Full(me?.PartnerAgency, unit);
        }
        warrant.DecidedAt = now;
        warrant.DecisionNote = note;
    }

    private static async Task SaveAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new InvalidOperationException(
                "Diese Anordnung wurde gerade von jemand anderem geändert. Bitte die Seite neu laden.");
        }
    }

    private async Task NotifyRequesterAsync(Warrant warrant, ClaimsPrincipal actor, string title,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(warrant.CreatedById) || warrant.CreatedById == actor.GetAgentId())
        {
            return;
        }
        try
        {
            // the title names the case number only: the bell may be mirrored to a channel
            await notifications.NotifyAsync(warrant.CreatedById, NotificationType.RequestDecided, title,
                $"/anordnungen/{warrant.Id}", cancellationToken);
        }
        catch { /* best effort */ }
    }

    private static async Task<List<WarrantView>> ProjectAsync(AppDbContext db, IReadOnlyCollection<Warrant> rows,
        DateTime nowUtc, bool ignoreFilters, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return new();
        }
        var personIds = rows.Select(w => w.PersonId).Distinct().ToList();
        var caseIds = rows.Where(w => w.CaseId != null).Select(w => w.CaseId!).Distinct().ToList();

        // flat lookups by id; the trash keeps names of records that went to the trash themselves
        var people = ignoreFilters ? db.People.IgnoreQueryFilters() : db.People;
        var persons = await people.AsNoTracking()
            .Where(p => personIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.CaseNumber })
            .ToDictionaryAsync(p => p.Id, cancellationToken);
        var casesQuery = ignoreFilters ? db.Cases.IgnoreQueryFilters() : db.Cases;
        var cases = caseIds.Count == 0
            ? new Dictionary<string, (string Title, string CaseNumber)>()
            : (await casesQuery.AsNoTracking()
                .Where(c => caseIds.Contains(c.Id))
                .Select(c => new { c.Id, c.Title, c.CaseNumber })
                .ToListAsync(cancellationToken))
                .ToDictionary(c => c.Id, c => (c.Title, c.CaseNumber));

        return rows.Select(w =>
        {
            persons.TryGetValue(w.PersonId, out var person);
            (string Title, string CaseNumber)? @case = w.CaseId is { } cid && cases.TryGetValue(cid, out var found)
                ? found
                : null;
            return new WarrantView(w, WarrantRules.StateAt(w, nowUtc),
                person?.Name ?? Unknown, person?.CaseNumber ?? string.Empty,
                @case?.Title, @case?.CaseNumber);
        }).ToList();
    }
}
