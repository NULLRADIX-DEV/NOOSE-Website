using Microsoft.Extensions.Logging;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Announcements;
using NOOSE_Website.Data.Entities.Requests;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Notifications;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Financing;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Watchlist;
using NOOSE_Website.Data.Entities.Absences;
using NOOSE_Website.Data.Entities.Meetings;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Models.Abstractions;

namespace NOOSE_Website.Infrastructure.Notifications;

/// <summary>Maps a changed entity to its watchable parent record(s); allowlist-based to prevent notification loops.</summary>
public static class WatchlistRecordRollup
{
    public static IReadOnlyList<(string Type, string Id)> Map(object entity, ILogger logger)
    {
        switch (entity)
        {
            // ---- root records ----
            case Person p: return One(nameof(Person), p.Id);
            case Faction f: return One(nameof(Faction), f.Id);
            case PersonGroup g: return One(nameof(PersonGroup), g.Id);
            case Party pa: return One(nameof(Party), pa.Id);
            case Operation o: return One(nameof(Operation), o.Id);
            case Case v: return One(nameof(Case), v.Id);
            case Taskforce t: return One(nameof(Taskforce), t.Id);

            // ---- child to parent ----
            case PersonDoc d: return One(nameof(Person), d.PersonId);
            case Observation ob: return One(nameof(Person), ob.PersonId);
            case FactionMember fm: return One(nameof(Faction), fm.FactionId);
            case FactionAgent fa: return One(nameof(Faction), fa.FactionId);
            case PersonGroupMember gm: return One(nameof(PersonGroup), gm.PersonGroupId);
            case PersonGroupAgent ga: return One(nameof(PersonGroup), ga.PersonGroupId);
            case PartyMember pm: return One(nameof(Party), pm.PartyId);
            case PartyAgent paa: return One(nameof(Party), paa.PartyId);
            case OperationAgent oa: return One(nameof(Operation), oa.OperationId);
            case CaseAgent va: return One(nameof(Case), va.CaseId);
            case TaskforceAgent ta: return One(nameof(Taskforce), ta.TaskforceId);
            case TaskforceMessage tn: return One(nameof(Taskforce), tn.TaskforceId);
            case AgentNote av: return One(nameof(Agent), av.AgentId);
            case AgentPromotionRequest from: return One(nameof(Agent), from.AgentId);
            case Agent ag: return One(nameof(Agent), ag.Id);
            case AgentRankHistory adv: return One(nameof(Agent), adv.AgentId);

            // ---- polymorphic target ----
            case Comment k: return One(k.EntityType, k.EntityId);
            case Source q: return One(q.EntityType, q.EntityId);
            case TagMapping tz: return One(tz.EntityType, tz.EntityId);
            // going public is the most consequential thing that can happen to a file, so followers hear about it
            case OeffentlicheFahndung of: return One(nameof(Person), of.PersonId);
            // publishing is the most consequential thing that happens to a faction file
            case OeffentlichesFraktionsprofil ofp: return One(nameof(Faction), ofp.FactionId);

            // ---- relations ----
            case Link vk: return Two((vk.SourceType, vk.SourceId), (vk.TargetType, vk.TargetId));
            case PersonRelation pb: return Two((nameof(Person), pb.PersonAId), (nameof(Person), pb.PersonBId));

            // not watchable
            // a gallery photo is file material, but no follower wants a push per upload; the four galleries
            // stay silent alike
            case PersonPhoto:
            case FactionPhoto:
            case PersonGroupPhoto:
            case PartyPhoto:
            // the objection is two hops from a file (notice, then record) and this map has no database; the
            // publication of the notice stays the watchable event
            case FahndungEinspruch:
            case Job:
            case JobAssignment:
            case Announcement:
            case AnnouncementAcknowledgment:
            case Request:
            case Tag:
            case Notification:
            case SavedSearch:
            case WatchlistEntry:
            case Meeting:
            case MeetingAgendaItem:
            case MeetingAttendance:
            case MeetingSignOff:
            case Absence:
            // funding is deliberately not watchable: followers of a personnel file must not be
            // pushed another agent's budget details
            case FinancingRequest:
            case FinancingRequestLine:
            case FinancingItem:
            // the public area's own tables carry no record reference anyone follows: editorial pages, press
            // releases, standing warnings and released monthly texts are outward text, while module switches, a
            // citizen's own account and the warning value list are configuration — none of them is casework
            case OeffentlicheSeite:
            // the FAQ is a page of its own, not a file: a rubric is a heading and a question is outward
            // text, so no follower of a record could be meant by either
            case OeffentlicheFaqRubrik:
            case OeffentlicheFaqEintrag:
            case Pressemitteilung:
            case OeffentlicheWarnung:
            case OeffentlicherLagebericht:
            case OeffentlichesModul:
            case BuergerProfil:
            case Warnhinweis:
            // the bounty is treasury, not casework, and resolving it would need two hops through the database
            // this static map has no access to — publishing the notice is the watchable event
            case FahndungKopfgeldAnteil:
            // a tip reaches its file over two hops as well, and every chat line would fire again — the tip is
            // watched through the notice that was published, not line by line
            case Hinweis:
            case HinweisNachricht:
            // a reward is three hops from a file (reward, share, notice) and money at that: the watchable event is
            // the notice being marked captured
            case HinweisBelohnung:
            // a ticket hangs off no record at all — it is correspondence with a citizen, so there is nothing
            // for a follower of a file to be told about
            case Ticket:
            case TicketNachricht:
            case TicketParticipant:
            // an editorial publication about the agency itself; no file has a follower who could care
            case OeffentlichesFuehrungsprofil:
            // a template is configuration; what a follower could care about is the message it produced
            case OeffentlicheVorlage:
            // statutes are reference text, not casework, and nobody follows a law book
            case Law:
            case LawBook:
                return Array.Empty<(string, string)>();

            default:
                if (entity is IAuditable)
                {
                    logger.LogWarning(
                        "Watchlist-Rollup kennt den auditierbaren Typ {Typ} nicht – Änderungen daran benachrichtigen keine Folger.",
                        entity.GetType().Name);
                }
                return Array.Empty<(string, string)>();
        }
    }

    private static IReadOnlyList<(string, string)> One(string? type, string? id)
        => string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(id)
            ? Array.Empty<(string, string)>()
            : new[] { (type, id) };

    private static IReadOnlyList<(string, string)> Two((string? Type, string? Id) a, (string? Type, string? Id) b)
    {
        var list = new List<(string, string)>(2);
        if (!string.IsNullOrWhiteSpace(a.Type) && !string.IsNullOrWhiteSpace(a.Id))
        {
            list.Add((a.Type, a.Id));
        }
        if (!string.IsNullOrWhiteSpace(b.Type) && !string.IsNullOrWhiteSpace(b.Id))
        {
            list.Add((b.Type, b.Id));
        }
        return list;
    }
}
