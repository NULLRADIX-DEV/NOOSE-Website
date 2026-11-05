using NOOSE_Website.Data.Entities;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Which Discord roles an agent should carry and which ones the bot may take away; pure, no I/O.</summary>
public static class DiscordRoleRules
{
    /// <summary>Error column value for a member who is not on the Discord server; not a failure.</summary>
    public const string NotOnServer = "Nicht auf dem Server";

    /// <summary>Roles for the agent's current state; null means leave the member alone.</summary>
    public static DiscordRoleTarget? Target(Agent agent, DiscordRoleMapping map)
    {
        if (agent.Id == DemoIdentity.AgentId || string.IsNullOrWhiteSpace(agent.DiscordId))
        {
            return null;
        }

        if (agent.PartnerAgency is not null)
        {
            return agent.Status is AgentStatus.Active or AgentStatus.Terminated or AgentStatus.Blocked
                ? Nothing(map)
                : null;
        }

        // never released: not FIB, may belong to another faction
        if (!CountsAsFormerAgent(agent))
        {
            return null;
        }

        return agent.Status switch
        {
            AgentStatus.Active => agent.IsTeamLead ? TeamLeadOnly(map) : ActiveInternal(agent, map),
            AgentStatus.Terminated or AgentStatus.Blocked => agent.IsTeamLead ? Nothing(map) : ResidentOnly(map),
            _ => null,
        };
    }

    /// <summary>A deleted account is treated like a terminated internal one.</summary>
    public static DiscordRoleTarget DeletedTarget(DiscordRoleMapping map) => ResidentOnly(map);

    /// <summary>Released at some point and not a partner, so a terminated or blocked account is still a former agent.</summary>
    public static bool CountsAsFormerAgent(Agent agent) => agent.Rank != null && agent.PartnerAgency == null;

    /// <summary>Add what is missing, remove managed roles that should not be there; unmapped roles stay.</summary>
    public static DiscordRoleDiff Diff(DiscordRoleTarget target, IReadOnlyCollection<string> current)
    {
        var have = new HashSet<string>(current, StringComparer.Ordinal);
        var add = target.Desired.Where(role => !have.Contains(role)).Order(StringComparer.Ordinal).ToList();
        var remove = have.Where(role => target.Managed.Contains(role) && !target.Desired.Contains(role))
            .Order(StringComparer.Ordinal).ToList();
        return new DiscordRoleDiff(add, remove);
    }

    /// <summary>Error text for a mapping that must not be saved; null when fine.</summary>
    public static string? Validate(DiscordRoleMapping map)
    {
        var resident = DiscordRoleMapping.Ids(map.Resident).ToHashSet(StringComparer.Ordinal);
        if (resident.Count == 0)
        {
            return null;
        }
        foreach (var (label, ids) in OtherEntries(map))
        {
            if (DiscordRoleMapping.Ids(ids).Any(resident.Contains))
            {
                return $"Die Rolle für Bewohner darf in keinem anderen Eintrag stehen (hier: {label}), "
                       + "sonst verliert ein aktiver Agent sie im selben Zug wieder.";
            }
        }
        return null;
    }

    private static DiscordRoleTarget ActiveInternal(Agent agent, DiscordRoleMapping map)
    {
        var desired = new HashSet<string>(DiscordRoleMapping.Ids(map.Base), StringComparer.Ordinal);
        if (agent.Rank is { } rank)
        {
            desired.UnionWith(Lookup(map.ByRank, rank));
        }
        var unit = DivisionRules.Of(agent);
        if (unit.IsBoard)
        {
            desired.UnionWith(DiscordRoleMapping.Ids(map.Board));
        }
        else if (unit.Home is { } home)
        {
            desired.UnionWith(Lookup(map.ByDivision, home));
        }
        if (agent.IsHRB)
        {
            desired.UnionWith(DiscordRoleMapping.Ids(map.Administration));
        }
        if (agent.IsAdmin)
        {
            desired.UnionWith(DiscordRoleMapping.Ids(map.Admin));
        }
        return new DiscordRoleTarget(desired, map.AllManaged);
    }

    private static DiscordRoleTarget TeamLeadOnly(DiscordRoleMapping map)
        => new(new HashSet<string>(DiscordRoleMapping.Ids(map.TeamLead), StringComparer.Ordinal), ManagedWithoutResident(map));

    private static DiscordRoleTarget ResidentOnly(DiscordRoleMapping map)
        => new(new HashSet<string>(DiscordRoleMapping.Ids(map.Resident), StringComparer.Ordinal), map.AllManaged);

    private static DiscordRoleTarget Nothing(DiscordRoleMapping map)
        => new(new HashSet<string>(StringComparer.Ordinal), ManagedWithoutResident(map));

    private static HashSet<string> ManagedWithoutResident(DiscordRoleMapping map)
    {
        var managed = new HashSet<string>(map.AllManaged, StringComparer.Ordinal);
        managed.ExceptWith(DiscordRoleMapping.Ids(map.Resident));
        return managed;
    }

    private static IEnumerable<string> Lookup<TKey>(IReadOnlyDictionary<TKey, IReadOnlyList<string>>? entries, TKey key)
        where TKey : notnull
        => entries is not null && entries.TryGetValue(key, out var list) ? DiscordRoleMapping.Ids(list) : [];

    private static IEnumerable<(string Label, IReadOnlyList<string>? Ids)> OtherEntries(DiscordRoleMapping map)
    {
        yield return ("Grundrollen", map.Base);
        foreach (var (rank, ids) in map.ByRank ?? new Dictionary<Rank, IReadOnlyList<string>>())
        {
            yield return (RankDisplay.DefaultName(rank), ids);
        }
        foreach (var (division, ids) in map.ByDivision ?? new Dictionary<Division, IReadOnlyList<string>>())
        {
            yield return (DivisionDisplay.Short(division), ids);
        }
        yield return ("Board", map.Board);
        yield return ("Administration", map.Administration);
        yield return ("Admin", map.Admin);
        yield return ("Teamleitung", map.TeamLead);
    }
}
