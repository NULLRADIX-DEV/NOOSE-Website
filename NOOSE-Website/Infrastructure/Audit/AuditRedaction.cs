using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Notifications;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Data.Entities.Public;

namespace NOOSE_Website.Infrastructure.Audit;

/// <summary>Fields whose value never enters an audit row; the change is recorded, the content is not.</summary>
/// <remarks>
/// The change protocol on /nachweis is open to every internal agent, and it filters by nothing but the entity type
/// the reader picks. Citizen correspondence is narrower than that: a ticket thread is leadership-only, and a tip
/// carries an anonymity promise. Both used to stay out of the protocol by accident — the interceptor captures field
/// values on Modified only, and every message row was a Created row until they became editable. This registry turns
/// that accident into a rule, on the write side, so no later reader of ChangesJson can undo it.
///
/// Scoped by type, not by field name: the same "Text" on a Comment is supposed to show its before and after.
/// </remarks>
public static class AuditRedaction
{
    // CLR names, because that is what the interceptor stamps into the audit row
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        $"{nameof(HinweisNachricht)}.{nameof(HinweisNachricht.Text)}",
        $"{nameof(TicketNachricht)}.{nameof(TicketNachricht.Text)}",
        // the closing remark names what the desk thought of a concern, and /nachweis is read house-wide
        $"{nameof(Ticket)}.{nameof(Ticket.ClosingNote)}",
        // a snippet is one agent's private phrasing, and the panel that offers it says so in as many words:
        // "Sie gehören dir allein." Without this the protocol would hand the sentence - and the name the
        // agent gave it - to every internal reader, which is the opposite of the promise.
        $"{nameof(TextSnippet)}.{nameof(TextSnippet.Text)}",
        $"{nameof(TextSnippet)}.{nameof(TextSnippet.Name)}",
        // a file name the citizen chose is their content, exactly like the message it hangs on
        $"{nameof(TicketNachricht)}.{nameof(TicketNachricht.AttachmentOriginalName)}",
        // a watched search's hits were found under its owner's rights, VS records included
        $"{nameof(SavedSearch)}.{nameof(SavedSearch.KnownHitsJson)}",
        $"{nameof(SavedSearch)}.{nameof(SavedSearch.UnseenHitsJson)}",
        $"{nameof(SavedSearch)}.{nameof(SavedSearch.ScopeStamp)}",
        // a folded notice rewrites its title, and the title is written for one recipient's eyes: a watch report
        // names hits only its owner may see
        $"{nameof(Notification)}.{nameof(Notification.Title)}",
        // the answer key and an agent's own answers: internal tests are taken by the same agents who read the
        // protocol, so a toggled option or a colleague's draft would be a crib sheet
        $"{nameof(BewerbungTestOption)}.{nameof(BewerbungTestOption.IsCorrect)}",
        $"{nameof(BewerbungTestQuestion)}.{nameof(BewerbungTestQuestion.CorrectYesNo)}",
        $"{nameof(BewerbungTestQuestion)}.{nameof(BewerbungTestQuestion.Keywords)}",
        $"{nameof(BewerbungTestQuestion)}.{nameof(BewerbungTestQuestion.MinKeywordHits)}",
        $"{nameof(AgentTestAnswer)}.{nameof(AgentTestAnswer.FreeTextAnswer)}",
        $"{nameof(AgentTestAnswer)}.{nameof(AgentTestAnswer.SelectedOptionId)}",
        $"{nameof(AgentTestAnswer)}.{nameof(AgentTestAnswer.SelectedOptionIds)}",
        // the full Google original would land twice in the protocol on every refresh; ContentHtml keeps the text
        $"{nameof(Document)}.{nameof(Document.ImportedHtml)}",
    };

    /// <summary>Whether this field's before/after pair stays out of the audit row.</summary>
    public static bool Hides(string entityType, string property)
        => Fields.Contains($"{entityType}.{property}");
}
