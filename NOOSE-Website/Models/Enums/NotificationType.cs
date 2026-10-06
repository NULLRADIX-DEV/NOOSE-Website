using MudBlazor;

namespace NOOSE_Website.Models.Enums;

/// <summary>In-app notification type.</summary>
public enum NotificationType
{
    /// <summary>Request approved or rejected.</summary>
    RequestDecided = 0,

    /// <summary>User mentioned via @.</summary>
    Mention = 1,

    /// <summary>Account event occurred.</summary>
    Account = 2,

    /// <summary>Watched record changed.</summary>
    RecordModified = 3,

    /// <summary>Task assigned to user.</summary>
    JobAssigned = 4,

    /// <summary>Announcement published.</summary>
    Announcement = 5,

    /// <summary>Follow-up date due.</summary>
    Followup = 6,

    /// <summary>Appointment assigned.</summary>
    AppointmentAssigned = 7,

    /// <summary>New situation report.</summary>
    SituationReport = 8,

    /// <summary>Recruiting/application event.</summary>
    Recruiting = 9,

    /// <summary>Assigned task approaching its due date.</summary>
    JobDueSoon = 10,

    /// <summary>New meeting scheduled.</summary>
    MeetingScheduled = 11,

    /// <summary>Meeting starting soon.</summary>
    MeetingReminder = 12,

    /// <summary>Agent filed an absence.</summary>
    AbsenceFiled = 13,

    /// <summary>New personnel-file entry.</summary>
    PersonnelEntry = 14,

    /// <summary>Threat score rose sharply.</summary>
    ThreatSpike = 15,

    /// <summary>An agent abduction was filed.</summary>
    AbductionFiled = 16,

    /// <summary>Funding request filed or decided.</summary>
    Financing = 17,

    /// <summary>New website feedback filed.</summary>
    Feedback = 18,

    /// <summary>A wanted notice went public. Routable — this one is meant for the outside channel.</summary>
    PublicWantedPublished = 19,

    /// <summary>A published notice reached its expiry date. Deliberately not routable: it is an internal fact.</summary>
    PublicWantedExpired = 20,

    /// <summary>The advertised bounty on a live notice went up. Routable — a raise is meant for the outside channel.</summary>
    PublicWantedBountyRaised = 21,

    /// <summary>A citizen filed a tip. Deliberately not routable: it belongs in the inbox, not in a public channel.</summary>
    PublicTipReceived = 22,

    /// <summary>The agency wrote to a citizen about their tip. Not routable for the same reason.</summary>
    PublicTipAnswered = 23,

    /// <summary>A citizen's tip was rewarded. Not routable: a reward announcement in the public channel outs the tipster.</summary>
    PublicRewardPaid = 24,

    /// <summary>A citizen opened a ticket. Not routable: the concern of a named citizen has no place in a public channel.</summary>
    PublicTicketOpened = 25,

    /// <summary>Leadership answered a ticket. Not routable for the same reason.</summary>
    PublicTicketAnswered = 26,

    /// <summary>A citizen objected to a public notice. Not routable — it names a citizen disputing an accusation.</summary>
    PublicObjectionReceived = 27,

    /// <summary>The agency decided an objection. Not routable for the same reason; it is addressed to one citizen.</summary>
    PublicObjectionDecided = 28,

    /// <summary>A press release went public. Routable — an official statement naming no citizen belongs in the channel.</summary>
    PublicPressPublished = 29,

    /// <summary>An internal note on a ticket, or being attached to one. Not routable: it is house correspondence,
    /// and the citizen thread it sits next to names a citizen.</summary>
    PublicTicketInternal = 30,

    /// <summary>A new appointment was scheduled. Routable — the leadership channel, not the participants.</summary>
    AppointmentScheduled = 31,

    /// <summary>A ticket was opened. Routable — the leadership channel; the only citizen-desk category that is.
    /// Rings no bell of its own: <see cref="PublicTicketOpened"/> is the desk notice, this one exists so the
    /// opening pings and a reply on a running ticket does not.</summary>
    PublicTicketCreated = 32,

    /// <summary>An agent was terminated. Routable — its own channel, with a rich embed; rings no bell of its own,
    /// the personnel note is the in-app record.</summary>
    AgentTerminated = 33,

    /// <summary>A citizen reported catching a wanted person. Routable, and the one citizen submission that is:
    /// somebody is holding a wanted person right now. The post stays generic and names nobody.</summary>
    PublicCaptureReported = 34,

    /// <summary>A tip arrived at the desk. Routable — the role ping for a fresh submission; rings no bell of its
    /// own, <see cref="PublicTipReceived"/> is the desk notice. This one exists so the submission pings and a
    /// citizen's reply on a running tip does not. The post stays generic and names neither citizen nor subject.</summary>
    PublicTipCreated = 35,

    /// <summary>A partner agency asked for more records. Not routable: it names what an agency wants to see.</summary>
    PartnerInquiry = 36,
}

/// <summary>Display labels and icons.</summary>
public static class NotificationTypeDisplay
{
    public static string Name(NotificationType type) => type switch
    {
        NotificationType.RequestDecided => "Antrag entschieden",
        NotificationType.Mention => "Erwähnung",
        NotificationType.Account => "Konto",
        NotificationType.RecordModified => "Beobachtete Akte geändert",
        NotificationType.JobAssigned => "Aufgabe",
        NotificationType.Announcement => "Ankündigung",
        NotificationType.Followup => "Wiedervorlage fällig",
        NotificationType.AppointmentAssigned => "Termin",
        NotificationType.AppointmentScheduled => "Neuer Termin",
        NotificationType.PublicCaptureReported => "Ergreifungsmeldung",
        NotificationType.SituationReport => "Lagebericht",
        NotificationType.Recruiting => "Bewerbung",
        NotificationType.JobDueSoon => "Aufgabe fällig",
        NotificationType.MeetingScheduled => "Besprechung",
        NotificationType.MeetingReminder => "Besprechung beginnt bald",
        NotificationType.AbsenceFiled => "Abmeldung",
        NotificationType.PersonnelEntry => "Personalakte-Eintrag",
        NotificationType.AgentTerminated => "Kündigung",
        NotificationType.ThreatSpike => "Bedrohungs-Score gestiegen",
        NotificationType.AbductionFiled => "Agenten-Entführung",
        NotificationType.Financing => "Finanzierung",
        NotificationType.Feedback => "Feedback",
        NotificationType.PublicWantedPublished => "Öffentliche Ausschreibung",
        NotificationType.PublicWantedExpired => "Ausschreibung abgelaufen",
        NotificationType.PublicWantedBountyRaised => "Kopfgeld erhöht",
        NotificationType.PublicTipReceived => "Bürgerhinweis",
        NotificationType.PublicTipCreated => "Neuer Bürgerhinweis",
        NotificationType.PublicTipAnswered => "Antwort zu deinem Hinweis",
        NotificationType.PublicRewardPaid => "Belohnung ausgezahlt",
        NotificationType.PublicTicketOpened => "Neues Bürger-Ticket",
        NotificationType.PublicTicketCreated => "Neues Ticket",
        NotificationType.PublicTicketAnswered => "Antwort zu deinem Ticket",
        NotificationType.PublicTicketInternal => "Internes zum Ticket",
        NotificationType.PublicObjectionReceived => "Einspruch gegen eine Ausschreibung",
        NotificationType.PublicObjectionDecided => "Entscheidung zu deinem Einspruch",
        NotificationType.PublicPressPublished => "Pressemitteilung",
        NotificationType.PartnerInquiry => "Anfrage einer Partnerbehörde",
        _ => "Benachrichtigung",
    };

    /// <summary>Icon per notification type.</summary>
    public static string Icon(NotificationType type) => type switch
    {
        NotificationType.RequestDecided => Icons.Material.Filled.Gavel,
        NotificationType.Mention => Icons.Material.Filled.AlternateEmail,
        NotificationType.Account => Icons.Material.Filled.ManageAccounts,
        NotificationType.RecordModified => Icons.Material.Filled.Visibility,
        NotificationType.JobAssigned => Icons.Material.Filled.AssignmentInd,
        NotificationType.Announcement => Icons.Material.Filled.Campaign,
        NotificationType.Followup => Icons.Material.Filled.EventRepeat,
        NotificationType.AppointmentAssigned => Icons.Material.Filled.Event,
        NotificationType.AppointmentScheduled => Icons.Material.Filled.EventAvailable,
        NotificationType.PublicCaptureReported => Icons.Material.Filled.LocalPolice,
        NotificationType.SituationReport => Icons.Material.Filled.Assessment,
        NotificationType.Recruiting => Icons.Material.Filled.HowToReg,
        NotificationType.JobDueSoon => Icons.Material.Filled.AssignmentLate,
        NotificationType.MeetingScheduled => Icons.Material.Filled.Groups,
        NotificationType.MeetingReminder => Icons.Material.Filled.NotificationsActive,
        NotificationType.AbsenceFiled => Icons.Material.Filled.EventBusy,
        NotificationType.PersonnelEntry => Icons.Material.Filled.Description,
        NotificationType.AgentTerminated => Icons.Material.Filled.PersonRemove,
        NotificationType.ThreatSpike => Icons.Material.Filled.TrendingUp,
        NotificationType.AbductionFiled => Icons.Material.Filled.PersonOff,
        NotificationType.Financing => Icons.Material.Filled.RequestQuote,
        NotificationType.Feedback => Icons.Material.Filled.Feedback,
        NotificationType.PublicWantedPublished => Icons.Material.Filled.PersonSearch,
        NotificationType.PublicWantedExpired => Icons.Material.Filled.TimerOff,
        NotificationType.PublicWantedBountyRaised => Icons.Material.Filled.Paid,
        NotificationType.PublicTipReceived => Icons.Material.Filled.TipsAndUpdates,
        NotificationType.PublicTipCreated => Icons.Material.Filled.TipsAndUpdates,
        NotificationType.PublicTipAnswered => Icons.Material.Filled.MarkEmailUnread,
        NotificationType.PublicRewardPaid => Icons.Material.Filled.Redeem,
        NotificationType.PublicTicketOpened => Icons.Material.Filled.Forum,
        NotificationType.PublicTicketCreated => Icons.Material.Filled.QuestionAnswer,
        NotificationType.PublicTicketAnswered => Icons.Material.Filled.MarkChatUnread,
        NotificationType.PublicTicketInternal => Icons.Material.Filled.Lock,
        NotificationType.PublicObjectionReceived => Icons.Material.Filled.Balance,
        NotificationType.PublicObjectionDecided => Icons.Material.Filled.Gavel,
        NotificationType.PublicPressPublished => Icons.Material.Filled.Feed,
        NotificationType.PartnerInquiry => Icons.Material.Filled.RequestPage,
        _ => Icons.Material.Filled.Notifications,
    };
}
