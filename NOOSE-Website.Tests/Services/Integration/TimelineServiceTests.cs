using System.Security.Claims;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Activities;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Meetings;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Timeline;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="TimelineService"/> against in-memory SQLite. Read-only service: no Permission guard, visibility is the gate.</summary>
public sealed class TimelineServiceTests
{
    private static TimelineService Build(SqliteTestContext ctx) => new(ctx.Factory);

    // Rank >= SpecialAgentInCharge(4) => IsLeadership() + MayClassifiedRead().
    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).WithCodename("Falcon").Build();

    // Junior agent: not leadership, cannot read classified records.
    private static ClaimsPrincipal Junior()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).Build();

    // External partner: read-only, sees only shared, non-classified records.
    private static ClaimsPrincipal Partner()
        => ClaimsPrincipalBuilder.Agent("partner1").AsPartner(PartnerAgency.LSPD, PartnerRank.Member).Build();

    private static DateTime Utc(int day, int hour = 0)
        => new(2026, 3, day, hour, 0, 0, DateTimeKind.Utc);

    // ---------- visibility gate ----------

    [Fact]
    public async Task GetTimelineAsync_NonexistentRecord_ReturnsEmpty()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        var result = await svc.GetTimelineAsync("Person", "does-not-exist", Leader());

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetTimelineAsync_ClassifiedPerson_Junior_ReturnsEmpty()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", configure: p => p.IsClassified = true));
            db.Comments.Add(new Comment { EntityType = "Person", EntityId = "p1", Text = "geheim", CreatedAt = Utc(1) });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetTimelineAsync("Person", "p1", Junior());

        // classified record is invisible to a non-privileged viewer even though events exist
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetTimelineAsync_ClassifiedPerson_Leader_ReturnsEntries()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", configure: p => p.IsClassified = true));
            db.Comments.Add(new Comment { EntityType = "Person", EntityId = "p1", Text = "geheim", CreatedAt = Utc(1) });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetTimelineAsync("Person", "p1", Leader());

        Assert.Single(result);
        Assert.Equal(TimelineCategory.Comment, result[0].Category);
    }

    [Fact]
    public async Task GetTimelineAsync_Partner_UnsharedRecord_ReturnsEmpty()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.Comments.Add(new Comment { EntityType = "Person", EntityId = "p1", Text = "hallo", CreatedAt = Utc(1) });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        // partner has no share on p1 => record not visible
        var result = await svc.GetTimelineAsync("Person", "p1", Partner());

        Assert.Empty(result);
    }

    // ---------- audit base ----------

    [Fact]
    public async Task GetTimelineAsync_Audit_CreatedAndModified_MappedWithChanges()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = "Person", EntityId = "p1", Action = AuditAction.Created,
                AgentName = "Falcon", Timestamp = Utc(1),
            });
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = "Person", EntityId = "p1", Action = AuditAction.Modified,
                AgentName = "Hawk", Timestamp = Utc(2),
                ChangesJson = "{\"Name\":[\"Alt\",\"Neu\"]}",
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetTimelineAsync("Person", "p1", Leader());

        Assert.Equal(2, result.Count);
        var created = Assert.Single(result.Where(e => e.Category == TimelineCategory.Asset));
        Assert.Equal("Akte angelegt", created.Title);
        Assert.Equal("Falcon", created.ActorName);

        var modified = Assert.Single(result.Where(e => e.Category == TimelineCategory.Change));
        Assert.Equal("Akte geändert", modified.Title);
        Assert.NotNull(modified.Changes);
        var change = Assert.Single(modified.Changes!);
        Assert.Equal("Name", change.Field);
        Assert.Equal("Alt", change.Alt);
        Assert.Equal("Neu", change.New);
    }

    [Fact]
    public async Task GetTimelineAsync_Audit_LinkEntityType_Excluded()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = "Person", EntityId = "p1", Action = AuditAction.Created, Timestamp = Utc(1),
            });
            // Link audit rows are filtered out of the audit base
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = "Link", EntityId = "p1", Action = AuditAction.Created, Timestamp = Utc(2),
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetTimelineAsync("Person", "p1", Leader());

        Assert.Single(result);
        Assert.Equal(TimelineCategory.Asset, result[0].Category);
    }

    // ---------- classification history ----------

    [Fact]
    public async Task GetTimelineAsync_ClassificationHistory_Mapped()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.ClassificationHistory.Add(new ClassificationHistory
            {
                EntityType = "Person", EntityId = "p1", Value = Classification.SuspicionCase,
                Justification = "Neue Erkenntnisse", Timestamp = Utc(1), AgentName = "Falcon",
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetTimelineAsync("Person", "p1", Leader());

        var entry = Assert.Single(result);
        Assert.Equal(TimelineCategory.Classification, entry.Category);
        Assert.StartsWith("Einstufung:", entry.Title);
        Assert.Equal("Neue Erkenntnisse", entry.Detail);
        Assert.Equal("Falcon", entry.ActorName);
    }

    // ---------- comments ----------

    [Fact]
    public async Task GetTimelineAsync_Comment_Included_AndTruncated()
    {
        using var ctx = new SqliteTestContext();
        var longText = new string('x', 200);
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.Comments.Add(new Comment
            {
                EntityType = "Person", EntityId = "p1", Text = longText,
                AuthorName = "Falcon", CreatedAt = Utc(1),
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetTimelineAsync("Person", "p1", Leader());

        var entry = Assert.Single(result);
        Assert.Equal(TimelineCategory.Comment, entry.Category);
        Assert.Equal("Kommentar", entry.Title);
        Assert.Equal("Falcon", entry.ActorName);
        Assert.NotNull(entry.Detail);
        Assert.EndsWith("…", entry.Detail!);
        // truncated to 160 chars + ellipsis
        Assert.Equal(161, entry.Detail!.Length);
    }

    // ---------- sources ----------

    [Fact]
    public async Task GetTimelineAsync_Source_Included_ActorResolvedFromCodename()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.Users.Add(Seed.Agent("author", configure: a => a.Codename = "Ghost"));
            db.Sources.Add(new Source
            {
                EntityType = "Person", EntityId = "p1", Type = SourceType.Link,
                Title = "Zeugenaussage", CreatedAt = Utc(1), CreatedById = "author",
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetTimelineAsync("Person", "p1", Leader());

        var entry = Assert.Single(result);
        Assert.Equal(TimelineCategory.Source, entry.Category);
        Assert.Equal("Quelle hinzugefügt: Zeugenaussage", entry.Title);
        Assert.Equal("Web-Link", entry.Detail);
        // actor resolved from the seeded agent's codename via CreatedById
        Assert.Equal("Ghost", entry.ActorName);
    }

    // ---------- followups ----------

    [Fact]
    public async Task GetTimelineAsync_Followup_Done_EmitsTwoEntries()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.Followups.Add(new Followup
            {
                EntityType = "Person", EntityId = "p1", DueAt = Utc(5),
                Note = "Rückruf", Done = true, DoneAt = Utc(6),
                CreatedAt = Utc(1), CreatedById = "author",
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetTimelineAsync("Person", "p1", Leader());

        var followups = result.Where(e => e.Category == TimelineCategory.Followup).ToList();
        Assert.Equal(2, followups.Count);
        Assert.Contains(followups, e => e.Title == "Wiedervorlage angelegt");
        Assert.Contains(followups, e => e.Title == "Wiedervorlage erledigt");
    }

    [Fact]
    public async Task GetTimelineAsync_Followup_Open_EmitsOneEntry()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.Followups.Add(new Followup
            {
                EntityType = "Person", EntityId = "p1", DueAt = Utc(5),
                Note = "offen", Done = false, CreatedAt = Utc(1),
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetTimelineAsync("Person", "p1", Leader());

        var entry = Assert.Single(result);
        Assert.Equal(TimelineCategory.Followup, entry.Category);
        Assert.Equal("Wiedervorlage angelegt", entry.Title);
    }

    // ---------- ordering ----------

    [Fact]
    public async Task GetTimelineAsync_OrdersNewestFirst()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.Comments.Add(new Comment { EntityType = "Person", EntityId = "p1", Text = "oldest", CreatedAt = Utc(1) });
            db.Comments.Add(new Comment { EntityType = "Person", EntityId = "p1", Text = "middle", CreatedAt = Utc(5) });
            db.Comments.Add(new Comment { EntityType = "Person", EntityId = "p1", Text = "newest", CreatedAt = Utc(9) });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetTimelineAsync("Person", "p1", Leader());

        Assert.Equal(3, result.Count);
        Assert.True(result[0].Timestamp >= result[1].Timestamp);
        Assert.True(result[1].Timestamp >= result[2].Timestamp);
        Assert.Equal(Utc(9), result[0].Timestamp);
        Assert.Equal(Utc(1), result[2].Timestamp);
    }

    // ---------- person-specific: relation, observation, photo, links ----------

    [Fact]
    public async Task GetTimelineAsync_PersonRelation_Mapped()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            db.People.Add(Seed.Person("p2", "Moritz"));
            db.PersonRelations.Add(new PersonRelation
            {
                PersonAId = "p1", PersonBId = "p2", Type = RelationType.Ally,
                Note = "Kompagnon", CreatedAt = Utc(1),
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetTimelineAsync("Person", "p1", Leader());

        var entry = Assert.Single(result.Where(e => e.Category == TimelineCategory.Relation));
        Assert.StartsWith("Beziehung", entry.Title);
        Assert.Equal("Kompagnon", entry.Detail);
    }

    [Fact]
    public async Task GetTimelineAsync_ObservationAndPhoto_Mapped()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.Observations.Add(new Observation
            {
                PersonId = "p1", Start = Utc(2), Location = "Hafen",
                Sighting = "Übergabe beobachtet",
            });
            // photos surface via the audit fan-out (Person → PersonPhoto), not a direct query
            db.PersonPhotos.Add(new PersonPhoto
            {
                Id = "ph1", PersonId = "p1", OriginalName = "foto.jpg", FileNameSaved = "abc.jpg",
                ContentType = "image/jpeg", CreatedAt = Utc(3),
            });
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = "PersonPhoto", EntityId = "ph1", Action = AuditAction.Created,
                AgentName = "Falcon", Timestamp = Utc(3),
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetTimelineAsync("Person", "p1", Leader());

        var obs = Assert.Single(result.Where(e => e.Category == TimelineCategory.Observation));
        Assert.StartsWith("Observation", obs.Title);
        Assert.Contains("Hafen", obs.Title);
        Assert.Equal("Übergabe beobachtet", obs.Detail);

        var photo = Assert.Single(result.Where(e => e.Category == TimelineCategory.Photo));
        Assert.Equal("Foto hinzugefügt", photo.Title);
        Assert.Equal("Falcon", photo.ActorName);
    }

    [Fact]
    public async Task GetTimelineAsync_Reward_ReachesTheFileOverThreeHops()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.OeffentlicheFahndungen.Add(new OeffentlicheFahndung
            {
                Id = "f1", PersonId = "p1", DisplayName = "Max Mustermann",
                Status = PublicWantedStatus.Gefasst, CreatedAt = Utc(2),
            });
            db.FahndungKopfgeldAnteile.Add(new FahndungKopfgeldAnteil
            {
                Id = "k1", WantedId = "f1", Amount = 50_000m, Status = BountyShareStatus.Ausgezahlt,
                Timestamp = Utc(3), CreatedAt = Utc(3),
            });
            db.Hinweise.Add(new Hinweis
            {
                Id = "h1", CaseNumber = "FIB-H-2026-0001", CitizenProfileId = "profil1", WantedId = "f1",
                Text = "Am Hafen gesehen.", CreatedAt = Utc(3),
            });
            db.HinweisBelohnungen.Add(new HinweisBelohnung
            {
                Id = "b1", ReceiptNumber = "FIB-BEL-2026-0001", TipId = "h1", ShareId = "k1",
                Amount = 50_000m, PaidAt = Utc(4), CreatedAt = Utc(4),
            });
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(HinweisBelohnung), EntityId = "b1", Action = AuditAction.Created,
                AgentName = "Falcon", Timestamp = Utc(4),
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetTimelineAsync("Person", "p1", Leader());

        // without the staged fan-out and the MapAudit arm this would be missing or read as "Akte geändert"
        Assert.Contains(result, e => e.Title == "Belohnung ausgezahlt");
        Assert.DoesNotContain(result, e => e.Title == "Akte geändert");
    }

    [Fact]
    public async Task GetTimelineAsync_PublicWanted_AuditFanOut_ShowsExactlyOneRowPerEvent()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.OeffentlicheFahndungen.Add(new OeffentlicheFahndung
            {
                Id = "f1", PersonId = "p1", DisplayName = "Max Mustermann",
                Status = PublicWantedStatus.Veroeffentlicht, CreatedAt = Utc(2),
            });
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(OeffentlicheFahndung), EntityId = "f1", Action = AuditAction.Created,
                AgentName = "Falcon", Timestamp = Utc(2),
            });
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(OeffentlicheFahndung), EntityId = "f1", Action = AuditAction.Modified,
                AgentName = "Falcon", Timestamp = Utc(3),
                ChangesJson = "{\"Status\":[\"Veroeffentlicht\",\"Zurueckgezogen\"]}",
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetTimelineAsync("Person", "p1", Leader());

        var rows = result.Where(e => e.Title.Contains("Öffentliche Ausschreibung", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, rows.Count);
        // no second, Person-typed manual row: that one would fall through MapAudit and read as "Akte geändert"
        Assert.DoesNotContain(result, e => e.Title == "Akte geändert");
    }

    [Fact]
    public async Task GetTimelineAsync_PublicWanted_OfADeletedNotice_StaysOnTheFile()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.OeffentlicheFahndungen.Add(new OeffentlicheFahndung
            {
                Id = "f1", PersonId = "p1", DisplayName = "Max Mustermann",
                IsDeleted = true, DeletedAt = Utc(4), CreatedAt = Utc(2),
            });
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(OeffentlicheFahndung), EntityId = "f1", Action = AuditAction.Deleted,
                AgentName = "Falcon", Timestamp = Utc(4),
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetTimelineAsync("Person", "p1", Leader());

        Assert.Contains(result, e => e.Title == "Öffentliche Ausschreibung gelöscht");
    }

    [Fact]
    public async Task GetTimelineAsync_PhotoRemoval_AuditFanOut_ShowsRemoval()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            // soft-deleted photo still fanned in via IgnoreQueryFilters → removal is traceable
            db.PersonPhotos.Add(new PersonPhoto
            {
                Id = "ph1", PersonId = "p1", OriginalName = "foto.jpg", FileNameSaved = "abc.jpg",
                ContentType = "image/jpeg", CreatedAt = Utc(1), IsDeleted = true, DeletedAt = Utc(4),
            });
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = "PersonPhoto", EntityId = "ph1", Action = AuditAction.Deleted, Timestamp = Utc(4),
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetTimelineAsync("Person", "p1", Leader());

        var photo = Assert.Single(result.Where(e => e.Category == TimelineCategory.Photo));
        Assert.Equal("Foto entfernt", photo.Title);
    }

    // ---------- meeting fan-out ----------

    [Fact]
    public async Task GetTimelineAsync_MeetingChildren_FannedIn()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Meetings.Add(new Meeting { Id = "mtg1", Title = "Wochenlage", CreatedAt = Utc(1) });
            db.MeetingAgendaItems.Add(new MeetingAgendaItem { Id = "ag1", MeetingId = "mtg1", Title = "TOP 1", CreatedAt = Utc(2) });
            db.MeetingAttendances.Add(new MeetingAttendance { Id = "at1", MeetingId = "mtg1", AgentId = "a1", CreatedAt = Utc(2) });
            db.MeetingSignOffs.Add(new MeetingSignOff { Id = "so1", MeetingId = "mtg1", AgentId = "a2", CreatedAt = Utc(2) });
            db.AuditLogs.Add(new AuditLog { EntityType = "MeetingAgendaItem", EntityId = "ag1", Action = AuditAction.Created, Timestamp = Utc(2) });
            db.AuditLogs.Add(new AuditLog { EntityType = "MeetingAttendance", EntityId = "at1", Action = AuditAction.Modified, Timestamp = Utc(3) });
            db.AuditLogs.Add(new AuditLog { EntityType = "MeetingSignOff", EntityId = "so1", Action = AuditAction.Created, Timestamp = Utc(3) });
            db.AuditLogs.Add(new AuditLog { EntityType = "MeetingSignOff", EntityId = "so1", Action = AuditAction.Deleted, Timestamp = Utc(4) });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetTimelineAsync("Meeting", "mtg1", Leader());

        Assert.Single(result.Where(e => e.Category == TimelineCategory.Agenda && e.Title == "Tagesordnungspunkt angelegt"));
        Assert.Single(result.Where(e => e.Category == TimelineCategory.Attendance && e.Title == "Anwesenheit geändert"));
        Assert.Single(result.Where(e => e.Category == TimelineCategory.SignOff && e.Title == "Abmeldung eingetragen"));
        Assert.Single(result.Where(e => e.Category == TimelineCategory.SignOff && e.Title == "Abmeldung entfernt"));
    }

    // ---------- custom-field fan-out ----------

    [Fact]
    public async Task GetTimelineAsync_CustomFieldValue_FannedIn()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.CustomFieldValues.Add(new CustomFieldValue { Id = "cfv1", EntityType = "Person", EntityId = "p1", CustomFieldDefinitionId = "d1", Value = "42" });
            db.AuditLogs.Add(new AuditLog { EntityType = "CustomFieldValue", EntityId = "cfv1", Action = AuditAction.Modified, Timestamp = Utc(2) });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetTimelineAsync("Person", "p1", Leader());

        var entry = Assert.Single(result.Where(e => e.Category == TimelineCategory.Change));
        Assert.Equal("Sonderfeld geändert", entry.Title);
    }

    [Fact]
    public async Task GetTimelineAsync_TakeoverLink_NamesTheTipByCaseNumber()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            db.Hinweise.Add(new Hinweis
            {
                Id = "h1", CaseNumber = "FIB-H-2026-0001", CitizenProfileId = "profil1",
                Text = "Am Hafen gesehen.", CreatedAt = Utc(1),
            });
            db.Links.Add(new Link
            {
                SourceType = "Hinweis", SourceId = "h1", TargetType = "Person", TargetId = "p1",
                Label = "Übernahme aus Bürgerhinweis FIB-H-2026-0001", Automatic = false, CreatedAt = Utc(2),
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetTimelineAsync("Person", "p1", Leader());

        // RecordsReference resolves the counterpart; without its arm the entry would read "Akte"
        var entry = Assert.Single(result.Where(e => e.Category == TimelineCategory.Link));
        Assert.Contains("Bürgerhinweis FIB-H-2026-0001", entry.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetTimelineAsync_Link_Mapped_AndDeletedEmitsRemoval()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            db.People.Add(Seed.Person("p2", "Moritz"));
            db.Links.Add(new Link
            {
                SourceType = "Person", SourceId = "p1", TargetType = "Person", TargetId = "p2",
                Label = "Komplize", Automatic = false, CreatedAt = Utc(1),
                IsDeleted = true, DeletedAt = Utc(4),
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetTimelineAsync("Person", "p1", Leader());

        var links = result.Where(e => e.Category == TimelineCategory.Link).ToList();
        // one "linked" entry + one "removed" entry for the soft-deleted link
        Assert.Equal(2, links.Count);
        Assert.Contains(links, e => e.Title.StartsWith("Verknüpft mit", StringComparison.Ordinal));
        Assert.Contains(links, e => e.Title.Contains("entfernt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetTimelineAsync_AutomaticLink_Excluded()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            db.People.Add(Seed.Person("p2", "Moritz"));
            db.Links.Add(new Link
            {
                SourceType = "Person", SourceId = "p1", TargetType = "Person", TargetId = "p2",
                Automatic = true, CreatedAt = Utc(1),
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetTimelineAsync("Person", "p1", Leader());

        // automatic links are never shown on the timeline
        Assert.Empty(result);
    }

    // ---------- faction-specific: activities + audit fan-out ----------

    [Fact]
    public async Task GetTimelineAsync_FactionActivity_Mapped()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction("f1"));
            var activity = new AgentActivity
            {
                Id = "act1", Title = "Streife", Kind = "Patrouille",
                ActivityDate = Utc(2), ContentHtml = "<p>Alles ruhig</p>", CreatedById = "author",
            };
            db.AgentActivities.Add(activity);
            db.AgentActivityLinks.Add(new AgentActivityLink
            {
                AgentActivityId = "act1", TargetType = "Faction", TargetId = "f1",
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetTimelineAsync("Faction", "f1", Leader());

        var entry = Assert.Single(result.Where(e => e.Category == TimelineCategory.Activity));
        Assert.Contains("Aktivität", entry.Title);
        Assert.Contains("Streife", entry.Title);
        // HTML stripped to plain snippet
        Assert.Equal("Alles ruhig", entry.Detail);
    }

    [Fact]
    public async Task GetTimelineAsync_FactionMemberAudit_MappedAsMembership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction("f1"));
            db.People.Add(Seed.Person("pm", "Member"));
            var member = new FactionMember { Id = "m1", FactionId = "f1", PersonId = "pm", CreatedAt = Utc(1) };
            db.FactionMembers.Add(member);
            // audit row on the child member entity, discovered via the faction audit fan-out
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = "FactionMember", EntityId = "m1", Action = AuditAction.Created,
                AgentName = "Falcon", Timestamp = Utc(2),
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetTimelineAsync("Faction", "f1", Leader());

        var entry = Assert.Single(result.Where(e => e.Category == TimelineCategory.Membership));
        Assert.Equal("Mitglied aufgenommen", entry.Title);
        Assert.Equal("Falcon", entry.ActorName);
    }

    // ---------- new since the last visit ----------

    [Fact]
    public async Task GetTimelineAsync_Entries_CarryTheActingAgentsId()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.Comments.Add(new Comment
            {
                EntityType = "Person", EntityId = "p1", Text = "Vermerk", AuthorName = "Adler",
                CreatedById = "a1", CreatedAt = Utc(1),
            });
            db.ClassificationHistory.Add(new ClassificationHistory
            {
                EntityType = "Person", EntityId = "p1", Value = Classification.SuspicionCase,
                Timestamp = Utc(2), AgentName = "Bussard", AgentId = "a2",
            });
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = "Person", EntityId = "p1", Action = AuditAction.Modified,
                AgentName = "Condor", AgentId = "a3", Timestamp = Utc(3),
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetTimelineAsync("Person", "p1", Leader());

        Assert.Equal("a1", Assert.Single(result, e => e.Category == TimelineCategory.Comment).ActorId);
        Assert.Equal("a2", Assert.Single(result, e => e.Category == TimelineCategory.Classification).ActorId);
        Assert.Equal("a3", Assert.Single(result, e => e.Category == TimelineCategory.Change).ActorId);
    }

    [Fact]
    public async Task GetTimelineAsync_Tip_CarriesNeitherTheSubmittersNameNorHisId()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.OeffentlicheFahndungen.Add(new OeffentlicheFahndung
            {
                Id = "f1", PersonId = "p1", DisplayName = "Max Mustermann",
                Status = PublicWantedStatus.Veroeffentlicht, CreatedAt = Utc(1),
            });
            db.Hinweise.Add(new Hinweis
            {
                Id = "h1", CaseNumber = "FIB-H-2026-0001", CitizenProfileId = "profil1", WantedId = "f1",
                Text = "Am Hafen gesehen.", CreatedAt = Utc(2),
            });
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(Hinweis), EntityId = "h1", Action = AuditAction.Created,
                AgentName = "Zivilist", AgentId = "civil-1", Timestamp = Utc(2),
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetTimelineAsync("Person", "p1", Leader());

        var tip = Assert.Single(result, e => e.Title == "Bürgerhinweis eingegangen");
        Assert.Null(tip.ActorName);
        Assert.Null(tip.ActorId);
    }

    [Fact]
    public async Task GetNewSinceAsync_LeavesOutTheViewersOwnAndEverythingBeforeTheVisit()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.Comments.Add(new Comment { Id = "c-old", EntityType = "Person", EntityId = "p1", Text = "alt", CreatedById = "other", CreatedAt = Utc(1) });
            db.Comments.Add(new Comment { Id = "c-mine", EntityType = "Person", EntityId = "p1", Text = "meiner", CreatedById = "lead", CreatedAt = Utc(5) });
            db.Comments.Add(new Comment { Id = "c-new", EntityType = "Person", EntityId = "p1", Text = "neu", CreatedById = "other", CreatedAt = Utc(5) });
            db.AuditLogs.Add(new AuditLog { EntityType = "Person", EntityId = "p1", Action = AuditAction.Modified, AgentId = "lead", Timestamp = Utc(6) });
            db.AuditLogs.Add(new AuditLog { EntityType = "Person", EntityId = "p1", Action = AuditAction.Modified, AgentId = "other", Timestamp = Utc(6) });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetNewSinceAsync("Person", "p1", Leader(), Utc(3));

        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.Equal("other", e.ActorId));
        Assert.Contains(result, e => e.Category == TimelineCategory.Comment && e.Detail == "neu");
        Assert.Contains(result, e => e.Category == TimelineCategory.Change);
    }

    [Fact]
    public async Task GetNewSinceAsync_JudgesAnObservationByWhenItWasEntered()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            // happened before the visit, entered after it: new
            db.Observations.Add(new Observation
            {
                PersonId = "p1", Start = Utc(1), Location = "Hafen", CreatedById = "other", CreatedAt = Utc(5),
            });
            // dated after the visit, but already there when the viewer looked: not new
            db.Observations.Add(new Observation
            {
                PersonId = "p1", Start = Utc(6), Location = "Pier", CreatedById = "other", CreatedAt = Utc(2),
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetNewSinceAsync("Person", "p1", Leader(), Utc(3));

        var entry = Assert.Single(result);
        Assert.Contains("Hafen", entry.Title);
        Assert.Equal(Utc(1), entry.Timestamp);
    }

    [Fact]
    public async Task GetNewSinceAsync_ClassifiedPerson_Junior_ReturnsEmpty()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", configure: p => p.IsClassified = true));
            db.Comments.Add(new Comment { EntityType = "Person", EntityId = "p1", Text = "geheim", CreatedById = "other", CreatedAt = Utc(5) });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var result = await svc.GetNewSinceAsync("Person", "p1", Junior(), Utc(3));

        Assert.Empty(result);
    }
}
