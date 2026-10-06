namespace NOOSE_Website.Models.Common;

/// <summary>Leadership's answer to a partner inquiry: the record to release and how.</summary>
public sealed record PartnerInquiryAnswer(string EntityType, string EntityId, bool ToAccountOnly, bool IncludesChildren, string? Note);
