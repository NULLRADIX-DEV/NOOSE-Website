using NOOSE_Website.Data.Entities.Common;

namespace NOOSE_Website.Services.Llm.Tools;

/// <summary>Shared rules of the law tools: the rank allowlist, and a law's plain-text form.</summary>
public static class LegalToolGate
{
    /// <summary>Tool names of the legal chat mode; the only tools a partner is ever offered.</summary>
    public static readonly IReadOnlySet<string> ToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        LawBooksTool.ToolName, LawSearchTool.ToolName, LawReadTool.ToolName,
    };

    /// <summary>False when the viewer is a partner whose rank may not see law books at all.</summary>
    public static async Task<bool> MayReadLawsAsync(NooseiToolContext context, IPartnerVisibilityPolicyService policy, CancellationToken cancellationToken)
        => !context.Scope.IsPartner
            || await policy.GetAllowedTypesAsync(context.Actor, cancellationToken) is not { } allowed
            || allowed.Contains(nameof(Law));

    public const string NotReleased = "Für dich sind keine Gesetzbücher freigegeben.";

    /// <summary>Citation form, e.g. "StGB § 1 – Mord".</summary>
    public static string Cite(Law law) => $"{law.LawBook} {law.Paragraph} – {law.Title}";
}
