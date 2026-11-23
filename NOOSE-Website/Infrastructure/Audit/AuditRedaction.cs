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
/// Leadership reads the change log on /nachweis, a wider circle than these fields were written for.
/// </remarks>
public static class AuditRedaction
{
    // CLR names, because that is what the interceptor stamps into the audit row
    // scoped by type: Comment.Text keeps its before and after
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    {
        $"{nameof(HinweisNachricht)}.{nameof(HinweisNachricht.Text)}",
        $"{nameof(TicketNachricht)}.{nameof(TicketNachricht.Text)}",
        // the closing note names what the desk thought of a concern
        $"{nameof(Ticket)}.{nameof(Ticket.ClosingNote)}",
        // one agent's private phrasing, the panel promises "Sie gehören dir allein."
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
        // the answer key and an agent's own answers, a crib sheet for the next candidate
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
        // owner-private chat, the title is the first question
        $"{nameof(NooseiConversation)}.{nameof(NooseiConversation.Title)}",
        $"{nameof(NooseiConversation)}.{nameof(NooseiConversation.ScopeStamp)}",
        // why a promotion was turned down
        $"{nameof(AgentPromotionRequest)}.{nameof(AgentPromotionRequest.DecisionNote)}",
    };

    /// <summary>Whether this field's before/after pair stays out of the audit row.</summary>
    public static bool Hides(string entityType, string property)
        => Fields.Contains($"{entityType}.{property}");
}
