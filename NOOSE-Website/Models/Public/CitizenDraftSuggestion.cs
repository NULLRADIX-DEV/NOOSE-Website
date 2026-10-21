using NOOSE_Website.Models.Llm;

namespace NOOSE_Website.Models.Public;

/// <summary>A drafted text for a citizen plus what it cost; nothing is sent until the agent sends it.</summary>
public sealed record CitizenDraftSuggestion(string Text, long QuotaTokens, LlmQuotaStatus Quota);
