using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Investigations;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Investigations;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IInvestigationService" />
public partial class InvestigationService(
    IDbContextFactory<AppDbContext> dbFactory,
    ICaseNumberService caseNumber,
    INotificationService notifications) : IInvestigationService
{
    public const string CaseNumberPrefix = "ER";
    private const string NotFound = "Die Ermittlung wurde nicht gefunden.";

    /// <summary>Entry types a participant can delete or limit, with the tab they live on.</summary>
    public static readonly IReadOnlyDictionary<string, string> EntryTabs = new Dictionary<string, string>
    {
        [nameof(InvestigationCharge)] = InvestigationTabs.Charges,
        [nameof(InvestigationPerson)] = InvestigationTabs.People,
        [nameof(InvestigationEvidence)] = InvestigationTabs.Evidence,
        [nameof(InvestigationInterrogation)] = InvestigationTabs.Interrogations,
        [nameof(InvestigationTask)] = InvestigationTabs.Tasks,
        [nameof(InvestigationNote)] = InvestigationTabs.Notes,
    };

    // ---- reading ----

    public async Task<List<InvestigationListItem>> GetListAsync(ClaimsPrincipal viewer, string? taskforceId = null, string? personId = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Investigations.AsNoTracking().OnlyVisible(db, ViewerScope.From(viewer));
        if (taskforceId is not null)
        {
            query = query.Where(i => i.TaskforceId == taskforceId);
        }
        if (personId is not null)
        {
            query = query.Where(i => db.InvestigationPeople.Any(p => p.InvestigationId == i.Id && p.PersonId == personId));
        }
        var rows = await query
            .OrderByDescending(i => i.OpenedAt)
            .Select(i => new { i.Id, i.CaseNumber, i.Title, i.Status, i.TaskforceId, i.SidCaseNumber, i.OpenedAt })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return new();
        }
        var ids = rows.Select(r => r.Id).ToList();
        var tfIds = rows.Select(r => r.TaskforceId).Distinct().ToList();
        var taskforces = await db.Taskforces.AsNoTracking().IgnoreQueryFilters()
            .Where(t => tfIds.Contains(t.Id)).Select(t => new { t.Id, t.Name }).ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);
        var participants = await db.InvestigationParticipants.AsNoTracking()
            .Where(p => ids.Contains(p.InvestigationId))
            .Select(p => new { p.InvestigationId, p.Role, p.AgentId })
            .ToListAsync(cancellationToken);
        var names = await NamesAsync(db, participants.Where(p => p.Role == InvestigationRole.Prosecutor).Select(p => p.AgentId!), cancellationToken);
        return rows.Select(r => new InvestigationListItem(
                r.Id, r.CaseNumber, r.Title, r.Status, r.TaskforceId, taskforces.GetValueOrDefault(r.TaskforceId), r.SidCaseNumber, r.OpenedAt,
                participants.Where(p => p.InvestigationId == r.Id && p.Role == InvestigationRole.Prosecutor && p.AgentId != null)
                    .Select(p => names.GetValueOrDefault(p.AgentId!) ?? "—").ToList(),
                participants.Count(p => p.InvestigationId == r.Id)))
            .ToList();
    }

    public async Task<InvestigationAccessView> GetAccessAsync(string id, ClaimsPrincipal viewer, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await InvestigationAccess.ResolveAsync(db, id, viewer, cancellationToken);
    }

    public async Task<InvestigationDetail?> GetAsync(string id, ClaimsPrincipal viewer, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var access = await InvestigationAccess.ResolveAsync(db, id, viewer, cancellationToken);
        if (!access.CanSee)
        {
            return null;
        }
        var inv = await db.Investigations.AsNoTracking().FirstAsync(i => i.Id == id, cancellationToken);
        var taskforce = await db.Taskforces.AsNoTracking().IgnoreQueryFilters()
            .Where(t => t.Id == inv.TaskforceId).Select(t => new { t.Name, t.CaseNumber }).FirstOrDefaultAsync(cancellationToken);
        var tfVisible = await Visibility.IsRecordVisibleAsync(db, nameof(Taskforce), inv.TaskforceId, ViewerScope.From(viewer), cancellationToken);

        var restrictions = await db.InvestigationItemRestrictions.AsNoTracking()
            .Where(r => r.InvestigationId == id).ToListAsync(cancellationToken);
        var hidden = new HashSet<string>();
        foreach (var type in EntryTabs.Keys)
        {
            hidden.UnionWith(await InvestigationAccess.HiddenItemIdsAsync(db, id, type, access, cancellationToken));
        }
        bool Shows(string itemId) => !hidden.Contains(itemId);

        var allParticipants = await db.InvestigationParticipants.AsNoTracking()
            .Where(p => p.InvestigationId == id).ToListAsync(cancellationToken);
        var labels = await TargetLabelsAsync(db, allParticipants.Select(p => p.AgentId), allParticipants.Select(p => p.PartnerUnitId), cancellationToken);
        var prosecutors = allParticipants.Where(p => p.Role == InvestigationRole.Prosecutor && p.AgentId != null)
            .Select(p => labels.GetValueOrDefault(p.AgentId!) ?? "—").ToList();

        var detail = new InvestigationDetail
        {
            Id = inv.Id,
            CaseNumber = inv.CaseNumber,
            Title = inv.Title,
            Status = inv.Status,
            TaskforceId = inv.TaskforceId,
            TaskforceName = taskforce?.Name,
            TaskforceCaseNumber = taskforce?.CaseNumber,
            TaskforceVisible = tfVisible,
            SidCaseNumber = inv.SidCaseNumber,
            FiveNetCaseNumber = inv.FiveNetCaseNumber,
            OpenedAt = inv.OpenedAt,
            Indictment = inv.Indictment,
            CreatedAt = inv.CreatedAt,
            NarrativeHtml = access.CanRead(InvestigationTabs.Narrative) ? inv.NarrativeHtml : null,
            Access = access,
            Prosecutors = prosecutors,
            Charges = !access.CanRead(InvestigationTabs.Charges) ? [] : (await db.InvestigationCharges.AsNoTracking()
                    .Where(c => c.InvestigationId == id).OrderBy(c => c.SortOrder).ThenBy(c => c.CreatedAt).ToListAsync(cancellationToken))
                .Where(c => Shows(c.Id))
                .Select(c => new InvestigationChargeView(c.Id, c.LawId, c.Paragraph, c.LawBook, c.Description, c.Check, c.SortOrder)).ToList(),
            People = !access.CanRead(InvestigationTabs.People) ? [] : await PeopleAsync(db, id, Shows, cancellationToken),
            Evidence = !access.CanRead(InvestigationTabs.Evidence) ? [] : (await db.InvestigationEvidence.AsNoTracking()
                    .Where(e => e.InvestigationId == id).OrderBy(e => e.Number).ToListAsync(cancellationToken))
                .Where(e => Shows(e.Id))
                .Select(e => new InvestigationEvidenceView(e.Id, e.Number, e.Name, e.Description, e.Owner, e.Officer, e.Status, e.SecuredAt)).ToList(),
            Interrogations = !access.CanRead(InvestigationTabs.Interrogations) ? [] : (await db.InvestigationInterrogations.AsNoTracking()
                    .Include(v => v.Entries)
                    .Where(v => v.InvestigationId == id).OrderBy(v => v.HeldAt ?? v.CreatedAt).ToListAsync(cancellationToken))
                .Where(v => Shows(v.Id))
                .Select(v => new InvestigationInterrogationView(v.Id, v.InvestigationPersonId, v.Name, v.Role, v.Summons, v.HeldAt, v.Officers,
                    v.ProsecutorPresent, v.Summary, v.ProtocolReference, v.Usable,
                    v.Entries.OrderBy(e => e.SortOrder).Select(e => new InvestigationQa(e.Question, e.Answer)).ToList())).ToList(),
            Tasks = !access.CanRead(InvestigationTabs.Tasks) ? [] : await TasksAsync(db, id, Shows, cancellationToken),
            Notes = !access.CanRead(InvestigationTabs.Notes) ? [] : (await db.InvestigationNotes.AsNoTracking()
                    .Where(n => n.InvestigationId == id).OrderByDescending(n => n.CreatedAt).ToListAsync(cancellationToken))
                .Where(n => Shows(n.Id))
                .Select(n => new InvestigationNoteView(n.Id, n.Text, n.AuthorName, n.CreatedById, n.CreatedAt)).ToList(),
            Participants = !access.CanRead(InvestigationTabs.Participants) ? [] : await ParticipantViewsAsync(db, allParticipants, labels, cancellationToken),
            RestrictedIds = restrictions.Select(r => r.ItemId).Where(Shows).ToHashSet(),
            Restrictions = !access.MayManage ? new Dictionary<string, IReadOnlyList<InvestigationTarget>>() : restrictions
                .GroupBy(r => r.ItemId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<InvestigationTarget>)g
                    .Select(r => new InvestigationTarget(r.AgentId, r.PartnerUnitId,
                        labels.GetValueOrDefault(r.AgentId ?? r.PartnerUnitId ?? string.Empty) ?? "—")).ToList()),
        };
        return detail;
    }

    private static async Task<List<InvestigationPersonView>> PeopleAsync(AppDbContext db, string id, Func<string, bool> shows, CancellationToken cancellationToken)
    {
        var rows = await db.InvestigationPeople.AsNoTracking().Where(p => p.InvestigationId == id)
            .OrderBy(p => p.CreatedAt).ToListAsync(cancellationToken);
        var personIds = rows.Where(r => r.PersonId != null).Select(r => r.PersonId!).Distinct().ToList();
        var files = await db.People.AsNoTracking().Where(p => personIds.Contains(p.Id))
            .Select(p => new { p.Id, p.CaseNumber }).ToDictionaryAsync(p => p.Id, p => p.CaseNumber, cancellationToken);
        return rows.Where(r => shows(r.Id))
            .Select(r => new InvestigationPersonView(r.Id, r.Name, r.PersonId, r.PersonId is null ? null : files.GetValueOrDefault(r.PersonId),
                r.Phone, r.Role, r.Status, r.Note))
            .ToList();
    }

    private static async Task<List<InvestigationTaskView>> TasksAsync(AppDbContext db, string id, Func<string, bool> shows, CancellationToken cancellationToken)
    {
        var rows = await db.InvestigationTasks.AsNoTracking().Where(t => t.InvestigationId == id)
            .OrderBy(t => t.Done).ThenBy(t => t.SortOrder).ThenBy(t => t.CreatedAt).ToListAsync(cancellationToken);
        var names = await NamesAsync(db, rows.Where(r => r.AssigneeId != null).Select(r => r.AssigneeId!), cancellationToken);
        return rows.Where(r => shows(r.Id))
            .Select(r => new InvestigationTaskView(r.Id, r.Text, r.Done, r.DoneAt, r.AssigneeId,
                r.AssigneeId is null ? null : names.GetValueOrDefault(r.AssigneeId), r.DueAt, r.SortOrder))
            .ToList();
    }

    private static async Task<List<InvestigationParticipantView>> ParticipantViewsAsync(AppDbContext db, List<InvestigationParticipant> rows,
        IReadOnlyDictionary<string, string> labels, CancellationToken cancellationToken)
    {
        var agentIds = rows.Where(r => r.AgentId != null).Select(r => r.AgentId!).ToList();
        var agents = await db.Users.AsNoTracking().Where(a => agentIds.Contains(a.Id))
            .Select(a => new { a.Id, a.PartnerAgency, a.PartnerUnitId }).ToDictionaryAsync(a => a.Id, cancellationToken);
        var unitIds = rows.Select(r => r.PartnerUnitId).Concat(agents.Values.Select(a => a.PartnerUnitId)).Where(u => u != null).Distinct().ToList();
        var units = await db.PartnerUnits.AsNoTracking().IgnoreQueryFilters().Where(u => unitIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, cancellationToken);
        return rows
            .OrderBy(r => r.PartnerUnitId is null ? 0 : 1).ThenBy(r => r.Role == InvestigationRole.Lead ? 0 : 1)
            .Select(r =>
            {
                var agent = r.AgentId is null ? null : agents.GetValueOrDefault(r.AgentId);
                var unitKey = r.PartnerUnitId ?? agent?.PartnerUnitId;
                var unit = unitKey is null ? null : units.GetValueOrDefault(unitKey);
                var isPartner = r.PartnerUnitId is not null || agent?.PartnerAgency is not null;
                var unitDefaults = unit is null ? new Dictionary<string, InvestigationAccessLevel>()
                    : InvestigationTabAccess.Parse(unit.InvestigationDefaultsJson);
                var defaults = InvestigationTabs.Slugs.ToDictionary(s => s, s =>
                {
                    var probe = new InvestigationParticipant { Role = r.Role, PartnerUnitId = r.PartnerUnitId };
                    return r.Role == InvestigationRole.Lead ? InvestigationAccessLevel.Edit
                        : InvestigationTabs.Cap(s, InvestigationAccess.RowLevel(probe, s, isPartner, unitDefaults));
                });
                var detail = r.PartnerUnitId is not null ? "ganze Einheit"
                    : agent?.PartnerAgency is { } agency ? PartnerUnitDisplay.Full(agency, unit?.Name) : "FIB";
                return new InvestigationParticipantView(r.Id, r.AgentId, r.PartnerUnitId,
                    labels.GetValueOrDefault(r.AgentId ?? r.PartnerUnitId ?? string.Empty) ?? "—", detail, r.Role, r.PartnerUnitId is not null,
                    InvestigationTabAccess.Parse(r.TabAccessJson), defaults);
            })
            .ToList();
    }

    // ---- creating ----

    public async Task<bool> MayCreateAsync(ClaimsPrincipal viewer, CancellationToken cancellationToken = default)
        => (await GetCreatableTaskforcesAsync(viewer, cancellationToken)).Count > 0;

    public async Task<List<(string Id, string Label)>> GetCreatableTaskforcesAsync(ClaimsPrincipal viewer, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = await CreatableTaskforcesAsync(db, viewer, cancellationToken);
        if (query is null)
        {
            return new();
        }
        return (await query.OrderBy(t => t.Name).Select(t => new { t.Id, t.Name, t.CaseNumber }).ToListAsync(cancellationToken))
            .Select(t => (t.Id, $"{t.Name} ({t.CaseNumber})")).ToList();
    }

    /// <summary>Taskforces the actor may open an investigation on; null when not at all.</summary>
    private static async Task<IQueryable<Taskforce>?> CreatableTaskforcesAsync(AppDbContext db, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var meId = actor.GetAgentId();
        if (meId is null)
        {
            return null;
        }
        var active = db.Taskforces.Where(t => !t.IsArchived && t.Status != TaskforceStatus.Rejected && t.Status != TaskforceStatus.Resolved);
        if (!actor.IsPartner())
        {
            if (!actor.MayWrite() || !actor.IsInternalAgent())
            {
                return null;
            }
            return actor.IsLeadership() ? active : active.Where(t => db.TaskforceAgents.Any(ta => ta.TaskforceId == t.Id && ta.AgentId == meId));
        }
        if (!actor.MayContribute() || actor.IsDemo())
        {
            return null;
        }
        var me = await db.Users.AsNoTracking().Where(u => u.Id == meId)
            .Select(u => new { u.PartnerAgency, u.PartnerUnitId }).FirstOrDefaultAsync(cancellationToken);
        if (me?.PartnerAgency is not { } agency
            || !await db.PartnerUnits.AnyAsync(u => u.Id == me.PartnerUnitId && u.MayCreateInvestigations, cancellationToken))
        {
            return null;
        }
        return active.Where(t => !t.IsClassified && t.Scope == TaskforceScope.CrossAgency
            && db.TaskforcePartnerAgencies.Any(tp => tp.TaskforceId == t.Id && tp.Agency == agency));
    }

    public Task<string> CreateAsync(InvestigationInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
        => CreateWithContentAsync(input, null, [], [], [], [], [], [], actor, cancellationToken);

    public async Task<string> CreateWithContentAsync(InvestigationInput input, string? narrativeHtml,
        IReadOnlyList<InvestigationChargeInput> charges, IReadOnlyList<InvestigationPersonInput> people,
        IReadOnlyList<InvestigationEvidenceInput> evidence, IReadOnlyList<InvestigationInterrogationInput> interrogations,
        IReadOnlyList<(string Text, bool Done)> tasks, IReadOnlyList<string> notes,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var creatable = await CreatableTaskforcesAsync(db, actor, cancellationToken)
            ?? throw new UnauthorizedAccessException("Du darfst keine Ermittlung anlegen.");
        if (!await creatable.AnyAsync(t => t.Id == input.TaskforceId, cancellationToken))
        {
            throw new UnauthorizedAccessException("An dieser Taskforce darfst du keine Ermittlung anlegen.");
        }
        var meId = actor.GetAgentId()!;
        var inv = new Investigation();
        Apply(inv, input);
        inv.NarrativeHtml = CleanNarrative(narrativeHtml);
        inv.Participants.Add(new InvestigationParticipant { InvestigationId = inv.Id, AgentId = meId, Role = InvestigationRole.Lead });

        var order = 0;
        foreach (var c in charges)
        {
            db.InvestigationCharges.Add(ApplyCharge(new InvestigationCharge { InvestigationId = inv.Id, SortOrder = ++order }, c));
        }
        var personIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in people)
        {
            var row = await ApplyPersonAsync(db, new InvestigationPerson { InvestigationId = inv.Id }, p, cancellationToken);
            db.InvestigationPeople.Add(row);
            personIds.TryAdd(row.Name, row.Id);
        }
        var number = 0;
        foreach (var e in evidence)
        {
            db.InvestigationEvidence.Add(ApplyEvidence(new InvestigationEvidence { InvestigationId = inv.Id, Number = ++number }, e));
        }
        foreach (var v in interrogations)
        {
            var row = ApplyInterrogation(new InvestigationInterrogation { InvestigationId = inv.Id }, v);
            row.InvestigationPersonId ??= personIds.GetValueOrDefault(row.Name);
            db.InvestigationInterrogations.Add(row);
        }
        order = 0;
        foreach (var (text, done) in tasks.Where(t => !string.IsNullOrWhiteSpace(t.Text)))
        {
            db.InvestigationTasks.Add(new InvestigationTask
            {
                InvestigationId = inv.Id, Text = Clip(text, 500)!, SortOrder = ++order,
                Done = done, DoneAt = done ? DateTime.UtcNow : null, DoneById = done ? meId : null,
            });
        }
        foreach (var note in notes.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            db.InvestigationNotes.Add(new InvestigationNote { InvestigationId = inv.Id, Text = Clip(note, 4000)!, AuthorName = actor.GetCodename() });
        }

        // case-number allocation needs an enclosing transaction so counter and record commit together
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        inv.CaseNumber = await caseNumber.NextAsync(db, CaseNumberPrefix, cancellationToken);
        db.Investigations.Add(inv);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return inv.Id;
    }

    // ---- cover sheet and narrative ----

    public async Task UpdateAsync(string id, InvestigationInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var access = await InvestigationAccess.ResolveAsync(db, id, actor, cancellationToken);
        InvestigationAccess.RequireEdit(access, InvestigationTabs.Overview);
        var inv = await db.Investigations.FirstOrDefaultAsync(i => i.Id == id, cancellationToken) ?? throw new KeyNotFoundException(NotFound);
        if (input.TaskforceId != inv.TaskforceId)
        {
            // moving takes leadership: the new taskforce changes who sees the file
            if (!actor.IsLeadership() || !actor.MayWrite())
            {
                throw new UnauthorizedAccessException("Eine Ermittlung hängt nur die Führung an eine andere Taskforce.");
            }
            if (!await db.Taskforces.AnyAsync(t => t.Id == input.TaskforceId, cancellationToken))
            {
                throw new InvalidOperationException("Diese Taskforce gibt es nicht.");
            }
        }
        Apply(inv, input);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetNarrativeAsync(string id, string? html, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var access = await InvestigationAccess.ResolveAsync(db, id, actor, cancellationToken);
        InvestigationAccess.RequireEdit(access, InvestigationTabs.Narrative);
        var inv = await db.Investigations.FirstOrDefaultAsync(i => i.Id == id, cancellationToken) ?? throw new KeyNotFoundException(NotFound);
        inv.NarrativeHtml = CleanNarrative(html);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static void Apply(Investigation inv, InvestigationInput input)
    {
        var title = (input.Title ?? string.Empty).Trim();
        if (title.Length == 0)
        {
            throw new InvalidOperationException("Die Ermittlung braucht einen Titel.");
        }
        inv.Title = Clip(title, 200)!;
        inv.TaskforceId = input.TaskforceId;
        inv.SidCaseNumber = Clip(input.SidCaseNumber, 64);
        inv.FiveNetCaseNumber = Clip(input.FiveNetCaseNumber, 64);
        inv.Status = input.Status;
        inv.OpenedAt = input.OpenedAt == default ? DateTime.UtcNow : input.OpenedAt;
        inv.Indictment = Clip(input.Indictment, 1000);
    }

    /// <summary>Sanitized rich text without images: the image endpoint is internal-only.</summary>
    internal static string? CleanNarrative(string? html)
    {
        var clean = ImageTag().Replace(HtmlCleanup.Clean(html), string.Empty);
        return string.IsNullOrWhiteSpace(HtmlCleanup.PlainText(clean)) ? null : clean;
    }

    [GeneratedRegex("<img\\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ImageTag();

    // ---- tab entries ----

    public Task<string> SaveChargeAsync(string id, InvestigationChargeInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
        => SaveRowAsync(id, input.Id, InvestigationTabs.Charges, actor, db => db.InvestigationCharges,
            async (db, row, isNew) =>
            {
                if (isNew)
                {
                    row.SortOrder = await db.InvestigationCharges.Where(c => c.InvestigationId == id).CountAsync(cancellationToken) + 1;
                }
                ApplyCharge(row, input);
            }, cancellationToken);

    public Task<string> SavePersonAsync(string id, InvestigationPersonInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
        => SaveRowAsync(id, input.Id, InvestigationTabs.People, actor, db => db.InvestigationPeople,
            async (db, row, _) => await ApplyPersonAsync(db, row, input, cancellationToken, actor), cancellationToken);

    public Task<string> SaveEvidenceAsync(string id, InvestigationEvidenceInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
        => SaveRowAsync(id, input.Id, InvestigationTabs.Evidence, actor, db => db.InvestigationEvidence,
            async (db, row, isNew) =>
            {
                if (isNew)
                {
                    row.Number = (await db.InvestigationEvidence.IgnoreQueryFilters().Where(e => e.InvestigationId == id)
                        .MaxAsync(e => (int?)e.Number, cancellationToken) ?? 0) + 1;
                }
                ApplyEvidence(row, input);
            }, cancellationToken);

    public async Task<string> SaveInterrogationAsync(string id, InvestigationInterrogationInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var access = await InvestigationAccess.ResolveAsync(db, id, actor, cancellationToken);
        InvestigationAccess.RequireEdit(access, InvestigationTabs.Interrogations);
        InvestigationInterrogation row;
        if (input.Id is null)
        {
            row = new InvestigationInterrogation { InvestigationId = id };
            db.InvestigationInterrogations.Add(row);
        }
        else
        {
            row = await db.InvestigationInterrogations.Include(v => v.Entries)
                .FirstOrDefaultAsync(v => v.Id == input.Id && v.InvestigationId == id, cancellationToken)
                ?? throw new KeyNotFoundException("Diese Vernehmung gibt es nicht.");
            await RequireNotHiddenAsync(db, id, nameof(InvestigationInterrogation), row.Id, access, cancellationToken);
            db.InvestigationInterrogationEntries.RemoveRange(row.Entries);
            row.Entries.Clear();
        }
        if (input.InvestigationPersonId is { } personRef
            && !await db.InvestigationPeople.AnyAsync(p => p.Id == personRef && p.InvestigationId == id, cancellationToken))
        {
            throw new InvalidOperationException("Diese Person steht nicht in der Ermittlung.");
        }
        ApplyInterrogation(row, input);
        await db.SaveChangesAsync(cancellationToken);
        return row.Id;
    }

    public async Task<string> SaveTaskAsync(string id, InvestigationTaskInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        string? notify = null;
        var rowId = await SaveRowAsync(id, input.Id, InvestigationTabs.Tasks, actor, db => db.InvestigationTasks,
            async (db, row, isNew) =>
            {
                var text = (input.Text ?? string.Empty).Trim();
                if (text.Length == 0)
                {
                    throw new InvalidOperationException("Die Aufgabe braucht einen Text.");
                }
                if (input.AssigneeId is { } assignee && assignee != row.AssigneeId)
                {
                    // only someone who is in the file can own a task of it
                    if (!await InvestigationAccess.IsVisibleAsync(db, id, await ScopeOfAsync(db, assignee, cancellationToken), cancellationToken))
                    {
                        throw new InvalidOperationException("Die Aufgabe kann nur an Beteiligte gehen.");
                    }
                    notify = assignee;
                }
                if (isNew)
                {
                    row.SortOrder = await db.InvestigationTasks.Where(t => t.InvestigationId == id).CountAsync(cancellationToken) + 1;
                }
                row.Text = Clip(text, 500)!;
                row.AssigneeId = input.AssigneeId;
                row.DueAt = input.DueAt;
            }, cancellationToken);
        if (notify is not null && notify != actor.GetAgentId())
        {
            await NotifyAsync(id, notify, NotificationType.InvestigationTask, "Dir wurde eine Aufgabe zugewiesen", cancellationToken);
        }
        return rowId;
    }

    public async Task SetTaskDoneAsync(string id, string taskId, bool done, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
        => await SaveRowAsync(id, taskId, InvestigationTabs.Tasks, actor, db => db.InvestigationTasks, (_, row, _) =>
        {
            row.Done = done;
            row.DoneAt = done ? DateTime.UtcNow : null;
            row.DoneById = done ? actor.GetAgentId() : null;
            return Task.CompletedTask;
        }, cancellationToken);

    public Task<string> AddNoteAsync(string id, string text, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
        => SaveRowAsync(id, null, InvestigationTabs.Notes, actor, db => db.InvestigationNotes, (_, row, _) =>
        {
            var clean = (text ?? string.Empty).Trim();
            if (clean.Length == 0)
            {
                throw new InvalidOperationException("Die Notiz ist leer.");
            }
            row.Text = Clip(clean, 4000)!;
            row.AuthorName = actor.GetCodename();
            return Task.CompletedTask;
        }, cancellationToken);

    /// <summary>Create or update one row of a tab after the tab and entry checks.</summary>
    private async Task<string> SaveRowAsync<T>(string id, string? rowId, string tab, ClaimsPrincipal actor,
        Func<AppDbContext, DbSet<T>> set, Func<AppDbContext, T, bool, Task> apply, CancellationToken cancellationToken)
        where T : InvestigationRow, new()
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var access = await InvestigationAccess.ResolveAsync(db, id, actor, cancellationToken);
        InvestigationAccess.RequireEdit(access, tab);
        T row;
        var isNew = rowId is null;
        if (isNew)
        {
            row = new T { InvestigationId = id };
            set(db).Add(row);
        }
        else
        {
            row = await set(db).FirstOrDefaultAsync(r => r.Id == rowId && r.InvestigationId == id, cancellationToken)
                ?? throw new KeyNotFoundException("Diesen Eintrag gibt es nicht.");
            await RequireNotHiddenAsync(db, id, typeof(T).Name, row.Id, access, cancellationToken);
        }
        await apply(db, row, isNew);
        await db.SaveChangesAsync(cancellationToken);
        return row.Id;
    }

    public async Task DeleteEntryAsync(string id, string itemType, string itemId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        if (!EntryTabs.TryGetValue(itemType, out var tab))
        {
            throw new ArgumentException("Unbekannte Art von Eintrag.", nameof(itemType));
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var access = await InvestigationAccess.ResolveAsync(db, id, actor, cancellationToken);
        InvestigationAccess.RequireEdit(access, tab);
        await RequireNotHiddenAsync(db, id, itemType, itemId, access, cancellationToken);
        InvestigationRow? row = itemType switch
        {
            nameof(InvestigationCharge) => await db.InvestigationCharges.FirstOrDefaultAsync(r => r.Id == itemId && r.InvestigationId == id, cancellationToken),
            nameof(InvestigationPerson) => await db.InvestigationPeople.FirstOrDefaultAsync(r => r.Id == itemId && r.InvestigationId == id, cancellationToken),
            nameof(InvestigationEvidence) => await db.InvestigationEvidence.FirstOrDefaultAsync(r => r.Id == itemId && r.InvestigationId == id, cancellationToken),
            nameof(InvestigationInterrogation) => await db.InvestigationInterrogations.FirstOrDefaultAsync(r => r.Id == itemId && r.InvestigationId == id, cancellationToken),
            nameof(InvestigationTask) => await db.InvestigationTasks.FirstOrDefaultAsync(r => r.Id == itemId && r.InvestigationId == id, cancellationToken),
            nameof(InvestigationNote) => await db.InvestigationNotes.FirstOrDefaultAsync(r => r.Id == itemId && r.InvestigationId == id, cancellationToken),
            _ => null,
        };
        if (row is null)
        {
            throw new KeyNotFoundException("Diesen Eintrag gibt es nicht.");
        }
        if (row is InvestigationNote && row.CreatedById != access.MeId && !access.MayManage)
        {
            throw new UnauthorizedAccessException("Fremde Notizen löscht nur, wer die Ermittlung verwaltet.");
        }
        if (row is InvestigationPerson && await db.InvestigationInterrogations.AnyAsync(v => v.InvestigationPersonId == itemId, cancellationToken))
        {
            throw new InvalidOperationException("Die Person hat noch Vernehmungen. Erst diese löschen.");
        }
        db.Remove(row);
        // a deleted entry keeps no limits; the soft-deleted row stays in the history
        db.InvestigationItemRestrictions.RemoveRange(
            await db.InvestigationItemRestrictions.Where(r => r.ItemType == itemType && r.ItemId == itemId).ToListAsync(cancellationToken));
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task RequireNotHiddenAsync(AppDbContext db, string id, string itemType, string itemId,
        InvestigationAccessView access, CancellationToken cancellationToken)
    {
        if ((await InvestigationAccess.HiddenItemIdsAsync(db, id, itemType, access, cancellationToken)).Contains(itemId))
        {
            throw new KeyNotFoundException("Diesen Eintrag gibt es nicht.");
        }
    }

    private static InvestigationCharge ApplyCharge(InvestigationCharge row, InvestigationChargeInput input)
    {
        var paragraph = (input.Paragraph ?? string.Empty).Trim();
        var book = (input.LawBook ?? string.Empty).Trim();
        if (paragraph.Length == 0 || book.Length == 0)
        {
            throw new InvalidOperationException("Ein Tatvorwurf braucht Paragraf und Gesetzbuch.");
        }
        row.LawId = input.LawId;
        row.Paragraph = Clip(paragraph, 64)!;
        row.LawBook = Clip(book, 128)!;
        row.Description = Clip(input.Description, 500) ?? string.Empty;
        row.Check = input.Check;
        return row;
    }

    private static async Task<InvestigationPerson> ApplyPersonAsync(AppDbContext db, InvestigationPerson row, InvestigationPersonInput input,
        CancellationToken cancellationToken, ClaimsPrincipal? actor = null)
    {
        var name = (input.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw new InvalidOperationException("Die Person braucht einen Namen.");
        }
        if (input.PersonId is { } personId && personId != row.PersonId)
        {
            // linking a person file needs that file to be visible; partners link only what was released to them
            if (actor is not null && !await Visibility.IsRecordVisibleAsync(db, "Person", personId, ViewerScope.From(actor), cancellationToken))
            {
                throw new InvalidOperationException("Diese Personenakte kannst du nicht verknüpfen.");
            }
        }
        row.Name = Clip(name, 200)!;
        row.PersonId = input.PersonId;
        row.Phone = Clip(input.Phone, 64);
        row.Role = Clip(string.IsNullOrWhiteSpace(input.Role) ? "Zeuge" : input.Role.Trim(), 100)!;
        row.Status = input.Status;
        row.Note = Clip(input.Note, 1000);
        return row;
    }

    private static InvestigationEvidence ApplyEvidence(InvestigationEvidence row, InvestigationEvidenceInput input)
    {
        var name = (input.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw new InvalidOperationException("Das Beweismittel braucht eine Bezeichnung.");
        }
        row.Name = Clip(name, 200)!;
        row.Description = Clip(input.Description, 2000);
        row.Owner = Clip(input.Owner, 200);
        row.Officer = Clip(input.Officer, 200);
        row.Status = input.Status;
        row.SecuredAt = input.SecuredAt;
        return row;
    }

    private static InvestigationInterrogation ApplyInterrogation(InvestigationInterrogation row, InvestigationInterrogationInput input)
    {
        var name = (input.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw new InvalidOperationException("Die Vernehmung braucht den Namen der vernommenen Person.");
        }
        row.InvestigationPersonId = input.InvestigationPersonId;
        row.Name = Clip(name, 200)!;
        row.Role = Clip(string.IsNullOrWhiteSpace(input.Role) ? "Zeuge" : input.Role.Trim(), 100)!;
        row.Summons = Clip(input.Summons, 300);
        row.HeldAt = input.HeldAt;
        row.Officers = Clip(input.Officers, 300);
        row.ProsecutorPresent = Clip(input.ProsecutorPresent, 200);
        row.Summary = Clip(input.Summary, 4000);
        row.ProtocolReference = Clip(input.ProtocolReference, 500);
        row.Usable = input.Usable;
        var order = 0;
        foreach (var qa in input.Entries.Where(e => !string.IsNullOrWhiteSpace(e.Question) || !string.IsNullOrWhiteSpace(e.Answer)))
        {
            row.Entries.Add(new InvestigationInterrogationEntry
            {
                InterrogationId = row.Id, SortOrder = ++order,
                Question = Clip(qa.Question, 2000) ?? string.Empty, Answer = Clip(qa.Answer, 4000) ?? string.Empty,
            });
        }
        return row;
    }

    // ---- participants and entry limits ----

    public async Task<List<InvestigationTarget>> GetCandidatesAsync(string id, ClaimsPrincipal viewer, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var access = await InvestigationAccess.ResolveAsync(db, id, viewer, cancellationToken);
        InvestigationAccess.RequireEdit(access, InvestigationTabs.Participants);
        var agencies = await PartnerAgenciesAsync(db, id, cancellationToken);
        var result = new List<InvestigationTarget>();
        // internal agents follow AgentSelection; partners are a documented exception (services-details.md)
        foreach (var a in await db.Users.AsNoTracking().OnlySelectable().OrderBy(a => a.Codename)
                     .Select(a => new { a.Id, a.Codename }).ToListAsync(cancellationToken))
        {
            result.Add(new InvestigationTarget(a.Id, null, $"{a.Codename} · FIB"));
        }
        if (agencies.Count > 0)
        {
            foreach (var u in await db.PartnerUnits.AsNoTracking().Where(u => agencies.Contains(u.Agency))
                         .OrderBy(u => u.Agency).ThenBy(u => u.SortOrder).ToListAsync(cancellationToken))
            {
                result.Add(new InvestigationTarget(null, u.Id, $"{PartnerUnitDisplay.Full(u.Agency, u.Name)} (ganze Einheit)"));
            }
            foreach (var a in await db.Users.AsNoTracking()
                         .Where(a => a.Status == AgentStatus.Active && a.PartnerAgency != null && agencies.Contains(a.PartnerAgency.Value))
                         .OrderBy(a => a.Codename).Select(a => new { a.Id, a.Codename, a.PartnerAgency, a.PartnerUnitId })
                         .ToListAsync(cancellationToken))
            {
                result.Add(new InvestigationTarget(a.Id, null, $"{a.Codename} · {PartnerUnitDisplay.OfAccount(a.PartnerAgency, a.PartnerUnitId)}"));
            }
        }
        return result;
    }

    /// <summary>Agencies taking part in the investigation's taskforce, when it is cross-agency.</summary>
    private static async Task<List<PartnerAgency>> PartnerAgenciesAsync(AppDbContext db, string id, CancellationToken cancellationToken)
        => await db.Investigations.Where(i => i.Id == id)
            .SelectMany(i => db.TaskforcePartnerAgencies.Where(tp => tp.TaskforceId == i.TaskforceId
                && db.Taskforces.Any(t => t.Id == tp.TaskforceId && t.Scope == TaskforceScope.CrossAgency)))
            .Select(tp => tp.Agency)
            .ToListAsync(cancellationToken);

    public async Task<string> SaveParticipantAsync(string id, InvestigationParticipantInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var access = await InvestigationAccess.ResolveAsync(db, id, actor, cancellationToken);
        InvestigationAccess.RequireEdit(access, InvestigationTabs.Participants);
        if ((input.AgentId is null) == (input.PartnerUnitId is null))
        {
            throw new ArgumentException("Beteiligt ist entweder ein Konto oder eine Einheit.", nameof(input));
        }
        var agencies = await PartnerAgenciesAsync(db, id, cancellationToken);
        var role = input.Role;
        if (input.AgentId is { } agentId)
        {
            var agent = await db.Users.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken)
                ?? throw new InvalidOperationException("Dieses Konto gibt es nicht.");
            if (agent.PartnerAgency is { } agency)
            {
                if (agent.Status != AgentStatus.Active || !agencies.Contains(agency))
                {
                    throw new InvalidOperationException("Partner können nur aus Behörden beteiligt werden, die an der Taskforce mitarbeiten.");
                }
                if (role == InvestigationRole.Prosecutor
                    && !await db.PartnerUnits.AnyAsync(u => u.Id == agent.PartnerUnitId && u.IsProsecution, cancellationToken))
                {
                    throw new InvalidOperationException("Staatsanwalt kann nur ein Konto der Staatsanwaltschaft sein.");
                }
            }
            else
            {
                if (!await db.Users.OnlySelectable().AnyAsync(a => a.Id == agentId, cancellationToken))
                {
                    throw new InvalidOperationException("Dieser Agent kann nicht beteiligt werden.");
                }
                if (role == InvestigationRole.Prosecutor)
                {
                    throw new InvalidOperationException("Staatsanwalt kann nur ein Konto der Staatsanwaltschaft sein.");
                }
            }
        }
        else
        {
            var unit = await db.PartnerUnits.AsNoTracking().FirstOrDefaultAsync(u => u.Id == input.PartnerUnitId, cancellationToken)
                ?? throw new InvalidOperationException("Diese Einheit gibt es nicht.");
            if (!agencies.Contains(unit.Agency))
            {
                throw new InvalidOperationException("Diese Behörde arbeitet nicht an der Taskforce mit.");
            }
            role = InvestigationRole.Investigator;
        }

        var row = input.Id is not null
            ? await db.InvestigationParticipants.FirstOrDefaultAsync(p => p.Id == input.Id && p.InvestigationId == id, cancellationToken)
            : await db.InvestigationParticipants.FirstOrDefaultAsync(p => p.InvestigationId == id
                && (input.AgentId != null ? p.AgentId == input.AgentId : p.PartnerUnitId == input.PartnerUnitId), cancellationToken);
        var isNew = row is null;
        if (row is null)
        {
            row = new InvestigationParticipant { InvestigationId = id, AgentId = input.AgentId, PartnerUnitId = input.PartnerUnitId };
            db.InvestigationParticipants.Add(row);
        }
        else if (row.AgentId != input.AgentId || row.PartnerUnitId != input.PartnerUnitId)
        {
            throw new InvalidOperationException("Ein Beteiligter wechselt nicht das Konto; bitte neu eintragen.");
        }
        row.Role = role;
        row.TabAccessJson = InvestigationTabAccess.Serialize(input.Overrides);
        await db.SaveChangesAsync(cancellationToken);
        if (isNew && input.AgentId is { } added && added != actor.GetAgentId())
        {
            await NotifyAsync(id, added, NotificationType.InvestigationParticipation, "Du wurdest an einer Ermittlung beteiligt", cancellationToken);
        }
        return row.Id;
    }

    public async Task RemoveParticipantAsync(string id, string participantId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var access = await InvestigationAccess.ResolveAsync(db, id, actor, cancellationToken);
        InvestigationAccess.RequireEdit(access, InvestigationTabs.Participants);
        var row = await db.InvestigationParticipants.FirstOrDefaultAsync(p => p.Id == participantId && p.InvestigationId == id, cancellationToken)
            ?? throw new KeyNotFoundException("Diesen Beteiligten gibt es nicht.");
        if (row.Role == InvestigationRole.Lead
            && !await db.InvestigationParticipants.AnyAsync(p => p.InvestigationId == id && p.Id != row.Id && p.Role == InvestigationRole.Lead, cancellationToken))
        {
            throw new InvalidOperationException("Die letzte Ermittlungsleitung lässt sich nicht austragen. Erst eine neue eintragen.");
        }
        db.InvestigationParticipants.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetRestrictionAsync(string id, string itemType, string itemId, IReadOnlyList<InvestigationTarget> targets,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        if (!EntryTabs.ContainsKey(itemType))
        {
            throw new ArgumentException("Unbekannte Art von Eintrag.", nameof(itemType));
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var access = await InvestigationAccess.ResolveAsync(db, id, actor, cancellationToken);
        if (!access.MayManage)
        {
            throw new UnauthorizedAccessException("Einträge beschränkt nur, wer die Ermittlung verwaltet.");
        }
        var participants = await db.InvestigationParticipants.AsNoTracking().Where(p => p.InvestigationId == id).ToListAsync(cancellationToken);
        foreach (var t in targets)
        {
            if (!participants.Any(p => (t.AgentId != null && p.AgentId == t.AgentId) || (t.PartnerUnitId != null && p.PartnerUnitId == t.PartnerUnitId)))
            {
                throw new InvalidOperationException("Einträge lassen sich nur auf Beteiligte beschränken.");
            }
        }
        var existing = await db.InvestigationItemRestrictions
            .Where(r => r.InvestigationId == id && r.ItemType == itemType && r.ItemId == itemId).ToListAsync(cancellationToken);
        db.InvestigationItemRestrictions.RemoveRange(existing);
        foreach (var t in targets.DistinctBy(t => (t.AgentId, t.PartnerUnitId)))
        {
            db.InvestigationItemRestrictions.Add(new InvestigationItemRestriction
            {
                InvestigationId = id, ItemType = itemType, ItemId = itemId, AgentId = t.AgentId, PartnerUnitId = t.AgentId is null ? t.PartnerUnitId : null,
            });
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- history, trash ----

    public async Task<List<InvestigationHistoryEntry>> GetHistoryAsync(string id, ClaimsPrincipal viewer, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var access = await InvestigationAccess.ResolveAsync(db, id, viewer, cancellationToken);
        InvestigationAccess.RequireRead(access, InvestigationTabs.History);

        var children = new Dictionary<string, (string Type, string Label)>();
        void Add(IEnumerable<(string Id, string Label)> rows, string type)
        {
            foreach (var (rowId, label) in rows)
            {
                children[rowId] = (type, label);
            }
        }
        Add((await db.InvestigationCharges.IgnoreQueryFilters().Where(r => r.InvestigationId == id).Select(r => new { r.Id, r.Paragraph, r.LawBook }).ToListAsync(cancellationToken))
            .Select(r => (r.Id, $"{r.Paragraph} {r.LawBook}")), nameof(InvestigationCharge));
        Add((await db.InvestigationPeople.IgnoreQueryFilters().Where(r => r.InvestigationId == id).Select(r => new { r.Id, r.Name }).ToListAsync(cancellationToken))
            .Select(r => (r.Id, r.Name)), nameof(InvestigationPerson));
        Add((await db.InvestigationEvidence.IgnoreQueryFilters().Where(r => r.InvestigationId == id).Select(r => new { r.Id, r.Number, r.Name }).ToListAsync(cancellationToken))
            .Select(r => (r.Id, $"#{r.Number} {r.Name}")), nameof(InvestigationEvidence));
        Add((await db.InvestigationInterrogations.IgnoreQueryFilters().Where(r => r.InvestigationId == id).Select(r => new { r.Id, r.Name }).ToListAsync(cancellationToken))
            .Select(r => (r.Id, r.Name)), nameof(InvestigationInterrogation));
        Add((await db.InvestigationTasks.IgnoreQueryFilters().Where(r => r.InvestigationId == id).Select(r => new { r.Id, r.Text }).ToListAsync(cancellationToken))
            .Select(r => (r.Id, r.Text)), nameof(InvestigationTask));
        Add((await db.InvestigationNotes.IgnoreQueryFilters().Where(r => r.InvestigationId == id).Select(r => new { r.Id }).ToListAsync(cancellationToken))
            .Select(r => (r.Id, "Notiz")), nameof(InvestigationNote));
        Add((await db.InvestigationParticipants.IgnoreQueryFilters().Where(r => r.InvestigationId == id).Select(r => new { r.Id }).ToListAsync(cancellationToken))
            .Select(r => (r.Id, "Beteiligung")), nameof(InvestigationParticipant));

        var ids = children.Keys.ToList();
        var rows = await db.AuditLogs.AsNoTracking()
            .Where(a => (a.EntityType == nameof(Investigation) && a.EntityId == id) || ids.Contains(a.EntityId))
            .OrderByDescending(a => a.Timestamp)
            .Take(400)
            .ToListAsync(cancellationToken);
        return rows.Select(a =>
            {
                if (a.EntityType == nameof(Investigation))
                {
                    return new InvestigationHistoryEntry(a.Timestamp, a.AgentName, InvestigationTabs.Label(InvestigationTabs.Overview),
                        ActionLabel(a.Action) + " Akte", a.ChangesJson);
                }
                var (type, label) = children[a.EntityId];
                return new InvestigationHistoryEntry(a.Timestamp, a.AgentName,
                    InvestigationTabs.Label(InvestigationTabs.ForChildType(type) ?? InvestigationTabs.Overview),
                    $"{ActionLabel(a.Action)}: {label}", a.ChangesJson);
            })
            .ToList();
    }

    private static string ActionLabel(AuditAction action) => action switch
    {
        AuditAction.Created => "Angelegt",
        AuditAction.Deleted => "Gelöscht",
        AuditAction.Restored => "Wiederhergestellt",
        _ => "Geändert",
    };

    public async Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var access = await InvestigationAccess.ResolveAsync(db, id, actor, cancellationToken);
        if (!access.FullAccess)
        {
            throw new UnauthorizedAccessException("Eine Ermittlung löscht nur, wer sie leitet oder die Führung.");
        }
        var inv = await db.Investigations.FirstOrDefaultAsync(i => i.Id == id, cancellationToken) ?? throw new KeyNotFoundException(NotFound);
        db.Investigations.Remove(inv);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<Investigation>> GetTrashAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Investigations.IgnoreQueryFilters().AsNoTracking()
            .Where(i => i.IsDeleted).OrderByDescending(i => i.DeletedAt).ToListAsync(cancellationToken);
    }

    public async Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadershipNoReader(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var inv = await db.Investigations.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.Id == id && i.IsDeleted, cancellationToken)
            ?? throw new KeyNotFoundException(NotFound);
        inv.IsDeleted = false;
        inv.DeletedAt = null;
        inv.DeletedById = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- helpers ----

    private async Task NotifyAsync(string id, string recipientId, NotificationType type, string text, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var number = await db.Investigations.Where(i => i.Id == id).Select(i => i.CaseNumber).FirstOrDefaultAsync(cancellationToken);
            await notifications.NotifyAsync(recipientId, type, $"{text} ({number})", $"/ermittlungen/{id}", cancellationToken);
        }
        catch
        {
            /* best effort */
        }
    }

    /// <summary>A scope for another account, read from its row (used to check a task owner is in the file).</summary>
    private static async Task<ViewerScope> ScopeOfAsync(AppDbContext db, string agentId, CancellationToken cancellationToken)
    {
        var a = await db.Users.AsNoTracking().Where(u => u.Id == agentId)
            .Select(u => new { u.PartnerAgency, u.Rank, u.IsAdmin }).FirstOrDefaultAsync(cancellationToken);
        var leadership = a is not null && a.PartnerAgency is null && (a.IsAdmin || a.Rank >= Rank.SectionChief);
        return new ViewerScope(leadership, leadership, agentId, a?.PartnerAgency);
    }

    private static async Task<Dictionary<string, string>> NamesAsync(AppDbContext db, IEnumerable<string> agentIds, CancellationToken cancellationToken)
    {
        var ids = agentIds.Distinct().ToList();
        return ids.Count == 0 ? new()
            : await db.Users.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Codename, cancellationToken);
    }

    /// <summary>Labels for accounts (codename) and units (agency · unit), keyed by id.</summary>
    private static async Task<Dictionary<string, string>> TargetLabelsAsync(AppDbContext db, IEnumerable<string?> agentIds, IEnumerable<string?> unitIds,
        CancellationToken cancellationToken)
    {
        var labels = await NamesAsync(db, agentIds.Where(a => a != null)!, cancellationToken);
        var units = unitIds.Where(u => u != null).Distinct().ToList();
        foreach (var u in await db.PartnerUnits.AsNoTracking().IgnoreQueryFilters().Where(x => units.Contains(x.Id)).ToListAsync(cancellationToken))
        {
            labels[u.Id] = PartnerUnitDisplay.Full(u.Agency, u.Name);
        }
        return labels;
    }

    private static string? Clip(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var t = value.Trim();
        return t.Length <= max ? t : t[..max];
    }
}
