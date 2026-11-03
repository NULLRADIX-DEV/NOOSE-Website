using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Models.Dashboard;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

public class DashboardService(IDbContextFactory<AppDbContext> dbFactory, IRequestService requestService,
    IRecencyService recency) : IDashboardService
{
    public async Task<DashboardMetrics> GetMetricsAsync(bool isLeadership, string? meId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Classification filter mirrors each list view so the tile matches its hit count.
        var people = await db.People.OnlyActive().CountAsync(p => isLeadership || !p.IsClassified, cancellationToken);
        var factions = await db.Factions.OnlyActive().CountAsync(f => isLeadership || !f.IsClassified, cancellationToken);
        var groups = await db.PersonGroups.OnlyActive().CountAsync(g => isLeadership || !g.IsClassified, cancellationToken);
        var parties = await db.Parties.OnlyActive().CountAsync(p => isLeadership || !p.IsClassified, cancellationToken);
        var operations = await db.Operations.OnlyActive().CountAsync(o => isLeadership || !o.IsClassified, cancellationToken);

        // Open cases = not yet completed/archived.
        var openCases = await db.Cases.OnlyActive().CountAsync(v => (isLeadership || !v.IsClassified)
            && v.Status != CaseStatus.Completed && v.Status != CaseStatus.Archived, cancellationToken);

        // Open requests = upgrades + pending registrations + name changes + requested taskforces + promotions.
        var openRequests = await requestService.GetOpenCountAsync(isLeadership, cancellationToken)
            + await db.Users.CountAsync(a => a.Status == AgentStatus.Pending, cancellationToken)
            + await db.Users.CountAsync(a => a.NameChangeRequestedAt != null, cancellationToken)
            + await db.Taskforces.OnlyVisible(db, isLeadership, meId).OnlyActive().CountAsync(t => t.Status == TaskforceStatus.Requested, cancellationToken)
            + await PromotionRules.CountOpenAsync(db, await StandingAsync(db, meId, cancellationToken), cancellationToken);

        // The classified count is itself classified, so leadership-only.
        var classified = 0;
        if (isLeadership)
        {
            classified =
                  await db.People.OnlyActive().CountAsync(p => p.IsClassified, cancellationToken)
                + await db.Factions.OnlyActive().CountAsync(f => f.IsClassified, cancellationToken)
                + await db.PersonGroups.OnlyActive().CountAsync(g => g.IsClassified, cancellationToken)
                + await db.Parties.OnlyActive().CountAsync(p => p.IsClassified, cancellationToken)
                + await db.Operations.OnlyActive().CountAsync(o => o.IsClassified, cancellationToken)
                + await db.Taskforces.OnlyActive().CountAsync(t => t.IsClassified, cancellationToken)
                + await db.Cases.OnlyActive().CountAsync(v => v.IsClassified, cancellationToken);
        }

        // Stale records: per type past the configured red threshold, referenced by ModifiedAt ?? CreatedAt.
        var settings = await recency.GetAllSettingsAsync(cancellationToken);
        var now = DateTime.UtcNow;
        DateTime CutoffDate(string type) => now.AddDays(-settings[type].StaleDays);
        var sP = CutoffDate(nameof(Person));
        var sF = CutoffDate(nameof(Faction));
        var sG = CutoffDate(nameof(PersonGroup));
        var sPt = CutoffDate(nameof(Party));
        var sO = CutoffDate(nameof(Operation));
        var sT = CutoffDate(nameof(Taskforce));
        var sV = CutoffDate(nameof(Case));
        // A type with aging disabled contributes nothing; exempt records drop out per type.
        var staleRecords =
              (settings[nameof(Person)].AgingDisabled ? 0 : await db.People.OnlyActive().CountAsync(p => (isLeadership || !p.IsClassified) && !p.AgingDisabled && (p.ModifiedAt ?? p.CreatedAt) < sP, cancellationToken))
            // factions age by their four facet stamps, so the cutoff test goes through the shared filter
            + (settings[nameof(Faction)].AgingDisabled ? 0 : await db.Factions.OnlyActive()
                .Where(f => (isLeadership || !f.IsClassified) && !f.IsStateFaction && !f.AgingDisabled)
                .Where(FactionRecency.ReferenceBefore(sF))
                .CountAsync(cancellationToken))
            + (settings[nameof(PersonGroup)].AgingDisabled ? 0 : await db.PersonGroups.OnlyActive().CountAsync(g => (isLeadership || !g.IsClassified) && !g.AgingDisabled && (g.ModifiedAt ?? g.CreatedAt) < sG, cancellationToken))
            + (settings[nameof(Party)].AgingDisabled ? 0 : await db.Parties.OnlyActive().CountAsync(p => (isLeadership || !p.IsClassified) && !p.AgingDisabled && (p.ModifiedAt ?? p.CreatedAt) < sPt, cancellationToken))
            + (settings[nameof(Operation)].AgingDisabled ? 0 : await db.Operations.OnlyActive().CountAsync(o => (isLeadership || !o.IsClassified) && !o.AgingDisabled && (o.ModifiedAt ?? o.CreatedAt) < sO, cancellationToken))
            + (settings[nameof(Taskforce)].AgingDisabled ? 0 : await db.Taskforces.OnlyVisible(db, isLeadership, meId).OnlyActive().CountAsync(t => !t.AgingDisabled && (t.ModifiedAt ?? t.CreatedAt) < sT, cancellationToken))
            + (settings[nameof(Case)].AgingDisabled ? 0 : await db.Cases.OnlyActive().CountAsync(v => (isLeadership || !v.IsClassified) && !v.AgingDisabled && (v.ModifiedAt ?? v.CreatedAt) < sV, cancellationToken));

        // The org tile bundles factions, groups and parties; operations are their own tile.
        return new DashboardMetrics(people, factions + groups + parties, operations, openCases, openRequests, classified, staleRecords);
    }

    public async Task<List<DashboardStaleRecord>> GetUpdateNeedAsync(bool isLeadership, string? meId, int max = 30,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var settings = await recency.GetAllSettingsAsync(cancellationToken);
        var now = DateTime.UtcNow;
        var result = new List<DashboardStaleRecord>();

        // Update need starts at the yellow threshold; load the oldest N per type, then globally sort and cap.
        // A type with aging disabled is skipped entirely; exempt records drop out per type.
        var setP = settings[nameof(Person)];
        if (!setP.AgingDisabled)
        {
            var cutP = now.AddDays(-setP.WarningDays);
            foreach (var x in await db.People.OnlyActive()
                .Where(p => (isLeadership || !p.IsClassified) && !p.AgingDisabled && (p.ModifiedAt ?? p.CreatedAt) < cutP)
                .OrderBy(p => p.ModifiedAt ?? p.CreatedAt)
                .Select(p => new { p.Id, p.Name, p.CaseNumber, Reference = p.ModifiedAt ?? p.CreatedAt })
                .Take(max).ToListAsync(cancellationToken))
            {
                result.Add(new DashboardStaleRecord(DashboardRecordType.Person, x.Name, x.CaseNumber, $"/personen/{x.Id}",
                    RecencyAssessment.Level(setP.WarningDays, setP.StaleDays, x.Reference, now), x.Reference));
            }
        }

        var setF = settings[nameof(Faction)];
        if (!setF.AgingDisabled)
        {
            var cutF = now.AddDays(-setF.WarningDays);
            // The oldest of four facet stamps has no SQL minimum, so cap in memory after the cutoff filter.
            var factionRows = await db.Factions.OnlyActive()
                .Where(f => (isLeadership || !f.IsClassified) && !f.IsStateFaction && !f.AgingDisabled)
                .Where(FactionRecency.ReferenceBefore(cutF))
                .Select(f => new
                {
                    f.Id, f.Name, f.CaseNumber, f.CreatedAt,
                    f.MembersRefreshedAt, f.StockRefreshedAt, f.ActivitiesRefreshedAt, f.DocsRefreshedAt,
                })
                .ToListAsync(cancellationToken);
            foreach (var x in factionRows
                .Select(f => new
                {
                    f.Id, f.Name, f.CaseNumber,
                    Reference = FactionRecency.Reference(f.CreatedAt, f.MembersRefreshedAt, f.StockRefreshedAt,
                        f.ActivitiesRefreshedAt, f.DocsRefreshedAt),
                })
                .OrderBy(f => f.Reference)
                .Take(max))
            {
                result.Add(new DashboardStaleRecord(DashboardRecordType.Faction, x.Name, x.CaseNumber, $"/fraktionen/{x.Id}",
                    RecencyAssessment.Level(setF.WarningDays, setF.StaleDays, x.Reference, now), x.Reference));
            }
        }

        var setG = settings[nameof(PersonGroup)];
        if (!setG.AgingDisabled)
        {
            var cutG = now.AddDays(-setG.WarningDays);
            foreach (var x in await db.PersonGroups.OnlyActive()
                .Where(g => (isLeadership || !g.IsClassified) && !g.AgingDisabled && (g.ModifiedAt ?? g.CreatedAt) < cutG)
                .OrderBy(g => g.ModifiedAt ?? g.CreatedAt)
                .Select(g => new { g.Id, g.Name, g.CaseNumber, Reference = g.ModifiedAt ?? g.CreatedAt })
                .Take(max).ToListAsync(cancellationToken))
            {
                result.Add(new DashboardStaleRecord(DashboardRecordType.PersonGroup, x.Name, x.CaseNumber, $"/personengruppen/{x.Id}",
                    RecencyAssessment.Level(setG.WarningDays, setG.StaleDays, x.Reference, now), x.Reference));
            }
        }

        var setPt = settings[nameof(Party)];
        if (!setPt.AgingDisabled)
        {
            var cutPt = now.AddDays(-setPt.WarningDays);
            foreach (var x in await db.Parties.OnlyActive()
                .Where(p => (isLeadership || !p.IsClassified) && !p.AgingDisabled && (p.ModifiedAt ?? p.CreatedAt) < cutPt)
                .OrderBy(p => p.ModifiedAt ?? p.CreatedAt)
                .Select(p => new { p.Id, p.Name, p.CaseNumber, Reference = p.ModifiedAt ?? p.CreatedAt })
                .Take(max).ToListAsync(cancellationToken))
            {
                result.Add(new DashboardStaleRecord(DashboardRecordType.Party, x.Name, x.CaseNumber, $"/parteien/{x.Id}",
                    RecencyAssessment.Level(setPt.WarningDays, setPt.StaleDays, x.Reference, now), x.Reference));
            }
        }

        var setO = settings[nameof(Operation)];
        if (!setO.AgingDisabled)
        {
            var cutO = now.AddDays(-setO.WarningDays);
            foreach (var x in await db.Operations.OnlyActive()
                .Where(o => (isLeadership || !o.IsClassified) && !o.AgingDisabled && (o.ModifiedAt ?? o.CreatedAt) < cutO)
                .OrderBy(o => o.ModifiedAt ?? o.CreatedAt)
                .Select(o => new { o.Id, Name = o.Title, o.CaseNumber, Reference = o.ModifiedAt ?? o.CreatedAt })
                .Take(max).ToListAsync(cancellationToken))
            {
                result.Add(new DashboardStaleRecord(DashboardRecordType.Operation, x.Name, x.CaseNumber, $"/operationen/{x.Id}",
                    RecencyAssessment.Level(setO.WarningDays, setO.StaleDays, x.Reference, now), x.Reference));
            }
        }

        var setT = settings[nameof(Taskforce)];
        if (!setT.AgingDisabled)
        {
            var cutT = now.AddDays(-setT.WarningDays);
            foreach (var x in await db.Taskforces.OnlyVisible(db, isLeadership, meId).OnlyActive()
                .Where(t => !t.AgingDisabled && (t.ModifiedAt ?? t.CreatedAt) < cutT)
                .OrderBy(t => t.ModifiedAt ?? t.CreatedAt)
                .Select(t => new { t.Id, t.Name, t.CaseNumber, Reference = t.ModifiedAt ?? t.CreatedAt })
                .Take(max).ToListAsync(cancellationToken))
            {
                result.Add(new DashboardStaleRecord(DashboardRecordType.Taskforce, x.Name, x.CaseNumber, $"/taskforces/{x.Id}",
                    RecencyAssessment.Level(setT.WarningDays, setT.StaleDays, x.Reference, now), x.Reference));
            }
        }

        var setV = settings[nameof(Case)];
        if (!setV.AgingDisabled)
        {
            var cutV = now.AddDays(-setV.WarningDays);
            foreach (var x in await db.Cases.OnlyActive()
                .Where(v => (isLeadership || !v.IsClassified) && !v.AgingDisabled && (v.ModifiedAt ?? v.CreatedAt) < cutV)
                .OrderBy(v => v.ModifiedAt ?? v.CreatedAt)
                .Select(v => new { v.Id, Name = v.Title, v.CaseNumber, Reference = v.ModifiedAt ?? v.CreatedAt })
                .Take(max).ToListAsync(cancellationToken))
            {
                result.Add(new DashboardStaleRecord(DashboardRecordType.Case, x.Name, x.CaseNumber, $"/vorgaenge/{x.Id}",
                    RecencyAssessment.Level(setV.WarningDays, setV.StaleDays, x.Reference, now), x.Reference));
            }
        }

        // Oldest first, then globally cap.
        return result.OrderBy(e => e.ReferenceUtc).Take(max).ToList();
    }

    public async Task<List<DashboardFactionHazard>> GetFactionsByHazardAsync(bool isLeadership,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Most dangerous first; hazard level derived on-read from the threat score, no score sorts last.
        var rows = await db.Factions.OnlyActive()
            .Where(f => isLeadership || !f.IsClassified)
            .OrderByDescending(f => f.ThreatScore ?? 0)
            .ThenBy(f => f.Name)
            .Select(f => new { f.Id, f.Name, f.CaseNumber, f.ThreatScore })
            .ToListAsync(cancellationToken);

        return rows.Select(f => new DashboardFactionHazard(
            f.Id, f.Name, f.CaseNumber, $"/fraktionen/{f.Id}", HazardLevelLogic.From(f.ThreatScore))).ToList();
    }

    public async Task<List<DashboardFactionHazard>> GetPeopleByHazardAsync(bool isLeadership,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Counterpart to the faction tile, most dangerous first; only scored people (> 0), top 15.
        var rows = await db.People.OnlyActive()
            .Where(p => (isLeadership || !p.IsClassified) && p.ThreatScore != null && p.ThreatScore > 0)
            .OrderByDescending(p => p.ThreatScore)
            .ThenBy(p => p.Name)
            .Select(p => new { p.Id, p.Name, p.CaseNumber, p.ThreatScore })
            .Take(15)
            .ToListAsync(cancellationToken);

        return rows.Select(p => new DashboardFactionHazard(
            p.Id, p.Name, p.CaseNumber, $"/personen/{p.Id}", HazardLevelLogic.From(p.ThreatScore))).ToList();
    }

    // same list the inbox shows
    private static async Task<PromotionStanding> StandingAsync(AppDbContext db, string? meId, CancellationToken cancellationToken)
        => PromotionStanding.Of(meId is null
            ? null
            : await db.Users.AsNoTracking().FirstOrDefaultAsync(a => a.Id == meId, cancellationToken));

    public async Task<DashboardDistributions> GetDistributionsAsync(bool isLeadership, string? meId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // All four distributions are classification-filtered like the metric tiles.

        // Cases by classification; all enum values filled so the legend stays stable.
        var classificationCount = (await db.Cases.OnlyActive()
                .Where(v => isLeadership || !v.IsClassified)
                .GroupBy(v => v.Classification)
                .Select(g => new { Value = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.Value, x => x.Count);
        var casesByClassification = ClassificationDisplay.All
            .Select(e => new DistributionSegment(ClassificationDisplay.Name(e), classificationCount.GetValueOrDefault(e)))
            .ToList();

        // Person-doc outcomes; classification filtered via the parent person (INNER JOIN also hides deleted ones).
        var outcomeCount = (await db.PersonDocs
                .Where(d => isLeadership || !d.Person!.IsClassified)
                .GroupBy(d => d.Outcome)
                .Select(g => new { Value = g.Key, Count = g.Count() })
                .ToListAsync(cancellationToken))
            .ToDictionary(x => x.Value, x => x.Count);
        var measureOutcomes = MeasureOutcomeDisplay.All
            .Select(a => new DistributionSegment(MeasureOutcomeDisplay.Name(a), outcomeCount.GetValueOrDefault(a)))
            .ToList();

        // Factions by hazard, derived on-read from the threat score; bucketed in-memory to avoid a CASE translation.
        var scores = await db.Factions.OnlyActive()
            .Where(f => isLeadership || !f.IsClassified)
            .Select(f => f.ThreatScore)
            .ToListAsync(cancellationToken);
        var hazardCount = scores
            .GroupBy(HazardLevelLogic.From)
            .ToDictionary(g => g.Key, g => g.Count());
        var factionsByHazard = HazardLevelLogic.All
            .Select(s => new DistributionSegment(HazardLevelLogic.Name(s), hazardCount.GetValueOrDefault(s)))
            .ToList();

        // Open requests by kind; same five sub-counts as the metric tile, broken out individually.
        var openRequestsByKind = new List<DistributionSegment>
        {
            new("Hochstufung", await requestService.GetOpenCountAsync(isLeadership, cancellationToken)),
            new("Registrierung", await db.Users.CountAsync(a => a.Status == AgentStatus.Pending, cancellationToken)),
            new("Namensänderung", await db.Users.CountAsync(a => a.NameChangeRequestedAt != null, cancellationToken)),
            new("Taskforce", await db.Taskforces.OnlyVisible(db, isLeadership, meId).OnlyActive().CountAsync(t => t.Status == TaskforceStatus.Requested, cancellationToken)),
            new("Beförderung", await PromotionRules.CountOpenAsync(db, await StandingAsync(db, meId, cancellationToken), cancellationToken)),
        };

        return new DashboardDistributions(casesByClassification, measureOutcomes, factionsByHazard, openRequestsByKind);
    }
}
