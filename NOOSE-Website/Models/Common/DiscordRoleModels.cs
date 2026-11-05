using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Common;

/// <summary>Discord role ids per entry; one entry may hold several roles, one role may sit in several entries.</summary>
public sealed record DiscordRoleMapping(
    IReadOnlyList<string> Base,
    IReadOnlyDictionary<Rank, IReadOnlyList<string>> ByRank,
    IReadOnlyDictionary<Division, IReadOnlyList<string>> ByDivision,
    IReadOnlyList<string> Board,
    IReadOnlyList<string> Administration,
    IReadOnlyList<string> Admin,
    IReadOnlyList<string> TeamLead,
    IReadOnlyList<string> Resident)
{
    /// <summary>Nothing mapped, so the bot assigns and removes nothing.</summary>
    public static DiscordRoleMapping Empty { get; } = new(
        [],
        new Dictionary<Rank, IReadOnlyList<string>>(),
        new Dictionary<Division, IReadOnlyList<string>>(),
        [], [], [], [], []);

    /// <summary>Every mapped role id; the bot touches nothing else.</summary>
    public IReadOnlySet<string> AllManaged
    {
        get
        {
            var all = new HashSet<string>(StringComparer.Ordinal);
            foreach (var list in Lists())
            {
                all.UnionWith(Ids(list));
            }
            return all;
        }
    }

    /// <summary>Usable ids of one entry; a missing list counts as empty, blank ids are dropped.</summary>
    internal static IEnumerable<string> Ids(IReadOnlyList<string>? list)
        => list is null ? [] : list.Where(id => !string.IsNullOrWhiteSpace(id));

    private IEnumerable<IReadOnlyList<string>?> Lists()
    {
        yield return Base;
        foreach (var list in ByRank?.Values ?? [])
        {
            yield return list;
        }
        foreach (var list in ByDivision?.Values ?? [])
        {
            yield return list;
        }
        yield return Board;
        yield return Administration;
        yield return Admin;
        yield return TeamLead;
        yield return Resident;
    }
}

/// <summary>What one agent should carry (Desired) and which roles the bot may take away from them (Managed).</summary>
public sealed record DiscordRoleTarget(IReadOnlySet<string> Desired, IReadOnlySet<string> Managed);

/// <summary>Roles to put on and to take off a member.</summary>
public sealed record DiscordRoleDiff(IReadOnlyList<string> Add, IReadOnlyList<string> Remove)
{
    public bool IsEmpty => Add.Count == 0 && Remove.Count == 0;
}
