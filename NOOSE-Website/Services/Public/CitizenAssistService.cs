using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Llm;
using NOOSE_Website.Models.Public;
using NOOSE_Website.Services.Llm;

namespace NOOSE_Website.Services.Public;

/// <summary>NOOSEI drafts what the agency tells a citizen; the agent edits it and sends it themselves.</summary>
public interface ICitizenAssistService
{
    bool IsAvailable { get; }

    /// <summary>Draft for the citizen thread of one ticket, from the conversation so far.</summary>
    Task<CitizenDraftSuggestion> SuggestTicketReplyAsync(string ticketId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    /// <summary>Draft for the next message to a tipster, from the tip and the conversation with them.</summary>
    Task<CitizenDraftSuggestion> SuggestTipReplyAsync(string tipId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    /// <summary>Draft of the reasoning behind a decision on an objection, worded from the agent's own notes.</summary>
    Task<CitizenDraftSuggestion> SuggestObjectionNoteAsync(string objectionId, ObjectionStatus outcome,
        string? notes, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ICitizenAssistService" />
/// <remarks>
/// One way to the model, like every other feature: <c>INooseiGateway.AskAsync</c> checks the right and the weekly
/// quota, books once and logs the call. Nothing here is sent — the draft lands in a field, and the existing write
/// paths (<c>ReplyToCitizenAsync</c>, <c>AskCitizenAsync</c>, <c>SetStatusAsync</c>) stay the only way out.
/// <para>
/// Each draft runs behind the guard of the path that would send it, so nobody pays quota for a text they could not
/// send. The answer is scrubbed of mention tokens before it reaches the field: the citizen-facing paths refuse them
/// at the service, so a model that emitted one would hand the agent a message that cannot be sent.
/// </para>
/// </remarks>
public sealed class CitizenAssistService(
    IDbContextFactory<AppDbContext> dbFactory,
    INooseiGateway noosei,
    INooseiSettingsService settings) : ICitizenAssistService
{
    /// <summary>Lines of the conversation handed to the model; older ones are dropped, not summarised.</summary>
    private const int MaxLines = 20;

    private const int MaxCharsPerLine = 1_500;

    /// <summary>The submission itself, a tip or an objection; longer than one line, it is the core of the matter.</summary>
    private const int MaxSubmissionChars = 3_000;

    public bool IsAvailable => noosei.IsConfigured;

    public async Task<CitizenDraftSuggestion> SuggestTicketReplyAsync(string ticketId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        // the desk answers the citizen, so the desk drafts the answer; write guard runs inside it, before the rank
        Permission.RequireTicketHandling(actor);
        Permission.RequireLlmUse(actor);
        EnsureConfigured();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var ticket = await db.Tickets.AsNoTracking()
            .Where(t => t.Id == ticketId)
            .Select(t => new { t.Id, t.CaseNumber, t.Subject, t.Kind, t.Category })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Ticket nicht gefunden.");
        if (ticket.Kind == TicketArt.Intern)
        {
            throw new InvalidOperationException("Ein internes Ticket hat keinen Bürger-Schriftwechsel.");
        }
        if (!await TicketVisibility.MayReadAsync(db, ticketId, actor, cancellationToken))
        {
            throw new UnauthorizedAccessException("Du bist an diesem Ticket nicht beteiligt.");
        }

        var lines = await db.TicketNachrichten.AsNoTracking()
            .Where(m => m.TicketId == ticketId && m.Audience == TicketMessageAudience.Buerger)
            // the id breaks the tie: the opening message and the automatic confirmation share one SaveChanges'
            // timestamp, and without it the two could swap places between calls
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Take(MaxLines)
            .Select(m => new ThreadLine(m.Text, m.AuthorIsCitizen, m.CreatedAt, m.Id))
            .ToListAsync(cancellationToken);
        if (lines.Count == 0)
        {
            throw new InvalidOperationException("In diesem Ticket steht noch keine Nachricht.");
        }

        var prompt = new StringBuilder();
        prompt.AppendLine($"Betreff: {ticket.Subject}");
        prompt.AppendLine($"Kategorie: {TicketKategorieDisplay.Name(ticket.Category)}");
        prompt.AppendLine();
        AppendThread(prompt, lines, "Bürger:");
        prompt.AppendLine("Entwirf die nächste Antwort der Behörde.");

        return await DraftAsync(NooseiPrompts.TicketReply, prompt.ToString(), nameof(Ticket), ticket.Id,
            TicketRules.MaxMessageLength, actor, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The projection reads no citizen field at all — no name, no profile, no trust tier. The anonymity promise then
    /// holds by the shape of the query, not by a branch someone could forget; same idea as the search provider.
    /// </remarks>
    public async Task<CitizenDraftSuggestion> SuggestTipReplyAsync(string tipId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        // the guard of AskCitizenAsync: whoever may write to the tipster may have it drafted
        Permission.RequireTipHandling(actor);
        Permission.RequireLlmUse(actor);
        EnsureConfigured();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var tip = await db.Hinweise.AsNoTracking()
            .Where(h => h.Id == tipId)
            .Select(h => new
            {
                h.Id,
                h.Kind,
                h.Status,
                h.Text,
                h.Handover,
                h.HandoverLocation,
                WantedName = h.Wanted!.DisplayName,
                WantedCaseNumber = h.Wanted.CaseNumber,
            })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Hinweis nicht gefunden.");
        // same refusal as sending; a draft for a closed thread would cost quota and go nowhere
        if (TipRules.IsClosed(tip.Status))
        {
            throw new InvalidOperationException("Dieser Hinweis ist abgeschlossen.");
        }

        var lines = await db.HinweisNachrichten.AsNoTracking()
            .Where(m => m.HinweisId == tipId && m.Audience == TipMessageAudience.Buerger)
            // the tip and its automatic confirmation share one SaveChanges' timestamp, same as the ticket
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Take(MaxLines)
            .Select(m => new ThreadLine(m.Text, m.AuthorIsCitizen, m.CreatedAt, m.Id))
            .ToListAsync(cancellationToken);

        var prompt = new StringBuilder();
        prompt.AppendLine($"Art: {TipKindDisplay.Name(tip.Kind)}");
        prompt.AppendLine(tip.WantedName is { Length: > 0 } name
            ? $"Bezug: öffentliche Fahndung „{name}“ ({tip.WantedCaseNumber})"
            : "Bezug: keine bestimmte Fahndung");
        prompt.AppendLine($"Stand: {TipStatusDisplay.Name(tip.Status)}");
        if (tip.Kind == TipKind.Ergreifung && tip.Handover is { } handover)
        {
            prompt.AppendLine($"Übergabe: {TipHandoverDisplay.Name(handover)}");
            if (!string.IsNullOrWhiteSpace(tip.HandoverLocation))
            {
                prompt.AppendLine($"Ort laut Meldung: {PromptRedactor.Clip(tip.HandoverLocation, MaxCharsPerLine)}");
            }
        }
        prompt.AppendLine();
        // the submission is the tip's own column, not a thread row
        prompt.AppendLine("Hinweis des Hinweisgebers:");
        prompt.AppendLine(PromptRedactor.Clip(tip.Text, MaxSubmissionChars));
        prompt.AppendLine();
        if (lines.Count > 0)
        {
            AppendThread(prompt, lines, "Hinweisgeber:");
        }
        prompt.AppendLine("Entwirf die nächste Nachricht der Behörde an den Hinweisgeber.");

        return await DraftAsync(NooseiPrompts.TipReply, prompt.ToString(), nameof(Hinweis), tip.Id,
            TipRules.MaxMessageLength, actor, cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The model only knows the objection, never why the agency decided as it did. The reasons therefore come from
    /// the agent's notes; without notes the draft marks the gap instead of inventing grounds the citizen would read
    /// as the official ones.
    /// </remarks>
    public async Task<CitizenDraftSuggestion> SuggestObjectionNoteAsync(string objectionId, ObjectionStatus outcome,
        string? notes, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        // the guard of SetStatusAsync: only who may decide drafts the decision
        Permission.RequireObjectionHandling(actor);
        Permission.RequireLlmUse(actor);
        if (outcome is not (ObjectionStatus.Angenommen or ObjectionStatus.Abgelehnt))
        {
            throw new InvalidOperationException("Ein Entwurf gehört zu einer Entscheidung: stattgeben oder ablehnen.");
        }
        EnsureConfigured();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var objection = await db.FahndungEinsprueche.AsNoTracking()
            .Where(e => e.Id == objectionId)
            .Select(e => new
            {
                e.Id,
                e.Status,
                e.Text,
                WantedName = e.Wanted!.DisplayName,
                WantedCaseNumber = e.Wanted.CaseNumber,
                WantedStatus = e.Wanted.Status,
            })
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("Einspruch nicht gefunden.");
        // checked before the model runs: a decision the desk cannot take is no draft worth paying for
        if (!ObjectionRules.IsTransitionAllowed(objection.Status, outcome))
        {
            throw new InvalidOperationException("Diese Entscheidung ist von hier aus nicht vorgesehen.");
        }
        // same precondition SetStatusAsync enforces; the draft would announce a withdrawal that has not happened
        if (outcome == ObjectionStatus.Angenommen && PublicWantedService.PubliclyVisible.Contains(objection.WantedStatus))
        {
            throw new InvalidOperationException(
                "Die Ausschreibung ist noch öffentlich. Zieh sie zuerst zurück, dann lässt sich dem Einspruch stattgeben.");
        }

        var prompt = new StringBuilder();
        prompt.AppendLine($"Ausschreibung: „{objection.WantedName}“ ({objection.WantedCaseNumber}), "
            + $"Stand: {PublicWantedStatusDisplay.Name(objection.WantedStatus)}");
        prompt.AppendLine($"Entscheidung: {(outcome == ObjectionStatus.Angenommen
            ? "Dem Einspruch wird stattgegeben."
            : "Der Einspruch wird abgelehnt.")}");
        prompt.AppendLine();
        prompt.AppendLine("Einspruch des Bürgers:");
        prompt.AppendLine(PromptRedactor.Clip(objection.Text, MaxSubmissionChars));
        prompt.AppendLine();
        var points = PromptRedactor.Clip(MentionParser.Strip(notes), ObjectionRules.MaxNoteLength);
        if (points.Length > 0)
        {
            prompt.AppendLine("Stichpunkte des Agenten zur Begründung:");
            prompt.AppendLine(points);
        }
        else
        {
            prompt.AppendLine("Der Agent hat keine Stichpunkte zur Begründung angegeben.");
        }
        prompt.AppendLine();
        prompt.AppendLine("Entwirf die Begründung, die der Bürger mit der Entscheidung liest.");

        return await DraftAsync(NooseiPrompts.ObjectionDecision, prompt.ToString(), nameof(FahndungEinspruch),
            objection.Id, ObjectionRules.MaxNoteLength, actor, cancellationToken);
    }

    private sealed record ThreadLine(string Text, bool FromCitizen, DateTime CreatedAt, string Id);

    private void EnsureConfigured()
    {
        if (!noosei.IsConfigured)
        {
            throw new InvalidOperationException("NOOSEI ist nicht konfiguriert.");
        }
    }

    private static void AppendThread(StringBuilder prompt, IEnumerable<ThreadLine> lines, string citizenLabel)
    {
        prompt.AppendLine("Schriftwechsel, älteste Nachricht zuerst:");
        foreach (var line in lines.OrderBy(l => l.CreatedAt).ThenBy(l => l.Id))
        {
            // the sender's role only; the agency rows carry no agent, so there is nothing else to say
            prompt.AppendLine(line.FromCitizen ? citizenLabel : "Behörde:");
            prompt.AppendLine(PromptRedactor.Clip(line.Text, MaxCharsPerLine));
            prompt.AppendLine();
        }
    }

    private async Task<CitizenDraftSuggestion> DraftAsync(string systemPrompt, string body, string entityType,
        string entityId, int maxLength, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var answer = await noosei.AskAsync(
            new NooseiCall(
                // no feature of its own: this is composing, and a new value would need its own token rules
                LlmFeature.Compose,
                [
                    LlmMessage.System(NooseiPrompts.Combine(systemPrompt, await AddendumAsync(cancellationToken))),
                    LlmMessage.User(body),
                ],
                LoggedPrompt: body,
                Temperature: 0.3,
                MaxTokens: 1_200,
                EntityType: entityType,
                EntityId: entityId),
            actor,
            cancellationToken);

        var draft = Scrub(answer.Text, maxLength);
        if (string.IsNullOrWhiteSpace(draft))
        {
            throw new InvalidOperationException("NOOSEI hat keinen verwertbaren Entwurf geliefert.");
        }
        return new CitizenDraftSuggestion(draft, answer.Charge.QuotaTokens, answer.Charge.Status);
    }

    /// <summary>Strips what the citizen-facing paths would refuse and clips to what they accept.</summary>
    private static string Scrub(string? text, int maxLength)
    {
        var plain = MentionParser.Strip(text).Trim();
        return plain.Length > maxLength ? plain[..maxLength].TrimEnd() : plain;
    }

    private async Task<string?> AddendumAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await settings.GetAddendumAsync(cancellationToken);
        }
        catch (Exception)
        {
            // a missing house rule must never block a draft
            return null;
        }
    }
}
