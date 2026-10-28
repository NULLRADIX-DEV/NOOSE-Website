using Microsoft.AspNetCore.Identity;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities;

/// <summary>NOOSE user account; Discord OAuth only, no password.</summary>
public class Agent : IdentityUser
{
    public string Codename { get; set; } = string.Empty;

    [Column("Klarname")]
    public string? RealName { get; set; }

    [Column("Dienstnummer")]
    public string? BadgeNumber { get; set; }

    public string DiscordId { get; set; } = string.Empty;

    public string? DiscordUsername { get; set; }

    public string? AvatarUrl { get; set; }

    /// <summary>Approved profile picture; bare file name under the agent avatar upload path.</summary>
    [Column("Profilbild")]
    public string? AvatarFileName { get; set; }

    [Column("ProfilbildTyp")]
    public string? AvatarContentType { get; set; }

    /// <summary>Uploaded but not yet released; leadership decides on the release inbox.</summary>
    [Column("AusstehendesProfilbild")]
    public string? PendingAvatarFileName { get; set; }

    [Column("AusstehendesProfilbildTyp")]
    public string? PendingAvatarContentType { get; set; }

    [Column("ProfilbildBeantragtAm")]
    public DateTime? AvatarRequestedAt { get; set; }

    [Column("Dienstgrad")]
    public Rank? Rank { get; set; }

    [Column("IstTRU")]
    public bool IsTRU { get; set; }

    [Column("IstHRB")]
    public bool IsHRB { get; set; }

    [Column("IstAdmin")]
    public bool IsAdmin { get; set; }

    /// <summary>Visibility marker only; grants no rights.</summary>
    [Column("IstTeamLeitung")]
    public bool IsTeamLead { get; set; }

    /// <summary>Null = internal NOOSE agent.</summary>
    [Column("Partnerbehoerde")]
    public PartnerAgency? PartnerAgency { get; set; }

    /// <summary>Null = internal NOOSE agent.</summary>
    [Column("Partnerrang")]
    public PartnerRank? PartnerRank { get; set; }

    public AgentStatus Status { get; set; } = AgentStatus.Pending;

    [Column("RegistriertAm")]
    public DateTime RegisteredAt { get; set; }
    [Column("FreigegebenAm")]
    public DateTime? ReleasedAt { get; set; }
    [Column("FreigegebenVonId")]
    public string? ReleasedById { get; set; }

    [Column("GesperrtGrund")]
    public string? BlockedReason { get; set; }

    [Column("GekuendigtAm")]
    public DateTime? TerminatedAt { get; set; }
    [Column("GekuendigtVonId")]
    public string? TerminatedById { get; set; }
    [Column("GekuendigtVonName")]
    public string? TerminatedByName { get; set; }
    [Column("Kuendigungsgrund")]
    public string? TerminationReason { get; set; }

    [Column("DiscordRollenSyncAm")]
    public DateTime? DiscordRolesSyncAt { get; set; }

    [Column("AusstehenderCodename")]
    public string? PendingCodename { get; set; }

    [Column("AusstehenderKlarname")]
    public string? PendingRealName { get; set; }

    [Column("AusstehendeDienstnummer")]
    public string? PendingBadgeNumber { get; set; }

    [Column("NamensaenderungBeantragtAm")]
    public DateTime? NameChangeRequestedAt { get; set; }

    /// <summary>Per-user nav preferences (favorites, order, recents) as JSON.</summary>
    [Column("NavEinstellungen")]
    public string? NavPreferencesJson { get; set; }

    /// <summary>Monthly funding budget overriding the rank default; null = rank default applies.</summary>
    [Column("Finanzierungsbudget")]
    public decimal? FinancingBudgetOverride { get; set; }

    /// <summary>Weekly NOOSEI token quota overriding the rank default; null = rank default applies.</summary>
    [Column("KiKontingent")]
    public long? LlmQuotaOverride { get; set; }

    /// <summary>When the account last became a selectable agent again (unblocked, reinstated, made internal); the
    /// inactivity radar never counts from before it.</summary>
    [Column("AktivSeitAm")]
    public DateTime? ActiveSinceAt { get; set; }

    /// <summary>Last page visit, stamped at most hourly; one of the inactivity radar's traces.</summary>
    [Column("ZuletztGesehenAm")]
    public DateTime? LastSeenAt { get; set; }

    /// <summary>When leadership last asked this agent to check in over the inactivity radar.</summary>
    [Column("InaktivNachgefasstAm")]
    public DateTime? InactivityNudgedAt { get; set; }

    [Column("InaktivNachgefasstVonName")]
    public string? InactivityNudgedByName { get; set; }

    /// <summary>Off the public roster on /fuehrung until the agent opts in; the page never says anyone is missing.</summary>
    /// <remarks>
    /// Initialised in C#, not as a database default: EF would skip a false that equals the CLR default and let the
    /// column default win, so nobody could ever be created visible.
    /// </remarks>
    [Column("AufMitarbeiterlisteVerborgen")]
    public bool HiddenFromPublicRoster { get; set; } = true;
}
