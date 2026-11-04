using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Llm;

namespace NOOSE_Website.Services;

/// <summary>What the NOOSEI brief panel renders: the cached brief plus whether the source changed since.</summary>
public sealed record DossierSummaryView(
    bool Configured,
    bool Exists,
    DossierBrief? Brief,
    DateTime? GeneratedAt,
    bool IsStale);

/// <summary>Cached structured NOOSEI briefs per record. The model is called only when the source content changed
/// or a regenerate is forced — an unchanged content hash reuses the stored brief.</summary>
public interface IDossierSummaryService
{
    bool IsConfigured { get; }

    /// <summary>Cached brief for a record; never calls the model. Null when the viewer may not see the record.</summary>
    Task<DossierSummaryView?> GetAsync(string entityType, string entityId, ClaimsPrincipal viewer, CancellationToken cancellationToken = default);

    /// <summary>Generate the brief, reusing the cache when the content hash is unchanged (unless force=true).</summary>
    Task<DossierSummaryView> GenerateAsync(string entityType, string entityId, ClaimsPrincipal actor, bool force, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IDossierSummaryService" />
public sealed class DossierSummaryService(
    IDbContextFactory<AppDbContext> dbFactory,
    INooseiGateway noosei,
    INooseiProviderService providerService,
    IMemoryCache cache,
    IOptions<LlmOptions> options) : IDossierSummaryService
{
    private readonly LlmOptions _o = options.Value;

    public bool IsConfigured => _o.IsConfigured;

    public async Task<DossierSummaryView?> GetAsync(string entityType, string entityId, ClaimsPrincipal viewer, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var scope = ViewerScope.From(viewer);
        if (!await Visibility.IsRecordVisibleAsync(db, entityType, entityId, scope, cancellationToken))
        {
            return null;
        }

        var row = await db.DossierSummaries.AsNoTracking()
            .FirstOrDefaultAsync(x => x.EntityType == entityType && x.EntityId == entityId, cancellationToken);
        if (row is null)
        {
            return new DossierSummaryView(IsConfigured, false, null, null, false);
        }

        // Staleness: rebuild the context hash (DB reads only, no model call) and compare.
        // Null scope on purpose: the cached brief is generated at minimum privilege, so the hash must be too.
        var context = await DossierContextBuilder.BuildAsync(db, entityType, entityId, null, cancellationToken);
        var stale = context is null || Hash(PromptRedactor.Clip(context.Value.Text)) != row.ContentHash;
        return View(row, stale);
    }

    public async Task<DossierSummaryView> GenerateAsync(string entityType, string entityId, ClaimsPrincipal actor, bool force, CancellationToken cancellationToken = default)
    {
        Permission.RequireLlmUse(actor);
        Permission.RequireWriteAccess(actor);
        if (!_o.IsConfigured)
        {
            throw new InvalidOperationException("FIBI ist nicht konfiguriert.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var scope = ViewerScope.From(actor);
        if (!await Visibility.IsRecordVisibleAsync(db, entityType, entityId, scope, cancellationToken))
        {
            throw new UnauthorizedAccessException("Diese Akte ist für dich nicht sichtbar.");
        }

        // one cached row per record, so it is assembled at the record's own audience, never at the actor's
        var context = await DossierContextBuilder.BuildAsync(db, entityType, entityId, null, cancellationToken)
            ?? throw new InvalidOperationException("Akte nicht gefunden.");

        // Last gate before egress: the deployment-wide kill switch.
        PromptRedactor.GuardClassified(context.IsClassified, _o);

        var userPrompt = PromptRedactor.Clip(context.Text);
        var hash = Hash(userPrompt);

        var existing = await db.DossierSummaries
            .FirstOrDefaultAsync(x => x.EntityType == entityType && x.EntityId == entityId, cancellationToken);

        // Unchanged source and no force → reuse, no model call.
        if (!force && existing is not null && existing.ContentHash == hash && existing.BriefJson is not null)
        {
            return View(existing, stale: false);
        }

        var (brief, briefModel) = await RequestBriefAsync(entityType, entityId, context.Title, userPrompt, actor, cancellationToken);
        if (brief is null)
        {
            throw new InvalidOperationException(
                "FIBI konnte keinen strukturierten Kurzbrief erzeugen. Bitte später erneut versuchen.");
        }

        if (existing is null)
        {
            existing = new DossierSummary { EntityType = entityType, EntityId = entityId };
            db.DossierSummaries.Add(existing);
        }
        existing.ContentHash = hash;
        existing.BriefJson = JsonSerializer.Serialize(brief, DossierBrief.Json);
        existing.SchemaVersion = NooseiSchemas.KurzbriefVersion;
        existing.PromptVersion = NooseiPrompts.BriefPromptVersion;
        // the upstream the brief actually ran on; a model id only identifies a model together with its endpoint
        // from the answer, not a second lookup: resolving the upstream again can straddle the ten-second
        // cache and stamp this row with a model the request never ran on
        existing.Model = briefModel ?? _o.ModelFor((await providerService.GetStateAsync(cancellationToken)).Active, LlmFeature.Brief);
        existing.GeneratedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return View(existing, stale: false);
    }

    // ---- structured-output ladder ----

    /// <summary>Asks for the brief, widening the request until the endpoint can serve the shape. Null = every rung failed.</summary>
    /// <remarks>
    /// Every rung is a paid call, so the ladder is climbed as narrowly as possible: a rung that only widens the
    /// provider pool answers a capability failure, never a malformed answer — a provider that accepted the schema
    /// and then wrote nonsense will do it again on the next provider, and the agent pays twice for nothing.
    /// </remarks>
    private async Task<(DossierBrief? Brief, string? Model)> RequestBriefAsync(
        string entityType, string entityId, string title, string userPrompt, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var provider = (await providerService.GetStateAsync(cancellationToken)).Active;
        var rungs = Rungs(provider).ToList();
        var memoryKey = LadderKey(provider, rungs.Count);
        // a refused rung is refused again: start where this upstream last answered
        var start = cache.TryGetValue(memoryKey, out int remembered) && remembered < rungs.Count ? remembered : 0;
        for (var i = start; i < rungs.Count; i++)
        {
            var (prompt, format, requireCapable, _) = rungs[i];
            try
            {
                var answer = await noosei.AskAsync(
                    new NooseiCall(
                        LlmFeature.Brief,
                        [LlmMessage.System(prompt), LlmMessage.User(userPrompt)],
                        LoggedPrompt: $"Kurzbrief für {title}",
                        ResponseFormat: format,
                        EntityType: entityType,
                        EntityId: entityId,
                        ContextRefs: [new LlmContextRef(entityType, entityId, title)],
                        RequireCapableProviders: requireCapable),
                    actor,
                    cancellationToken);

                if (Parse(answer.Text) is { } brief)
                {
                    cache.Set(memoryKey, i, LadderMemory);
                    // the model comes back with the answer: the rung that succeeded decides it, and resolving
                    // the upstream a second time afterwards can name a different one
                    return (brief, answer.Model);
                }
                // the shape was served, the content was not: skip every rung that only rerolls the provider
                while (i + 1 < rungs.Count && rungs[i + 1].WidensProvidersOnly)
                {
                    i++;
                }
            }
            // the endpoint cannot serve this rung at all — try the next, but never past the last one
            catch (LlmCapabilityException) when (i < rungs.Count - 1)
            {
            }
        }
        return (null, null);
    }

    /// <summary>How long the rung an upstream answered on is trusted before the schema is offered again.</summary>
    internal static readonly TimeSpan LadderMemory = TimeSpan.FromHours(12);

    // the rung list depends on these, so a config change never reuses a stale index
    private string LadderKey(LlmProvider provider, int rungCount)
        => $"kurzbrief-stufe:{provider}:{_o.ModelFor(provider, LlmFeature.Brief)}:{_o.StructuredOutput}:{rungCount}";

    private IEnumerable<(string Prompt, LlmResponseFormat Format, bool RequireCapable, bool WidensProvidersOnly)> Rungs(LlmProvider provider)
    {
        var strict = LlmResponseFormat.ForSchema(NooseiSchemas.KurzbriefName, NooseiSchemas.Kurzbrief);
        var jsonMode = NooseiPrompts.WithSchema(NooseiPrompts.Brief, NooseiSchemas.KurzbriefText);

        if (_o.StructuredOutput == StructuredOutputMode.PromptOnly)
        {
            yield return (jsonMode, LlmResponseFormat.JsonObject, false, false);
            yield break;
        }

        // rung 0: enforced schema, only capable providers
        yield return (NooseiPrompts.Brief, strict,
            _o.RequireCapableProviders && _o.StructuredOutput == StructuredOutputMode.Strict, false);

        // rung 1: same schema, wider provider pool — many honour a schema without advertising it.
        // Only worth paying for after a capability rejection, hence the flag. A direct endpoint has no pool to
        // widen: there the rung is the refused request a second time.
        if (provider == LlmProvider.OpenRouter
            && _o.RequireCapableProviders && _o.StructuredOutput == StructuredOutputMode.Strict)
        {
            yield return (NooseiPrompts.Brief, strict, false, true);
        }

        // rung 2: plain JSON mode with the schema pasted into the prompt
        yield return (jsonMode, LlmResponseFormat.JsonObject, false, false);
    }

    /// <summary>Parses the answer, repairing the two things models get wrong: code fences and surrounding chatter.</summary>
    public static DossierBrief? Parse(string? answer)
    {
        var json = Extract(answer);
        if (json is null)
        {
            return null;
        }
        try
        {
            var brief = JsonSerializer.Deserialize<DossierBrief>(json, DossierBrief.Json);
            return brief is null || brief.IsEmpty ? null : Normalise(brief);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DossierBrief Normalise(DossierBrief brief) => brief with
    {
        Tldr = brief.Tldr?.Trim() ?? string.Empty,
        Kernpunkte = Clean(brief.Kernpunkte),
        EinstufungBewertung = brief.EinstufungBewertung?.Trim() ?? string.Empty,
        Verbindungen = (brief.Verbindungen ?? []).Where(v => !string.IsNullOrWhiteSpace(v.Wer)).ToList(),
        Verlauf = (brief.Verlauf ?? []).Where(v => !string.IsNullOrWhiteSpace(v.Was)).ToList(),
        OffenePunkte = Clean(brief.OffenePunkte),
        Risiko = brief.Risiko ?? new BriefRisk("mittel", null),
    };

    private static IReadOnlyList<string> Clean(IReadOnlyList<string>? items)
        => (items ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();

    /// <summary>Strips code fences and any chatter around the object, then returns the outermost balanced braces.</summary>
    private static string? Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var trimmed = FenceRegex.Replace(text.Trim(), string.Empty).Trim();

        var start = trimmed.IndexOf('{');
        if (start < 0)
        {
            return null;
        }
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (c == '\\' && inString)
            {
                escaped = true;
                continue;
            }
            if (c == '"')
            {
                inString = !inString;
                continue;
            }
            if (inString)
            {
                continue;
            }
            if (c == '{')
            {
                depth++;
            }
            else if (c == '}' && --depth == 0)
            {
                return trimmed[start..(i + 1)];
            }
        }
        return null;
    }

    private static readonly Regex FenceRegex = new(@"^```[a-zA-Z]*\s*|\s*```$", RegexOptions.Multiline | RegexOptions.Compiled);

    // ---- helpers ----

    private DossierSummaryView View(DossierSummary row, bool stale)
    {
        DossierBrief? brief = null;
        if (!string.IsNullOrWhiteSpace(row.BriefJson))
        {
            try
            {
                brief = JsonSerializer.Deserialize<DossierBrief>(row.BriefJson, DossierBrief.Json);
            }
            catch (JsonException) { /* a stored brief from an older shape simply renders as missing */ }
        }
        // an unreadable payload counts as stale, so the panel offers a regenerate instead of an empty box
        return new DossierSummaryView(IsConfigured, brief is not null, brief, row.GeneratedAt, stale || brief is null);
    }

    // the score-recalculation timestamp advances on every daily sweep even when nothing changed;
    // excluding it keeps a brief from being falsely marked stale after each nightly recompute
    private static readonly Regex VolatileHashLines = new(@"(?im)^Score berechnet am:.*$\r?\n?", RegexOptions.Compiled);

    /// <summary>Content hash incl. prompt and schema version, so a prompt bump invalidates every stored brief.</summary>
    private static string Hash(string text)
    {
        var seed = $"v{NooseiPrompts.BriefPromptVersion}/{NooseiSchemas.KurzbriefVersion}\n"
            + VolatileHashLines.Replace(text, string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));
    }
}
