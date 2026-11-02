using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Llm.Tools;
using static NOOSE_Website.Services.NooseiProposalRules;

namespace NOOSE_Website.Services;

/// <summary>Turns a proposal tool's arguments into a card. Reads only; executing is <see cref="INooseiProposalExecutor"/>.</summary>
public interface INooseiProposalService
{
    /// <summary>Checks a proposal against the asker's rights and turns it into a card; writes nothing.</summary>
    Task<NooseiToolResult> ProposeAsync(NooseiProposalDraft draft, NooseiToolContext context, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="INooseiProposalService" />
/// <remarks>Takes only the database: no write service is in reach of a tool.</remarks>
public sealed class NooseiProposalService(IDbContextFactory<AppDbContext> dbFactory) : INooseiProposalService
{
    public async Task<NooseiToolResult> ProposeAsync(NooseiProposalDraft draft, NooseiToolContext context,
        CancellationToken cancellationToken = default)
    {
        if (!context.Actor.MayWrite())
        {
            return Refuse("In deiner Rolle lässt sich nichts ändern, also auch nichts vorschlagen.");
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return draft.Kind switch
        {
            NooseiProposalKind.Followup => await FollowupAsync(db, draft, context, cancellationToken),
            NooseiProposalKind.Tag => await TagAsync(db, draft, context, cancellationToken),
            NooseiProposalKind.Watch => await WatchAsync(db, draft, context, cancellationToken),
            NooseiProposalKind.Link => await LinkAsync(db, draft, context, cancellationToken),
            NooseiProposalKind.Job => await JobAsync(db, draft, context, cancellationToken),
            _ => Refuse("Unbekannte Vorschlagsart."),
        };
    }

    private static async Task<NooseiToolResult> FollowupAsync(AppDbContext db, NooseiProposalDraft draft,
        NooseiToolContext context, CancellationToken cancellationToken)
    {
        if (await TargetAsync(db, draft.Type, draft.Id, FollowupTypes, context.Scope, cancellationToken) is not { } target)
        {
            return NooseiToolResult.NotFound();
        }
        if (Due(draft, out var dueError) is not { } due)
        {
            return Refuse(dueError ?? "Gib an, wann die Wiedervorlage fällig ist (in_tagen oder faellig_am).");
        }
        var note = Trim(draft.Text);
        if (note is { Length: > MaxNote })
        {
            return Refuse($"Die Notiz darf höchstens {MaxNote} Zeichen haben.");
        }
        var (agents, unknown) = await AgentsAsync(db, draft.Agents, cancellationToken);
        if (unknown.Count > 0)
        {
            return Refuse(UnknownAgents(unknown));
        }
        if (agents.Count > 1)
        {
            return Refuse("Eine Wiedervorlage hat genau einen Zuständigen.");
        }

        var lines = new List<NooseiProposalLine>
        {
            new("Akte", target.Label),
            new("Fällig am", Day(due)),
            new("Zuständig", agents.Count == 1 ? Codename(agents[0], context) : "du selbst"),
        };
        if (note is not null)
        {
            lines.Add(new("Notiz", note));
        }
        return Offer(NooseiProposalKind.Followup, "Wiedervorlage setzen", lines,
            new NooseiProposalArgs
            {
                EntityType = target.Type,
                EntityId = target.Id,
                Due = due,
                Text = note,
                AgentIds = agents.Select(a => a.Id).ToList(),
            },
            [target]);
    }

    private static async Task<NooseiToolResult> TagAsync(AppDbContext db, NooseiProposalDraft draft,
        NooseiToolContext context, CancellationToken cancellationToken)
    {
        if (await TargetAsync(db, draft.Type, draft.Id, TagTypes, context.Scope, cancellationToken) is not { } target)
        {
            return NooseiToolResult.NotFound();
        }
        var wanted = draft.Tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (wanted.Count == 0)
        {
            return Refuse("Nenne mindestens ein Stichwort.");
        }
        var catalog = await db.Tags.AsNoTracking().Select(t => new { t.Id, t.Name }).ToListAsync(cancellationToken);
        var byName = catalog
            .GroupBy(t => t.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var unknown = wanted.Where(n => !byName.ContainsKey(n)).ToList();
        if (unknown.Count > 0)
        {
            // a new tag stays a human decision
            var offer = string.Join(", ", catalog.Select(t => t.Name).OrderBy(n => n, StringComparer.CurrentCulture).Take(40));
            return Refuse($"Diese Stichwörter gibt es nicht: {string.Join(", ", unknown)}. Vorschlagen lassen sich nur "
                + $"vorhandene Stichwörter{(offer.Length > 0 ? ": " + offer : ".")}");
        }

        var present = (await db.TagMappings.AsNoTracking()
                .Where(m => m.EntityType == target.Type && m.EntityId == target.Id)
                .Select(m => m.TagId)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        var fresh = wanted.Select(n => byName[n]).Where(t => !present.Contains(t.Id)).ToList();
        if (fresh.Count == 0)
        {
            return new NooseiToolResult($"{target.Label} trägt diese Stichwörter bereits. Kein Vorschlag nötig.", [target.Ref]);
        }
        return Offer(NooseiProposalKind.Tag, fresh.Count == 1 ? "Stichwort vergeben" : "Stichwörter vergeben",
            [new("Akte", target.Label), new(fresh.Count == 1 ? "Stichwort" : "Stichwörter", string.Join(", ", fresh.Select(t => t.Name)))],
            new NooseiProposalArgs
            {
                EntityType = target.Type,
                EntityId = target.Id,
                TagIds = fresh.Select(t => t.Id).ToList(),
            },
            [target]);
    }

    private static async Task<NooseiToolResult> WatchAsync(AppDbContext db, NooseiProposalDraft draft,
        NooseiToolContext context, CancellationToken cancellationToken)
    {
        if (await TargetAsync(db, draft.Type, draft.Id, WatchTypes, context.Scope, cancellationToken) is not { } target)
        {
            return NooseiToolResult.NotFound();
        }
        var meId = context.Scope.MeId;
        if (await db.Watchlists.AnyAsync(w => w.AgentId == meId && w.EntityType == target.Type && w.EntityId == target.Id,
                cancellationToken))
        {
            return new NooseiToolResult($"Der Agent beobachtet {target.Label} bereits. Kein Vorschlag nötig.", [target.Ref]);
        }
        return Offer(NooseiProposalKind.Watch, "Akte beobachten", [new("Akte", target.Label)],
            new NooseiProposalArgs { EntityType = target.Type, EntityId = target.Id },
            [target]);
    }

    private static async Task<NooseiToolResult> LinkAsync(AppDbContext db, NooseiProposalDraft draft,
        NooseiToolContext context, CancellationToken cancellationToken)
    {
        var first = NooseiRecordTypes.Clr(draft.Type, NooseiUse.Read);
        var second = NooseiRecordTypes.Clr(draft.TargetType, NooseiUse.Read);
        if (first is null || second is null || draft.Id is not { } firstId || draft.TargetId is not { } secondId)
        {
            return NooseiToolResult.NotFound();
        }
        // the anchor lists links on its page
        var (anchorType, anchorId, targetType, targetId) =
            RecordBatch.LinkTargetsFor(first).Contains(second) ? (first, firstId, second, secondId)
            : RecordBatch.LinkTargetsFor(second).Contains(first) ? (second, secondId, first, firstId)
            : default;
        if (anchorType is null)
        {
            return Refuse($"{NooseiRecordTypes.Plural(first)} und {NooseiRecordTypes.Plural(second)} lassen sich "
                + "nicht miteinander verknüpfen.");
        }
        if (anchorType == targetType && anchorId == targetId)
        {
            return Refuse("Eine Akte lässt sich nicht mit sich selbst verknüpfen.");
        }
        var anchor = await ResolveAsync(db, anchorType, anchorId, LinkTypes, context.Scope, cancellationToken);
        var target = await ResolveAsync(db, targetType, targetId, LinkTypes, context.Scope, cancellationToken);
        if (anchor is null || target is null)
        {
            return NooseiToolResult.NotFound();
        }
        var label = Trim(draft.Text);
        if (label is { Length: > MaxLabel })
        {
            return Refuse($"Die Bezeichnung darf höchstens {MaxLabel} Zeichen haben.");
        }
        if (await LinkedAsync(db, anchor, target, cancellationToken))
        {
            return new NooseiToolResult($"{anchor.Label} und {target.Label} sind bereits verknüpft. Kein Vorschlag nötig.",
                [anchor.Ref, target.Ref]);
        }

        var lines = new List<NooseiProposalLine> { new("Akte", anchor.Label), new("Verknüpfen mit", target.Label) };
        if (label is not null)
        {
            lines.Add(new("Bezeichnung", label));
        }
        return Offer(NooseiProposalKind.Link, "Verknüpfung ziehen", lines,
            new NooseiProposalArgs
            {
                EntityType = anchor.Type,
                EntityId = anchor.Id,
                TargetType = target.Type,
                TargetId = target.Id,
                Text = label,
            },
            [anchor, target]);
    }

    private static async Task<NooseiToolResult> JobAsync(AppDbContext db, NooseiProposalDraft draft,
        NooseiToolContext context, CancellationToken cancellationToken)
    {
        var title = Trim(draft.Title);
        if (title is null)
        {
            return Refuse("Eine Aufgabe braucht einen Titel.");
        }
        if (title.Length > MaxTitle)
        {
            return Refuse($"Der Titel darf höchstens {MaxTitle} Zeichen haben.");
        }
        var description = Trim(draft.Text);
        if (description is { Length: > MaxDescription })
        {
            return Refuse($"Die Beschreibung darf höchstens {MaxDescription} Zeichen haben.");
        }
        DateOnly? due = null;
        if (draft.InDays is not null || draft.DueDate is not null)
        {
            if (Due(draft, out var dueError) is not { } parsed)
            {
                return Refuse(dueError!);
            }
            due = parsed;
        }
        if (Priority(draft.Priority) is not { } priority)
        {
            return Refuse("Die Priorität ist niedrig, normal oder hoch.");
        }
        var (agents, unknown) = await AgentsAsync(db, draft.Agents, cancellationToken);
        if (unknown.Count > 0)
        {
            return Refuse(UnknownAgents(unknown));
        }
        if (agents.Count == 0 && context.Scope.MeId is { } meId
            && await db.Users.OnlySelectable().Where(u => u.Id == meId).Select(u => u.Codename).FirstOrDefaultAsync(cancellationToken) is { } own)
        {
            // "leg mir eine Aufgabe an"
            agents = [new AgentPick(meId, own)];
        }

        Target? reference = null;
        if (draft.TargetType is not null || draft.TargetId is not null)
        {
            reference = await TargetAsync(db, draft.TargetType, draft.TargetId, JobReferenceTypes, context.Scope, cancellationToken);
            if (reference is null)
            {
                return NooseiToolResult.NotFound();
            }
        }
        // may echo classified text
        var restricted = draft.Restricted || reference is { Classified: true } || reference?.Type is nameof(Taskforce) or nameof(Job);

        var lines = new List<NooseiProposalLine> { new("Titel", title) };
        if (description is not null)
        {
            lines.Add(new("Beschreibung", description));
        }
        lines.Add(new("Fällig am", due is { } d ? Day(d) : "ohne Frist"));
        lines.Add(new("Priorität", JobPriorityDisplay.Name(priority)));
        lines.Add(new("Zuständig", agents.Count == 0 ? "niemand" : string.Join(", ", agents.Select(a => Codename(a, context)))));
        if (reference is not null)
        {
            lines.Add(new("Bezug", reference.Label));
        }
        lines.Add(new("Sichtbar für", restricted ? "nur Zuständige, Ersteller und Führung" : "alle Agenten"));
        return Offer(NooseiProposalKind.Job, "Aufgabe anlegen", lines,
            new NooseiProposalArgs
            {
                Title = title,
                Text = description,
                Due = due,
                Priority = priority,
                Restricted = restricted,
                AgentIds = agents.Select(a => a.Id).ToList(),
                TargetType = reference?.Type,
                TargetId = reference?.Id,
            },
            reference is null ? [] : [reference]);
    }

    /// <summary>The card plus the line the model reads, which says that nothing was written.</summary>
    private static NooseiToolResult Offer(NooseiProposalKind kind, string title, IReadOnlyList<NooseiProposalLine> lines,
        NooseiProposalArgs args, IReadOnlyList<Target> targets)
    {
        var proposal = new NooseiProposal(Guid.NewGuid().ToString(), kind, title, lines, args);
        var summary = string.Join("; ", lines.Select(l => $"{l.Label}: {l.Value}"));
        var text = $"Vorschlag vorbereitet, NICHT ausgeführt — {title} ({summary}). Unter deiner Antwort erscheint "
            + "dafür eine Karte; erst der Klick des Agenten auf „Ausführen\" schreibt. Behaupte nie, es sei schon erledigt.";
        return new NooseiToolResult(NooseiLimits.Clip(text), targets.Select(t => t.Ref).ToList(), Proposal: proposal);
    }

    private static NooseiToolResult Refuse(string message) => new(message, null, true);

    private static string UnknownAgents(IEnumerable<string> names)
        => $"Kein zuteilbarer Agent mit diesem Codenamen: {string.Join(", ", names)}. Nenne Agenten nur mit ihrem "
            + "genauen Codenamen.";

    private static string Codename(AgentPick agent, NooseiToolContext context)
        => agent.Id == context.Scope.MeId ? $"{agent.Codename} (du selbst)" : agent.Codename;
}
