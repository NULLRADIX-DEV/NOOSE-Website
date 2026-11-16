using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services.Administration;

/// <summary>Reported personal relationships and the bias they cause; the one place that answers "related?".</summary>
public static class PersonalRelationRules
{
    public const int MaxNoteLength = 1000;

    public const string BiasMessage =
        "Befangen: Zwischen euch ist eine Personalbeziehung gemeldet. Das läuft über die Administration Division.";

    /// <summary>The stored spelling of a pair: lower id first.</summary>
    public static (string A, string B) Pair(string x, string y)
        => string.CompareOrdinal(x, y) <= 0 ? (x, y) : (y, x);

    public static async Task<bool> AreRelatedAsync(AppDbContext db, string? first, string? second,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(first) || string.IsNullOrEmpty(second)
            || string.Equals(first, second, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        var (a, b) = Pair(first, second);
        return await db.AgentRelationships.AsNoTracking()
            .AnyAsync(r => r.AgentAId == a && r.AgentBId == b, cancellationToken);
    }

    /// <summary>Ids of everyone related to this agent, for warnings shown before an action.</summary>
    public static async Task<HashSet<string>> RelatedIdsAsync(AppDbContext db, string? agentId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(agentId))
        {
            return new(StringComparer.Ordinal);
        }
        var rows = await db.AgentRelationships.AsNoTracking()
            .Where(r => r.AgentAId == agentId || r.AgentBId == agentId)
            .Select(r => r.AgentAId == agentId ? r.AgentBId : r.AgentAId)
            .ToListAsync(cancellationToken);
        return rows.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Refuses a personnel action between related agents.</summary>
    public static async Task RequireNotRelatedAsync(AppDbContext db, ClaimsPrincipal actor, string targetId,
        CancellationToken cancellationToken = default)
    {
        if (await AreRelatedAsync(db, actor.GetAgentId(), targetId, cancellationToken))
        {
            throw new UnauthorizedAccessException(BiasMessage);
        }
    }
}

/// <summary>Who sees and who handles a disciplinary proceeding.</summary>
/// <remarks>
/// The Board, admins and the supervision see all of them. The division sees all except those against its own members,
/// which belong to the Board alone. The accused sees the outcome only, never the running proceeding. A related
/// viewer never sees it.
/// </remarks>
public static class DisciplinaryRules
{
    public const int MaxTextLength = 20000;

    /// <summary>Prefilled into every new notice; the handler may change it per proceeding.</summary>
    public const string DefaultRightsNotice =
        "Gegen diese Verfügung kannst du innerhalb von sieben Tagen schriftlich Einspruch bei der Behördenleitung einlegen. "
        + "Der Einspruch hat keine aufschiebende Wirkung.";

    public static bool IsClosed(DisciplinaryStatus status)
        => status is DisciplinaryStatus.Discontinued or DisciplinaryStatus.Decided;

    public static bool IsPending(DisciplinaryStatus status)
        => status is DisciplinaryStatus.Open or DisciplinaryStatus.Suspended;

    /// <summary>Board, admin or supervision: the side that also sees proceedings against the division.</summary>
    public static bool IsLeadershipSide(ViewerScope scope)
        => scope.IsAdmin || DivisionRules.IsBoard(scope.Rank) || (scope.MayAdministration && !scope.IsAdministrationMember);

    public static bool IsLeadershipSide(ClaimsPrincipal actor) => actor.IsAdmin() || actor.IsBoard();

    /// <summary>A trainee or an agent on probation gets the simplified hearing.</summary>
    public static bool MayBeSimplified(Rank? rank, bool onProbation) => rank == Rank.Trainee || onProbation;

    public static async Task<bool> MaySeeAsync(AppDbContext db, string accusedId, DisciplinaryStatus status,
        ViewerScope scope, CancellationToken cancellationToken = default)
    {
        if (scope.IsPartner || !scope.IsInternalAgent)
        {
            return false;
        }
        if (scope.MeId is { Length: > 0 } me && string.Equals(me, accusedId, StringComparison.OrdinalIgnoreCase))
        {
            return IsClosed(status);
        }
        if (!scope.MayAdministration
            || await PersonalRelationRules.AreRelatedAsync(db, scope.MeId, accusedId, cancellationToken))
        {
            return false;
        }
        if (IsLeadershipSide(scope))
        {
            return true;
        }
        return !await db.Users.AsNoTracking().AnyAsync(u => u.Id == accusedId && u.IsHRB, cancellationToken);
    }

    /// <summary>Guard of every write on a proceeding; the division flag of the accused is read fresh.</summary>
    public static async Task RequireMayHandleAsync(AppDbContext db, ClaimsPrincipal actor, string accusedId,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireAdministrationWrite(actor);
        if (string.Equals(actor.GetAgentId(), accusedId, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("Ein Verfahren gegen dich selbst führst du nicht.");
        }
        await PersonalRelationRules.RequireNotRelatedAsync(db, actor, accusedId, cancellationToken);
        if (!IsLeadershipSide(actor)
            && await db.Users.AsNoTracking().AnyAsync(u => u.Id == accusedId && u.IsHRB, cancellationToken))
        {
            throw new UnauthorizedAccessException(
                "Verfahren gegen Mitglieder der Administration Division führt nur die Behördenleitung.");
        }
    }

    /// <summary>An open proceeding blocks promotion; called by every rank write that raises the rank.</summary>
    public static async Task RequireNoOpenProceedingAsync(AppDbContext db, string agentId, Rank? fromRank, Rank toRank,
        CancellationToken cancellationToken = default)
    {
        if (fromRank is { } from && toRank <= from)
        {
            return;
        }
        if (await db.DisciplinaryProceedings.AsNoTracking()
                .AnyAsync(d => d.AgentId == agentId
                    && (d.Status == DisciplinaryStatus.Open || d.Status == DisciplinaryStatus.Suspended), cancellationToken))
        {
            throw new InvalidOperationException(
                "Gegen den Agenten läuft ein Disziplinarverfahren. Bis zum Abschluss ist eine Beförderung gesperrt.");
        }
    }
}

/// <summary>Transfer requests: who files, who hears, who decides.</summary>
public static class TransferRules
{
    public const int MaxTextLength = 2000;

    public static bool IsPending(TransferRequestStatus status)
        => status is TransferRequestStatus.Open or TransferRequestStatus.Heard;

    /// <summary>The agent for themself, leadership and the Administration for anyone.</summary>
    public static bool MayRequest(ClaimsPrincipal actor, string agentId)
        => actor.MayWrite() && actor.IsInternalAgent()
           && (string.Equals(actor.GetAgentId(), agentId, StringComparison.OrdinalIgnoreCase)
               || actor.IsLeadership() || actor.IsAdministrationMember());

    /// <summary>The agent, the requester and the Administration side.</summary>
    public static bool MaySee(ViewerScope scope, string agentId, string? requesterId)
        => scope.IsInternalAgent
           && (scope.MayAdministration
               || (scope.MeId is { Length: > 0 } me
                   && (string.Equals(me, agentId, StringComparison.OrdinalIgnoreCase)
                       || string.Equals(me, requesterId, StringComparison.OrdinalIgnoreCase))));

    public static bool MayHear(ClaimsPrincipal actor, string agentId)
        => actor.MayAdministrationWrite() && !IsSelf(actor, agentId);

    public static bool MayDecide(ClaimsPrincipal actor, string agentId)
        => actor.MayWrite() && actor.MayDivisionSet() && !IsSelf(actor, agentId);

    private static bool IsSelf(ClaimsPrincipal actor, string agentId)
        => string.Equals(actor.GetAgentId(), agentId, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Service references: issued by the Administration, read by the agent too.</summary>
public static class ServiceReferenceRules
{
    public static bool MaySee(ViewerScope scope, string agentId)
        => scope.IsInternalAgent
           && (scope.MayAdministration
               || (scope.MeId is { Length: > 0 } me && string.Equals(me, agentId, StringComparison.OrdinalIgnoreCase)));

    public static bool MayIssue(ClaimsPrincipal actor, string agentId)
        => actor.MayAdministrationWrite()
           && !string.Equals(actor.GetAgentId(), agentId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Rounded mean of the evaluations as a starting grade; null without any.</summary>
    public static EvaluationGrade? SuggestedGrade(IEnumerable<EvaluationGrade> grades)
    {
        var list = grades.Select(g => (int)g).ToList();
        if (list.Count == 0)
        {
            return null;
        }
        var mean = (int)Math.Round(list.Average(), MidpointRounding.AwayFromZero);
        return (EvaluationGrade)Math.Clamp(mean, (int)EvaluationGrade.VeryGood, (int)EvaluationGrade.Insufficient);
    }
}

/// <summary>Decoration proposals: anyone proposes someone else, the Administration decides.</summary>
public static class DecorationProposalRules
{
    public const int MaxReasonLength = 1000;

    public static bool MayPropose(ClaimsPrincipal actor, string agentId)
        => actor.MayWrite() && actor.IsInternalAgent()
           && !string.Equals(actor.GetAgentId(), agentId, StringComparison.OrdinalIgnoreCase);

    public static bool MayDecide(ClaimsPrincipal actor, string agentId)
        => actor.MayAdministrationWrite()
           && !string.Equals(actor.GetAgentId(), agentId, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Parliamentary inquiries: parliament files and reads answers, the Administration answers.</summary>
public static class ParliamentRules
{
    public const int DefaultDueDays = 7;
    public const int MaxTitleLength = 200;
    public const int MaxTextLength = 20000;

    public static bool IsParliament(ClaimsPrincipal actor) => actor.GetPartnerAgency() == PartnerAgency.Parlament;

    public static bool IsParliament(ViewerScope scope) => scope.PartnerAgency == PartnerAgency.Parlament;

    /// <summary>A parliament account that may write, or the Administration recording one on its behalf.</summary>
    public static bool MayFile(ClaimsPrincipal actor)
        => (IsParliament(actor) && !actor.IsOnlyReader() && !actor.IsDemo()) || actor.MayAdministrationWrite();

    public static bool MaySee(ViewerScope scope) => IsParliament(scope) || (scope.MayAdministration && !scope.IsPartner);

    /// <summary>Parliament sees the answer once it is given; the Administration sees drafts too.</summary>
    public static bool MaySeeAnswer(ViewerScope scope, ParliamentaryInquiryStatus status)
        => (scope.MayAdministration && !scope.IsPartner) || status == ParliamentaryInquiryStatus.Answered;
}

/// <summary>The Administration chat.</summary>
public static class AdministrationChatRules
{
    public const int MaxLength = 4000;

    /// <summary>Without a read mark, unread counts this far back, so a new member is not handed the whole history.</summary>
    public static readonly TimeSpan UnreadWindow = TimeSpan.FromDays(3);

    public const string Href = "/administration?tab=chat";
}
