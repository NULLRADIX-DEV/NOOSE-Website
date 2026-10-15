using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Common;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IMentionService" />
public class MentionService(IDbContextFactory<AppDbContext> dbFactory, ISearchService search) : IMentionService
{
    private const int CandidatesPerGroup = 5;

    /// <summary>Stands in for a target the viewer may not see; never the record's name.</summary>
    internal const string ClassifiedLabel = "Verschlusssache";

    /// <summary>Stands in for a target that is gone, trashed or outside the viewer's taskforces.</summary>
    internal const string MissingLabel = "(nicht verfügbar)";

    // releasable record types whose name a partner mention may reveal once released; all other types are hidden from partners
    private static readonly string[] PartnerReleasableMentionTypes =
    {
        nameof(Person), nameof(Faction), nameof(PersonGroup), nameof(Party),
        nameof(Operation), nameof(Case), nameof(Taskforce), nameof(Document),
    };

    public async Task<IReadOnlyList<MentionSegment>> ResolveAsync(string? text, bool isLeadership, string? meId, CancellationToken cancellationToken = default, PartnerAgency? partnerAgency = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<MentionSegment>();
        }
        var tokens = MentionParser.Parse(text);
        if (tokens.Count == 0)
        {
            return new[] { new MentionSegment(false, text) };
        }

        var refs = tokens.Select(t => (t.Type, t.Id)).Distinct().ToList();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // foreign taskforces stay unresolved -> shown as unavailable chip
        var map = await RecordsReference.ResolveAsync(db, refs, cancellationToken, mayAllTaskforces: isLeadership, meId: meId);
        if (partnerAgency is { } agency)
        {
            await ApplyPartnerScopeAsync(db, map, agency, meId, cancellationToken);
        }
        return Segment(text, tokens, map, isLeadership);
    }

    public async Task<IReadOnlyList<IReadOnlyList<MentionSegment>>> ResolveManyAsync(IReadOnlyList<string?> texts, bool isLeadership, string? meId, CancellationToken cancellationToken = default, PartnerAgency? partnerAgency = null)
    {
        // collect all tokens from all texts and resolve in one query
        var tokenPerText = texts.Select(MentionParser.Parse).ToList();
        var refs = tokenPerText.SelectMany(ts => ts).Select(t => (t.Type, t.Id)).Distinct().ToList();

        Dictionary<(string, string), RecordsReference.Resolution> map;
        if (refs.Count == 0)
        {
            map = new();
        }
        else
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            // foreign taskforces stay unresolved
            map = await RecordsReference.ResolveAsync(db, refs, cancellationToken, mayAllTaskforces: isLeadership, meId: meId);
            if (partnerAgency is { } agency)
            {
                await ApplyPartnerScopeAsync(db, map, agency, meId, cancellationToken);
            }
        }

        var result = new List<IReadOnlyList<MentionSegment>>(texts.Count);
        for (var i = 0; i < texts.Count; i++)
        {
            result.Add(Segment(texts[i] ?? string.Empty, tokenPerText[i], map, isLeadership));
        }
        return result;
    }

    public async Task<string> ResolveHtmlAsync(string? html, bool isLeadership, string? meId, CancellationToken cancellationToken = default, PartnerAgency? partnerAgency = null, bool plain = false)
    {
        var refs = MentionHtml.Refs(html);
        if (refs.Count == 0)
        {
            return html ?? string.Empty;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // foreign taskforces stay unresolved -> shown as unavailable
        var map = await RecordsReference.ResolveAsync(db, refs, cancellationToken, mayAllTaskforces: isLeadership, meId: meId);
        if (partnerAgency is { } agency)
        {
            await ApplyPartnerScopeAsync(db, map, agency, meId, cancellationToken);
        }
        return MentionHtml.Rewrite(html, map, isLeadership, plain);
    }

    public async Task<IReadOnlyDictionary<string, string>> HtmlLabelsAsync(string? html, bool isLeadership, string? meId, CancellationToken cancellationToken = default, PartnerAgency? partnerAgency = null)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        var refs = MentionHtml.Refs(html);
        if (refs.Count == 0)
        {
            return labels;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var map = await RecordsReference.ResolveAsync(db, refs, cancellationToken, mayAllTaskforces: isLeadership, meId: meId);
        if (partnerAgency is { } agency)
        {
            await ApplyPartnerScopeAsync(db, map, agency, meId, cancellationToken);
        }
        foreach (var (type, id) in refs)
        {
            // every ref gets a chip: an unlabelled token would sit in the editor as loose text the writer can break
            labels[$"{type}:{id}"] = map.TryGetValue((type, id), out var a)
                ? (a.Classified && !isLeadership ? ClassifiedLabel : a.Display)
                : MissingLabel;
        }
        return labels;
    }

    /// <summary>Drops resolved mentions a partner may not see (unreleased, classified, or a non-releasable type) so they render as a neutral unavailable chip — no name, Aktenzeichen or link.</summary>
    private static async Task ApplyPartnerScopeAsync(
        AppDbContext db, Dictionary<(string, string), RecordsReference.Resolution> map,
        PartnerAgency agency, string? meId, CancellationToken cancellationToken)
    {
        if (map.Count == 0)
        {
            return;
        }
        var keep = new HashSet<(string, string)>();
        foreach (var type in PartnerReleasableMentionTypes)
        {
            // classified targets are never partner-visible, so don't even consider them
            var ids = map.Where(kv => kv.Key.Item1 == type && !kv.Value.Classified)
                .Select(kv => kv.Key.Item2).Distinct().ToList();
            if (ids.Count == 0)
            {
                continue;
            }
            foreach (var id in await PartnerVisibility.ReleasedParentIdsAsync(db, type, ids, agency, meId, cancellationToken))
            {
                keep.Add((type, id));
            }
            // a partner also sees a document they authored themselves, even without a share
            if (type == nameof(Document) && meId is not null)
            {
                foreach (var id in await db.Documents.Where(d => ids.Contains(d.Id) && d.CreatedById == meId)
                    .Select(d => d.Id).ToListAsync(cancellationToken))
                {
                    keep.Add((type, id));
                }
            }
        }
        foreach (var key in map.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            map.Remove(key);
        }
    }

    /// <summary>The one place that decides how a resolved, classified or missing target reads; shared with <see cref="MentionHtml"/>.</summary>
    internal static List<MentionSegment> Segment(string text, IReadOnlyList<MentionToken> tokens,
        Dictionary<(string, string), RecordsReference.Resolution> map, bool isLeadership)
    {
        if (tokens.Count == 0)
        {
            return new() { new MentionSegment(false, text) };
        }
        var segments = new List<MentionSegment>();
        var pos = 0;
        foreach (var tok in tokens)
        {
            if (tok.Start > pos)
            {
                segments.Add(new MentionSegment(false, text.Substring(pos, tok.Start - pos)));
            }
            if (map.TryGetValue((tok.Type, tok.Id), out var a))
            {
                // classified the viewer can't see -> neutral chip without name/link
                segments.Add(a.Classified && !isLeadership
                    ? new MentionSegment(true, ClassifiedLabel, tok.Type, null, Hidden: true)
                    : new MentionSegment(true, a.Display, tok.Type, a.Href, false));
            }
            else
            {
                // target deleted/unknown
                segments.Add(new MentionSegment(true, MissingLabel, tok.Type, null, false));
            }
            pos = tok.Start + tok.Length;
        }
        if (pos < text.Length)
        {
            segments.Add(new MentionSegment(false, text.Substring(pos)));
        }
        return segments;
    }

    public async Task<List<MentionHit>> CandidatesAsync(string? text, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s))
        {
            return new();
        }

        var mayClassifiedRead = actor.MayClassifiedRead();
        var mayRealName = actor.MayRealNameSee();
        var meId = actor.GetAgentId();
        var hit = new List<MentionHit>();

        // records via quick search, classification- and taskforce-membership-filtered
        var records = await search.QuickSearchAsync(s, actor, 8, cancellationToken);
        hit.AddRange(records
            // not everything the palette offers can carry a mention: the token is @{Typ:GUID}, and an offer the
            // parser cannot match again would be stored raw in the comment and rendered literally. ReferenceTypes
            // names the types that resolve to a reference at all - necessary, but not sufficient: the demo
            // agent's row id is "demo-agent", which is in a reference type and still not a GUID. The second test is
            // the parser's own acceptance condition, so it can only drop offers that were already broken.
            .Where(a => LinkService.ReferenceTypes.Contains(a.Category, StringComparer.Ordinal)
                && Guid.TryParse(a.TargetId, out _))
            .Select(a => new MentionHit(a.Category, a.TargetId, a.Name, a.CaseNumber)));

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // real name gated by mayRealName, kept separate from the classified-read flag.
        // Deliberately NOT AgentSelection.OnlySelectable: partners stay mentionable here.
        var agents = await db.Users
            .Where(u => u.Status == AgentStatus.Active && !u.IsTeamLead
                && (u.Codename.Contains(s) || (mayRealName && u.RealName != null && u.RealName.Contains(s))))
            .OrderBy(u => u.Codename).Take(CandidatesPerGroup)
            .Select(u => new { u.Id, u.Codename, u.RealName }).ToListAsync(cancellationToken);
        hit.AddRange(agents.Select(a => new MentionHit(nameof(Agent), a.Id,
            string.IsNullOrWhiteSpace(a.Codename) ? "(unbenannter Agent)" : a.Codename,
            mayRealName ? a.RealName : null)));

        // sources by title; visibility derived from the parent record
        var sourcesRaw = await db.Sources.Where(q => q.Title.Contains(s))
            .OrderByDescending(q => q.ModifiedAt ?? q.CreatedAt).Take(20)
            .Select(q => new { q.Id, q.Title, q.EntityType, q.EntityId }).ToListAsync(cancellationToken);
        if (sourcesRaw.Count > 0)
        {
            var parentsRefs = sourcesRaw.Select(q => (q.EntityType, q.EntityId)).Distinct().ToList();
            // taskforce parents must respect membership, not just classification
            var parentsMap = await RecordsReference.ResolveAsync(db, parentsRefs, cancellationToken,
                mayAllTaskforces: mayClassifiedRead, meId: meId);
            var count = 0;
            foreach (var q in sourcesRaw)
            {
                if (!parentsMap.TryGetValue((q.EntityType, q.EntityId), out var e) || (e.Classified && !mayClassifiedRead))
                {
                    continue; // parent missing/trashed or classified without access
                }
                hit.Add(new MentionHit(nameof(Source), q.Id, string.IsNullOrWhiteSpace(q.Title) ? "Quelle" : q.Title, e.Display));
                if (++count >= CandidatesPerGroup)
                {
                    break;
                }
            }
        }

        return hit;
    }
}
