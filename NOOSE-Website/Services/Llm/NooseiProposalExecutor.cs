using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Jobs;
using static NOOSE_Website.Services.NooseiProposalRules;

namespace NOOSE_Website.Services;

/// <summary>The agent's click on a proposal card. Never handed to a NOOSEI tool.</summary>
public interface INooseiProposalExecutor
{
    /// <summary>Writes an open proposal with the actor's own rights, checked again at the click.</summary>
    Task<NooseiProposal> ExecuteAsync(string messageId, string proposalId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Closes an open proposal without writing.</summary>
    Task<NooseiProposal> DismissAsync(string messageId, string proposalId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="INooseiProposalExecutor" />
/// <remarks>Writes use the batch paths where they exist: they carry the guard and allowlist the page would.</remarks>
public sealed class NooseiProposalExecutor(
    IDbContextFactory<AppDbContext> dbFactory,
    IFollowupService followups,
    ITagService tags,
    IWatchlistService watchlist,
    ILinkService links,
    IJobService jobs,
    ILogger<NooseiProposalExecutor> logger) : INooseiProposalExecutor
{
    private const string AlreadyDecided = "Dieser Vorschlag wurde bereits ausgeführt oder verworfen.";
    private const string Gone = "Vorschlag nicht gefunden.";
    private const string Unclear = "Beim Ausführen ist ein Fehler aufgetreten. Prüfe in der Akte, ob die Änderung "
        + "angekommen ist; der Vorschlag bleibt geschlossen.";
    private const string ReferenceMissing = "Aufgabe angelegt, der Bezug ließ sich aber nicht verknüpfen.";

    /// <summary>Retries of one card's swap while sibling cards change the same row.</summary>
    private const int MaxSwaps = 8;

    public async Task<NooseiProposal> ExecuteAsync(string messageId, string proposalId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        var proposal = await LoadOpenAsync(messageId, proposalId, actor, cancellationToken);
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            await CheckAsync(db, proposal, actor, cancellationToken);
        }

        // claim before write
        var claimedAt = DateTime.UtcNow;
        var claimed = await ChangeAsync(messageId, proposalId,
                p => p.IsOpen ? p with { State = NooseiProposalState.Executed, DecidedAt = claimedAt } : null,
                cancellationToken)
            ?? throw new InvalidOperationException(AlreadyDecided);

        (string? Href, string? Warning) result;
        try
        {
            result = await WriteAsync(claimed, actor, cancellationToken);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException)
        {
            // refused before writing
            await SettleAsync(messageId, proposalId, claimedAt, p => p with { State = NooseiProposalState.Open, DecidedAt = null });
            throw;
        }
        catch (Exception ex)
        {
            // may have written
            logger.LogError(ex, "FIBI-Vorschlag {Proposal} brach beim Schreiben ab.", proposalId);
            await SettleAsync(messageId, proposalId, claimedAt, p => p with { Warning = Unclear });
            throw new InvalidOperationException(Unclear);
        }

        var done = claimed with { ResultHref = result.Href, Warning = result.Warning };
        return await SettleAsync(messageId, proposalId, claimedAt, _ => done) ?? done;
    }

    public async Task<NooseiProposal> DismissAsync(string messageId, string proposalId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        // the swap skips the write barrier
        Permission.RequireWriteAccess(actor);
        await LoadOpenAsync(messageId, proposalId, actor, cancellationToken);
        return await ChangeAsync(messageId, proposalId,
                p => p.IsOpen ? p with { State = NooseiProposalState.Dismissed, DecidedAt = DateTime.UtcNow } : null,
                cancellationToken)
            ?? throw new InvalidOperationException(AlreadyDecided);
    }

    /// <summary>The owner's open proposal, read from storage; the circuit only names it.</summary>
    private async Task<NooseiProposal> LoadOpenAsync(string messageId, string proposalId, ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.NooseiMessages.AsNoTracking()
            .Where(m => m.Id == messageId && m.Role == "assistant")
            .Select(m => new { m.ProposalsJson, m.Conversation!.AgentId, m.Conversation.Mode })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException(Gone);
        Permission.RequireConversationOwner(actor, row.AgentId);
        if (row.Mode != NooseiChatMode.Agency)
        {
            throw new InvalidOperationException(Gone);
        }
        var proposal = NooseiProposalJson.Read(row.ProposalsJson).FirstOrDefault(p => p.Id == proposalId)
            ?? throw new InvalidOperationException(Gone);
        return proposal.IsOpen ? proposal : throw new InvalidOperationException(AlreadyDecided);
    }

    /// <summary>The proposal rules again, against the actor's rights of now.</summary>
    private static async Task CheckAsync(AppDbContext db, NooseiProposal proposal, ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var scope = ViewerScope.From(actor);
        var a = proposal.Args;
        switch (proposal.Kind)
        {
            case NooseiProposalKind.Followup:
                await RequireAsync(db, a.EntityType, a.EntityId, FollowupTypes, scope, cancellationToken);
                RequireDue(a.Due ?? throw new InvalidOperationException(Gone));
                RequireLength(a.Text, MaxNote);
                await RequireAgentsAsync(db, a.AgentIds, cancellationToken);
                break;
            case NooseiProposalKind.Tag:
                await RequireAsync(db, a.EntityType, a.EntityId, TagTypes, scope, cancellationToken);
                if (a.TagIds.Count == 0
                    || await db.Tags.CountAsync(t => a.TagIds.Contains(t.Id), cancellationToken) != a.TagIds.Count)
                {
                    throw new InvalidOperationException("Ein vorgeschlagenes Stichwort gibt es nicht mehr.");
                }
                break;
            case NooseiProposalKind.Watch:
                await RequireAsync(db, a.EntityType, a.EntityId, WatchTypes, scope, cancellationToken);
                break;
            case NooseiProposalKind.Link:
                await RequireAsync(db, a.EntityType, a.EntityId, LinkTypes, scope, cancellationToken);
                await RequireAsync(db, a.TargetType, a.TargetId, LinkTypes, scope, cancellationToken);
                RequireLength(a.Text, MaxLabel);
                break;
            case NooseiProposalKind.Job:
                if (string.IsNullOrWhiteSpace(a.Title))
                {
                    throw new InvalidOperationException("Eine Aufgabe braucht einen Titel.");
                }
                RequireLength(a.Title, MaxTitle);
                RequireLength(a.Text, MaxDescription);
                if (a.Due is { } due)
                {
                    RequireDue(due);
                }
                await RequireAgentsAsync(db, a.AgentIds, cancellationToken);
                if (a.TargetType is not null || a.TargetId is not null)
                {
                    await RequireAsync(db, a.TargetType, a.TargetId, JobReferenceTypes, scope, cancellationToken);
                }
                break;
            default:
                throw new InvalidOperationException(Gone);
        }
    }

    private async Task<(string? Href, string? Warning)> WriteAsync(NooseiProposal proposal, ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        var a = proposal.Args;
        switch (proposal.Kind)
        {
            case NooseiProposalKind.Followup:
                // a local day, like the dialog
                await followups.CreateAsync(a.EntityType!, a.EntityId!,
                    new FollowupInput(a.Due!.Value.ToDateTime(TimeOnly.MinValue), a.Text, a.AgentIds.FirstOrDefault()),
                    actor, cancellationToken);
                return (SearchNavigation.For(a.EntityType, a.EntityId!), null);
            case NooseiProposalKind.Tag:
                Done(await tags.AddManyAsync([(a.EntityType!, a.EntityId!)], a.TagIds, actor, cancellationToken));
                return (SearchNavigation.For(a.EntityType, a.EntityId!), null);
            case NooseiProposalKind.Watch:
                Done(await watchlist.FollowManyAsync([(a.EntityType!, a.EntityId!)], actor, cancellationToken));
                return (SearchNavigation.For(a.EntityType, a.EntityId!), null);
            case NooseiProposalKind.Link:
                Done(await links.CreateManyAsync(a.EntityType!, a.EntityId!, [(a.TargetType!, a.TargetId!)], a.Text,
                    actor, cancellationToken));
                return (SearchNavigation.For(a.EntityType, a.EntityId!), null);
            case NooseiProposalKind.Job:
                var job = await jobs.CreateAsync(new JobInput
                {
                    Title = a.Title!,
                    Description = a.Text,
                    Priority = a.Priority,
                    DueDate = a.Due?.ToDateTime(TimeOnly.MinValue),
                    IsRestricted = a.Restricted,
                }, a.AgentIds, actor, cancellationToken);
                return ($"/aufgaben/{job.Id}", await ReferenceAsync(job.Id, a, actor, cancellationToken));
            default:
                throw new InvalidOperationException(Gone);
        }
    }

    /// <summary>Links the job to its reference; the job stands either way, like in the job editor.</summary>
    private async Task<string?> ReferenceAsync(string jobId, NooseiProposalArgs a, ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        if (a.TargetType is not { } type || a.TargetId is not { } id)
        {
            return null;
        }
        try
        {
            var outcome = await links.CreateManyAsync(nameof(Job), jobId, [(type, id)], null, actor, cancellationToken);
            return outcome.Refused > 0 ? ReferenceMissing : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Bezug der FIBI-Aufgabe {Job} konnte nicht verknüpft werden.", jobId);
            return ReferenceMissing;
        }
    }

    /// <summary>A refused record means the rights changed since the proposal.</summary>
    private static void Done(BatchOutcome outcome)
    {
        if (outcome.Refused > 0)
        {
            throw new UnauthorizedAccessException("Das ist mit deinen Rechten nicht (mehr) möglich.");
        }
    }

    /// <summary>Applies a change to this execution's claim; logged, never thrown over the outcome.</summary>
    private async Task<NooseiProposal?> SettleAsync(string messageId, string proposalId, DateTime claimedAt,
        Func<NooseiProposal, NooseiProposal> change)
    {
        try
        {
            return await ChangeAsync(messageId, proposalId,
                p => p.State == NooseiProposalState.Executed && p.DecidedAt == claimedAt ? change(p) : null,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Stand des FIBI-Vorschlags {Proposal} ließ sich nicht speichern.", proposalId);
            return null;
        }
    }

    /// <summary>Changes one card of the row, re-reading on each try so sibling cards are kept.</summary>
    /// <returns>The changed card, or null when <paramref name="change"/> declined it.</returns>
    private async Task<NooseiProposal?> ChangeAsync(string messageId, string proposalId,
        Func<NooseiProposal, NooseiProposal?> change, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < MaxSwaps; attempt++)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var stored = await db.NooseiMessages.AsNoTracking()
                .Where(m => m.Id == messageId)
                .Select(m => m.ProposalsJson)
                .FirstOrDefaultAsync(cancellationToken);
            var proposals = NooseiProposalJson.Read(stored);
            var index = proposals.ToList().FindIndex(p => p.Id == proposalId);
            if (stored is null || index < 0 || change(proposals[index]) is not { } changed)
            {
                return null;
            }
            var json = NooseiProposalJson.Serialize(proposals.Select((p, i) => i == index ? changed : p).ToList())!;
            if (await db.NooseiMessages
                    .Where(m => m.Id == messageId && m.ProposalsJson == stored)
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.ProposalsJson, json), cancellationToken) == 1)
            {
                return changed;
            }
        }
        throw new InvalidOperationException("Der Vorschlag wird gerade geändert. Bitte noch einmal versuchen.");
    }
}
