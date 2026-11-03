using System.Linq.Expressions;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services.Public;

/// <summary>Limits, the constant sender and the state transitions of a citizen ticket; read by service and form alike.</summary>
/// <remarks>
/// The quotas live here rather than in the rate-limiting middleware because a ticket is opened over SignalR, which
/// never reaches the middleware. Two independent caps: the open one clears itself as soon as leadership closes, the
/// daily one catches open-close-open.
/// </remarks>
public static class TicketRules
{
    /// <summary>What the citizen sees as the sender of every agency line.</summary>
    public const string AgencySender = "FIB – Führungsebene";

    public const int SubjectMinLength = 5;
    public const int SubjectMaxLength = 160;

    /// <summary>Below this the first message is not a concern; it is a click.</summary>
    public const int MinLength = 20;

    /// <summary>Length of one message in either thread; deliberately the same number as a tip message.</summary>
    public const int MaxMessageLength = TipRules.MaxMessageLength;

    /// <summary>Internal remark on a closure; the column is sized to the same number.</summary>
    public const int ClosingNoteMaxLength = 1000;

    /// <summary>Attachments one ticket may carry across both threads.</summary>
    /// <remarks>
    /// Counted per ticket rather than per message: the cap exists so a conversation cannot become a file drop, and
    /// per-message it would be no cap at all.
    /// </remarks>
    public const int MaxAttachments = 10;

    /// <summary>Tickets a citizen may have running at once.</summary>
    public const int MaxOpen = 2;

    /// <summary>New tickets per account per rolling 24 hours.</summary>
    public const int PerDay = 3;

    public static readonly TimeSpan QuotaWindow = TimeSpan.FromHours(24);

    /// <summary>A ticket waiting on the citizen is reminded once after this.</summary>
    public static readonly TimeSpan NudgeAfter = TimeSpan.FromDays(3);

    /// <summary>And closed as "no contact" after this, counted from the last activity.</summary>
    public static readonly TimeSpan AutoCloseAfter = TimeSpan.FromDays(7);

    /// <summary>An unanswered citizen line is due after this.</summary>
    public static readonly TimeSpan ReactionDue = TimeSpan.FromHours(12);

    /// <summary>And overdue after this.</summary>
    public static readonly TimeSpan ReactionOverdue = TimeSpan.FromHours(24);

    /// <summary>Still running; drives the desk badge, the open cap and the citizen's reply button.</summary>
    public static bool IsOpen(TicketStatus status) => status != TicketStatus.Geschlossen;

    /// <summary>Traffic light over how long the newest citizen line has gone unanswered.</summary>
    /// <remarks>
    /// A closed ticket and one whose newest line is the agency's are both <see cref="TicketReaction.Keine"/>: the
    /// column answers "who is waiting", so a ticket nobody waits on must not compete for attention with one that
    /// does. Both timestamps are UTC — the desk renders local, the comparison must not.
    /// </remarks>
    public static TicketReaction Reaction(TicketStatus status, DateTime? waitingSinceUtc, DateTime nowUtc)
    {
        if (!IsOpen(status) || waitingSinceUtc is not { } since)
        {
            return TicketReaction.Keine;
        }
        var waited = nowUtc - since;
        if (waited >= ReactionOverdue)
        {
            return TicketReaction.Ueberfaellig;
        }
        return waited >= ReactionDue ? TicketReaction.Faellig : TicketReaction.Frisch;
    }

    /// <summary>Query twin of <see cref="IsOpen"/>; the open cap counts exactly these rows.</summary>
    public static readonly Expression<Func<Ticket, bool>> OpenRows =
        t => t.Status != TicketStatus.Geschlossen;

    /// <summary>Lines the agency addressed to the citizen. The pre-filter of a reaction time, not the answer.</summary>
    public static readonly Expression<Func<TicketNachricht, bool>> AgencyRows =
        m => m.Audience == TicketMessageAudience.Buerger && !m.AuthorIsCitizen;

    /// <summary>Whether an agency line is a human answer rather than the automatic entry confirmation.</summary>
    /// <remarks>
    /// The confirmation is written into the ticket's own SaveChanges, so the interceptor stamps it with the ticket's
    /// timestamp and it is otherwise indistinguishable from a real reply. Without the strict comparison every ticket
    /// with an active template reports a reaction time of zero, and the desk looks perfect.
    /// </remarks>
    public static bool IsHumanAgencyReply(
        TicketMessageAudience audience, bool authorIsCitizen, DateTime createdAt, DateTime ticketCreatedAt)
        => audience == TicketMessageAudience.Buerger && !authorIsCitizen && createdAt > ticketCreatedAt;

    /// <summary>Allowed status moves; anything else is refused rather than silently applied.</summary>
    /// <remarks>
    /// Closed is closed for the citizen — a reply is refused rather than reopening the thread — but leadership may
    /// reopen, because the alternative is a citizen filing the same concern again and spending a quota slot on it.
    /// </remarks>
    public static bool IsTransitionAllowed(TicketStatus from, TicketStatus to)
    {
        if (from == to)
        {
            return false;
        }
        return from switch
        {
            TicketStatus.Offen => to is TicketStatus.InBearbeitung or TicketStatus.WartetAufBuerger
                or TicketStatus.Geschlossen,
            TicketStatus.InBearbeitung => to is TicketStatus.WartetAufBuerger or TicketStatus.Geschlossen,
            TicketStatus.WartetAufBuerger => to is TicketStatus.InBearbeitung or TicketStatus.Geschlossen,
            TicketStatus.Geschlossen => to is TicketStatus.InBearbeitung,
            _ => false,
        };
    }

    /// <summary>Statuses a handler may set by hand, given the current one.</summary>
    public static IReadOnlyList<TicketStatus> AllowedTargets(TicketStatus from)
        => TicketStatusDisplay.All.Where(t => IsTransitionAllowed(from, t)).ToList();

    /// <summary>Which statuses one desk tab shows.</summary>
    public static Expression<Func<Ticket, bool>> ScopeFilter(TicketInboxScope scope) => scope switch
    {
        TicketInboxScope.Offen => t => t.Status == TicketStatus.Offen,
        TicketInboxScope.Bearbeitung => t => t.Status == TicketStatus.InBearbeitung,
        TicketInboxScope.Wartet => t => t.Status == TicketStatus.WartetAufBuerger,
        _ => t => t.Status == TicketStatus.Geschlossen,
    };
}
