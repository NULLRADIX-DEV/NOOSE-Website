using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Services;

/// <summary>Demo data for the editorial half of the public area: pages, press, warnings, reports, organisation and leadership profiles, released law.</summary>
public partial class DemoDataService
{
    private const string PressPrefix = "PM";

    private sealed record PageSpec(string Slug, string Title, string MenuTitle, string Icon, int Sort, string[] Paragraphs);

    private static readonly PageSpec[] PageSpecs =
    [
        new("auftrag", "Auftrag der NOOSE", "Auftrag", "Shield", 10,
        [
            "Das National Office of Security Enforcement ist die Bundesbehörde für die Abwehr schwerer und "
            + "organisierter Kriminalität im Bundesstaat San Andreas.",
            "Wir führen Akten über Personen und Organisationen, gegen die ein Anfangsverdacht besteht, koordinieren "
            + "behördenübergreifende Einsätze und unterstützen die örtlichen Polizeibehörden bei Lagen, die deren "
            + "Mittel übersteigen.",
            "Diese Seite ist der öffentliche Teil unserer Arbeit: Fahndungen, amtliche Warnungen, Presse und die "
            + "Möglichkeit, uns Beobachtungen zu melden.",
        ]),
        new("befugnisse", "Befugnisse", "Befugnisse", "Gavel", 20,
        [
            "Die NOOSE handelt auf gesetzlicher Grundlage. Maßnahmen, die in Grundrechte eingreifen, stehen unter "
            + "Richtervorbehalt und werden dokumentiert.",
            "Zu unseren Befugnissen gehören die Identitätsfeststellung, die Durchsuchung von Personen und Sachen bei "
            + "Gefahr im Verzug, die vorläufige Festnahme sowie die Sicherstellung von Beweismitteln.",
            "Jede Maßnahme wird in der Akte der betroffenen Person festgehalten. Betroffene können der Veröffentlichung "
            + "einer Fahndung über den Bürgerbereich widersprechen.",
        ]),
        new("zustaendigkeiten", "Zuständigkeiten", "Zuständigkeiten", "Groups", 30,
        [
            "Zuständig sind wir für organisierte Kriminalität, Waffen- und Menschenhandel, Straftaten gegen die "
            + "öffentliche Sicherheit sowie für die Beobachtung verbotener Organisationen.",
            "Nicht zuständig sind wir für Verkehrsdelikte, Nachbarschaftsstreitigkeiten und Alltagskriminalität. "
            + "Wenden Sie sich in diesen Fällen bitte an das LSPD.",
            "In medizinischen Notlagen wählen Sie den Notruf. Über diese Seite können wir keine Soforthilfe leisten.",
        ]),
        // introduction only: the questions themselves are rows, seeded by SeedPublicFaqAsync below
        new("faq", "Häufige Fragen", "FAQ", "MenuBook", 40,
        [
            "Antworten auf die Fragen, die uns am häufigsten erreichen. Klappen Sie einen Abschnitt auf, um die "
            + "einzelnen Fragen zu sehen.",
        ]),
        new("kontakt", "Kontakt und Erreichbarkeit", "Kontakt", "Forum", 50,
        [
            "Für allgemeine Anliegen nutzen Sie bitte den Ticket-Bereich Ihres Bürgerkontos. Anliegen werden von der "
            + "Führungsebene bearbeitet.",
            "Für konkrete Beobachtungen nutzen Sie das Hinweisformular — dort landen die Angaben direkt beim "
            + "zuständigen Schalter und werden priorisiert.",
            "In dringenden Fällen und bei akuter Gefahr wählen Sie den Notruf. Diese Seite wird nicht rund um die Uhr gelesen.",
        ]),
    ];

    private static async Task<int> SeedPublicPagesAsync(AppDbContext db, CancellationToken ct)
    {
        var existing = (await db.OeffentlicheSeiten.IgnoreQueryFilters().ToListAsync(ct))
            .ToDictionary(p => p.Slug, p => p, StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var spec in PageSpecs)
        {
            var html = string.Concat(spec.Paragraphs.Select(Paragraph));
            if (existing.TryGetValue(spec.Slug, out var page))
            {
                // the startup seeder leaves placeholder drafts behind; the demo publishes real text
                if (page.Status == PublicPageStatus.Veroeffentlicht)
                {
                    continue;
                }
                page.Title = spec.Title;
                page.MenuTitle = spec.MenuTitle;
                page.IconName = spec.Icon;
                page.SortOrder = spec.Sort;
                page.ShowInMenu = true;
                page.DraftHtml = html;
                page.ContentHtml = html;
                page.Status = PublicPageStatus.Veroeffentlicht;
                page.PublishedAt = DateTime.UtcNow.AddDays(-40);
                page.PublishedById = DemoIdentity.AgentId;
                added++;
                continue;
            }
            db.OeffentlicheSeiten.Add(new OeffentlicheSeite
            {
                Slug = spec.Slug,
                Title = spec.Title,
                MenuTitle = spec.MenuTitle,
                IconName = spec.Icon,
                SortOrder = spec.Sort,
                ShowInMenu = true,
                DraftHtml = html,
                ContentHtml = html,
                Status = PublicPageStatus.Veroeffentlicht,
                PublishedAt = DateTime.UtcNow.AddDays(-40),
                PublishedById = DemoIdentity.AgentId,
            });
            added++;
        }
        return added;
    }

    private sealed record FaqSpec(string Title, string Icon, bool DefaultOpen, (string Question, string Answer)[] Entries);

    private static readonly FaqSpec[] FaqSpecs =
    [
        new("Hinweise & Meldungen", "Tips", true,
        [
            ("Wie gebe ich einen Hinweis?",
                "Über das Hinweisformular. Sie brauchen dafür ein Bürgerkonto mit hinterlegtem Namen; auf Wunsch "
                + "behandeln wir Ihre Identität vertraulich."),
            ("Was passiert mit meinem Hinweis?",
                "Er wird bewertet, priorisiert und einem Bearbeiter zugewiesen. Über Rückfragen und den Abschluss "
                + "informieren wir Sie in Ihrem Bürgerbereich."),
            ("Bekomme ich eine Belohnung?",
                "Führt Ihr Hinweis zur Ergreifung einer ausgeschriebenen Person, kann eine ausgelobte Belohnung "
                + "ausgezahlt werden. Über die Auszahlung erhalten Sie einen Beleg."),
        ]),
        new("Fahndung & Einspruch", "PersonSearch", false,
        [
            ("Ich stehe zu Unrecht auf der Fahndungsseite. Was kann ich tun?",
                "Legen Sie über Ihr Bürgerkonto Einspruch ein. Sie erhalten eine begründete Entscheidung."),
            ("Darf ich eine gesuchte Person selbst festhalten?",
                "Nein. Melden Sie Ihre Beobachtung und halten Sie Abstand. Wurde eine gesuchte Person bereits "
                + "gestellt, können Sie das über die Ergreifungsmeldung mitteilen."),
        ]),
    ];

    /// <summary>Seeds the structured FAQ under /faq; idempotent over the section titles.</summary>
    private static async Task<int> SeedPublicFaqAsync(AppDbContext db, CancellationToken ct)
    {
        var known = (await db.OeffentlicheFaqRubriken.Select(r => r.Title).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var taken = (await db.OeffentlicheFaqEintraege.Select(e => e.Anchor).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        var sort = 0;
        foreach (var spec in FaqSpecs)
        {
            sort += 10;
            if (known.Contains(spec.Title))
            {
                continue;
            }

            var rubrik = new OeffentlicheFaqRubrik
            {
                Title = spec.Title,
                IconName = spec.Icon,
                SortOrder = sort,
                IsVisible = true,
                DefaultOpen = spec.DefaultOpen,
            };
            db.OeffentlicheFaqRubriken.Add(rubrik);
            added++;

            var entrySort = 0;
            foreach (var (question, answer) in spec.Entries)
            {
                entrySort += 10;
                // the same anchor the service would mint, suffix included, and cut the same way: the column
                // holds 64 characters and the index on it is unique
                var basis = PublicPageSlug.Normalize(question);
                var anchor = basis;
                for (var n = 2; !taken.Add(anchor); n++)
                {
                    var suffix = "-" + n;
                    var room = PublicPageSlug.MaxLength - suffix.Length;
                    anchor = (basis.Length <= room ? basis : basis[..room].TrimEnd('-')) + suffix;
                }
                db.OeffentlicheFaqEintraege.Add(new OeffentlicheFaqEintrag
                {
                    RubrikId = rubrik.Id,
                    Question = question,
                    Anchor = anchor,
                    AnswerHtml = Paragraph(answer),
                    SortOrder = entrySort,
                    IsVisible = true,
                });
                added++;
            }
        }
        return added;
    }

    private sealed record PressSpec(string Title, string Teaser, int DaysAgo, string[] Paragraphs);

    private static readonly PressSpec[] PressSpecs =
    [
        new("Zugriff im Hafenviertel abgeschlossen",
            "Bei einer abgestimmten Maßnahme im Hafenviertel wurden drei Personen gestellt. Die zugehörige "
            + "Ausschreibung ist beendet.", 5,
        [
            "Einsatzkräfte der NOOSE haben gestern Abend gemeinsam mit dem LSPD ein Lagergebäude auf Elysian Island "
            + "durchsucht. Drei Personen wurden vorläufig festgenommen.",
            "Sichergestellt wurden mehrere Schusswaffen sowie Unterlagen, die im Zusammenhang mit einem laufenden "
            + "Verfahren ausgewertet werden.",
            "Die öffentliche Ausschreibung zu einem der Beschuldigten wurde nach der Ergreifung abgeschlossen. "
            + "Der Behörde ging im Vorfeld ein Bürgerhinweis zu, der zur Ergreifung beigetragen hat.",
        ]),
        new("Warnung vor gefälschten Kontrollstellen",
            "Unbekannte geben sich als Einsatzkräfte aus und halten Fahrzeuge an. Die Behörde warnt und bittet um Hinweise.", 9,
        [
            "In den vergangenen Tagen wurden mehrere Fälle gemeldet, in denen Unbekannte in dunkler Kleidung "
            + "Fahrzeuge angehalten und Wertsachen gefordert haben.",
            "Echte Kontrollen erfolgen ausschließlich durch uniformierte Kräfte in gekennzeichneten Fahrzeugen. "
            + "Im Zweifel fahren Sie zur nächsten Dienststelle weiter und melden den Vorfall.",
        ]),
        new("Jahresbilanz: mehr Hinweise aus der Bevölkerung",
            "Die Zahl der über die Website eingereichten Hinweise ist deutlich gestiegen. Jeder fünfte führte zu einer Maßnahme.", 24,
        [
            "Seit der Freischaltung des Bürgerbereichs hat sich die Zahl verwertbarer Hinweise mehr als verdoppelt.",
            "Die Behörde dankt allen, die Beobachtungen gemeldet haben, und weist erneut darauf hin, dass niemand "
            + "selbst eingreifen soll.",
        ]),
        new("Neue Gefahrenlage für den Süden der Stadt",
            "Nach mehreren Auseinandersetzungen gilt für Davis und Rancho bis auf Weiteres eine erhöhte Gefahrenlage.", 11,
        [
            "Nach wiederholten Auseinandersetzungen zwischen rivalisierenden Gruppierungen hat die Behörde die "
            + "Gefahrenlage für den Süden der Stadt auf „Erhöht\" gesetzt.",
            "Die Einstufung wird laufend überprüft. Über Änderungen informieren wir auf dieser Seite.",
        ]),
    ];

    private async Task<int> SeedPressAsync(AppDbContext db, List<TimeStamp> stamps, CancellationToken ct)
    {
        var existing = (await db.Pressemitteilungen.IgnoreQueryFilters().Select(p => p.Title).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var spec in PressSpecs)
        {
            if (!existing.Add(spec.Title))
            {
                continue;
            }
            var html = string.Concat(spec.Paragraphs.Select(Paragraph));
            var published = DateTime.UtcNow.AddDays(-spec.DaysAgo);
            var release = new Pressemitteilung
            {
                CaseNumber = await caseNumbers.NextAsync(db, PressPrefix, ct),
                Title = spec.Title,
                Teaser = spec.Teaser,
                DraftHtml = html,
                ContentTitle = spec.Title,
                ContentTeaser = spec.Teaser,
                ContentHtml = html,
                Status = PressReleaseStatus.Veroeffentlicht,
                PublishedAt = published,
                PublishedById = DemoIdentity.AgentId,
                DiscordPushedAt = published,
            };
            db.Pressemitteilungen.Add(release);
            stamps.Add(new TimeStamp(nameof(Pressemitteilung), release.Id, published));
            added++;
        }
        return added;
    }

    private sealed record WarningSpec(string Title, int DaysAgo, int? ValidDays, string[] Paragraphs);

    private static readonly WarningSpec[] WarningSpecs =
    [
        new("Gefälschte Kontrollstellen auf der Route 68", 4, 21,
        [
            "Unbekannte halten auf der Route 68 Fahrzeuge an und geben sich als Einsatzkräfte aus. "
            + "Halten Sie nur an gekennzeichneten Kontrollstellen und bei uniformierten Kräften.",
            "Melden Sie verdächtige Anhaltevorgänge über das Hinweisformular oder unter dem Notruf.",
        ]),
        new("Erhöhte Gefahrenlage in Davis und Rancho", 11, 30,
        [
            "Nach mehreren bewaffneten Auseinandersetzungen raten wir davon ab, sich nachts in Davis und Rancho "
            + "aufzuhalten. Meiden Sie insbesondere die Umgebung der Grove Street.",
        ]),
        new("Vorsicht bei angeblichen Spendensammlern", 2, 14,
        [
            "In Del Perro sammeln Unbekannte angeblich für Hinterbliebene von Einsatzkräften. "
            + "Die Behörde sammelt keine Spenden und beauftragt niemanden damit.",
        ]),
    ];

    private static async Task<int> SeedWarningsAsync(AppDbContext db, List<TimeStamp> stamps, CancellationToken ct)
    {
        var existing = (await db.OeffentlicheWarnungen.IgnoreQueryFilters().Select(w => w.Title).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var spec in WarningSpecs)
        {
            if (!existing.Add(spec.Title))
            {
                continue;
            }
            var html = string.Concat(spec.Paragraphs.Select(Paragraph));
            var published = DateTime.UtcNow.AddDays(-spec.DaysAgo);
            var warning = new OeffentlicheWarnung
            {
                Title = spec.Title,
                DraftHtml = html,
                ContentTitle = spec.Title,
                ContentHtml = html,
                ValidUntil = spec.ValidDays is int days ? DateTime.UtcNow.AddDays(days) : null,
                Status = PublicWarningStatus.Veroeffentlicht,
                PublishedAt = published,
                PublishedById = DemoIdentity.AgentId,
            };
            db.OeffentlicheWarnungen.Add(warning);
            stamps.Add(new TimeStamp(nameof(OeffentlicheWarnung), warning.Id, published));
            added++;
        }
        return added;
    }

    /// <summary>Public monthly reports; each needs an internal report as its anchor.</summary>
    private static async Task<int> SeedPublicReportsAsync(AppDbContext db, CancellationToken ct)
    {
        var existing = (await db.OeffentlicheLageberichte.IgnoreQueryFilters()
                .Select(r => new { r.Year, r.Month }).ToListAsync(ct))
            .Select(r => r.Year * 100 + r.Month)
            .ToHashSet();
        var anchors = (await db.SituationReports.IgnoreQueryFilters().ToListAsync(ct))
            .ToDictionary(r => r.Year * 100 + r.Month, r => r);

        var added = 0;
        var now = DateTime.UtcNow;
        for (var back = 1; back <= 3; back++)
        {
            var month = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-back);
            var key = month.Year * 100 + month.Month;
            if (!existing.Add(key))
            {
                continue;
            }
            if (!anchors.TryGetValue(key, out var anchor))
            {
                anchor = new SituationReport
                {
                    Year = month.Year,
                    Month = month.Month,
                    Title = $"Lagebericht {MonthName(month.Month)} {month.Year}",
                    SnapshotJson = "{}",
                };
                db.SituationReports.Add(anchor);
                anchors[key] = anchor;
                added++;
            }

            var title = $"Lagebild {MonthName(month.Month)} {month.Year}";
            var html = string.Concat(new[]
            {
                "Die Zahl der offenen Ausschreibungen ist gegenüber dem Vormonat annähernd konstant geblieben. "
                + "Schwerpunkte der Ermittlungen waren erneut der Waffenhandel im Hafenbereich und die "
                + "Auseinandersetzungen im Süden der Stadt.",
                "Aus der Bevölkerung erreichten uns zahlreiche Hinweise; ein erheblicher Teil davon war verwertbar "
                + "und hat zu Maßnahmen geführt. Die Behörde dankt ausdrücklich dafür.",
                "Die Gefahrenlage wurde im Berichtszeitraum überprüft und angepasst. Einzelheiten zu laufenden "
                + "Verfahren können aus ermittlungstaktischen Gründen nicht veröffentlicht werden.",
            }.Select(Paragraph));

            db.OeffentlicheLageberichte.Add(new OeffentlicherLagebericht
            {
                SituationReportId = anchor.Id,
                Year = month.Year,
                Month = month.Month,
                Title = title,
                DraftHtml = html,
                ContentTitle = title,
                ContentHtml = html,
                Status = PublicReportStatus.Veroeffentlicht,
                PublishedAt = month.AddMonths(1).AddDays(2),
                PublishedById = DemoIdentity.AgentId,
            });
            added++;
        }
        return added;
    }

    private static string MonthName(int month) => month switch
    {
        1 => "Januar", 2 => "Februar", 3 => "März", 4 => "April", 5 => "Mai", 6 => "Juni",
        7 => "Juli", 8 => "August", 9 => "September", 10 => "Oktober", 11 => "November", _ => "Dezember",
    };

    /// <summary>Public organisation profiles for the factions that are not classified.</summary>
    private static async Task<int> SeedFactionProfilesAsync(
        AppDbContext db, Dictionary<string, Faction> factions, CancellationToken ct)
    {
        var existing = (await db.OeffentlicheFraktionsprofile.IgnoreQueryFilters()
                .Select(p => p.FactionId).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var candidates = factions.Values
            .Where(f => !f.IsClassified && !f.IsDeleted && !f.IsStateFaction)
            .OrderByDescending(f => f.ThreatScore ?? 0)
            .Take(8)
            .ToList();

        var added = 0;
        var index = 0;
        foreach (var faction in candidates)
        {
            if (!existing.Add(faction.Id))
            {
                continue;
            }
            var banned = (faction.ThreatScore ?? 0) >= 55;
            var text = banned
                ? $"Die Gruppierung {faction.Name} ist durch Verfügung des Innenministeriums verboten. "
                    + "Mitgliedschaft, Unterstützung und das Verwenden ihrer Kennzeichen sind strafbar. "
                    + "Wer Kontakt zu Angehörigen der Gruppierung beobachtet, wird um Hinweise gebeten."
                : $"Die Gruppierung {faction.Name} wird von der Behörde beobachtet. Es besteht der Verdacht auf "
                    + "Straftaten von erheblicher Bedeutung. Eine Mitgliedschaft ist für sich genommen nicht strafbar; "
                    + "die Behörde bittet gleichwohl um Hinweise zu Straftaten aus dem Umfeld.";

            db.OeffentlicheFraktionsprofile.Add(new OeffentlichesFraktionsprofil
            {
                FactionId = faction.Id,
                DisplayName = faction.Name,
                DescriptionHtml = Paragraph(text),
                Standing = banned ? PublicFactionStanding.Verboten : PublicFactionStanding.Beobachtet,
                Status = PublicProfileStatus.Veroeffentlicht,
                PublicHazardLevel = HazardLevelLogic.From(faction.ThreatScore),
                PublishedAt = DateTime.UtcNow.AddDays(-30 + (index++ * 3)),
                PublishedById = DemoIdentity.AgentId,
            });
            added++;
        }
        return added;
    }

    private sealed record LeadershipSpec(string Name, string Title, string Role, int Sort);

    private static readonly LeadershipSpec[] LeadershipSpecs =
    [
        new("Marcus Hale", "Director", "Leitung der Behörde", 10),
    ];

    /// <summary>The one outward surface that names people. Only the demo agent is released here.</summary>
    private static async Task<int> SeedLeadershipProfilesAsync(AppDbContext db, CancellationToken ct)
    {
        var existing = (await db.OeffentlicheFuehrungsprofile.Select(p => p.AgentId).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        if (existing.Contains(DemoIdentity.AgentId))
        {
            return 0;
        }

        var added = 0;
        foreach (var spec in LeadershipSpecs)
        {
            db.OeffentlicheFuehrungsprofile.Add(new OeffentlichesFuehrungsprofil
            {
                PublicKey = Guid.NewGuid().ToString("N"),
                AgentId = DemoIdentity.AgentId,
                DisplayName = spec.Name,
                Title = spec.Title,
                RoleText = spec.Role,
                SortOrder = spec.Sort,
                PublishedAt = DateTime.UtcNow.AddDays(-45),
                PublishedById = DemoIdentity.AgentId,
            });
            added++;
        }
        return added;
    }

    private sealed record LawSpec(string Book, string Paragraph, string Title, string Text, string Sentence);

    private static readonly LawSpec[] LawSpecs =
    [
        new("StGB", "§ 129", "Bildung krimineller Vereinigungen",
            "Wer eine Vereinigung gründet, deren Zweck oder Tätigkeit auf die Begehung von Straftaten gerichtet ist, "
            + "oder sich an einer solchen Vereinigung als Mitglied beteiligt, wird bestraft.",
            "Freiheitsstrafe bis zu fünf Jahren oder Geldstrafe"),
        new("StGB", "§ 244", "Diebstahl mit Waffen; Bandendiebstahl",
            "Wer einen Diebstahl begeht, bei dem er eine Waffe bei sich führt oder als Mitglied einer Bande handelt, "
            + "die sich zur fortgesetzten Begehung solcher Taten verbunden hat, wird bestraft.",
            "Freiheitsstrafe von sechs Monaten bis zu zehn Jahren"),
        new("StGB", "§ 250", "Schwerer Raub",
            "Wer bei einem Raub eine Waffe verwendet, das Opfer in die Gefahr einer schweren Gesundheitsschädigung "
            + "bringt oder als Mitglied einer Bande handelt, wird wegen schweren Raubes bestraft.",
            "Freiheitsstrafe nicht unter drei Jahren"),
        new("WaffG", "§ 52", "Unerlaubter Umgang mit Waffen",
            "Wer ohne die erforderliche Erlaubnis eine Schusswaffe erwirbt, besitzt, führt oder einem anderen "
            + "überlässt, wird bestraft.",
            "Freiheitsstrafe bis zu fünf Jahren oder Geldstrafe"),
        new("BtmG", "§ 29a", "Betäubungsmittel in nicht geringer Menge",
            "Wer mit Betäubungsmitteln in nicht geringer Menge unerlaubt Handel treibt, sie herstellt, abgibt oder "
            + "besitzt, wird bestraft.",
            "Freiheitsstrafe nicht unter einem Jahr"),
        new("StPO", "§ 127", "Vorläufige Festnahme",
            "Wird jemand auf frischer Tat betroffen oder verfolgt, so ist jedermann befugt, ihn auch ohne "
            + "richterliche Anordnung vorläufig festzunehmen, wenn er der Flucht verdächtig ist oder seine "
            + "Identität nicht sofort festgestellt werden kann.",
            "keine Strafnorm — Befugnisnorm"),
    ];

    private static readonly (string Abbreviation, string Name)[] LawBookSpecs =
    [
        ("StGB", "Strafgesetzbuch des Staates San Andreas"),
        ("StPO", "Strafprozessordnung des Staates San Andreas"),
        ("BtmG", "Betäubungsmittelgesetz des Staates San Andreas"),
        ("WaffG", "Waffengesetz des Staates San Andreas"),
    ];

    /// <summary>Releases a handful of paragraphs for the public law extract; the flag on the record is the release.</summary>
    private static async Task<int> SeedLawsAsync(AppDbContext db, CancellationToken ct)
    {
        var added = 0;
        var books = (await db.LawBooks.IgnoreQueryFilters().Select(b => b.Abbreviation).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < LawBookSpecs.Length; i++)
        {
            var (abbreviation, name) = LawBookSpecs[i];
            if (books.Add(abbreviation))
            {
                db.LawBooks.Add(new LawBook { Abbreviation = abbreviation, Name = name, SortOrder = i + 1 });
                added++;
            }
        }

        var existing = (await db.Laws.IgnoreQueryFilters().ToListAsync(ct))
            .GroupBy(l => l.LawBook + "|" + l.Paragraph, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var sortOrder = 0;
        foreach (var spec in LawSpecs)
        {
            sortOrder++;
            var key = spec.Book + "|" + spec.Paragraph;
            if (existing.TryGetValue(key, out var law))
            {
                if (!law.IsPublic)
                {
                    law.IsPublic = true;
                    added++;
                }
                continue;
            }
            db.Laws.Add(new Law
            {
                LawBook = spec.Book,
                Paragraph = spec.Paragraph,
                Title = spec.Title,
                Text = HtmlCleanup.FromPlain(spec.Text),
                Sentence = spec.Sentence,
                SortOrder = sortOrder,
                IsPublic = true,
            });
            added++;
        }
        return added;
    }

    private sealed record TemplateSpec(PublicTemplateKind Kind, string Title, string Text, int Sort);

    private static readonly TemplateSpec[] TemplateSpecs =
    [
        new(PublicTemplateKind.TicketAntwort, "Zwischenstand zu einem Anliegen",
            "Guten Tag BUERGER,\n\nzu Ihrem Anliegen AKTENZEICHEN können wir Ihnen einen Zwischenstand geben: "
            + "die Prüfung läuft. Sobald uns Neues vorliegt, melden wir uns in diesem Ticket.\n\n"
            + "Mit freundlichen Grüßen\nNOOSE – Führungsebene", 20),
        new(PublicTemplateKind.TicketAntwort, "Abschluss eines Anliegens",
            "Guten Tag BUERGER,\n\nwir betrachten Ihr Anliegen AKTENZEICHEN als abgeschlossen. "
            + "Sollten Sie Rückfragen haben, können Sie hier weiter schreiben.\n\n"
            + "Mit freundlichen Grüßen\nNOOSE – Führungsebene", 30),
        new(PublicTemplateKind.HinweisRueckfrage, "Rückfrage zu Ort und Zeit",
            "Guten Tag BUERGER,\n\nvielen Dank für Ihren Hinweis AKTENZEICHEN. Können Sie Ort und Uhrzeit Ihrer "
            + "Beobachtung noch genauer eingrenzen? Auch Kleinigkeiten helfen uns weiter.\n\nNOOSE", 20),
        new(PublicTemplateKind.HinweisAblehnung, "Kein Anfangsverdacht",
            "Guten Tag BUERGER,\n\nwir haben Ihren Hinweis AKTENZEICHEN vom DATUM geprüft. "
            + "Für eine Maßnahme reichen die Angaben nicht aus; wir schließen den Vorgang daher ab. "
            + "Bitte melden Sie sich erneut, wenn Ihnen Neues auffällt.\n\nNOOSE", 20),
    ];

    private static async Task<int> SeedPublicTemplatesAsync(AppDbContext db, CancellationToken ct)
    {
        var existing = (await db.OeffentlicheVorlagen.IgnoreQueryFilters()
                .Select(v => new { v.Kind, v.Title }).ToListAsync(ct))
            .Select(v => (int)v.Kind + "|" + v.Title)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var spec in TemplateSpecs)
        {
            if (!existing.Add((int)spec.Kind + "|" + spec.Title))
            {
                continue;
            }
            db.OeffentlicheVorlagen.Add(new OeffentlicheVorlage
            {
                Kind = spec.Kind,
                Title = spec.Title,
                Text = spec.Text,
                IsActive = true,
                SortOrder = spec.Sort,
            });
            added++;
        }
        return added;
    }
}
