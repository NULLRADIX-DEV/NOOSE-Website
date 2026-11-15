using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.DrugRoutes;
using NOOSE_Website.Data.Entities.Evidence;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Handbook;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Taskforces;

namespace NOOSE_Website.Services;

/// <summary>The phonetic keys + stems a record contributes to the search side-index, keyed by the target record
/// (aliases fold onto their person) and the origin row (SourceId, the delete key).</summary>
public readonly record struct SearchIndexRow(
    string EntityType, string EntityId, string SourceId,
    IReadOnlyList<string> PhoneticKeys, IReadOnlyList<string> Stems);

/// <summary>Central mapping of search-indexed entities to their phonetic seeds (name fields) and stem fields (all text).
/// Kept in one place instead of a marker interface so entity classes stay clean and EF-agnostic.</summary>
public static class SearchIndexProjection
{
    /// <summary>Index contribution for an entity, or null when the type is not search-indexed.</summary>
    /// <remarks>
    /// Called for every tracked entry of every <c>SaveChangesAsync</c> in the app, so what goes in here is names
    /// and identifiers only — never a longtext body. Stemming a document would write thousands of rows inside the
    /// user's transaction, which is why <c>SideIndexed</c> and <c>Heavy</c> are mutually exclusive in the catalog.
    /// </remarks>
    public static SearchIndexRow? For(object entity) => entity switch
    {
        Person p => Build(nameof(Person), p.Id, p.Id,
            new[] { p.Name }, new[] { p.Name, p.Description, p.WantedReason, p.CaseNumber }),
        // aliases pay into their person's index (target = PersonId, source = alias id)
        PersonAlias a => Build(nameof(Person), a.PersonId, a.Id,
            new[] { a.AliasName }, new[] { a.AliasName }),
        Faction f => Build(nameof(Faction), f.Id, f.Id,
            new[] { f.Name },
            new[] { f.Name, f.Kind, f.Radio, f.Darkchat, f.IssuingTimes, f.Estate, f.Targets, f.Description, f.CaseNumber }),
        PersonGroup g => Build(nameof(PersonGroup), g.Id, g.Id,
            new[] { g.Name }, new[] { g.Name, g.Description, g.Targets, g.CaseNumber }),
        Party p => Build(nameof(Party), p.Id, p.Id,
            new[] { p.Name }, new[] { p.Name, p.Description, p.Targets, p.Remarks, p.CaseNumber }),
        DrugRoute r => Build(nameof(DrugRoute), r.Id, r.Id,
            new[] { r.Designation }, new[] { r.Designation, r.Substance, r.Description, r.Findings, r.Remarks, r.CaseNumber }),
        Operation o => Build(nameof(Operation), o.Id, o.Id,
            new[] { o.Title }, new[] { o.Title, o.Type, o.Location, o.Expiry, o.Result, o.Remarks, o.CaseNumber }),
        Taskforce t => Build(nameof(Taskforce), t.Id, t.Id,
            new[] { t.Name }, new[] { t.Name, t.Purpose, t.Remarks, t.CaseNumber }),
        Case c => Build(nameof(Case), c.Id, c.Id,
            new[] { c.Title }, new[] { c.Title, c.Type, c.Description, c.Summary, c.ClosingNote, c.CaseNumber }),
        Job j => Build(nameof(Job), j.Id, j.Id,
            new[] { j.Title }, new[] { j.Title, j.Description, j.CaseNumber }),
        // codename only: the real name is leadership-exclusive and this table carries no visibility gate.
        // Note the cost — Identity writes the user row on every login, so this re-tokenises inside that transaction.
        // It is two short fields; do not grow it.
        Agent a => Build(nameof(Agent), a.Id, a.Id,
            new[] { a.Codename }, new[] { a.Codename, a.BadgeNumber }),
        // the paragraph is misremembered, not misspelled — the title is what needs the phonetic pass
        Law g => Build(nameof(Law), g.Id, g.Id,
            new[] { g.Title }, new[] { g.Paragraph, g.Title, g.LawBook }),
        EvidenceItem i => Build(nameof(EvidenceItem), i.Id, i.Id,
            new[] { i.Name }, new[] { i.Name, i.Category }),
        // keyed on the SLUG, not the row id: the hit carries the slug too, and the side-index pass dedupes
        // its candidates against the target ids already found. Two different keys there meant every article
        // the plain recall had found came back a second time. SourceId stays the row id, so a rename
        // rewrites the entry instead of leaving the old slug behind (the PersonAlias arm does the same).
        HandbookArticle a2 => Build(nameof(HandbookArticle), a2.Slug, a2.Id,
            new[] { a2.Title }, new[] { a2.Title, a2.Summary, a2.Slug }),
        // the synonyms are the point of the phonetic pass here: somebody misremembers the spelling of a term
        GlossaryTerm t2 => Build(nameof(GlossaryTerm), t2.Id, t2.Id,
            new[] { t2.Term }, new[] { t2.Term, t2.Synonyms, t2.ShortDefinition }),
        // Informant is deliberately absent: the only field worth a phonetic pass is the V-person's real name, and
        // this table has no gate — partners search against it too. Indexing the case number instead buys nothing:
        // an exact case number is already found by the LIKE recall.
        _ => null,
    };

    private static SearchIndexRow Build(string type, string id, string sourceId, string?[] names, string?[] texts)
        => new(type, id, sourceId, SearchTokenizer.PhoneticKeys(names), SearchTokenizer.Stems(texts));
}
