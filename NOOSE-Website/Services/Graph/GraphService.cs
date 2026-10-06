using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Graph;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IGraphService" />
public class GraphService(IDbContextFactory<AppDbContext> dbFactory, IPartnerVisibilityPolicyService? partnerPolicy = null) : IGraphService
{
    private const int MaxNode = 1500;

    private const int MaxPathDepth = 12;
    private const int MaxVisited = 8000;

    /// <summary>Node types a partner may ever see in the graph; docs, observations, jobs and agents never.</summary>
    public static readonly IReadOnlySet<string> PartnerNodeTypes = new HashSet<string>
    {
        nameof(Person), nameof(Faction), nameof(PersonGroup), nameof(Party),
        nameof(Operation), nameof(Case), nameof(Law), nameof(Taskforce),
    };

    public async Task<GraphData> GetGraphAsync(GraphQuery query, ClaimsPrincipal viewer, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await MayUseAsync(db, viewer, cancellationToken))
        {
            return new GraphData(Array.Empty<GraphNode>(), Array.Empty<GraphEdge>(), false);
        }
        var isLeadership = viewer.IsLeadership();
        var meId = viewer.GetAgentId();

        var rawEdges = await GraphEdgeLoader.LoadRawEdgesAsync(db, query.KindFilter, cancellationToken);

        var keys = new HashSet<string>();
        foreach (var k in rawEdges)
        {
            keys.Add(k.Source);
            keys.Add(k.Target);
        }
        if (query.FocusType is not null && query.FocusId is not null)
        {
            keys.Add($"{query.FocusType}:{query.FocusId}");
        }
        var markKey = query.MarkType is not null && query.MarkId is not null
            ? $"{query.MarkType}:{query.MarkId}"
            : null;
        if (markKey is not null)
        {
            // an isolated record still shows up marked
            keys.Add(markKey);
        }

        var node = await ResolveNodeAsync(db, keys, isLeadership, meId, viewer.IsPartner(), cancellationToken);
        await KeepPartnerVisibleAsync(db, node, viewer, cancellationToken);

        if (query.TypeFilter is { Count: > 0 })
        {
            var allowed = query.TypeFilter.ToHashSet();
            foreach (var key in node.Keys.ToList())
            {
                if (!allowed.Contains(node[key].Type))
                {
                    node.Remove(key);
                }
            }
        }

        var edges = rawEdges
            .Where(k => k.Source != k.Target && node.ContainsKey(k.Source) && node.ContainsKey(k.Target))
            .ToList();

        var truncated = false;
        HashSet<string> keep;

        if (query.FocusType is not null && query.FocusId is not null)
        {
            var focusKey = $"{query.FocusType}:{query.FocusId}";
            if (!node.ContainsKey(focusKey))
            {
                return new GraphData(Array.Empty<GraphNode>(), Array.Empty<GraphEdge>(), false);
            }
            keep = Radius(focusKey, edges, Math.Clamp(query.Depth, 1, 3));
        }
        else
        {
            keep = node.Keys.ToHashSet();
            if (keep.Count > MaxNode)
            {
                var degree = DegreeCount(edges);
                keep = keep
                    .OrderByDescending(k => degree.TryGetValue(k, out var g) ? g : 0)
                    .Take(MaxNode)
                    .ToHashSet();
                truncated = true;
                if (markKey is not null && node.ContainsKey(markKey))
                {
                    // the marking must not lose to the degree cut
                    keep.Add(markKey);
                }
            }
        }

        var finalEdges = edges
            .Where(k => keep.Contains(k.Source) && keep.Contains(k.Target))
            .ToList();
        var degreeFinal = DegreeCount(finalEdges);

        var nodeList = keep
            .Where(node.ContainsKey)
            .Select(k => node[k] with
            {
                Degree = degreeFinal.TryGetValue(k, out var g) ? g : 0,
                IsFocus = k == markKey,
            })
            .ToList();
        var edgesList = finalEdges
            .Select(k => new GraphEdge(k.Source, k.Target, k.Label, k.Kind, k.Automatic))
            .ToList();

        // opt-in analytics on the final rendered graph (never persisted)
        if (query.ComputeCommunities || query.ComputeCentrality)
        {
            var ids = nodeList.Select(n => n.Id).ToList();
            var pairs = edgesList.Select(e => (e.Source, e.Target)).ToList();
            var comm = query.ComputeCommunities ? GraphAnalytics.Communities(ids, pairs) : null;
            var bet = query.ComputeCentrality ? GraphAnalytics.Betweenness(ids, pairs) : null;
            nodeList = nodeList.Select(n =>
            {
                var b = bet is null ? n.Betweenness : bet.GetValueOrDefault(n.Id, 0);
                return n with
                {
                    CommunityId = comm is null ? n.CommunityId : comm.GetValueOrDefault(n.Id, 0),
                    Betweenness = b,
                    IsKeyFigure = bet is null ? n.IsKeyFigure : GraphAnalytics.IsKey(b),
                };
            }).ToList();
        }

        return new GraphData(nodeList, edgesList, truncated);
    }

    public async Task<PathResult> FindPathAsync(string sourceType, string sourceId, string targetType, string targetId, ClaimsPrincipal viewer, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await MayUseAsync(db, viewer, cancellationToken))
        {
            return new PathResult(false, Array.Empty<GraphNode>(), Array.Empty<GraphEdge>());
        }
        var isLeadership = viewer.IsLeadership();
        var meId = viewer.GetAgentId();

        var sourceKey = $"{sourceType}:{sourceId}";
        var targetKey = $"{targetType}:{targetId}";

        var rawEdges = await GraphEdgeLoader.LoadRawEdgesAsync(db, null, cancellationToken);
        var keys = new HashSet<string> { sourceKey, targetKey };
        foreach (var k in rawEdges)
        {
            keys.Add(k.Source);
            keys.Add(k.Target);
        }
        var node = await ResolveNodeAsync(db, keys, isLeadership, meId, viewer.IsPartner(), cancellationToken);
        await KeepPartnerVisibleAsync(db, node, viewer, cancellationToken);

        if (!node.ContainsKey(sourceKey) || !node.ContainsKey(targetKey))
        {
            return new PathResult(false, Array.Empty<GraphNode>(), Array.Empty<GraphEdge>());
        }
        if (sourceKey == targetKey)
        {
            return new PathResult(true, new[] { node[sourceKey] }, Array.Empty<GraphEdge>());
        }

        var adj = new Dictionary<string, List<RawEdge>>();
        void Connect(string a, RawEdge k)
        {
            if (!adj.TryGetValue(a, out var list))
            {
                list = new();
                adj[a] = list;
            }
            list.Add(k);
        }
        foreach (var k in rawEdges)
        {
            if (k.Source == k.Target || !node.ContainsKey(k.Source) || !node.ContainsKey(k.Target))
            {
                continue;
            }
            Connect(k.Source, k);
            Connect(k.Target, k);
        }

        // BFS with predecessors.
        var predecessor = new Dictionary<string, RawEdge>();
        var depth = new Dictionary<string, int> { [sourceKey] = 0 };
        var queue = new Queue<string>();
        queue.Enqueue(sourceKey);
        var visited = 0;
        var found = false;

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current == targetKey)
            {
                found = true;
                break;
            }
            if (++visited > MaxVisited || depth[current] >= MaxPathDepth || !adj.TryGetValue(current, out var neighbors))
            {
                continue;
            }
            foreach (var edge in neighbors)
            {
                var other = edge.Source == current ? edge.Target : edge.Source;
                if (depth.ContainsKey(other))
                {
                    continue;
                }
                depth[other] = depth[current] + 1;
                predecessor[other] = edge;
                queue.Enqueue(other);
            }
        }

        if (!found)
        {
            return new PathResult(false, Array.Empty<GraphNode>(), Array.Empty<GraphEdge>());
        }

        var nodePath = new List<string> { targetKey };
        var edgesPath = new List<RawEdge>();
        var cursor = targetKey;
        while (cursor != sourceKey)
        {
            var edge = predecessor[cursor];
            edgesPath.Add(edge);
            cursor = edge.Source == cursor ? edge.Target : edge.Source;
            nodePath.Add(cursor);
        }
        nodePath.Reverse();
        edgesPath.Reverse();

        return new PathResult(
            true,
            nodePath.Select(k => node[k]).ToList(),
            edgesPath.Select(k => new GraphEdge(k.Source, k.Target, k.Label, k.Kind, k.Automatic)).ToList());
    }

    /// <summary>Internal agents always; partners only with the agency's graph function.</summary>
    private static async Task<bool> MayUseAsync(AppDbContext db, ClaimsPrincipal viewer, CancellationToken cancellationToken)
        => !viewer.IsPartner()
            || (await PartnerVisibility.FeaturesAsync(db, viewer.GetPartnerAgency(), cancellationToken)).HasFlag(PartnerFeature.Graph);

    /// <summary>For a partner, drops every node that is not a released record of a partner node type the rank may see; edges follow the nodes.</summary>
    private async Task KeepPartnerVisibleAsync(AppDbContext db, Dictionary<string, GraphNode> nodes, ClaimsPrincipal viewer, CancellationToken cancellationToken)
    {
        if (viewer.GetPartnerAgency() is not { } agency)
        {
            return;
        }
        var meId = viewer.GetAgentId();
        var rankTypes = partnerPolicy is null ? null : await partnerPolicy.GetAllowedTypesAsync(viewer, cancellationToken);
        var visible = new HashSet<string>();
        foreach (var byType in nodes.Values.GroupBy(n => n.Type)
                     .Where(g => PartnerNodeTypes.Contains(g.Key) && (rankTypes is null || rankTypes.Contains(g.Key))))
        {
            var ids = byType.Select(n => n.Id[(byType.Key.Length + 1)..]).ToList();
            var released = byType.Key switch
            {
                nameof(Person) => await db.People.Where(x => ids.Contains(x.Id)).OnlyPartnerVisible(db, agency, meId).Select(x => x.Id).ToListAsync(cancellationToken),
                nameof(Faction) => await db.Factions.Where(x => ids.Contains(x.Id)).OnlyPartnerVisible(db, agency, meId).Select(x => x.Id).ToListAsync(cancellationToken),
                nameof(PersonGroup) => await db.PersonGroups.Where(x => ids.Contains(x.Id)).OnlyPartnerVisible(db, agency, meId).Select(x => x.Id).ToListAsync(cancellationToken),
                nameof(Party) => await db.Parties.Where(x => ids.Contains(x.Id)).OnlyPartnerVisible(db, agency, meId).Select(x => x.Id).ToListAsync(cancellationToken),
                nameof(Operation) => await db.Operations.Where(x => ids.Contains(x.Id)).OnlyPartnerVisible(db, agency, meId).Select(x => x.Id).ToListAsync(cancellationToken),
                nameof(Case) => await db.Cases.Where(x => ids.Contains(x.Id)).OnlyPartnerVisible(db, agency, meId).Select(x => x.Id).ToListAsync(cancellationToken),
                nameof(Law) => await db.Laws.Where(x => ids.Contains(x.Id)).OnlyPartnerVisible(db, agency, meId).Select(x => x.Id).ToListAsync(cancellationToken),
                nameof(Taskforce) => await db.Taskforces.Where(x => ids.Contains(x.Id)).OnlyPartnerVisible(db, agency, meId).Select(x => x.Id).ToListAsync(cancellationToken),
                _ => new List<string>(),
            };
            visible.UnionWith(released.Select(id => $"{byType.Key}:{id}"));
        }
        foreach (var key in nodes.Keys.Where(k => !visible.Contains(k)).ToList())
        {
            nodes.Remove(key);
        }
    }

    // ---- Resolve nodes ----

    /// <remarks><paramref name="allTaskforces"/>: a partner sees released taskforces it is no member of; the partner filter gates them afterwards.</remarks>
    private static async Task<Dictionary<string, GraphNode>> ResolveNodeAsync(
        AppDbContext db, IEnumerable<string> keys, bool isLeadership, string? meId, bool allTaskforces, CancellationToken cancellationToken)
    {
        var targetType = new Dictionary<string, HashSet<string>>();
        foreach (var key in keys)
        {
            var idx = key.IndexOf(':');
            if (idx <= 0 || idx >= key.Length - 1)
            {
                continue;
            }
            var type = key[..idx];
            var id = key[(idx + 1)..];
            if (!targetType.TryGetValue(type, out var set))
            {
                set = new();
                targetType[type] = set;
            }
            set.Add(id);
        }

        var result = new Dictionary<string, GraphNode>();
        GraphNode Mk(string type, string id, string bez, string? under, string? href, int classification, bool classified)
            => new($"{type}:{id}", type, bez, under, href, classification, classified, null, 0);

        List<string> Ids(string type) => targetType.TryGetValue(type, out var s) ? s.ToList() : new();

        // ---- Person ----
        var personIds = Ids(nameof(Person));
        if (personIds.Count > 0)
        {
            var rows = await db.People.Where(p => personIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Name, p.CaseNumber, p.IsClassified, p.Classification, p.CreatedAt, p.ThreatScore, p.IsArchived })
                .ToListAsync(cancellationToken);
            foreach (var x in rows)
            {
                if (x.IsClassified && !isLeadership)
                {
                    continue;
                }
                result[$"{nameof(Person)}:{x.Id}"] = Mk(nameof(Person), x.Id, x.Name, x.CaseNumber, $"/personen/{x.Id}", (int)x.Classification, x.IsClassified)
                    with { CreatedAt = x.CreatedAt, ThreatScore = x.ThreatScore, IsArchived = x.IsArchived };
            }

            var visiblePers = rows.Where(r => isLeadership || !r.IsClassified).Select(r => r.Id).ToList();
            if (visiblePers.Count > 0)
            {
                var photos = await db.PersonPhotos.Where(f => visiblePers.Contains(f.PersonId))
                    .Select(f => new { f.Id, f.PersonId, f.CreatedAt })
                    .ToListAsync(cancellationToken);
                foreach (var grp in photos.GroupBy(f => f.PersonId))
                {
                    var first = grp.OrderBy(f => f.CreatedAt).First();
                    var key = $"{nameof(Person)}:{grp.Key}";
                    if (result.TryGetValue(key, out var kn))
                    {
                        result[key] = kn with { PhotoUrl = $"/dateien/personen/foto/{first.Id}" };
                    }
                }
            }
        }

        // ---- Faction ----
        var factionIds = Ids(nameof(Faction));
        if (factionIds.Count > 0)
        {
            foreach (var x in await db.Factions.Where(f => factionIds.Contains(f.Id))
                .Select(f => new { f.Id, f.Name, f.CaseNumber, f.IsClassified, f.Classification, f.CreatedAt, f.ThreatScore, f.IsArchived }).ToListAsync(cancellationToken))
            {
                if (x.IsClassified && !isLeadership)
                {
                    continue;
                }
                result[$"{nameof(Faction)}:{x.Id}"] = Mk(nameof(Faction), x.Id, x.Name, x.CaseNumber, $"/fraktionen/{x.Id}", (int)x.Classification, x.IsClassified)
                    with { CreatedAt = x.CreatedAt, ThreatScore = x.ThreatScore, IsArchived = x.IsArchived };
            }
        }

        // ---- Person group ----
        var groupsIds = Ids(nameof(PersonGroup));
        if (groupsIds.Count > 0)
        {
            foreach (var x in await db.PersonGroups.Where(g => groupsIds.Contains(g.Id))
                .Select(g => new { g.Id, g.Name, g.CaseNumber, g.IsClassified, g.Classification, g.IsArchived }).ToListAsync(cancellationToken))
            {
                if (x.IsClassified && !isLeadership)
                {
                    continue;
                }
                result[$"{nameof(PersonGroup)}:{x.Id}"] = Mk(nameof(PersonGroup), x.Id, x.Name, x.CaseNumber, $"/personengruppen/{x.Id}", (int)x.Classification, x.IsClassified)
                    with { IsArchived = x.IsArchived };
            }
        }

        // ---- Party ----
        var partyIds = Ids(nameof(Party));
        if (partyIds.Count > 0)
        {
            foreach (var x in await db.Parties.Where(p => partyIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Name, p.CaseNumber, p.IsClassified, p.Classification, p.IsArchived }).ToListAsync(cancellationToken))
            {
                if (x.IsClassified && !isLeadership)
                {
                    continue;
                }
                result[$"{nameof(Party)}:{x.Id}"] = Mk(nameof(Party), x.Id, x.Name, x.CaseNumber, $"/parteien/{x.Id}", (int)x.Classification, x.IsClassified)
                    with { IsArchived = x.IsArchived };
            }
        }

        // ---- Operation ----
        var operationIds = Ids(nameof(Operation));
        if (operationIds.Count > 0)
        {
            foreach (var x in await db.Operations.Where(o => operationIds.Contains(o.Id))
                .Select(o => new { o.Id, o.Title, o.CaseNumber, o.IsClassified, o.Classification, o.IsArchived }).ToListAsync(cancellationToken))
            {
                if (x.IsClassified && !isLeadership)
                {
                    continue;
                }
                result[$"{nameof(Operation)}:{x.Id}"] = Mk(nameof(Operation), x.Id, x.Title, x.CaseNumber, $"/operationen/{x.Id}", (int)x.Classification, x.IsClassified)
                    with { IsArchived = x.IsArchived };
            }
        }

        // ---- Case ----
        var caseIds = Ids(nameof(Case));
        if (caseIds.Count > 0)
        {
            foreach (var x in await db.Cases.Where(v => caseIds.Contains(v.Id))
                .Select(v => new { v.Id, v.Title, v.CaseNumber, v.IsClassified, v.Classification, v.IsArchived }).ToListAsync(cancellationToken))
            {
                if (x.IsClassified && !isLeadership)
                {
                    continue;
                }
                result[$"{nameof(Case)}:{x.Id}"] = Mk(nameof(Case), x.Id, x.Title, x.CaseNumber, $"/vorgaenge/{x.Id}", (int)x.Classification, x.IsClassified)
                    with { IsArchived = x.IsArchived };
            }
        }

        // ---- Taskforce ----
        var taskforceIds = Ids(nameof(Taskforce));
        if (taskforceIds.Count > 0)
        {
            var visible = await TaskforceVisibility.VisibleIdsAsync(db, taskforceIds, isLeadership || allTaskforces, meId, cancellationToken);
            foreach (var x in await db.Taskforces.Where(t => visible.Contains(t.Id))
                .Select(t => new { t.Id, t.Name, t.CaseNumber, t.IsClassified, t.IsArchived }).ToListAsync(cancellationToken))
            {
                result[$"{nameof(Taskforce)}:{x.Id}"] = Mk(nameof(Taskforce), x.Id, x.Name, x.CaseNumber, $"/taskforces/{x.Id}", 0, x.IsClassified)
                    with { IsArchived = x.IsArchived };
            }
        }

        // ---- Job ----
        var jobIds = Ids(nameof(Job));
        if (jobIds.Count > 0)
        {
            foreach (var x in await db.Jobs.Where(a => jobIds.Contains(a.Id))
                .Select(a => new { a.Id, a.Title, a.CaseNumber }).ToListAsync(cancellationToken))
            {
                result[$"{nameof(Job)}:{x.Id}"] = Mk(nameof(Job), x.Id, x.Title, x.CaseNumber, $"/aufgaben/{x.Id}", 0, false);
            }
        }

        // ---- Agent ----
        var agentIds = Ids(nameof(Agent));
        if (agentIds.Count > 0)
        {
            foreach (var x in await db.Users.Where(u => agentIds.Contains(u.Id))
                .Select(u => new { u.Id, u.Codename }).ToListAsync(cancellationToken))
            {
                var name = string.IsNullOrWhiteSpace(x.Codename) ? "(unbenannter Agent)" : x.Codename;
                result[$"{nameof(Agent)}:{x.Id}"] = Mk(nameof(Agent), x.Id, name, null, null, 0, false);
            }
        }

        // ---- Law ----
        var lawIds = Ids(nameof(Law));
        if (lawIds.Count > 0)
        {
            foreach (var x in await db.Laws.Where(g => lawIds.Contains(g.Id))
                .Select(g => new { g.Id, g.Paragraph, g.Title, g.LawBook }).ToListAsync(cancellationToken))
            {
                result[$"{nameof(Law)}:{x.Id}"] = Mk(nameof(Law), x.Id, $"{x.Paragraph} {x.Title}", x.LawBook, $"/gesetze/{x.Id}", 0, false);
            }
        }

        // ---- Person doc ----
        var docIds = Ids(nameof(PersonDoc));
        if (docIds.Count > 0)
        {
            foreach (var x in await db.PersonDocs.Where(d => docIds.Contains(d.Id))
                .Join(db.People, d => d.PersonId, p => p.Id,
                      (d, p) => new { d.Id, d.Timestamp, PersonId = p.Id, PersonName = p.Name, p.IsClassified })
                .ToListAsync(cancellationToken))
            {
                if (x.IsClassified && !isLeadership)
                {
                    continue;
                }
                result[$"{nameof(PersonDoc)}:{x.Id}"] = Mk(nameof(PersonDoc), x.Id,
                    $"Dok – {x.PersonName}", x.Timestamp.ToLocalTime().ToString("dd.MM.yyyy"),
                    $"/personen/{x.PersonId}?tab=doks", 0, x.IsClassified);
            }
        }

        // ---- Observation ----
        var observationIds = Ids(nameof(Observation));
        if (observationIds.Count > 0)
        {
            foreach (var x in await db.Observations.Where(o => observationIds.Contains(o.Id))
                .Join(db.People, o => o.PersonId, p => p.Id,
                      (o, p) => new { o.Id, o.Start, PersonId = p.Id, PersonName = p.Name, p.IsClassified })
                .ToListAsync(cancellationToken))
            {
                if (x.IsClassified && !isLeadership)
                {
                    continue;
                }
                result[$"{nameof(Observation)}:{x.Id}"] = Mk(nameof(Observation), x.Id,
                    $"Observation – {x.PersonName}", x.Start.ToLocalTime().ToString("dd.MM.yyyy"),
                    $"/personen/{x.PersonId}?tab=ueberwachung", 0, x.IsClassified);
            }
        }

        return result;
    }

    // ---- Graph helpers ----

    private static Dictionary<string, int> DegreeCount(IEnumerable<RawEdge> edges)
    {
        var degree = new Dictionary<string, int>();
        foreach (var k in edges)
        {
            degree[k.Source] = degree.TryGetValue(k.Source, out var a) ? a + 1 : 1;
            degree[k.Target] = degree.TryGetValue(k.Target, out var b) ? b + 1 : 1;
        }
        return degree;
    }

    private static HashSet<string> Radius(string start, IEnumerable<RawEdge> edges, int depth)
    {
        var adj = new Dictionary<string, List<string>>();
        void Connect(string a, string b)
        {
            if (!adj.TryGetValue(a, out var list))
            {
                list = new();
                adj[a] = list;
            }
            list.Add(b);
        }
        foreach (var k in edges)
        {
            Connect(k.Source, k.Target);
            Connect(k.Target, k.Source);
        }

        var visited = new HashSet<string> { start };
        var rand = new List<string> { start };
        for (var hop = 0; hop < depth; hop++)
        {
            var next = new List<string>();
            foreach (var node in rand)
            {
                if (!adj.TryGetValue(node, out var neighbors))
                {
                    continue;
                }
                foreach (var n in neighbors)
                {
                    if (visited.Add(n))
                    {
                        next.Add(n);
                    }
                }
            }
            if (next.Count == 0)
            {
                break;
            }
            rand = next;
        }
        return visited;
    }
}
