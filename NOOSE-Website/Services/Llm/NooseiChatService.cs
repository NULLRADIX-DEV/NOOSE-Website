using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Llm;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Llm;
using NOOSE_Website.Services.Llm.Tools;

namespace NOOSE_Website.Services;

/// <summary>A record NOOSEI read for an answer, as the chat renders it: type, id and the name it had then.</summary>
public sealed record NooseiSource(string Type, string Id, string Name);

/// <summary>One tool NOOSEI used for an answer, and how often it ran.</summary>
public sealed record NooseiToolUse(string Name, int Count)
{
    public string Label => NooseiToolLabels.Label(Name);
}

/// <summary>One entry of a conversation as the chat page renders it.</summary>
/// <param name="Text">Raw text as stored — the model answers in Markdown.</param>
/// <param name="Html">Sanitized HTML of <paramref name="Text"/>; null for the agent's own turns, which are
/// shown verbatim rather than interpreted as Markdown.</param>
/// <param name="Sources">Records the tools read for this answer; empty on the agent's own turns and on
/// every answer stored before answers carried sources.</param>
/// <param name="Tools">Tools this answer rests on, in call order. Read back from the stored tool rows, so a
/// reopened conversation shows the same list — which also means a call that produced nothing is absent, because
/// those rows are deliberately not kept.</param>
/// <param name="Truncated">The token ceiling cut this answer off. Stored, not a snackbar: it stays true when the
/// conversation is reopened, which is the only place the reader can still see what was missing.</param>
/// <param name="Degraded">Answered without record access.</param>
/// <param name="UnsupportedNote">Case numbers the answer cites that no tool result backs, already phrased.</param>
public sealed record NooseiChatMessage(
    string Id, bool FromUser, string Text, string? Html, DateTime CreatedAt, bool IsError, long? QuotaTokens,
    IReadOnlyList<NooseiSource> Sources, IReadOnlyList<NooseiToolUse> Tools,
    bool Truncated = false, bool Degraded = false, string? UnsupportedNote = null)
{
    /// <summary>The agent's own message on its way into the list, before it comes back from storage.</summary>
    public static NooseiChatMessage FromAgent(string text)
        => new(Guid.NewGuid().ToString(), true, text, null, DateTime.UtcNow, false, null, [], []);
}

/// <summary>A conversation in the owner's sidebar.</summary>
public sealed record NooseiConversationRow(string Id, string Title, DateTime LastMessageAt, int MessageCount);

/// <summary>Record a conversation was opened from, so follow-ups keep their subject.</summary>
public sealed record NooseiAnchor(string EntityType, string EntityId)
{
    /// <summary>Reads the <c>Typ:Id</c> form the chat page carries in its query string.</summary>
    /// <remarks>Only the shape is checked here. Whether the asker may see the record is decided later, against
    /// their live scope — a hand-typed query string is not evidence of anything.</remarks>
    public static NooseiAnchor? Parse(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }
        var cut = token.IndexOf(':');
        if (cut <= 0 || cut == token.Length - 1)
        {
            return null;
        }
        var type = Readable(token[..cut].Trim());
        var id = token[(cut + 1)..].Trim();
        return type is null || id.Length == 0 ? null : new NooseiAnchor(type, id);
    }

    /// <summary>An anchor must be a record NOOSEI can open, because the system line tells it to do exactly that.</summary>
    /// <remarks>Accepts the CLR name too, so a caller can pass <c>nameof(Person)</c> without translating. The
    /// capability gate is the point: a type outside it would reach <see cref="Visibility.IsRecordVisibleAsync" />,
    /// which treats an unknown type as visible to everyone — and a hand-typed query string is the one input that
    /// arrives here from outside.</remarks>
    private static string? Readable(string candidate)
        => NooseiRecordTypes.Clr(candidate, NooseiUse.Read)
            ?? NooseiRecordTypes.Clr(NooseiRecordTypes.German(candidate), NooseiUse.Read);

    public override string ToString() => EntityType + ":" + EntityId;
}

/// <summary>What one answered turn produced.</summary>
/// <param name="ScopeChanged">The owner's rights changed since the last turn, so the tool results of the
/// conversation were withheld from the replay. Worth saying: to the reader it looks like NOOSEI forgot what it
/// had just read.</param>
public sealed record NooseiTurn(
    string ConversationId, NooseiChatMessage Answer, LlmQuotaStatus Quota, long QuotaTokens, bool Degraded,
    bool ScopeChanged = false);

/// <summary>NOOSEI conversations: owner-private threads with real multi-turn history and record-database tools.</summary>
public interface INooseiChatService
{
    bool IsConfigured { get; }

    Task<IReadOnlyList<NooseiConversationRow>> GetConversationsAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NooseiChatMessage>> GetMessagesAsync(string conversationId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Asks a question; creates the conversation when no id is given.</summary>
    /// <param name="anchor">Record the chat was opened from. Only honoured on a new conversation, and only when
    /// the asker may see it.</param>
    Task<NooseiTurn> AskAsync(string? conversationId, string question, ClaimsPrincipal actor,
        IProgress<string>? progress = null, NooseiAnchor? anchor = null, CancellationToken cancellationToken = default);

    Task RenameAsync(string conversationId, string title, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task DeleteAsync(string conversationId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="INooseiChatService" />
public sealed class NooseiChatService(
    IDbContextFactory<AppDbContext> dbFactory,
    INooseiGateway noosei,
    INooseiSettingsService settings,
    NooseiToolRegistry tools,
    IOptions<LlmOptions> options,
    ILogger<NooseiChatService> logger) : INooseiChatService
{
    /// <summary>How many stored rows a replay reads. The token budget is the real limit; this only bounds how much
    /// longtext the query drags out of the database.</summary>
    private const int MaxReplayRows = 60;

    private readonly LlmOptions _o = options.Value;

    public bool IsConfigured => noosei.IsConfigured;

    public async Task<IReadOnlyList<NooseiConversationRow>> GetConversationsAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var agentId = OwnerId(actor);
        var mode = ModeOf(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.NooseiConversations.AsNoTracking()
            .Where(c => c.AgentId == agentId && c.Mode == mode)
            .OrderByDescending(c => c.LastMessageAt)
            .Take(50)
            .Select(c => new NooseiConversationRow(c.Id, c.Title, c.LastMessageAt, c.MessageCount))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<NooseiChatMessage>> GetMessagesAsync(string conversationId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var conversation = await LoadOwnAsync(db, conversationId, actor, cancellationToken);
        // tool rows come along: they are where the used-tools list under an answer comes from, and reading them
        // back beats a second copy of the same names on the assistant row
        var rows = await db.NooseiMessages.AsNoTracking()
            .Where(m => m.ConversationId == conversation.Id
                && (m.Role == "user" || m.Role == "assistant" || m.Role == "tool"))
            .OrderBy(m => m.Sequence)
            .ToListAsync(cancellationToken);

        var rendered = new List<NooseiChatMessage>(rows.Count);
        var pending = new List<string>();
        foreach (var row in rows)
        {
            if (row.Role == "tool")
            {
                if (row.ToolName is { Length: > 0 } name)
                {
                    pending.Add(name);
                }
                continue;
            }
            rendered.Add(Render(row, Collapse(pending)));
            pending.Clear();
        }
        return rendered;
    }

    /// <summary>Tool names in call order, repeats folded into a count.</summary>
    private static List<NooseiToolUse> Collapse(IReadOnlyList<string> names)
    {
        if (names.Count == 0)
        {
            return [];
        }
        var order = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            if (counts.TryGetValue(name, out var seen))
            {
                counts[name] = seen + 1;
                continue;
            }
            counts[name] = 1;
            order.Add(name);
        }
        return order.Select(n => new NooseiToolUse(n, counts[n])).ToList();
    }

    public async Task<NooseiTurn> AskAsync(
        string? conversationId, string question, ClaimsPrincipal actor,
        IProgress<string>? progress = null, NooseiAnchor? anchor = null, CancellationToken cancellationToken = default)
    {
        var mode = ModeOf(actor);
        var feature = mode == NooseiChatMode.Legal ? LlmFeature.LegalChat : LlmFeature.Chat;
        Permission.RequireLlmUse(actor, feature);
        var agentId = OwnerId(actor);
        var trimmed = PromptRedactor.Clip(question, PromptRedactor.MaxChatInputChars);
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException("Bitte eine Frage eingeben.");
        }

        var context = NooseiToolContext.From(actor);
        var stamp = ScopeStamp(context.Scope);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (mode == NooseiChatMode.Legal)
        {
            Permission.RequireInternalOrPartnerFeature(actor,
                await PartnerVisibility.FeaturesAsync(db, actor.GetPartnerAgency(), cancellationToken), PartnerFeature.Noosei);
        }
        // no anchor in law chat
        var conversation = conversationId is null
            ? Create(db, agentId, trimmed, stamp, mode,
                mode == NooseiChatMode.Legal ? null : await VisibleAnchorAsync(db, anchor, context.Scope, cancellationToken))
            : await LoadOwnAsync(db, conversationId, actor, cancellationToken);

        var history = new List<LlmMessage>();
        var scopeChanged = false;
        if (conversationId is not null)
        {
            (history, scopeChanged) = await ReplayAsync(db, conversation, stamp, cancellationToken);
        }

        var messages = new List<LlmMessage>(history.Count + 4)
        {
            LlmMessage.System(mode == NooseiChatMode.Legal
                ? NooseiPrompts.LegalChat
                : NooseiPrompts.Combine(NooseiPrompts.Chat, await AddendumAsync(cancellationToken))),
        };
        // re-checked every turn, not once at creation: the owner's rights may have been withdrawn since
        if (mode == NooseiChatMode.Agency && await AnchorLineAsync(db, conversation, context.Scope, cancellationToken) is { } line)
        {
            messages.Add(LlmMessage.System(line));
        }
        messages.AddRange(history);
        if (scopeChanged)
        {
            // without it the model fills the gap from the question-and-answer trail and cites evidence it lost
            messages.Add(LlmMessage.System(NooseiPrompts.ScopeChanged));
        }
        messages.Add(LlmMessage.User(trimmed));
        // the gateway appends its rounds to a copy of this list, so everything past here is this turn's own
        var sent = messages.Count;

        var answer = await noosei.AskAsync(
            new NooseiCall(
                feature,
                messages,
                LoggedPrompt: trimmed,
                Tools: tools.DefinitionsFor(mode),
                ToolExecutor: (call, ct) => RunToolAsync(call, context, mode, progress, ct),
                ConversationId: conversation.Id,
                EntityType: conversation.AnchorEntityType,
                EntityId: conversation.AnchorEntityId),
            actor,
            cancellationToken);

        if (!answer.Charge.Persisted)
        {
            // quota was spent but left no row: it is missing from the week, the log and the anomaly rules
            logger.LogWarning("NOOSEI-Antwort für {Agent} wurde nicht protokolliert ({Tokens} Kontingent-Token)",
                agentId, answer.Charge.QuotaTokens);
        }

        var text = string.IsNullOrWhiteSpace(answer.Text) ? "(Keine Antwort erhalten.)" : answer.Text!;
        var unsupported = NooseiCitations.Unsupported(text, NooseiCitations.Evidence(answer.Transcript, trimmed));
        if (unsupported.Count > 0)
        {
            // measurable rather than merely forbidden: the prompt asks for citations, this counts the ones it invents
            logger.LogInformation("NOOSEI-Antwort nennt {Count} unbelegte Aktenzeichen ({Numbers})",
                unsupported.Count, string.Join(", ", unsupported));
        }

        var turn = new TurnRecord(
            trimmed,
            text,
            answer.Transcript.Skip(sent).ToList(),
            answer.BarrenTools ?? [],
            answer.Charge.QuotaTokens,
            Sources(answer.Refs),
            answer.Truncated,
            answer.Degraded,
            NooseiCitations.Notice(unsupported));
        var stored = await AppendTurnAsync(db, conversation, turn, stamp, cancellationToken);

        return new NooseiTurn(conversation.Id, stored, answer.Charge.Status, answer.Charge.QuotaTokens,
            answer.Degraded, scopeChanged);
    }

    public async Task RenameAsync(string conversationId, string title, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException("Bitte einen Titel angeben.");
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var conversation = await LoadOwnAsync(db, conversationId, actor, cancellationToken);
        conversation.Title = Shorten(title, 200);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(string conversationId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var conversation = await LoadOwnAsync(db, conversationId, actor, cancellationToken);
        db.NooseiConversations.Remove(conversation);
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- tools ----

    private async Task<NooseiToolOutcome> RunToolAsync(LlmToolCall call, NooseiToolContext context, NooseiChatMode mode,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var tool = tools.Find(call.Name, mode);
        if (tool is null)
        {
            return NooseiToolOutcome.Failed($"Unbekanntes Werkzeug: {call.Name}.");
        }

        JsonElement arguments;
        try
        {
            arguments = JsonDocument.Parse(string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson).RootElement.Clone();
        }
        catch (JsonException)
        {
            return NooseiToolOutcome.Failed("Die Werkzeug-Parameter waren kein gültiges JSON. Bitte erneut versuchen.");
        }

        progress?.Report(NooseiToolLabels.Progress(call.Name));
        var result = await tool.InvokeAsync(arguments, context, cancellationToken);
        // reported after the call, because only the result knows which record was actually reached
        if (Touched(result.Refs) is { } record)
        {
            progress?.Report($"NOOSEI hat {record} gelesen.");
        }
        // a tool that answered "not found" answered; that belongs in the conversation, not in the barren list
        return new NooseiToolOutcome(result.Text, result.Refs);
    }

    /// <summary>Names the record a tool reached, when it reached exactly one — naming forty is noise.</summary>
    private static string? Touched(IReadOnlyList<LlmContextRef>? refs)
        => refs is { Count: 1 } && refs[0].Name is { Length: > 0 } name
            ? $"{NooseiRecordTypes.German(refs[0].Kind)} {name}"
            : null;

    // ---- storage ----

    private static NooseiConversation Create(AppDbContext db, string agentId, string question, string stamp,
        NooseiChatMode mode, NooseiAnchor? anchor)
    {
        var conversation = new NooseiConversation
        {
            AgentId = agentId,
            Title = Shorten(question, 80),
            LastMessageAt = DateTime.UtcNow,
            ScopeStamp = stamp,
            Mode = mode,
            AnchorEntityType = anchor?.EntityType,
            AnchorEntityId = anchor?.EntityId,
        };
        db.NooseiConversations.Add(conversation);
        return conversation;
    }

    /// <summary>Drops an anchor the asker may not see, so the conversation never stores one either.</summary>
    /// <remarks>Deliberately the same predicate the system line uses, not a weaker one: storing an anchor whose
    /// line would never be emitted only invites someone to "fix" the asymmetry later, in the wrong direction.</remarks>
    private static async Task<NooseiAnchor?> VisibleAnchorAsync(
        AppDbContext db, NooseiAnchor? anchor, ViewerScope scope, CancellationToken cancellationToken)
        => anchor is not null
            && await AnchorDisplayAsync(db, anchor.EntityType, anchor.EntityId, scope, cancellationToken) is not null
                ? anchor
                : null;

    /// <summary>The name of the anchored record, or null when this viewer may not have it.</summary>
    /// <remarks>
    /// Two gates, both required. <see cref="Visibility.IsRecordVisibleAsync"/> answers <c>true</c> for a type it
    /// does not know — Taskforce among them — so it cannot be the only one; <see cref="RecordsReference"/> simply
    /// omits a record whose membership or release the viewer lacks, which closes exactly that hole. Without both,
    /// a hand-typed <c>?akte=</c> would confirm a record by naming it back, which no tool would ever do.
    /// </remarks>
    private static async Task<string?> AnchorDisplayAsync(
        AppDbContext db, string type, string id, ViewerScope scope, CancellationToken cancellationToken)
    {
        if (!await Visibility.IsRecordVisibleAsync(db, type, id, scope, cancellationToken))
        {
            return null;
        }
        var resolved = await RecordsReference.ResolveAsync(db, [(type, id)], cancellationToken,
            scope.MayAllTaskforces, scope.MeId);
        return resolved.TryGetValue((type, id), out var hit) && !string.IsNullOrWhiteSpace(hit.Display)
            ? hit.Display
            : null;
    }

    /// <summary>The system line that tells NOOSEI which record the conversation is about.</summary>
    private static async Task<string?> AnchorLineAsync(
        AppDbContext db, NooseiConversation conversation, ViewerScope scope, CancellationToken cancellationToken)
    {
        if (conversation.AnchorEntityType is not { Length: > 0 } type
            || conversation.AnchorEntityId is not { Length: > 0 } id
            || await AnchorDisplayAsync(db, type, id, scope, cancellationToken) is not { } display)
        {
            return null;
        }

        return "Diese Unterhaltung wurde aus einer Akte heraus geöffnet und bezieht sich auf: "
            + $"{NooseiRecordTypes.German(type)} {display} (id={id}). "
            + "Beziehe unklare Fragen auf diese Akte und lies sie mit lies_akte, bevor du antwortest.";
    }

    private static async Task<NooseiConversation> LoadOwnAsync(
        AppDbContext db, string conversationId, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var conversation = await db.NooseiConversations
            .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken)
            ?? throw new InvalidOperationException("Unterhaltung nicht gefunden.");
        Permission.RequireOwnConversation(actor, conversation.AgentId);
        // never crosses chat modes
        if (conversation.Mode != ModeOf(actor))
        {
            throw new InvalidOperationException("Unterhaltung nicht gefunden.");
        }
        return conversation;
    }

    /// <summary>Partners talk to the law chat, everyone else to the agency chat.</summary>
    private static NooseiChatMode ModeOf(ClaimsPrincipal actor)
        => actor.IsPartner() ? NooseiChatMode.Legal : NooseiChatMode.Agency;

    /// <summary>Replays the recent history. Tool results are dropped when the owner's scope changed since:
    /// their text was authorised under rights that may since have been taken away.</summary>
    /// <returns>The wire history, and whether that rights change cost it its tool results.</returns>
    private async Task<(List<LlmMessage> Messages, bool ScopeChanged)> ReplayAsync(
        AppDbContext db, NooseiConversation conversation, string stamp, CancellationToken cancellationToken)
    {
        var sameScope = string.Equals(conversation.ScopeStamp, stamp, StringComparison.Ordinal);
        var rows = await db.NooseiMessages.AsNoTracking()
            .Where(m => m.ConversationId == conversation.Id)
            .OrderByDescending(m => m.Sequence)
            .Take(MaxReplayRows)
            .Select(m => new NooseiHistoryRow(
                m.Role, m.Content, m.ToolName, m.ToolCallId, m.ToolCallsJson, m.IsError))
            .ToListAsync(cancellationToken);
        rows.Reverse();

        var window = NooseiHistoryWindow.Build(rows, sameScope, _o.HistoryTokenBudget, _o.HistoryTurns);
        // only worth reporting when the change actually took evidence away
        var lost = !sameScope && rows.Any(r => r.Role is "tool" or NooseiHistoryWindow.ToolCallRole);
        return (window, lost);
    }

    /// <summary>What one answered turn writes into the conversation.</summary>
    /// <param name="NewMessages">Only the rounds this turn added — the replayed history must not be stored twice.</param>
    /// <param name="Barren">Tool call ids that produced no record content. Their rows are dropped, and with them
    /// every call the round made that has no answer left, so the stored turn stays a valid request shape.</param>
    private sealed record TurnRecord(
        string Question,
        string Answer,
        IReadOnlyList<LlmMessage> NewMessages,
        IReadOnlyList<string> Barren,
        long QuotaTokens,
        IReadOnlyList<NooseiSource> Sources,
        bool Truncated,
        bool Degraded,
        string? Unsupported);

    /// <summary>How many records are kept under one answer. A single filter call may touch forty; three of them
    /// would bury the answer under chips and blow up the stored row for no gain.</summary>
    private const int MaxSources = 24;

    /// <summary>Turns the refs of a turn into renderable sources: only linkable records, each once, name kept.</summary>
    /// <remarks>The per-tool entries the gateway adds carry no id and drop out here; they belong in the request
    /// log, not under an answer. A type without a real route drops out too — the person fallback in
    /// <see cref="SearchNavigation.Route"/> would send the reader into the wrong record.</remarks>
    private static List<NooseiSource> Sources(IReadOnlyList<LlmContextRef>? refs)
    {
        if (refs is not { Count: > 0 })
        {
            return [];
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var sources = new List<NooseiSource>();
        foreach (var reference in refs)
        {
            if (!SearchNavigation.Knows(reference.Kind))
            {
                continue;
            }
            if (reference.Id is not { Length: > 0 } id || reference.Name is not { Length: > 0 } name)
            {
                continue;
            }
            if (!seen.Add(reference.Kind + "|" + id))
            {
                continue;
            }
            sources.Add(new NooseiSource(reference.Kind, id, name));
            if (sources.Count == MaxSources)
            {
                break;
            }
        }
        return sources;
    }

    private static async Task<NooseiChatMessage> AppendTurnAsync(
        AppDbContext db, NooseiConversation conversation, TurnRecord turn, string stamp, CancellationToken cancellationToken)
    {
        var next = await db.NooseiMessages
            .Where(m => m.ConversationId == conversation.Id)
            .Select(m => (int?)m.Sequence)
            .MaxAsync(cancellationToken) ?? 0;

        var now = DateTime.UtcNow;
        db.NooseiMessages.Add(new NooseiMessage
        {
            ConversationId = conversation.Id,
            Sequence = ++next,
            Role = "user",
            Content = turn.Question,
            CreatedAt = now,
        });

        var kept = turn.NewMessages
            .Where(m => m.Role == LlmRole.Tool && m.ToolCallId is { Length: > 0 } id && !turn.Barren.Contains(id))
            .Select(m => m.ToolCallId!)
            .ToHashSet(StringComparer.Ordinal);

        // keep this turn's tool exchange, so a follow-up need not fetch it again — and so the model reads its own
        // earlier lookups as tool results with their arguments, not as statements it once made itself
        foreach (var message in turn.NewMessages)
        {
            switch (message.Role)
            {
                case LlmRole.Assistant when Kept(message.ToolCalls, kept) is { Count: > 0 } calls:
                    db.NooseiMessages.Add(new NooseiMessage
                    {
                        ConversationId = conversation.Id,
                        Sequence = ++next,
                        // its own role: the chat renders user|assistant, and this row has nothing to show
                        Role = NooseiHistoryWindow.ToolCallRole,
                        Content = message.Content,
                        ToolCallsJson = NooseiHistoryWindow.Serialize(calls),
                        CreatedAt = now,
                    });
                    break;
                case LlmRole.Tool when message.ToolCallId is { Length: > 0 } id && kept.Contains(id):
                    db.NooseiMessages.Add(new NooseiMessage
                    {
                        ConversationId = conversation.Id,
                        Sequence = ++next,
                        Role = "tool",
                        ToolName = message.Name,
                        ToolCallId = id,
                        Content = message.Content,
                        CreatedAt = now,
                    });
                    break;
            }
        }

        // built from the rows that were just written, not from the turn's refs: live and reopened must agree, and
        // only the stored rows know which calls survived the barren filter
        var used = Collapse(turn.NewMessages
            .Where(m => m.Role == LlmRole.Tool && m.ToolCallId is { Length: > 0 } id && kept.Contains(id))
            .Select(m => m.Name ?? string.Empty)
            .Where(n => n.Length > 0)
            .ToList());

        var stored = new NooseiMessage
        {
            ConversationId = conversation.Id,
            Sequence = ++next,
            Role = "assistant",
            Content = turn.Answer,
            QuotaTokens = turn.QuotaTokens,
            SourcesJson = turn.Sources.Count > 0 ? JsonSerializer.Serialize(turn.Sources) : null,
            Truncated = turn.Truncated,
            Degraded = turn.Degraded,
            UnsupportedCitations = turn.Unsupported is { Length: > 0 } note ? Shorten(note, 300) : null,
            CreatedAt = now,
        };
        db.NooseiMessages.Add(stored);

        conversation.LastMessageAt = now;
        conversation.MessageCount = next;
        conversation.ScopeStamp = stamp;
        await db.SaveChangesAsync(cancellationToken);

        return Render(stored, used);
    }

    /// <summary>The calls of a round that still have a stored answer; an unanswered one would make the whole stored
    /// turn an invalid request shape on the next replay.</summary>
    private static List<LlmToolCall>? Kept(IReadOnlyList<LlmToolCall>? calls, HashSet<string> kept)
        => calls?.Where(c => kept.Contains(c.Id)).ToList();

    // ---- helpers ----

    private async Task<string?> AddendumAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await settings.GetAddendumAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "NOOSEI-Zusatzhinweis konnte nicht gelesen werden.");
            return null;
        }
    }

    private static NooseiChatMessage Render(NooseiMessage row, IReadOnlyList<NooseiToolUse>? tools = null)
    {
        var fromUser = row.Role == "user";
        var text = row.Content ?? string.Empty;
        // the model answers in Markdown; MarkdownRenderer drops raw HTML and sanitizes what it produces
        var html = fromUser || string.IsNullOrWhiteSpace(text) ? null : MarkdownRenderer.ToSafeHtml(text);
        return new NooseiChatMessage(row.Id, fromUser, text, html, row.CreatedAt, row.IsError, row.QuotaTokens,
            ReadSources(row.SourcesJson), fromUser ? [] : tools ?? [],
            row.Truncated, row.Degraded, row.UnsupportedCitations);
    }

    /// <summary>Answers stored before sources existed, and a row corrupted by hand, both render without chips.</summary>
    private static IReadOnlyList<NooseiSource> ReadSources(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<NooseiSource>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string OwnerId(ClaimsPrincipal actor)
        => actor.GetAgentId() ?? throw new UnauthorizedAccessException("NOOSEI steht in dieser Rolle nicht zur Verfügung.");

    /// <summary>Fingerprint of everything that decides what a tool may return for this agent.</summary>
    private static string ScopeStamp(ViewerScope scope)
        => $"{scope.MayClassifiedRead}|{scope.MayAllTaskforces}|{scope.IsTru}|{scope.IsHrb}|{scope.PartnerAgency}";

    private static string Shorten(string text, int max)
    {
        var clean = text.Trim().ReplaceLineEndings(" ");
        return clean.Length <= max ? clean : clean[..max].TrimEnd() + "…";
    }
}
