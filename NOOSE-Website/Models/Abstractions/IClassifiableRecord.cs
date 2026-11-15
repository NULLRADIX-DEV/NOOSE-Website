namespace NOOSE_Website.Models.Abstractions;

/// <summary>Marks a record that carries the three secrecy flags: Person, Faction, PersonGroup, Party, DrugRoute, Operation, Case.</summary>
/// <remarks>
/// Exists so <see cref="Services.RecordVisibility"/> can state the rule once instead of six times. Documents look
/// similar but are NOT this: there <c>IsClassified</c> means leadership-exclusive, here it means restricted at all —
/// the same three columns read two different ways, which is exactly why they must not share a predicate.
/// </remarks>
public interface IClassifiableRecord
{
    bool IsClassified { get; set; }
    bool IsTRUClassified { get; set; }
    bool IsHRBClassified { get; set; }
}
