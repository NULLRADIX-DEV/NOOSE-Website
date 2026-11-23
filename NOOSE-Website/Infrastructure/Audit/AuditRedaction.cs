using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Administration;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Llm;
using NOOSE_Website.Data.Entities.Notifications;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Data.Entities.Public;

namespace NOOSE_Website.Infrastructure.Audit;

/// <summary>Fields whose value never enters an audit row; the change is recorded, the content is not.</summary>
/// <remarks>
/// The sections "Änderungen" and "Zugriffe" on /nachweis sit behind <c>Policies.LeadershipPage</c> (leadership and
/// the read-only supervision); <c>AuditLogQueryService</c> re-checks that and filters by nothing but the entity type
/// the reader picks. The page itself and the Chronik are open to every internal agent, but the Chronik anchors on
/// record types (Person, Case, ...) and their children, so it never prints the values of the entries below that
/// guard secrecy. That circle is still wider than the one most of these fields were written for: a tip carries an
/// anonymity promise, a snippet or a FIBI chat belongs to one agent, a watch report to its owner, the
/// Administration workspace to its division. The interceptor captures field values on Modified, Deleted (a soft
/// delete is a Modified row) and Restored, never on Created, so a field the user can edit would otherwise land in
/// ChangesJson in plain text. This registry turns "nobody has asked yet" into a rule, on the write side, so no later
/// reader of ChangesJson can undo it.
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
        // the closing remark names what the desk thought of a concern; the desk is the same circle that reads the
        // protocol, but the protocol must not become a second, unfiltered copy of the correspondence
        $"{nameof(Ticket)}.{nameof(Ticket.ClosingNote)}",
        // a snippet is one agent's private phrasing, and the panel that offers it says so in as many words:
        // "Sie gehören dir allein." Without this the protocol would hand the sentence - and the name the
        // agent gave it - to every reader of the protocol, which is the opposite of the promise.
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
        // the answer key and an agent's own answers: a toggled option or a colleague's draft would be a crib sheet
        // for anyone who later sits the test
        $"{nameof(BewerbungTestOption)}.{nameof(BewerbungTestOption.IsCorrect)}",
        $"{nameof(BewerbungTestQuestion)}.{nameof(BewerbungTestQuestion.CorrectYesNo)}",
        $"{nameof(BewerbungTestQuestion)}.{nameof(BewerbungTestQuestion.Keywords)}",
        $"{nameof(BewerbungTestQuestion)}.{nameof(BewerbungTestQuestion.MinKeywordHits)}",
        $"{nameof(AgentTestAnswer)}.{nameof(AgentTestAnswer.FreeTextAnswer)}",
        $"{nameof(AgentTestAnswer)}.{nameof(AgentTestAnswer.SelectedOptionId)}",
        $"{nameof(AgentTestAnswer)}.{nameof(AgentTestAnswer.SelectedOptionIds)}",
        // the full Google original would land twice in the protocol on every refresh; ContentHtml keeps the text
        $"{nameof(Document)}.{nameof(Document.ImportedHtml)}",
        // a credential: even the hash of the calendar feed link stays out of the protocol
        $"{nameof(Agent)}.{nameof(Agent.CalendarFeedTokenHash)}",
        // the Administration workspace is closed to most readers of the protocol: discipline, private relations,
        // hearings, references and the division's own chat stay out of it
        $"{nameof(AdministrationWorkspace)}.{nameof(AdministrationWorkspace.InformationHtml)}",
        $"{nameof(AdministrationMessage)}.{nameof(AdministrationMessage.Text)}",
        $"{nameof(DisciplinaryProceeding)}.{nameof(DisciplinaryProceeding.Grounds)}",
        $"{nameof(DisciplinaryProceeding)}.{nameof(DisciplinaryProceeding.Facts)}",
        $"{nameof(DisciplinaryProceeding)}.{nameof(DisciplinaryProceeding.Reasoning)}",
        $"{nameof(DisciplinaryProceeding)}.{nameof(DisciplinaryProceeding.RightsNotice)}",
        $"{nameof(DisciplinaryProceeding)}.{nameof(DisciplinaryProceeding.Measure)}",
        $"{nameof(TransferRequest)}.{nameof(TransferRequest.Reason)}",
        $"{nameof(TransferRequest)}.{nameof(TransferRequest.HearingNote)}",
        $"{nameof(TransferRequest)}.{nameof(TransferRequest.DecisionNote)}",
        $"{nameof(ServiceReference)}.{nameof(ServiceReference.TextHtml)}",
        $"{nameof(ServiceReference)}.{nameof(ServiceReference.Grade)}",
        $"{nameof(AgentRelationship)}.{nameof(AgentRelationship.Kind)}",
        $"{nameof(AgentRelationship)}.{nameof(AgentRelationship.Note)}",
        $"{nameof(DecorationProposal)}.{nameof(DecorationProposal.Reason)}",
        $"{nameof(ParliamentaryInquiry)}.{nameof(ParliamentaryInquiry.Question)}",
        $"{nameof(ParliamentaryInquiry)}.{nameof(ParliamentaryInquiry.Answer)}",
        // a FIBI chat is owner-private, leadership and admins see counts and cost, never the content; the title is
        // the first question, and the scope stamp fingerprints the owner's rights
        $"{nameof(NooseiConversation)}.{nameof(NooseiConversation.Title)}",
        $"{nameof(NooseiConversation)}.{nameof(NooseiConversation.ScopeStamp)}",
        // why a promotion was turned down, same treatment as TransferRequest.DecisionNote. Justification stays
        // visible on purpose: it is written once, never edited, and shown on the same personnel file tab to every
        // active agent, so the protocol would add no reader
        $"{nameof(AgentPromotionRequest)}.{nameof(AgentPromotionRequest.DecisionNote)}",
    };

    /// <summary>Whether this field's before/after pair stays out of the audit row.</summary>
    public static bool Hides(string entityType, string property)
        => Fields.Contains($"{entityType}.{property}");
}
