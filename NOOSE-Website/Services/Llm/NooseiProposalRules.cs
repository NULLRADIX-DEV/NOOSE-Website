using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Appointments;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Meetings;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Llm;
using NOOSE_Website.Services.Llm.Tools;

namespace NOOSE_Website.Services;

/// <summary>Rules a proposal is checked against, once when NOOSEI proposes it and again at the click.</summary>
public static class NooseiProposalRules
{
    public const int MaxNote = 500;
    public const int MaxTitle = 200;

    /// <summary>Below the job's own limit, so the card can show all of it.</summary>
    public const int MaxDescription = 1_000;

    public const int MaxLabel = 200;
    public const int MaxDays = 365;

    /// <summary>Types the reference resolver names; a card needs a name.</summary>
    private static readonly IReadOnlySet<string> Nameable = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(Person), nameof(Faction), nameof(PersonGroup), nameof(Party), nameof(Operation), nameof(Taskforce),
        nameof(Case), nameof(Job), nameof(Appointment), nameof(Meeting), nameof(Document), nameof(Hinweis),
        nameof(Ticket), nameof(Agent),
    };

    /// <summary>Records with a followup section.</summary>
    public static readonly IReadOnlySet<string> FollowupTypes = Offerable(
    [
        nameof(Person), nameof(Faction), nameof(PersonGroup), nameof(Party),
        nameof(Operation), nameof(Case), nameof(Taskforce),
    ]);

    public static readonly IReadOnlySet<string> TagTypes = Offerable(RecordBatch.Taggable);
    public static readonly IReadOnlySet<string> WatchTypes = Offerable(RecordBatch.Followable);
    public static readonly IReadOnlySet<string> LinkTypes = Offerable(RecordBatch.LinkAnchors.Concat(RecordBatch.Linkable));
    public static readonly IReadOnlySet<string> JobReferenceTypes = Offerable(RecordBatch.LinkTargetsFor(nameof(Job)));

    /// <summary>Openable by NOOSEI and nameable on a card.</summary>
    private static HashSet<string> Offerable(IEnumerable<string> clrs)
        => clrs.Where(t => Nameable.Contains(t) && NooseiRecordTypes.IsReadable(t)).ToHashSet(StringComparer.Ordinal);

    /// <summary>Schema enum of the types in <paramref name="clrs"/>, in catalog order.</summary>
    public static string EnumJson(IReadOnlySet<string> clrs)
        => "[" + string.Join(",", NooseiRecordTypes.Names(NooseiUse.Read)
            .Where(n => clrs.Contains(NooseiRecordTypes.Clr(n)!))
            .Select(n => "\"" + n + "\"")) + "]";

    /// <summary>A record as it appears on a card.</summary>
    public sealed record Target(string Type, string Id, string Display, bool Classified)
    {
        public string Label => $"{NooseiRecordTypes.German(Type)} {Display}";

        public LlmContextRef Ref => new(Type, Id, Display);
    }

    public sealed record AgentPick(string Id, string Codename);

    public static Task<Target?> TargetAsync(AppDbContext db, string? german, string? id, IReadOnlySet<string> allowed,
        ViewerScope scope, CancellationToken cancellationToken)
        => NooseiRecordTypes.Clr(german, NooseiUse.Read) is { } clr && id is not null
            ? ResolveAsync(db, clr, id, allowed, scope, cancellationToken)
            : Task.FromResult<Target?>(null);

    /// <summary>Allowed, existing and visible, with its card name; null otherwise.</summary>
    /// <remarks>Two gates like the chat anchor: the visibility gate, and the resolver that omits what it cannot name.</remarks>
    public static async Task<Target?> ResolveAsync(AppDbContext db, string type, string id, IReadOnlySet<string> allowed,
        ViewerScope scope, CancellationToken cancellationToken)
    {
        if (!allowed.Contains(type) || !await RecordBatch.ExistsAndVisibleAsync(db, type, id, scope, cancellationToken))
        {
            return null;
        }
        var resolved = await RecordsReference.ResolveAsync(db, [(type, id)], cancellationToken, scope.MayAllTaskforces, scope.MeId);
        return resolved.TryGetValue((type, id), out var hit) && !string.IsNullOrWhiteSpace(hit.Display)
            ? new Target(type, id, hit.Display, hit.Classified)
            : null;
    }

    public static async Task RequireAsync(AppDbContext db, string? type, string? id, IReadOnlySet<string> allowed,
        ViewerScope scope, CancellationToken cancellationToken)
    {
        if (type is null || id is null || await ResolveAsync(db, type, id, allowed, scope, cancellationToken) is null)
        {
            throw new UnauthorizedAccessException("Die Akte ist für dich nicht (mehr) zugänglich.");
        }
    }

    /// <summary>Selectable agents by exact codename, ignoring case.</summary>
    public static async Task<(List<AgentPick> Found, List<string> Unknown)> AgentsAsync(
        AppDbContext db, IReadOnlyList<string> codenames, CancellationToken cancellationToken)
    {
        var wanted = codenames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (wanted.Count == 0)
        {
            return ([], []);
        }
        // in memory: collation decides case otherwise
        var roster = await db.Users.OnlySelectable()
            .Where(u => u.Codename != "")
            .Select(u => new AgentPick(u.Id, u.Codename))
            .ToListAsync(cancellationToken);
        var found = new List<AgentPick>();
        var unknown = new List<string>();
        foreach (var name in wanted)
        {
            var hits = roster.Where(r => string.Equals(r.Codename.Trim(), name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (hits.Count == 1)
            {
                found.Add(hits[0]);
            }
            else
            {
                unknown.Add(name);
            }
        }
        return (found, unknown);
    }

    public static async Task RequireAgentsAsync(AppDbContext db, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        if (ids.Count > 0 && await db.Users.OnlySelectable().CountAsync(u => ids.Contains(u.Id), cancellationToken) != ids.Count)
        {
            throw new InvalidOperationException("Ein vorgeschlagener Agent ist nicht mehr zuteilbar.");
        }
    }

    public static async Task<bool> LinkedAsync(AppDbContext db, Target anchor, Target target, CancellationToken cancellationToken)
        => await db.Links.AsNoTracking().AnyAsync(v => v.Kind == LinkKind.Default
            && ((v.SourceType == anchor.Type && v.SourceId == anchor.Id && v.TargetType == target.Type && v.TargetId == target.Id)
                || (v.SourceType == target.Type && v.SourceId == target.Id && v.TargetType == anchor.Type && v.TargetId == anchor.Id)),
            cancellationToken);

    public static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    /// <summary>The due day from <c>in_tagen</c> or <c>faellig_am</c>; today up to a year ahead.</summary>
    public static DateOnly? Due(NooseiProposalDraft draft, out string? error)
    {
        error = null;
        DateOnly due;
        if (draft.InDays is { } days)
        {
            if (days is < 0 or > MaxDays)
            {
                error = $"in_tagen liegt zwischen 0 und {MaxDays}.";
                return null;
            }
            due = Today.AddDays(days);
        }
        else if (draft.DueDate is { } text)
        {
            if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out due))
            {
                error = "faellig_am bitte als JJJJ-MM-TT angeben, oder in_tagen benutzen.";
                return null;
            }
        }
        else
        {
            return null;
        }
        if (due < Today || due > Today.AddDays(MaxDays))
        {
            error = $"Das Datum muss zwischen heute ({Day(Today)}) und einem Jahr danach liegen.";
            return null;
        }
        return due;
    }

    public static void RequireDue(DateOnly due)
    {
        if (due < Today)
        {
            throw new InvalidOperationException("Das vorgeschlagene Datum liegt inzwischen in der Vergangenheit.");
        }
    }

    public static void RequireLength(string? text, int max)
    {
        if (text is { Length: var length } && length > max)
        {
            throw new InvalidOperationException($"Ein Text ist länger als {max} Zeichen.");
        }
    }

    public static JobPriority? Priority(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        null or "" or "normal" => JobPriority.Normal,
        "niedrig" => JobPriority.Low,
        "hoch" => JobPriority.High,
        _ => null,
    };

    public static string Day(DateOnly day) => day.ToString("dd.MM.yyyy", CultureInfo.GetCultureInfo("de-DE"));

    public static string? Trim(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
