using System.Security.Claims;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Record changes that need a written reason, logged as their own audit row next to the change.</summary>
public static class CriticalChange
{
    /// <summary>Longest reason the dialog and the service accept.</summary>
    public const int MaxReasonLength = 1000;

    // German on purpose: the change log prints unknown field names verbatim
    public const string DeletionField = "Löschgrund";
    public const string DowngradeField = "Herabstufungsgrund";
    public const string SecrecyField = "Lockerungsgrund";
    public const string WantedLiftField = "Aufhebungsgrund";

    /// <summary>Change-log field name of a record's secrecy level.</summary>
    public const string SecrecyLevelField = "SecrecyLevel";

    /// <summary>Classification goes down, back to Unbekannt included.</summary>
    public static bool IsDowngrade(Classification old, Classification @new) => @new < old;

    /// <summary>Someone who could not read the record before may read it afterwards.</summary>
    /// <remarks>Leadership is the narrowest audience, so only a move onto it never needs a reason.</remarks>
    public static bool LoosensSecrecy(DocumentClassification old, DocumentClassification @new)
        => old != DocumentClassification.None && @new != old && @new != DocumentClassification.Leadership;

    /// <summary>Secrecy level of a taskforce, whose single flag means leadership only.</summary>
    public static DocumentClassification TaskforceLevel(bool isClassified)
        => isClassified ? DocumentClassification.Leadership : DocumentClassification.None;

    /// <summary>Trimmed reason; throws when it is missing or too long.</summary>
    public static string RequireReason(string? reason, string change)
    {
        var note = reason?.Trim();
        if (string.IsNullOrEmpty(note))
        {
            throw new InvalidOperationException($"{change} braucht eine Begründung.");
        }
        if (note.Length > MaxReasonLength)
        {
            throw new InvalidOperationException($"Die Begründung ist zu lang (höchstens {MaxReasonLength} Zeichen).");
        }
        return note;
    }

    /// <summary>Audit row carrying the reason, after the field change it explains.</summary>
    public static AuditLog Row(string entityType, string entityId, string reasonField, string reason,
        ClaimsPrincipal actor, string? changedField = null, object? oldValue = null, object? newValue = null)
    {
        var changes = new Dictionary<string, object?[]>();
        if (changedField is not null)
        {
            changes[changedField] = [oldValue, newValue];
        }
        changes[reasonField] = [null, reason];
        return ManualAudit.Row(entityType, entityId, AuditAction.Justified, actor, changes);
    }

    /// <summary>Trimmed deletion reason; checked before anything is loaded.</summary>
    public static string RequireDeletionReason(string? reason) => RequireReason(reason, "Das Löschen einer Akte");

    /// <summary>Stages the reason row for a deletion on the same save as the delete.</summary>
    public static void NoteDeletion(AppDbContext db, string entityType, string entityId, string reason,
        ClaimsPrincipal actor)
        => db.AuditLogs.Add(Row(entityType, entityId, DeletionField, reason, actor));

    /// <summary>Stages the reason row for a downgrade; throws when one is due and missing.</summary>
    public static void NoteDowngrade(AppDbContext db, string entityType, string entityId,
        Classification old, Classification @new, string? reason, ClaimsPrincipal actor)
    {
        if (!IsDowngrade(old, @new))
        {
            return;
        }
        var note = RequireReason(reason, "Das Herabstufen der Einstufung");
        db.AuditLogs.Add(Row(entityType, entityId, DowngradeField, note, actor, nameof(Classification), old, @new));
    }

    /// <summary>Stages the reason row for a loosened Verschlusssache; throws when one is due and missing.</summary>
    public static void NoteSecrecy(AppDbContext db, string entityType, string entityId,
        DocumentClassification old, DocumentClassification @new, string? reason, ClaimsPrincipal actor)
    {
        if (!LoosensSecrecy(old, @new))
        {
            return;
        }
        var note = RequireReason(reason, "Das Lockern der Verschlusssache");
        db.AuditLogs.Add(Row(entityType, entityId, SecrecyField, note, actor, SecrecyLevelField, old, @new));
    }

    /// <summary>Stages the reason row for lifting a wanted flag; throws when one is due and missing.</summary>
    public static void NoteWantedLift(AppDbContext db, string personId, bool wasWanted, bool wanted,
        string? reason, ClaimsPrincipal actor)
    {
        if (!wasWanted || wanted)
        {
            return;
        }
        var note = RequireReason(reason, "Das Aufheben der Fahndung");
        db.AuditLogs.Add(Row(nameof(Person), personId, WantedLiftField, note, actor,
            nameof(Person.IsWanted), true, false));
    }
}
