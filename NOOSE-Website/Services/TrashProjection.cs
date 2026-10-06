using NOOSE_Website.Data.Entities.Abductions;
using NOOSE_Website.Data.Entities.Absences;
using NOOSE_Website.Data.Entities.Announcements;
using NOOSE_Website.Data.Entities.Appointments;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Changelog;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Handbook;
using NOOSE_Website.Data.Entities.Evidence;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Financing;
using NOOSE_Website.Data.Entities.Kasse;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Meetings;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Data.Entities.Radio;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Activities;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Informants;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Public;
using FeedbackEntity = NOOSE_Website.Data.Entities.Feedback.Feedback;

namespace NOOSE_Website.Services;

/// <summary>Flattens each deletable record type onto the shared trash row.</summary>
public static class TrashProjection
{
    private const string Dash = "—";

    public static TrashItem Person(Person x) => new("personen", x.Id, x.CaseNumber, x.Name, null, x.DeletedAt);

    public static TrashItem Faction(Faction x) => new("fraktionen", x.Id, x.CaseNumber, x.Name, null, x.DeletedAt);

    public static TrashItem Group(PersonGroup x) => new("personengruppen", x.Id, x.CaseNumber, x.Name, null, x.DeletedAt);

    public static TrashItem Party(Party x) => new("parteien", x.Id, x.CaseNumber, x.Name, null, x.DeletedAt);

    public static TrashItem Taskforce(Taskforce x) => new("taskforces", x.Id, x.CaseNumber, x.Name, null, x.DeletedAt);

    // a channel carries no Aktenzeichen, and the trash column is headed as one - frequency and label are the title
    public static TrashItem RadioChannel(RadioChannel x)
        => new("funk", x.Id, null, $"{x.Frequency} · {x.Label}", null, x.DeletedAt);

    public static TrashItem Case(Case x) => new("vorgaenge", x.Id, x.CaseNumber, x.Title, null, x.DeletedAt);

    public static TrashItem Operation(Operation x) => new("operationen", x.Id, x.CaseNumber, x.Title, null, x.DeletedAt);

    public static TrashItem Job(Job x) => new("aufgaben", x.Id, x.CaseNumber, x.Title, null, x.DeletedAt);

    public static TrashItem Announcement(Announcement x) => new("brett", x.Id, x.CaseNumber, x.Title, null, x.DeletedAt);

    public static TrashItem Appointment(Appointment x)
        => new("kalender", x.Id, x.CaseNumber, x.Title, Moment(x.Start), x.DeletedAt);

    public static TrashItem Meeting(Meeting x)
        => new("besprechungen", x.Id, x.CaseNumber, x.Title,
            Join(Moment(x.Start), x.Location, MeetingStatusDisplay.Name(x.Status)), x.DeletedAt);

    public static TrashItem Activity(AgentActivityListItem x)
        => new("aktivitaeten", x.Id, null, x.Title, x.OwnerName, x.DeletedAt);

    // absences carry no Aktenzeichen, so the agent identifies the row and the period is the detail
    public static TrashItem Absence(Absence x)
        => new("abmeldungen", x.Id, null, x.Agent?.Codename ?? x.AgentId,
            Join($"{x.FromDate:dd.MM.yyyy} – {x.ToDate:dd.MM.yyyy}",
                 $"{x.Days} Tage",
                 AbsenceCategoryDisplay.Name(x.Category)),
            x.DeletedAt);

    // abductions identify by Aktenzeichen; the victim codename is the detail
    public static TrashItem Abduction(AgentAbduction x)
        => new("entfuehrungen", x.Id, x.CaseNumber, x.VictimAgent?.Codename ?? x.VictimAgentId ?? "(gelöschter Agent)",
            AbductionOutcomeDisplay.Name(x.Outcome), x.DeletedAt);

    public static TrashItem Informant(InformantTrashItem x)
        => new("informanten", x.Id, x.CaseNumber, x.Name, x.Detail, x.DeletedAt);

    // evidence items carry no Aktenzeichen; the name identifies the row
    public static TrashItem EvidenceItem(EvidenceItem x)
        => new("asservate-items", x.Id, null, x.Name, x.Description, x.DeletedAt);

    public static TrashItem EvidenceEntry(EvidenceEntry x)
        => new("asservate-eintraege", x.Id, x.CaseNumber, EvidenceEntryTypeDisplay.Name(x.Type),
            Moment(x.Timestamp), x.DeletedAt);

    public static TrashItem KassenBuchung(KassenBuchung x)
        => new("kasse-buchungen", x.Id, x.CaseNumber,
            $"{KassenKontoDisplay.Name(x.Account)} · {KassenBuchungArtDisplay.Name(x.Kind)}",
            Join(Money.Format(x.Amount), Moment(x.Timestamp)), x.DeletedAt);

    public static TrashItem FinancingRequest(FinancingRequest x)
        => new("finanzierungen", x.Id, x.CaseNumber, FinancingStatusDisplay.Name(x.Status),
            Join(Money.Format(x.ApprovedSubsidy ?? x.RequestedSubsidy), $"{x.Lines.Count} Positionen"), x.DeletedAt);

    // feedback carries no Aktenzeichen; the kind plus a text snippet identifies the row
    public static TrashItem Feedback(FeedbackEntity x)
        => new("feedback", x.Id, null,
            $"{FeedbackKindDisplay.Name(x.Kind)}: {Snippet(x.Text)}",
            x.Agent?.Codename ?? x.AgentId, x.DeletedAt);

    // editorial pages carry no Aktenzeichen; the address identifies the row and the state is the detail
    public static TrashItem PublicPage(OeffentlicheSeite x)
        => new("oeffentliche-seiten", x.Id, null, x.Title,
            Join($"/info/{x.Slug}", PublicPageStatusDisplay.Name(x.Status)), x.DeletedAt);

    // never the body: the trash page is a list, and the markup may carry images
    public static TrashItem PressRelease(Pressemitteilung x)
        => new("pressemitteilungen", x.Id, x.CaseNumber, x.Title,
            PressReleaseStatusDisplay.Name(x.Status), x.DeletedAt);

    // never the body: it may carry images, and the trash page is a list
    public static TrashItem PublicWarning(OeffentlicheWarnung x)
        => new("oeffentliche-warnungen", x.Id, null, x.Title,
            PublicWarningStatusDisplay.Name(x.Status), x.DeletedAt);

    // never the body: it may carry images, and the period plus the state is what identifies the row
    public static TrashItem PublicReport(OeffentlicherLagebericht x)
        => new("oeffentliche-lageberichte", x.Id, null, x.Title,
            Join(ReportPeriod.Label(x.Year, x.Month), PublicReportStatusDisplay.Name(x.Status)), x.DeletedAt);

    // never the accusation text: the trash page is a list, and the snapshot's markup belongs to the editor
    public static TrashItem PublicWanted(OeffentlicheFahndung x)
        => new("oeffentliche-fahndungen", x.Id, x.CaseNumber, x.DisplayName,
            Join(PublicWantedKindDisplay.Name(x.Kind), PublicWantedStatusDisplay.Name(x.Status)), x.DeletedAt);

    // never the description: same reason, and it may carry images
    public static TrashItem PublicFactionProfile(OeffentlichesFraktionsprofil x)
        => new("oeffentliche-fraktionsprofile", x.Id, null, x.DisplayName,
            Join(PublicFactionStandingDisplay.Name(x.Standing), PublicProfileStatusDisplay.Name(x.Status)),
            x.DeletedAt);

    // no citizen name and no text: the trash list is read by every handler, and the anonymity promise does not
    // pause because a row was deleted
    public static TrashItem Tip(Hinweis x)
        => new("hinweise", x.Id, x.CaseNumber, $"Bürgerhinweis {x.CaseNumber}",
            TipStatusDisplay.Name(x.Status), x.DeletedAt);

    // the subject travels, the conversation does not: the trash page is a list, and the thread is the part
    // that belongs to the two people having it
    public static TrashItem Ticket(Data.Entities.Public.Ticket x)
        => new("tickets", x.Id, x.CaseNumber, x.Subject,
            TicketStatusDisplay.Name(x.Status), x.DeletedAt);

    // neither the citizen nor the argument: the bin is a list every handler reads, and what was disputed is the
    // part that belongs to the two sides of it
    public static TrashItem Objection(FahndungEinspruch x)
        => new("fahndungs-einsprueche", x.Id, x.CaseNumber, $"Einspruch {x.CaseNumber}",
            ObjectionStatusDisplay.Name(x.Status), x.DeletedAt);

    // the version is not repeated here: the trash page is a flat list, and the line itself is the identity
    public static TrashItem ChangelogLine(ChangelogEntry x)
        => new("neuerungen", x.Id, null, x.Title,
            Join(ChangelogKindDisplay.Name(x.Kind), x.Area), x.DeletedAt);

    public static TrashItem HandbookChapter(HandbookChapter x)
        => new("handbuch-kapitel", x.Id, null, x.Title, x.Description, x.DeletedAt);

    // never the body: the trash page is a list, and an article may carry images
    public static TrashItem HandbookArticle(HandbookArticle x)
        => new("handbuch-artikel", x.Id, null, x.Title, x.Summary, x.DeletedAt);

    // never the text: a paragraph body is HTML and may run for pages
    public static TrashItem Law(Law x)
        => new("gesetze", x.Id, null, $"{x.LawBook} {x.Paragraph} – {x.Title}", x.Section, x.DeletedAt);

    public static TrashItem LawBook(LawBook x)
        => new("gesetzbuecher", x.Id, null, $"{x.Abbreviation} – {x.Name}", x.Description, x.DeletedAt);

    private static string Snippet(string text)
        => text.Length <= 40 ? text : string.Concat(text.AsSpan(0, 40), "…");

    private static string Moment(DateTime value) => value.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

    private static string? Join(params string?[] parts)
    {
        var kept = parts.Where(p => !string.IsNullOrWhiteSpace(p) && p != Dash).ToArray();
        return kept.Length == 0 ? null : string.Join(" · ", kept);
    }
}
