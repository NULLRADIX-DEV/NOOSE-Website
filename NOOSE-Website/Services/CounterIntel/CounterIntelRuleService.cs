using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.CounterIntel;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.CounterIntel;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="ICounterIntelRuleService" />
public class CounterIntelRuleService(IDbContextFactory<AppDbContext> dbFactory) : ICounterIntelRuleService
{
    private const int SearchPerType = 8;

    public async Task<IReadOnlyList<CounterIntelRuleView>> GetAllAsync(
        ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadershipNoReader(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.CounterIntelRules.AsNoTracking()
            .OrderBy(r => r.Order).ThenBy(r => r.Name)
            .ToListAsync(cancellationToken);
        return rows.Select(View).OfType<CounterIntelRuleView>().ToList();
    }

    public async Task<IReadOnlyList<CounterIntelRuleView>> GetActiveAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.CounterIntelRules.AsNoTracking()
            .Where(r => r.IsActive)
            .OrderBy(r => r.Order).ThenBy(r => r.Name)
            .ToListAsync(cancellationToken);
        return rows.Select(View).OfType<CounterIntelRuleView>().ToList();
    }

    public async Task<string> CreateAsync(
        CounterIntelRuleInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadershipNoReader(actor);
        CounterIntelRuleValidation.Validate(input);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rule = new CounterIntelRule
        {
            Name = input.Name.Trim(),
            Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim(),
            Severity = input.Severity,
            IsActive = input.IsActive,
            Notify = input.Notify,
            Order = input.Order,
            DefinitionJson = input.Definition.ToJson(),
            ReportFrom = DateTime.UtcNow,
        };
        db.CounterIntelRules.Add(rule);
        await db.SaveChangesAsync(cancellationToken);
        return rule.Id;
    }

    public async Task UpdateAsync(
        string id, CounterIntelRuleInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadershipNoReader(actor);
        CounterIntelRuleValidation.Validate(input);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rule = await db.CounterIntelRules.FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
                   ?? throw new InvalidOperationException("Regel nicht gefunden.");

        // a rule that now means something new reports only what happens from here on
        if (FindingWatchRules.Rearms(rule.DefinitionJson, rule.IsActive, rule.Notify, input))
        {
            rule.ReportFrom = DateTime.UtcNow;
        }
        rule.Name = input.Name.Trim();
        rule.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        rule.Severity = input.Severity;
        rule.IsActive = input.IsActive;
        rule.Notify = input.Notify;
        rule.Order = input.Order;
        rule.DefinitionJson = input.Definition.ToJson();
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetActiveAsync(
        string id, bool isActive, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadershipNoReader(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rule = await db.CounterIntelRules.FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
                   ?? throw new InvalidOperationException("Regel nicht gefunden.");
        if (isActive && !rule.IsActive)
        {
            rule.ReportFrom = DateTime.UtcNow;
        }
        rule.IsActive = isActive;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<string> DuplicateAsync(
        string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadershipNoReader(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var source = await db.CounterIntelRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
                     ?? throw new InvalidOperationException("Regel nicht gefunden.");

        // copies start switched off so a half-edited duplicate never flags anyone
        var copy = new CounterIntelRule
        {
            Name = Shorten($"{source.Name} (Kopie)"),
            Description = source.Description,
            Severity = source.Severity,
            IsActive = false,
            Notify = source.Notify,
            Order = source.Order + 1,
            DefinitionJson = source.DefinitionJson,
            ReportFrom = DateTime.UtcNow,
        };
        db.CounterIntelRules.Add(copy);
        await db.SaveChangesAsync(cancellationToken);
        return copy.Id;
    }

    public async Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadershipNoReader(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rule = await db.CounterIntelRules.FirstOrDefaultAsync(r => r.Id == id, cancellationToken)
                   ?? throw new InvalidOperationException("Regel nicht gefunden.");
        db.CounterIntelRules.Remove(rule); // soft delete: the interceptor rewrites this
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> RestoreDefaultsAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadershipNoReader(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var ids = CounterIntelRuleDefaults.All.Select(d => d.Id).ToList();
        // ignore filters: a deleted default keeps its id, so it must be revived rather than re-inserted
        var existing = await db.CounterIntelRules.IgnoreQueryFilters()
            .Where(r => ids.Contains(r.Id))
            .ToListAsync(cancellationToken);

        var restored = 0;
        foreach (var d in CounterIntelRuleDefaults.All)
        {
            if (existing.FirstOrDefault(r => r.Id == d.Id) is { } row)
            {
                if (!row.IsDeleted)
                {
                    continue;
                }
                row.IsDeleted = false;
                row.DeletedAt = null;
                row.DeletedById = null;
                // back from the bin: what happened while it was gone is not news
                row.ReportFrom = DateTime.UtcNow;
            }
            else
            {
                db.CounterIntelRules.Add(new CounterIntelRule
                {
                    Id = d.Id,
                    Name = d.Name,
                    Description = d.Description,
                    Severity = d.Severity,
                    IsActive = d.IsActive,
                    Notify = d.Notify,
                    Order = d.Order,
                    DefinitionJson = d.Definition.ToJson(),
                    ReportFrom = DateTime.UtcNow,
                });
            }
            restored++;
        }
        if (restored > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        return restored;
    }

    public async Task<IReadOnlyList<AgentOption>> GetAgentOptionsAsync(
        ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadershipNoReader(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return (await AgentDirectory.AllAsync(db, cancellationToken))
            .Select(a => new AgentOption(a.Id, a.Codename))
            .ToList();
    }

    public async Task<IReadOnlyList<RecordOption>> SearchRecordsAsync(
        ClaimsPrincipal actor, IReadOnlyList<string> entityTypes, string? query, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadershipNoReader(actor);
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }
        var types = entityTypes.Count > 0
            ? entityTypes
            : CustomFieldRecordTypes.All.Select(t => t.TypeName).ToList();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await LookupAsync(db, types, query.Trim(), ids: null, perType: SearchPerType, cancellationToken);
    }

    public async Task<IReadOnlyList<RecordOption>> DescribeRecordsAsync(
        ClaimsPrincipal actor, IReadOnlyList<string> entityIds, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadershipNoReader(actor);
        if (entityIds.Count == 0)
        {
            return [];
        }
        var types = CustomFieldRecordTypes.All.Select(t => t.TypeName).ToList();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await LookupAsync(db, types, query: null, entityIds, perType: entityIds.Count, cancellationToken);
    }

    // one flat query per record type; ids are GUIDs, so an id can only ever hit its own type
    private static async Task<List<RecordOption>> LookupAsync(
        AppDbContext db, IReadOnlyList<string> types, string? query, IReadOnlyList<string>? ids,
        int perType, CancellationToken ct)
    {
        var result = new List<RecordOption>();

        async Task Collect(string type, IQueryable<Named> rows)
        {
            if (!types.Contains(type))
            {
                return;
            }
            var filtered = ids is not null
                ? rows.Where(r => ids.Contains(r.Id))
                : rows.Where(r => r.Name.Contains(query!) || r.CaseNumber.Contains(query!));
            foreach (var r in await filtered.OrderBy(r => r.Name).Take(perType).ToListAsync(ct))
            {
                result.Add(new RecordOption(type, r.Id, $"{CustomFieldRecordTypes.Display(type)}: {r.Name} ({r.CaseNumber})"));
            }
        }

        await Collect(nameof(Person), db.People.AsNoTracking().Select(x => new Named(x.Id, x.Name, x.CaseNumber)));
        await Collect(nameof(Faction), db.Factions.AsNoTracking().Select(x => new Named(x.Id, x.Name, x.CaseNumber)));
        await Collect(nameof(PersonGroup), db.PersonGroups.AsNoTracking().Select(x => new Named(x.Id, x.Name, x.CaseNumber)));
        await Collect(nameof(Party), db.Parties.AsNoTracking().Select(x => new Named(x.Id, x.Name, x.CaseNumber)));
        await Collect(nameof(Operation), db.Operations.AsNoTracking().Select(x => new Named(x.Id, x.Title, x.CaseNumber)));
        await Collect(nameof(Case), db.Cases.AsNoTracking().Select(x => new Named(x.Id, x.Title, x.CaseNumber)));
        await Collect(nameof(Taskforce), db.Taskforces.AsNoTracking().Select(x => new Named(x.Id, x.Name, x.CaseNumber)));
        return result;
    }

    // a rule whose JSON no longer parses is skipped, not thrown on: one bad row must not blind the cockpit
    private static CounterIntelRuleView? View(CounterIntelRule rule)
        => CounterIntelRuleDefinition.TryParse(rule.DefinitionJson) is { } definition
            ? new CounterIntelRuleView(rule.Id, rule.Name, rule.Description, rule.Severity, rule.IsActive, rule.Order,
                definition, rule.Notify, rule.ReportFrom)
            : null;

    private static string Shorten(string name) => name.Length <= 150 ? name : name[..150];

    // EF needs a named projection type; the record-picker only ever shows these three columns
    private sealed record Named(string Id, string Name, string CaseNumber);
}
