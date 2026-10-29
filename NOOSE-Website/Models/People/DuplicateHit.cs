using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.People;

/// <summary>A record that may be the person about to be created; Alias is set when the match came through one.</summary>
public sealed record DuplicateHit(Person Person, DuplicateReason Reason, string? Alias = null);
