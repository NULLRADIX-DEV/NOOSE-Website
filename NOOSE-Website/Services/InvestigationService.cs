using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Investigations;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Investigations;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IInvestigationService" />
public partial class InvestigationService(
    IDbContextFactory<AppDbContext> dbFactory,
    ICaseNumberService caseNumber,
    INotificationService notifications,
    IInvestigationPhotoStorageService photoStorage) : IInvestigationService
{
    public const string CaseNumberPrefix = "ER";
    private const string NotFound = "Die Ermittlung wurde nicht gefunden.";

    // the plain fields that may carry @-references; the input fields take the same limits, so the clip never bites
    public const int NoteMaxLength = 4000;
    public const int SummaryMaxLength = 4000;
    public const int EvidenceDescriptionMaxLength = 2000;
    public const int PersonNoteMaxLength = 1000;
    public const int IndictmentMaxLength = 1000;

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
        if (personId is not null && rows.Count > 0)
        {
            // that a person appears in a file is itself an entry of its people tab
            var kept = new List<string>();
            foreach (var r in rows)
            {
                var access = await InvestigationAccess.ResolveAsync(db, r.Id, viewer, cancellationToken);
                if (!access.CanRead(InvestigationTabs.People))
                {
                    continue;
                }
                var hidden = await InvestigationAccess.HiddenItemIdsAsync(db, r.Id, nameof(InvestigationPerson), access, cancellationToken);
                if (await db.InvestigationPeople.AnyAsync(p => p.InvestigationId == r.Id && p.PersonId == personId && !hidden.Contains(p.Id), cancellationToken))
                {
                    kept.Add(r.Id);
                }
            }
            rows = rows.Where(r => kept.Contains(r.Id)).ToList();
        }
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

        // names only, for the officer and assignee pickers of whoever writes there; the viewer is in even when only his unit is
        var picksOfficers = access.CanEdit(InvestigationTabs.Evidence) || access.CanEdit(InvestigationTabs.Interrogations)
            || access.CanEdit(InvestigationTabs.Tasks);
        var me = viewer.GetAgentId();
        var myName = me is null ? null
            : labels.GetValueOrDefault(me) ?? (await NamesAsync(db, [me], cancellationToken)).GetValueOrDefault(me);
        var officers = !picksOfficers ? [] : allParticipants
            .Where(p => p.AgentId != null && labels.ContainsKey(p.AgentId))
            .Select(p => new InvestigationOfficer(p.AgentId!, labels[p.AgentId!]))
            .Concat(me is not null && myName is not null ? [new InvestigationOfficer(me, myName)] : [])
            .DistinctBy(o => o.AgentId)
            .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

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
            People = !access.CanRead(InvestigationTabs.People) ? [] : await PeopleAsync(db, id, Shows, ViewerScope.From(viewer), cancellationToken),
            Evidence = !access.CanRead(InvestigationTabs.Evidence) ? [] : await EvidenceAsync(db, id, Shows, cancellationToken),
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
            Officers = officers,
            MyName = myName,
            RestrictedIds = restrictions.Select(r => r.ItemId).Where(Shows).ToHashSet(),
            Restrictions = !access.MayManage ? new Dictionary<string, IReadOnlyList<InvestigationTarget>>() : restrictions
                .GroupBy(r => r.ItemId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<InvestigationTarget>)g
                    .Select(r => new InvestigationTarget(r.AgentId, r.PartnerUnitId,
                        labels.GetValueOrDefault(r.AgentId ?? r.PartnerUnitId ?? string.Empty) ?? "—")).ToList()),
        };
        return detail;
    }

    private static async Task<List<InvestigationPersonView>> PeopleAsync(AppDbContext db, string id, Func<string, bool> shows, ViewerScope scope,
        CancellationToken cancellationToken)
    {
        var rows = await db.InvestigationPeople.AsNoTracking().Where(p => p.InvestigationId == id)
            .OrderBy(p => p.CreatedAt).ToListAsync(cancellationToken);
        var personIds = rows.Where(r => r.PersonId != null).Select(r => r.PersonId!).Distinct().ToList();
        // the link to a person file only where the viewer may open that file; the name in the list is the entry's own
        var files = new Dictionary<string, string>();
        foreach (var file in await db.People.AsNoTracking().Where(p => personIds.Contains(p.Id))
                     .Select(p => new { p.Id, p.CaseNumber }).ToListAsync(cancellationToken))
        {
            if (await Visibility.IsRecordVisibleAsync(db, nameof(Person), file.Id, scope, cancellationToken))
            {
                files[file.Id] = file.CaseNumber;
            }
        }
        return rows.Where(r => shows(r.Id))
            .Select(r => new InvestigationPersonView(r.Id, r.Name, r.PersonId, files.GetValueOrDefault(r.PersonId ?? string.Empty),
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
                    InvestigationTabAccess.Parse(r.TabAccessJson), defaults, isPartner);
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
            row.InvestigationPersonId ??= MatchPerson(row.Name, personIds);
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
            db.InvestigationNotes.Add(new InvestigationNote { InvestigationId = inv.Id, Text = ClipReferences(note, NoteMaxLength)!, AuthorName = actor.GetCodename() });
        }

        // case-number allocation needs an enclosing transaction so counter and record commit together
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        inv.CaseNumber = await caseNumber.NextAsync(db, CaseNumberPrefix, cancellationToken);
        db.Investigations.Add(inv);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return inv.Id;
    }

    /// <summary>The list entry an imported interrogation belongs to: the same name, else the one name that differs by a typo
    /// (at most two letters, as "Hummerman" for "Hummermann"); two close names link to neither.</summary>
    public static string? MatchPerson(string name, IReadOnlyDictionary<string, string> personIds)
    {
        var wanted = name.Trim();
        if (personIds.TryGetValue(wanted, out var exact))
        {
            return exact;
        }
        if (wanted.Length < 5)
        {
            return null;
        }
        var close = personIds.Where(p => Distance(p.Key.Trim().ToLowerInvariant(), wanted.ToLowerInvariant()) <= 2).Take(2).ToList();
        return close.Count == 1 ? close[0].Value : null;
    }

    private static int Distance(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 2)
        {
            return int.MaxValue;
        }
        var prev = Enumerable.Range(0, b.Length + 1).ToArray();
        for (var i = 1; i <= a.Length; i++)
        {
            var cur = new int[b.Length + 1];
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }
            prev = cur;
        }
        return prev[b.Length];
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
            // partners take part only through their agency's place in the taskforce
            var target = await db.TaskforcePartnerAgencies
                .Where(tp => tp.TaskforceId == input.TaskforceId
                    && db.Taskforces.Any(t => t.Id == tp.TaskforceId && t.Scope == TaskforceScope.CrossAgency))
                .Select(tp => tp.Agency).ToListAsync(cancellationToken);
            var stranded = await db.InvestigationParticipants
                .Where(p => p.InvestigationId == id
                    && ((p.AgentId != null && db.Users.Any(u => u.Id == p.AgentId && u.PartnerAgency != null && !target.Contains(u.PartnerAgency.Value)))
                        || (p.PartnerUnitId != null && db.PartnerUnits.Any(u => u.Id == p.PartnerUnitId && !target.Contains(u.Agency)))))
                .AnyAsync(cancellationToken);
            if (stranded)
            {
                throw new InvalidOperationException("An der neuen Taskforce arbeitet nicht jede beteiligte Partnerbehörde mit. "
                    + "Erst die Behörde dort eintragen oder ihre Beteiligten austragen.");
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
        inv.Indictment = ClipReferences(input.Indictment, IndictmentMaxLength);
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
            row.Text = ClipReferences(clean, NoteMaxLength)!;
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
        // the limits stay: the soft-deleted row still shows in the history, which must hide it as before
        db.Remove(row);
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
        row.Note = ClipReferences(input.Note, PersonNoteMaxLength);
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
        row.Description = ClipReferences(input.Description, EvidenceDescriptionMaxLength);
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
        row.Summary = ClipReferences(input.Summary, SummaryMaxLength);
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
                result.Add(new InvestigationTarget(null, u.Id, $"{PartnerUnitDisplay.Full(u.Agency, u.Name)} (ganze Einheit)", true));
            }
            foreach (var a in await db.Users.AsNoTracking()
                         .Where(a => a.Status == AgentStatus.Active && a.PartnerAgency != null && agencies.Contains(a.PartnerAgency.Value))
                         .OrderBy(a => a.Codename).Select(a => new { a.Id, a.Codename, a.PartnerAgency, a.PartnerUnitId })
                         .ToListAsync(cancellationToken))
            {
                result.Add(new InvestigationTarget(a.Id, null, $"{a.Codename} · {PartnerUnitDisplay.OfAccount(a.PartnerAgency, a.PartnerUnitId)}", true));
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
        if (!access.FullAccess)
        {
            // managing participants is not a way to raise one's own rights or to hand out the lead
            if (role == InvestigationRole.Lead || (!isNew && row.Role == InvestigationRole.Lead))
            {
                throw new UnauthorizedAccessException("Die Ermittlungsleitung vergibt und entzieht nur, wer die Ermittlung leitet oder die Führung.");
            }
            if ((row.AgentId is not null && row.AgentId == access.MeId) || (row.PartnerUnitId is not null && row.PartnerUnitId == access.MyUnitId))
            {
                throw new UnauthorizedAccessException("Die eigenen Rechte ändert nur die Ermittlungsleitung.");
            }
        }
        if (!isNew && row.Role == InvestigationRole.Lead && role != InvestigationRole.Lead
            && !await db.InvestigationParticipants.AnyAsync(p => p.InvestigationId == id && p.Id != row.Id && p.Role == InvestigationRole.Lead, cancellationToken))
        {
            throw new InvalidOperationException("Die letzte Ermittlungsleitung bleibt. Erst eine neue eintragen.");
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
        if (row.Role == InvestigationRole.Lead && !access.FullAccess)
        {
            throw new UnauthorizedAccessException("Die Ermittlungsleitung trägt nur aus, wer die Ermittlung leitet oder die Führung.");
        }
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
            // a typed token would otherwise stand raw in the history; the task text is no reference host
            .Select(r => (r.Id, MentionParser.Strip(r.Text))), nameof(InvestigationTask));
        Add((await db.InvestigationNotes.IgnoreQueryFilters().Where(r => r.InvestigationId == id).Select(r => new { r.Id }).ToListAsync(cancellationToken))
            .Select(r => (r.Id, "Notiz")), nameof(InvestigationNote));
        // a participation names who joined, an account by codename, a unit by its name
        var parts = await db.InvestigationParticipants.IgnoreQueryFilters().Where(r => r.InvestigationId == id)
            .Select(r => new
            {
                r.Id,
                Who = r.AgentId != null ? db.Users.Where(u => u.Id == r.AgentId).Select(u => u.Codename).FirstOrDefault()
                    : db.PartnerUnits.IgnoreQueryFilters().Where(u => u.Id == r.PartnerUnitId).Select(u => u.Name).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);
        Add(parts.Select(r => (r.Id, r.Who is null ? "Beteiligung" : $"Beteiligung {r.Who}")), nameof(InvestigationParticipant));
        // a link names only the kind of record: a participant may not be allowed to see which one it is
        var links = await db.InvestigationLinks.IgnoreQueryFilters()
            .Where(r => r.InvestigationId == id || (r.RecordType == nameof(Investigation) && r.RecordId == id))
            .Select(r => new { r.Id, r.InvestigationId, r.RecordType }).ToListAsync(cancellationToken);
        // a row from another file belongs to that file's team: its time and author stay out unless the reader sees that file
        var otherFiles = links.Where(r => r.InvestigationId != id).Select(r => r.InvestigationId).Distinct().ToList();
        var seenOthers = otherFiles.Count == 0 || await InvestigationReferences.ViewerAsync(db, ViewerScope.From(viewer), cancellationToken) is not { } scope
            ? []
            : (await db.Investigations.AsNoTracking().Where(i => otherFiles.Contains(i.Id)).OnlyVisible(db, scope)
                .Select(i => i.Id).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        Add(links.Where(r => r.InvestigationId == id || seenOthers.Contains(r.InvestigationId))
                .Select(r => (r.Id, $"Verknüpfung mit {LinkTypeName(r.InvestigationId == id ? r.RecordType : nameof(Investigation))}")),
            nameof(InvestigationLink));

        // entries limited to others stay out of the history as they stay out of the tabs
        var hiddenEvidence = new HashSet<string>();
        foreach (var itemType in EntryTabs.Keys)
        {
            foreach (var hidden in await InvestigationAccess.HiddenItemIdsAsync(db, id, itemType, access, cancellationToken))
            {
                children.Remove(hidden);
                if (itemType == nameof(InvestigationEvidence))
                {
                    hiddenEvidence.Add(hidden);
                }
            }
        }
        // a picture follows its piece of evidence, hidden with it
        var photos = await db.InvestigationEvidencePhotos.IgnoreQueryFilters().Where(r => r.InvestigationId == id)
            .Select(r => new { r.Id, r.EvidenceId, r.OriginalName }).ToListAsync(cancellationToken);
        foreach (var p in photos.Where(p => !hiddenEvidence.Contains(p.EvidenceId)))
        {
            var piece = children.TryGetValue(p.EvidenceId, out var e) ? $" an {e.Label}" : string.Empty;
            children[p.Id] = (nameof(InvestigationEvidencePhoto), $"Bild{piece} ({p.OriginalName})");
        }

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
                    // the narrative is an HTML body the change list leaves out; named by its tab, the row still says what changed
                    if (a.Action == AuditAction.Modified && OnlyNarrativeChanged(a.ChangesJson))
                    {
                        return new InvestigationHistoryEntry(a.Timestamp, a.AgentName, InvestigationTabs.Label(InvestigationTabs.Narrative),
                            "Geändert: Tathergang", null, a.EntityType);
                    }
                    return new InvestigationHistoryEntry(a.Timestamp, a.AgentName, InvestigationTabs.Label(InvestigationTabs.Overview),
                        $"{ActionLabel(a.Action)}: Akte", ChangesOf(a), a.EntityType);
                }
                var (type, label) = children[a.EntityId];
                return new InvestigationHistoryEntry(a.Timestamp, a.AgentName,
                    type == nameof(InvestigationLink) ? LinksSection
                        : InvestigationTabs.Label(InvestigationTabs.ForChildType(type) ?? InvestigationTabs.Overview),
                    // a link's changes are its label, which describes a record the reader may not see
                    $"{ActionLabel(a.Action)}: {label}", type == nameof(InvestigationLink) ? null : ChangesOf(a), type);
            })
            .ToList();
    }

    /// <summary>Rail label of the links section; it is no rights tab, the cover sheet right governs it.</summary>
    public const string LinksSection = "Verknüpfungen";

    /// <summary>Slug of the links section in the file's rail, kept outside the tab rights.</summary>
    public const string LinksSlug = "verknuepfungen";

    private static string LinkTypeName(string type) => type == nameof(Investigation) ? "Ermittlung" : RecordTypeDisplay.Name(type);

    // what changed is news on a change; a new or removed entry is named by its label alone
    private static string? ChangesOf(Infrastructure.Audit.AuditLog a) => a.Action == AuditAction.Modified ? a.ChangesJson : null;

    private static bool OnlyNarrativeChanged(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var fields = doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
            return fields.Count == 1 && fields[0] == nameof(Investigation.NarrativeHtml);
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
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
        // its warrants vanish with it, while the paper would still check out as valid in public
        var now = DateTime.UtcNow;
        if (await db.Warrants.AnyAsync(w => w.InvestigationId == id
                && (w.Status == WarrantStatus.Requested || (w.Status == WarrantStatus.Signed && (w.ValidUntil == null || w.ValidUntil > now))),
                cancellationToken))
        {
            throw new InvalidOperationException("Die Ermittlung hat noch offene oder gültige Anordnungen. Erst diese aufheben oder löschen.");
        }
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

    /// <summary>Like <see cref="Clip"/>, for fields that may carry @-references.</summary>
    /// <remarks>A plain cut could halve a token and leave it in the file as broken literal text.</remarks>
    private static string? ClipReferences(string? value, int max)
        => string.IsNullOrWhiteSpace(value) ? null
            : MentionParser.ClipSafe(value.Trim(), max) is { Length: > 0 } clipped ? clipped : null;
}
