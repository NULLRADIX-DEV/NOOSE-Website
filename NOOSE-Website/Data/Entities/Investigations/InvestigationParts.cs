using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using System.ComponentModel.DataAnnotations.Schema;

namespace NOOSE_Website.Data.Entities.Investigations;

/// <summary>Audit stamps and soft delete shared by every row of an investigation.</summary>
public abstract class InvestigationRow : IAuditable, ISoftDelete
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("ErmittlungId")]
    public string InvestigationId { get; set; } = string.Empty;

    [Column("ErstelltAm")]
    public DateTime CreatedAt { get; set; }
    [Column("ErstelltVonId")]
    public string? CreatedById { get; set; }
    [Column("GeaendertAm")]
    public DateTime? ModifiedAt { get; set; }
    [Column("GeaendertVonId")]
    public string? ModifiedById { get; set; }

    [Column("IstGeloescht")]
    public bool IsDeleted { get; set; }
    [Column("GeloeschtAm")]
    public DateTime? DeletedAt { get; set; }
    [Column("GeloeschtVonId")]
    public string? DeletedById { get; set; }
}

/// <summary>Who takes part: an account (agent or partner) with a role, or a whole partner unit; tab rights deviate per row.</summary>
[Table("ErmittlungBeteiligte")]
public class InvestigationParticipant : InvestigationRow
{
    public Investigation? Investigation { get; set; }

    /// <summary>Set for an account (internal agent or partner); null for a unit row.</summary>
    public string? AgentId { get; set; }

    /// <summary>Set for a whole partner unit; null for an account row.</summary>
    [Column("PartnerEinheitId")]
    public string? PartnerUnitId { get; set; }

    [Column("Rolle")]
    public InvestigationRole Role { get; set; } = InvestigationRole.Investigator;

    /// <summary>Per tab deviations from the default (slug → level); null = defaults only.</summary>
    [Column("TabRechteJson")]
    public string? TabAccessJson { get; set; }
}

/// <summary>One charge: a paragraph of a law book and whether it applies.</summary>
[Table("ErmittlungTatvorwuerfe")]
public class InvestigationCharge : InvestigationRow
{
    /// <summary>Catalog law, when picked from it; the snapshot below is what the file shows.</summary>
    [Column("GesetzId")]
    public string? LawId { get; set; }

    [Column("Paragraf")]
    public string Paragraph { get; set; } = string.Empty;

    [Column("Gesetzbuch")]
    public string LawBook { get; set; } = string.Empty;

    [Column("Beschreibung")]
    public string Description { get; set; } = string.Empty;

    [Column("Pruefung")]
    public InvestigationChargeCheck Check { get; set; }

    [Column("Reihenfolge")]
    public int SortOrder { get; set; }
}

/// <summary>A witness, the accused or anyone else the file lists.</summary>
[Table("ErmittlungPersonen")]
public class InvestigationPerson : InvestigationRow
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Linked person file, when there is one.</summary>
    public string? PersonId { get; set; }

    [Column("Telefon")]
    public string? Phone { get; set; }

    /// <summary>Free text with suggestions (Beschuldigter, Zeuge, Geschädigter …).</summary>
    [Column("Rolle")]
    public string Role { get; set; } = "Zeuge";

    public InvestigationPersonStatus Status { get; set; }

    [Column("Notiz")]
    public string? Note { get; set; }
}

/// <summary>One piece of evidence in the evidence log.</summary>
[Table("ErmittlungBeweismittel")]
public class InvestigationEvidence : InvestigationRow
{
    /// <summary>Running number inside the file.</summary>
    [Column("Nummer")]
    public int Number { get; set; }

    [Column("Bezeichnung")]
    public string Name { get; set; } = string.Empty;

    [Column("Beschreibung")]
    public string? Description { get; set; }

    [Column("Besitzer")]
    public string? Owner { get; set; }

    /// <summary>Officer who secured it, as written.</summary>
    [Column("Beamter")]
    public string? Officer { get; set; }

    public InvestigationEvidenceStatus Status { get; set; }

    [Column("Datum")]
    public DateTime? SecuredAt { get; set; }
}

/// <summary>An interrogation of someone on the list, with its question and answer protocol.</summary>
[Table("ErmittlungVernehmungen")]
public class InvestigationInterrogation : InvestigationRow
{
    [Column("ErmittlungPersonId")]
    public string? InvestigationPersonId { get; set; }

    /// <summary>Name as written when no list entry is linked.</summary>
    public string Name { get; set; } = string.Empty;

    [Column("Rolle")]
    public string Role { get; set; } = "Zeuge";

    [Column("Vorladung")]
    public string? Summons { get; set; }

    [Column("Zeitpunkt")]
    public DateTime? HeldAt { get; set; }

    [Column("Beamte")]
    public string? Officers { get; set; }

    [Column("StaatsanwaltAnwesend")]
    public string? ProsecutorPresent { get; set; }

    [Column("Zusammenfassung")]
    public string? Summary { get; set; }

    /// <summary>Reference to an external protocol, e.g. a FiveNet document.</summary>
    [Column("ProtokollVerweis")]
    public string? ProtocolReference { get; set; }

    [Column("Verwertbar")]
    public bool Usable { get; set; } = true;

    public List<InvestigationInterrogationEntry> Entries { get; set; } = new();
}

/// <summary>One question and its answer; hard-deleted with the protocol edit.</summary>
[Table("ErmittlungVernehmungFragen")]
public class InvestigationInterrogationEntry : IAuditable
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("VernehmungId")]
    public string InterrogationId { get; set; } = string.Empty;

    [Column("Reihenfolge")]
    public int SortOrder { get; set; }

    [Column("Frage")]
    public string Question { get; set; } = string.Empty;

    [Column("Antwort")]
    public string Answer { get; set; } = string.Empty;

    [Column("ErstelltAm")]
    public DateTime CreatedAt { get; set; }
    [Column("ErstelltVonId")]
    public string? CreatedById { get; set; }
    [Column("GeaendertAm")]
    public DateTime? ModifiedAt { get; set; }
    [Column("GeaendertVonId")]
    public string? ModifiedById { get; set; }
}

/// <summary>A to-do of the file.</summary>
[Table("ErmittlungAufgaben")]
public class InvestigationTask : InvestigationRow
{
    [Column("Beschreibung")]
    public string Text { get; set; } = string.Empty;

    [Column("Erledigt")]
    public bool Done { get; set; }

    [Column("ErledigtAm")]
    public DateTime? DoneAt { get; set; }

    [Column("ErledigtVonId")]
    public string? DoneById { get; set; }

    /// <summary>Responsible account, optional.</summary>
    [Column("ZustaendigerAgentId")]
    public string? AssigneeId { get; set; }

    [Column("FaelligAm")]
    public DateTime? DueAt { get; set; }

    [Column("Reihenfolge")]
    public int SortOrder { get; set; }
}

/// <summary>A note in the file.</summary>
[Table("ErmittlungNotizen")]
public class InvestigationNote : InvestigationRow
{
    public string Text { get; set; } = string.Empty;

    [Column("AutorName")]
    public string? AuthorName { get; set; }
}

/// <summary>Limits one entry to named accounts or units; an entry with rows here is hidden from everyone else without full access.</summary>
[Table("ErmittlungEintragFreigaben")]
public class InvestigationItemRestriction : IAuditable
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    [Column("ErmittlungId")]
    public string InvestigationId { get; set; } = string.Empty;

    /// <summary>CLR type name of the entry (nameof).</summary>
    [Column("EintragTyp")]
    public string ItemType { get; set; } = string.Empty;

    [Column("EintragId")]
    public string ItemId { get; set; } = string.Empty;

    public string? AgentId { get; set; }

    [Column("PartnerEinheitId")]
    public string? PartnerUnitId { get; set; }

    [Column("ErstelltAm")]
    public DateTime CreatedAt { get; set; }
    [Column("ErstelltVonId")]
    public string? CreatedById { get; set; }
    [Column("GeaendertAm")]
    public DateTime? ModifiedAt { get; set; }
    [Column("GeaendertVonId")]
    public string? ModifiedById { get; set; }
}
