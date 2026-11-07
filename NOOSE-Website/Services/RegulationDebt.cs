using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Regulation;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>One agent who owes the acknowledgment of one paragraph.</summary>
public sealed class RegulationDebtRow
{
    public string ParagraphId { get; init; } = "";
    public string AgentId { get; init; } = "";

    /// <summary>First version whose acknowledgment still counts.</summary>
    public int AcknowledgeFrom { get; init; }
}

/// <summary>One paragraph an agent still owes: its section and when the owed version was published.</summary>
public sealed class RegulationOpenRow
{
    public string ParagraphId { get; init; } = "";
    public string SectionId { get; init; } = "";
    public string Heading { get; init; } = "";

    /// <summary>The owed minimum version.</summary>
    public int Version { get; init; }

    /// <summary>When exactly that version was published.</summary>
    public DateTime Since { get; init; }
}

/// <summary>One acknowledgment, flat: no entity, no navigation.</summary>
public sealed class RegulationAckRow
{
    public string AgentId { get; init; } = "";
    public int Version { get; init; }
    public DateTime AcknowledgedAt { get; init; }
}

/// <summary>The one debt predicate: badge, my shift, bell and quota read only this.</summary>
public static class RegulationDebt
{
    /// <summary>Live paragraphs: neither repealed nor trashed, in a section that is not trashed.</summary>
    public static IQueryable<RegulationParagraph> Live(AppDbContext db)
        => from p in db.RegulationParagraphs
           where p.RepealedAt == null
           join s in db.RegulationSections on p.SectionId equals s.Id
           select p;

    /// <summary>Every selectable agent in the audience of a live paragraph, acknowledged or not.</summary>
    /// <remarks>
    /// The home division is derived as in <see cref="DivisionRules.Of(NOOSE_Website.Data.Entities.Agent)"/>: an empty
    /// column means HRT for the legacy TRU flag and CID otherwise, and the HRT below Special Agent is CID. The Board is
    /// its own seat and owes no division paragraph. <see cref="RegulationRules.InAudience"/> is the in-memory twin.
    /// </remarks>
    public static IQueryable<RegulationDebtRow> Audience(AppDbContext db)
    {
        var agents = db.Users.OnlySelectable();
        // locals so EF parameterizes
        var board = DivisionRules.BoardFloor;
        var hrtFloor = DivisionRules.HrtMinimumRank;
        var bureau = RegulationAudience.Bureau;
        var administration = RegulationAudience.Administration;
        var cid = RegulationAudience.Cid;
        var ctd = RegulationAudience.Ctd;
        var hrt = RegulationAudience.Hrt;
        // uncorrelated cross product
        return from p in Live(db)
               from u in agents
               where p.Audience == bureau
                   || (p.Audience == administration && u.IsHRB)
                   || (u.Rank != null && u.Rank < board && (
                       (p.Audience == hrt && u.Rank >= hrtFloor
                           && (u.Division == Division.Hrt || (u.Division == null && u.IsTRU)))
                       || (p.Audience == ctd && u.Division == Division.Ctd)
                       || (p.Audience == cid && (u.Division == Division.Cid
                           || (u.Division == null && !u.IsTRU)
                           || (u.Rank < hrtFloor && (u.Division == Division.Hrt || (u.Division == null && u.IsTRU)))))))
               select new RegulationDebtRow
               {
                   ParagraphId = p.Id,
                   AgentId = u.Id,
                   AcknowledgeFrom = p.AcknowledgeFromVersion,
               };
    }

    /// <summary>The audience rows that still lack an acknowledgment of a version that counts.</summary>
    public static IQueryable<RegulationDebtRow> Owed(AppDbContext db)
        => Audience(db).Where(r => !db.RegulationAcknowledgments.Any(a =>
            a.ParagraphId == r.ParagraphId && a.AgentId == r.AgentId && a.Version >= r.AcknowledgeFrom));

    /// <summary>What one agent still owes, joined to the version that asked; uncorrelated joins only.</summary>
    public static IQueryable<RegulationOpenRow> OpenFor(AppDbContext db, string agentId)
        => from r in Owed(db).Where(r => r.AgentId == agentId)
           join v in db.RegulationVersions on new { r.ParagraphId, Version = r.AcknowledgeFrom } equals new { v.ParagraphId, v.Version }
           join p in db.RegulationParagraphs on r.ParagraphId equals p.Id
           select new RegulationOpenRow
           {
               ParagraphId = p.Id,
               SectionId = p.SectionId,
               Heading = p.Heading,
               Version = r.AcknowledgeFrom,
               Since = v.PublishedAt,
           };

    /// <summary>Every acknowledgment of one paragraph, flat; the caller keeps each agent's highest version.</summary>
    public static IQueryable<RegulationAckRow> AcknowledgmentsOf(AppDbContext db, string paragraphId)
        => db.RegulationAcknowledgments.Where(a => a.ParagraphId == paragraphId)
            .Select(a => new RegulationAckRow { AgentId = a.AgentId, Version = a.Version, AcknowledgedAt = a.AcknowledgedAt });
}

/// <summary>Read gate and helpers of the regulation: every internal agent reads every live paragraph.</summary>
public static class RegulationVisibility
{
    /// <summary>Paragraphs the viewer may see, trashed sections excluded; nothing for a partner or a hand-built scope.</summary>
    public static IQueryable<RegulationParagraph> OnlyVisible(this IQueryable<RegulationParagraph> query, AppDbContext db,
        ViewerScope scope)
    {
        if (!RegulationRules.MayRead(scope))
        {
            return query.Where(_ => false);
        }
        return from p in query
               join s in db.RegulationSections on p.SectionId equals s.Id
               select p;
    }

    /// <summary>True if the paragraph exists and the viewer may see it; missing and hidden answer alike.</summary>
    public static Task<bool> IsVisibleAsync(AppDbContext db, string paragraphId, ViewerScope scope,
        CancellationToken cancellationToken = default)
        => db.RegulationParagraphs.OnlyVisible(db, scope).AnyAsync(p => p.Id == paragraphId, cancellationToken);

    /// <summary>Section numbers by id: the foreword has none, the others count up in order. Trashed sections are left out.</summary>
    public static async Task<Dictionary<string, (int? Number, string Title)>> SectionNumbersAsync(AppDbContext db,
        CancellationToken cancellationToken = default)
    {
        var sections = await db.RegulationSections.AsNoTracking()
            .OrderByDescending(s => s.IsPreamble).ThenBy(s => s.SortOrder).ThenBy(s => s.Title)
            .Select(s => new { s.Id, s.Title, s.IsPreamble })
            .ToListAsync(cancellationToken);
        var result = new Dictionary<string, (int? Number, string Title)>();
        var n = 0;
        foreach (var s in sections)
        {
            result[s.Id] = (s.IsPreamble ? null : ++n, s.Title);
        }
        return result;
    }

    /// <summary>Citation labels of paragraphs, for links, mentions, search and the bell; trashed rows keep their heading.</summary>
    public static async Task<Dictionary<string, string>> LabelsAsync(AppDbContext db, IReadOnlyCollection<string> paragraphIds,
        CancellationToken cancellationToken = default)
    {
        if (paragraphIds.Count == 0)
        {
            return new();
        }
        var numbers = await SectionNumbersAsync(db, cancellationToken);
        var rows = await db.RegulationParagraphs.AsNoTracking().IgnoreQueryFilters()
            .Where(p => paragraphIds.Contains(p.Id))
            .Select(p => new { p.Id, p.SectionId, p.Heading })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.Id,
            r => RegulationRules.ParagraphLabel(numbers.TryGetValue(r.SectionId, out var s) ? s.Number : null, r.Heading));
    }
}
