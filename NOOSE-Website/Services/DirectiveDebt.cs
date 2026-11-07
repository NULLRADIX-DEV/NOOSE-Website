using NOOSE_Website.Data;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>One agent who owes the acknowledgment of one directive.</summary>
public sealed class DirectiveDebtRow
{
    public string DirectiveId { get; init; } = "";
    public string AgentId { get; init; } = "";

    /// <summary>First version whose acknowledgment still counts.</summary>
    public int AcknowledgeFrom { get; init; }
}

/// <summary>One directive an agent still owes: the number for the label and when the owed version was published.</summary>
public sealed class DirectiveOpenRow
{
    public string DirectiveId { get; init; } = "";
    public int Number { get; init; }
    public string Title { get; init; } = "";

    /// <summary>The owed minimum version.</summary>
    public int Version { get; init; }

    /// <summary>When exactly that version was published.</summary>
    public DateTime Since { get; init; }
}

/// <summary>One acknowledgment, flat: no entity, no navigation.</summary>
public sealed class DirectiveAckRow
{
    public string AgentId { get; init; } = "";
    public int Version { get; init; }
    public DateTime AcknowledgedAt { get; init; }
}

/// <summary>The one debt predicate: badge, my shift, bell and quota read only this.</summary>
public static class DirectiveDebt
{
    /// <summary>Every selectable agent who may see a live directive, acknowledged or not.</summary>
    public static IQueryable<DirectiveDebtRow> Audience(AppDbContext db)
    {
        var agents = db.Users.OnlySelectable();
        // mirrors IsLeadership; supervision is never selectable
        var floor = Rank.SectionChief;
        // uncorrelated cross product
        return from d in db.Directives
               where d.RepealedAt == null
               from u in agents
               where d.Classification == DocumentClassification.None
                   || u.IsAdmin
                   || u.Rank >= floor
                   || (d.Classification == DocumentClassification.Tru && u.IsTRU)
                   || (d.Classification == DocumentClassification.Hrb && u.IsHRB)
               select new DirectiveDebtRow
               {
                   DirectiveId = d.Id,
                   AgentId = u.Id,
                   AcknowledgeFrom = d.AcknowledgeFromVersion,
               };
    }

    /// <summary>The audience rows that still lack an acknowledgment of a version that counts.</summary>
    public static IQueryable<DirectiveDebtRow> Owed(AppDbContext db)
        => Audience(db).Where(r => !db.DirectiveAcknowledgments.Any(a =>
            a.DirectiveId == r.DirectiveId && a.AgentId == r.AgentId && a.Version >= r.AcknowledgeFrom));

    /// <summary>What one agent still owes, joined to the version that asked; uncorrelated joins only.</summary>
    public static IQueryable<DirectiveOpenRow> OpenFor(AppDbContext db, string agentId)
        => from r in Owed(db).Where(r => r.AgentId == agentId)
           join v in db.DirectiveVersions on new { r.DirectiveId, Version = r.AcknowledgeFrom } equals new { v.DirectiveId, v.Version }
           join d in db.Directives on r.DirectiveId equals d.Id
           select new DirectiveOpenRow
           {
               DirectiveId = d.Id,
               Number = d.Number,
               Title = d.Title,
               Version = r.AcknowledgeFrom,
               Since = v.PublishedAt,
           };

    /// <summary>Every acknowledgment of one directive, flat; the caller keeps each agent's highest version.</summary>
    public static IQueryable<DirectiveAckRow> AcknowledgmentsOf(AppDbContext db, string directiveId)
        => db.DirectiveAcknowledgments.Where(a => a.DirectiveId == directiveId)
            .Select(a => new DirectiveAckRow { AgentId = a.AgentId, Version = a.Version, AcknowledgedAt = a.AcknowledgedAt });
}
