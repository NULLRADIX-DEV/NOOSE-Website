using System.Security.Claims;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Cases;
using NOOSE_Website.Models.CounterIntel;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Factions;
using NOOSE_Website.Models.Groups;
using NOOSE_Website.Models.Operations;
using NOOSE_Website.Models.Parties;
using NOOSE_Website.Models.People;
using NOOSE_Website.Models.Taskforces;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Critical record changes need a reason, logged as its own row; non-writers fail before anything is written.</summary>
public sealed class CriticalChangeTests
{
    private const string Id = "r1";
    private const string Reason = "Dublette von NOOSE-P-2026-0007";

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).WithCodename("Falcon").Build();

    // all three carry Director, so only the write guard can stop them
    private static ClaimsPrincipal NonWriter(string kind) => kind switch
    {
        "reader" => ClaimsPrincipalBuilder.Agent("ro").WithRank(Rank.Director).AsTeamLead().Build(),
        "demo" => ClaimsPrincipalBuilder.Agent("demo").WithRank(Rank.Director).AsDemo().Build(),
        _ => ClaimsPrincipalBuilder.Agent("partner").WithRank(Rank.Director)
            .AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build(),
    };

    private static readonly string[] AllTypes =
    [
        nameof(Person), nameof(Faction), nameof(PersonGroup), nameof(Party), nameof(Case), nameof(Operation),
        nameof(Taskforce),
    ];

    private static readonly string[] NonWriterKinds = ["reader", "demo", "partner"];

    public static TheoryData<string> RecordTypes => new(AllTypes);

    // taskforces carry no classification
    public static TheoryData<string> ClassifiedTypes => new(AllTypes.Where(t => t != nameof(Taskforce)));

    public static TheoryData<string> NonWriters => new(NonWriterKinds);

    public static TheoryData<string, string> NonWritersByType()
    {
        var data = new TheoryData<string, string>();
        foreach (var type in AllTypes)
        {
            foreach (var kind in NonWriterKinds)
            {
                data.Add(type, kind);
            }
        }
        return data;
    }

    // ---------- rules ----------

    [Theory]
    [InlineData(Classification.SuspicionCase, Classification.ReviewCase, true)]
    [InlineData(Classification.SecuredStateThreatening, Classification.SuspicionCase, true)]
    [InlineData(Classification.ReviewCase, Classification.Unknown, true)]
    [InlineData(Classification.ReviewCase, Classification.SuspicionCase, false)]
    [InlineData(Classification.Unknown, Classification.SecuredStateThreatening, false)]
    [InlineData(Classification.SuspicionCase, Classification.SuspicionCase, false)]
    public void Downgrade_is_every_step_down_including_back_to_unknown(Classification old, Classification @new, bool expected)
        => Assert.Equal(expected, CriticalChange.IsDowngrade(old, @new));

    [Theory]
    [InlineData(DocumentClassification.Leadership, DocumentClassification.None, true)]
    [InlineData(DocumentClassification.Tru, DocumentClassification.None, true)]
    [InlineData(DocumentClassification.Hrb, DocumentClassification.None, true)]
    [InlineData(DocumentClassification.Leadership, DocumentClassification.Tru, true)]
    [InlineData(DocumentClassification.Tru, DocumentClassification.Hrb, true)]
    [InlineData(DocumentClassification.Tru, DocumentClassification.Leadership, false)]
    [InlineData(DocumentClassification.Hrb, DocumentClassification.Leadership, false)]
    [InlineData(DocumentClassification.None, DocumentClassification.Tru, false)]
    [InlineData(DocumentClassification.Tru, DocumentClassification.Tru, false)]
    public void Loosening_is_every_move_that_lets_someone_new_read(
        DocumentClassification old, DocumentClassification @new, bool expected)
        => Assert.Equal(expected, CriticalChange.LoosensSecrecy(old, @new));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_missing_reason_is_refused(string? reason)
        => Assert.Throws<InvalidOperationException>(() => CriticalChange.RequireReason(reason, "Das Löschen einer Akte"));

    [Fact]
    public void A_reason_is_trimmed_and_capped()
    {
        Assert.Equal("Grund", CriticalChange.RequireReason("  Grund \n", "x"));
        Assert.Equal(CriticalChange.MaxReasonLength,
            CriticalChange.RequireReason(new string('a', CriticalChange.MaxReasonLength), "x").Length);
        Assert.Throws<InvalidOperationException>(
            () => CriticalChange.RequireReason(new string('a', CriticalChange.MaxReasonLength + 1), "x"));
    }

    [Fact]
    public void A_reason_row_has_its_own_label_and_timeline_title()
    {
        Assert.Equal("Begründung", AuditActionDisplay.Name(AuditAction.Justified));
        Assert.Contains(AuditAction.Justified, AuditActionDisplay.All);
        var (_, title) = TimelineDisplay.MapAudit(nameof(Person), AuditAction.Justified);
        Assert.Equal("Begründung", title);
    }

    [Fact]
    public void A_loosening_row_reads_as_levels_and_reason()
    {
        var row = CriticalChange.Row(nameof(Person), Id, CriticalChange.SecrecyField, Reason, Leader(),
            CriticalChange.SecrecyLevelField, DocumentClassification.Tru, DocumentClassification.None);

        var changes = AuditDisplay.Parse(row.ChangesJson);

        Assert.Equal(AuditAction.Justified, row.Action);
        Assert.Equal("lead", row.AgentId);
        Assert.Collection(changes,
            c =>
            {
                Assert.Equal("VS-Stufe", c.Field);
                Assert.Equal(DocumentClassificationDisplay.Label(DocumentClassification.Tru), c.Alt);
                Assert.Equal(DocumentClassificationDisplay.Label(DocumentClassification.None), c.New);
            },
            c =>
            {
                Assert.Equal(CriticalChange.SecrecyField, c.Field);
                Assert.Equal(Reason, c.New);
            });
    }

    // ---------- deleting ----------

    [Theory]
    [MemberData(nameof(RecordTypes))]
    public async Task Deleting_without_a_reason_is_refused_and_leaves_the_record(string type)
    {
        using var ctx = new SqliteTestContext();
        SeedRecord(ctx, type);

        await Assert.ThrowsAsync<InvalidOperationException>(() => DeleteAsync(ctx, type, "   ", Leader()));

        Assert.True(Exists(ctx, type));
        Assert.Empty(AuditRows(ctx));
    }

    [Theory]
    [MemberData(nameof(RecordTypes))]
    public async Task Deleting_with_a_reason_logs_it_as_its_own_row(string type)
    {
        using var ctx = new SqliteTestContext();
        SeedRecord(ctx, type);

        await DeleteAsync(ctx, type, $"  {Reason}  ", Leader());

        Assert.False(Exists(ctx, type));
        var row = Assert.Single(ReasonRows(ctx, type));
        Assert.Equal("lead", row.AgentId);
        var change = Assert.Single(AuditDisplay.Parse(row.ChangesJson));
        Assert.Equal(CriticalChange.DeletionField, change.Field);
        Assert.Equal(Reason, change.New);
    }

    [Theory]
    [MemberData(nameof(NonWritersByType))]
    public async Task Deleting_as_a_non_writer_fails_before_anything_is_written(string type, string kind)
    {
        using var ctx = new SqliteTestContext();
        SeedRecord(ctx, type);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => DeleteAsync(ctx, type, Reason, NonWriter(kind)));

        Assert.True(Exists(ctx, type));
        Assert.Empty(AuditRows(ctx));
    }

    [Theory]
    [MemberData(nameof(RecordTypes))]
    public async Task Restoring_as_read_only_supervision_fails_before_anything_is_written(string type)
    {
        using var ctx = new SqliteTestContext();
        SeedRecord(ctx, type, deleted: true);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => RestoreAsync(ctx, type, NonWriter("reader")));

        Assert.False(Exists(ctx, type));
    }

    [Fact]
    public async Task A_merge_names_the_surviving_file_as_the_reason_on_the_source()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("src", "Max Muster"));
            db.People.Add(Seed.Person("dst", "Max Mustermann", p => p.CaseNumber = "NOOSE-P-2026-0042"));
            db.SaveChanges();
        }

        await new PersonMergeService(ctx.Factory, Substitute.For<IPublicWantedService>())
            .MergeAsync("src", "dst", Leader());

        using var read = ctx.NewContext();
        var row = Assert.Single(read.AuditLogs.Where(a => a.EntityId == "src" && a.Action == AuditAction.Justified));
        var change = Assert.Single(AuditDisplay.Parse(row.ChangesJson));
        Assert.Equal(CriticalChange.DeletionField, change.Field);
        Assert.Contains("NOOSE-P-2026-0042", change.New);
    }

    // ---------- downgrading ----------

    [Theory]
    [MemberData(nameof(ClassifiedTypes))]
    public async Task Downgrading_without_a_reason_is_refused_and_changes_nothing(string type)
    {
        using var ctx = new SqliteTestContext();
        SeedRecord(ctx, type, classification: Classification.SuspicionCase);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ClassifyAsync(ctx, type, Classification.ReviewCase, null, Leader()));

        Assert.Equal(Classification.SuspicionCase, ClassificationOf(ctx, type));
        using var db = ctx.NewContext();
        Assert.Empty(db.ClassificationHistory.ToList());
        Assert.Empty(AuditRows(ctx));
    }

    [Theory]
    [MemberData(nameof(ClassifiedTypes))]
    public async Task Downgrading_with_a_reason_logs_the_step_and_the_reason(string type)
    {
        using var ctx = new SqliteTestContext();
        SeedRecord(ctx, type, classification: Classification.SuspicionCase);

        await ClassifyAsync(ctx, type, Classification.ReviewCase, Reason, Leader());

        Assert.Equal(Classification.ReviewCase, ClassificationOf(ctx, type));
        var row = Assert.Single(ReasonRows(ctx, type));
        Assert.Collection(AuditDisplay.Parse(row.ChangesJson),
            c =>
            {
                Assert.Equal("Einstufung", c.Field);
                Assert.Equal(ClassificationDisplay.Name(Classification.SuspicionCase), c.Alt);
                Assert.Equal(ClassificationDisplay.Name(Classification.ReviewCase), c.New);
            },
            c =>
            {
                Assert.Equal(CriticalChange.DowngradeField, c.Field);
                Assert.Equal(Reason, c.New);
            });
        // the classification history keeps its own copy for the Einstufung section
        using var db = ctx.NewContext();
        Assert.Equal(Reason, Assert.Single(db.ClassificationHistory.ToList()).Justification);
    }

    [Fact]
    public async Task Downgrading_back_to_unknown_needs_a_reason_too()
    {
        using var ctx = new SqliteTestContext();
        SeedRecord(ctx, nameof(Person), classification: Classification.ReviewCase);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ClassifyAsync(ctx, nameof(Person), Classification.Unknown, " ", Leader()));

        Assert.Equal(Classification.ReviewCase, ClassificationOf(ctx, nameof(Person)));
    }

    [Fact]
    public async Task Upgrading_needs_no_reason_and_logs_none()
    {
        using var ctx = new SqliteTestContext();
        SeedRecord(ctx, nameof(Person), classification: Classification.ReviewCase);

        await ClassifyAsync(ctx, nameof(Person), Classification.SuspicionCase, null, Leader());

        Assert.Equal(Classification.SuspicionCase, ClassificationOf(ctx, nameof(Person)));
        Assert.Empty(ReasonRows(ctx, nameof(Person)));
    }

    [Theory]
    [MemberData(nameof(NonWriters))]
    public async Task Classifying_as_a_non_writer_fails_before_anything_is_written(string kind)
    {
        using var ctx = new SqliteTestContext();
        SeedRecord(ctx, nameof(Person), classification: Classification.SuspicionCase);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => ClassifyAsync(ctx, nameof(Person), Classification.ReviewCase, Reason, NonWriter(kind)));

        Assert.Equal(Classification.SuspicionCase, ClassificationOf(ctx, nameof(Person)));
        Assert.Empty(AuditRows(ctx));
    }

    // ---------- loosening a Verschlusssache ----------

    [Theory]
    [MemberData(nameof(RecordTypes))]
    public async Task Lifting_a_verschlusssache_without_a_reason_is_refused(string type)
    {
        using var ctx = new SqliteTestContext();
        SeedRecord(ctx, type, secrecy: DocumentClassification.Leadership);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RefreshAsync(ctx, type, DocumentClassification.None, null, Leader()));

        Assert.Equal(DocumentClassification.Leadership, SecrecyOf(ctx, type));
        Assert.Empty(AuditRows(ctx));
    }

    [Theory]
    [MemberData(nameof(RecordTypes))]
    public async Task Lifting_a_verschlusssache_with_a_reason_logs_both_levels(string type)
    {
        using var ctx = new SqliteTestContext();
        SeedRecord(ctx, type, secrecy: DocumentClassification.Leadership);

        await RefreshAsync(ctx, type, DocumentClassification.None, Reason, Leader());

        Assert.Equal(DocumentClassification.None, SecrecyOf(ctx, type));
        var row = Assert.Single(ReasonRows(ctx, type));
        Assert.Collection(AuditDisplay.Parse(row.ChangesJson),
            c =>
            {
                Assert.Equal("VS-Stufe", c.Field);
                Assert.Equal(DocumentClassificationDisplay.Label(DocumentClassification.Leadership), c.Alt);
                Assert.Equal(DocumentClassificationDisplay.Label(DocumentClassification.None), c.New);
            },
            c => Assert.Equal(CriticalChange.SecrecyField, c.Field));
    }

    [Fact]
    public async Task Opening_a_leadership_file_to_tru_needs_a_reason()
    {
        using var ctx = new SqliteTestContext();
        SeedRecord(ctx, nameof(Faction), secrecy: DocumentClassification.Leadership);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => RefreshAsync(ctx, nameof(Faction), DocumentClassification.Tru, "", Leader()));

        Assert.Equal(DocumentClassification.Leadership, SecrecyOf(ctx, nameof(Faction)));
    }

    [Theory]
    [InlineData(DocumentClassification.Tru, DocumentClassification.Leadership)]
    [InlineData(DocumentClassification.None, DocumentClassification.Hrb)]
    [InlineData(DocumentClassification.Hrb, DocumentClassification.Hrb)]
    public async Task Tightening_or_keeping_the_level_needs_no_reason(
        DocumentClassification old, DocumentClassification @new)
    {
        using var ctx = new SqliteTestContext();
        SeedRecord(ctx, nameof(Person), secrecy: old);

        await RefreshAsync(ctx, nameof(Person), @new, null, Leader());

        Assert.Equal(@new, SecrecyOf(ctx, nameof(Person)));
        Assert.Empty(ReasonRows(ctx, nameof(Person)));
    }

    [Fact]
    public async Task A_reason_sent_along_with_a_tightening_is_not_logged()
    {
        using var ctx = new SqliteTestContext();
        SeedRecord(ctx, nameof(Taskforce));

        await RefreshAsync(ctx, nameof(Taskforce), DocumentClassification.Leadership, Reason, Leader());

        Assert.Equal(DocumentClassification.Leadership, SecrecyOf(ctx, nameof(Taskforce)));
        Assert.Empty(ReasonRows(ctx, nameof(Taskforce)));
    }

    [Fact]
    public async Task Merging_as_read_only_supervision_fails_before_anything_is_written()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("src", "Max Muster"));
            db.People.Add(Seed.Person("dst", "Max Mustermann"));
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            new PersonMergeService(ctx.Factory, Substitute.For<IPublicWantedService>())
                .MergeAsync("src", "dst", NonWriter("reader")));

        using var read = ctx.NewContext();
        Assert.Equal(2, read.People.Count());
        Assert.Empty(AuditRows(ctx));
    }

    [Theory]
    [MemberData(nameof(NonWriters))]
    public async Task Editing_as_a_non_writer_fails_before_anything_is_written(string kind)
    {
        using var ctx = new SqliteTestContext();
        SeedRecord(ctx, nameof(Case), secrecy: DocumentClassification.Leadership);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => RefreshAsync(ctx, nameof(Case), DocumentClassification.None, Reason, NonWriter(kind)));

        Assert.Equal(DocumentClassification.Leadership, SecrecyOf(ctx, nameof(Case)));
        Assert.Empty(AuditRows(ctx));
    }

    // ---------- lifting the wanted flag ----------

    [Fact]
    public async Task Lifting_the_wanted_flag_without_a_reason_is_refused()
    {
        using var ctx = new SqliteTestContext();
        SeedWanted(ctx, wanted: true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => People(ctx).WantedSetAsync(Id, false, null, Leader()));

        using var db = ctx.NewContext();
        Assert.True(db.People.Single(p => p.Id == Id).IsWanted);
        Assert.Empty(AuditRows(ctx));
    }

    [Fact]
    public async Task Lifting_the_wanted_flag_with_a_reason_logs_it()
    {
        using var ctx = new SqliteTestContext();
        SeedWanted(ctx, wanted: true);

        await People(ctx).WantedSetAsync(Id, false, Reason, Leader());

        using var db = ctx.NewContext();
        var person = db.People.Single(p => p.Id == Id);
        Assert.False(person.IsWanted);
        Assert.Null(person.WantedReason);
        var row = Assert.Single(ReasonRows(ctx, nameof(Person)));
        Assert.Collection(AuditDisplay.Parse(row.ChangesJson),
            c => Assert.Equal(("Zur Fahndung", "Ja", "Nein"), (c.Field, c.Alt, c.New)),
            c => Assert.Equal((CriticalChange.WantedLiftField, Reason), (c.Field, c.New)));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task Flagging_or_a_no_op_needs_no_lift_reason(bool wasWanted, bool wanted)
    {
        using var ctx = new SqliteTestContext();
        SeedWanted(ctx, wasWanted);

        await People(ctx).WantedSetAsync(Id, wanted, null, Leader());

        using var db = ctx.NewContext();
        Assert.Equal(wanted, db.People.Single(p => p.Id == Id).IsWanted);
        Assert.Empty(ReasonRows(ctx, nameof(Person)));
    }

    [Theory]
    [MemberData(nameof(NonWriters))]
    public async Task Lifting_the_wanted_flag_as_a_non_writer_fails_first(string kind)
    {
        using var ctx = new SqliteTestContext();
        SeedWanted(ctx, wanted: true);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => People(ctx).WantedSetAsync(Id, false, Reason, NonWriter(kind)));

        Assert.Empty(AuditRows(ctx));
    }

    // ---------- where the row shows up ----------

    [Fact]
    public async Task The_timeline_shows_the_reason_internally_but_not_to_a_partner()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(Id));
            db.PartnerShares.Add(new PartnerShare
            {
                EntityType = nameof(Person), EntityId = Id, Agency = PartnerAgency.LSPD, IncludesChildren = true,
            });
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(Person), EntityId = Id, Action = AuditAction.Modified, AgentName = "Falcon",
                Timestamp = DateTime.UtcNow, ChangesJson = """{"Classification":[2,1]}""",
            });
            db.AuditLogs.Add(CriticalChange.Row(nameof(Person), Id, CriticalChange.DowngradeField, Reason, Leader(),
                nameof(Classification), Classification.SuspicionCase, Classification.ReviewCase));
            db.SaveChanges();
        }
        var timeline = new TimelineService(ctx.Factory);
        var partner = ClaimsPrincipalBuilder.Agent("p").AsPartner(PartnerAgency.LSPD, PartnerRank.Member).Build();

        var internalView = await timeline.GetTimelineAsync(nameof(Person), Id, Leader());
        var partnerView = await timeline.GetTimelineAsync(nameof(Person), Id, partner);

        var entry = Assert.Single(internalView, e => e.Title == "Begründung");
        Assert.Contains(entry.Changes!, c => c.Field == CriticalChange.DowngradeField && c.New == Reason);
        Assert.Contains(partnerView, e => e.Title == "Akte geändert");
        Assert.DoesNotContain(partnerView, e => e.Title == "Begründung");
    }

    [Fact]
    public async Task Counter_intelligence_does_not_count_a_reason_as_a_second_write()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("lead"));
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(Person), EntityId = Id, Action = AuditAction.Deleted, AgentId = "lead",
                AgentName = "Falcon", Timestamp = DateTime.UtcNow,
            });
            db.AuditLogs.Add(CriticalChange.Row(nameof(Person), Id, CriticalChange.DeletionField, Reason, Leader()));
            db.SaveChanges();
        }

        using var read = ctx.NewContext();
        var events = await CounterIntelEventLoader.LoadAsync(read,
        [
            new CounterIntelRuleDefinition
            {
                WindowDays = 30, Threshold = 1,
                Actions = [CounterIntelActionKind.Deleted, CounterIntelActionKind.Restored],
            },
        ]);

        var only = Assert.Single(events);
        Assert.Equal(CounterIntelActionKind.Deleted, only.Action);
    }

    // ---------- harness ----------

    private static PersonService People(SqliteTestContext ctx)
        => new(ctx.Factory, Substitute.For<IFileStorageService>(), Substitute.For<IProfileSuggestionService>(),
            CaseNo(), Substitute.For<IThreatScoreService>(), Substitute.For<INotificationService>(),
            Substitute.For<IPublicWantedService>());

    private static FactionService Factions(SqliteTestContext ctx)
        => new(ctx.Factory, CaseNo(), Substitute.For<IProfileSuggestionService>(), Substitute.For<IPersonService>(),
            Substitute.For<IFactionPhotoStorageService>(), Substitute.For<IThreatScoreService>(),
            Substitute.For<INotificationService>(), Substitute.For<IPublicFactionProfileService>());

    private static PersonGroupService Groups(SqliteTestContext ctx)
        => new(ctx.Factory, CaseNo(), Substitute.For<IPersonService>(), Substitute.For<IThreatScoreService>(),
            Substitute.For<INotificationService>(), Substitute.For<IPersonGroupPhotoStorageService>());

    private static PartyService Parties(SqliteTestContext ctx)
        => new(ctx.Factory, CaseNo(), Substitute.For<IProfileSuggestionService>(), Substitute.For<IPersonService>(),
            Substitute.For<IThreatScoreService>(), Substitute.For<INotificationService>(),
            Substitute.For<IPartyPhotoStorageService>());

    private static CaseService Cases(SqliteTestContext ctx)
        => new(ctx.Factory, CaseNo(), Substitute.For<IProfileSuggestionService>(), Substitute.For<INotificationService>());

    private static OperationService Operations(SqliteTestContext ctx)
        => new(ctx.Factory, CaseNo(), Substitute.For<IProfileSuggestionService>(), Substitute.For<INotificationService>());

    private static TaskforceService Taskforces(SqliteTestContext ctx)
        => new(ctx.Factory, CaseNo(), Substitute.For<INotificationService>());

    private static ICaseNumberService CaseNo()
    {
        var caseNo = Substitute.For<ICaseNumberService>();
        caseNo.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("NOOSE-X-2026-0001");
        return caseNo;
    }

    private static void SeedRecord(SqliteTestContext ctx, string type,
        Classification classification = Classification.Unknown,
        DocumentClassification secrecy = DocumentClassification.None, bool deleted = false)
    {
        using var db = ctx.NewContext();
        var number = $"NOOSE-X-2026-{type}";
        switch (type)
        {
            case nameof(Person):
                db.People.Add(Seed.Person(Id, configure: p =>
                {
                    p.Classification = classification;
                    p.SecrecyLevel = secrecy;
                    p.IsDeleted = deleted;
                }));
                break;
            case nameof(Faction):
                db.Factions.Add(Seed.Faction(Id, configure: f =>
                {
                    f.Classification = classification;
                    f.SecrecyLevel = secrecy;
                    f.IsDeleted = deleted;
                }));
                break;
            case nameof(PersonGroup):
                db.PersonGroups.Add(new PersonGroup
                {
                    Id = Id, Name = "Die Bande", CaseNumber = number, Classification = classification,
                    SecrecyLevel = secrecy, IsDeleted = deleted,
                });
                break;
            case nameof(Party):
                db.Parties.Add(new Party
                {
                    Id = Id, Name = "Grove Street", CaseNumber = number, Classification = classification,
                    SecrecyLevel = secrecy, IsDeleted = deleted,
                });
                break;
            case nameof(Case):
                db.Cases.Add(Seed.Case(Id, configure: c =>
                {
                    c.Classification = classification;
                    c.SecrecyLevel = secrecy;
                    c.IsDeleted = deleted;
                }));
                break;
            case nameof(Operation):
                db.Operations.Add(new Operation
                {
                    Id = Id, Title = "Operation", CaseNumber = number, Classification = classification,
                    SecrecyLevel = secrecy, IsDeleted = deleted,
                });
                break;
            default:
                db.Taskforces.Add(new Taskforce
                {
                    Id = Id, Name = "Alpha TF", CaseNumber = number, Status = TaskforceStatus.Approved,
                    IsClassified = secrecy != DocumentClassification.None, IsDeleted = deleted,
                });
                break;
        }
        db.SaveChanges();
    }

    private static void SeedWanted(SqliteTestContext ctx, bool wanted)
    {
        using var db = ctx.NewContext();
        db.People.Add(Seed.Person(Id, configure: p =>
        {
            p.IsWanted = wanted;
            p.WantedReason = wanted ? "Bewaffneter Raub" : null;
        }));
        db.SaveChanges();
    }

    private static Task DeleteAsync(SqliteTestContext ctx, string type, string reason, ClaimsPrincipal actor)
        => type switch
        {
            nameof(Person) => People(ctx).DeleteAsync(Id, reason, actor),
            nameof(Faction) => Factions(ctx).DeleteAsync(Id, reason, actor),
            nameof(PersonGroup) => Groups(ctx).DeleteAsync(Id, reason, actor),
            nameof(Party) => Parties(ctx).DeleteAsync(Id, reason, actor),
            nameof(Case) => Cases(ctx).DeleteAsync(Id, reason, actor),
            nameof(Operation) => Operations(ctx).DeleteAsync(Id, reason, actor),
            _ => Taskforces(ctx).DeleteAsync(Id, reason, actor),
        };

    private static Task RestoreAsync(SqliteTestContext ctx, string type, ClaimsPrincipal actor)
        => type switch
        {
            nameof(Person) => People(ctx).RestoreAsync(Id, actor),
            nameof(Faction) => Factions(ctx).RestoreAsync(Id, actor),
            nameof(PersonGroup) => Groups(ctx).RestoreAsync(Id, actor),
            nameof(Party) => Parties(ctx).RestoreAsync(Id, actor),
            nameof(Case) => Cases(ctx).RestoreAsync(Id, actor),
            nameof(Operation) => Operations(ctx).RestoreAsync(Id, actor),
            _ => Taskforces(ctx).RestoreAsync(Id, actor),
        };

    private static Task ClassifyAsync(SqliteTestContext ctx, string type, Classification @new, string? reason,
        ClaimsPrincipal actor)
        => type switch
        {
            nameof(Person) => People(ctx).ClassificationSetAsync(Id, @new, reason, actor),
            nameof(Faction) => Factions(ctx).ClassificationSetAsync(Id, @new, reason, actor),
            nameof(PersonGroup) => Groups(ctx).ClassificationSetAsync(Id, @new, reason, actor),
            nameof(Party) => Parties(ctx).ClassificationSetAsync(Id, @new, reason, actor),
            nameof(Case) => Cases(ctx).ClassificationSetAsync(Id, @new, reason, actor),
            _ => Operations(ctx).ClassificationSetAsync(Id, @new, reason, actor),
        };

    private static Task RefreshAsync(SqliteTestContext ctx, string type, DocumentClassification level, string? reason,
        ClaimsPrincipal actor)
        => type switch
        {
            nameof(Person) => People(ctx).RefreshAsync(Id,
                new PersonInput { Name = "Max Mustermann", SecrecyLevel = level, SecrecyReason = reason }, actor),
            nameof(Faction) => Factions(ctx).RefreshAsync(Id,
                new FactionInput { Name = "Ballas", SecrecyLevel = level, SecrecyReason = reason }, actor),
            nameof(PersonGroup) => Groups(ctx).RefreshAsync(Id,
                new PersonGroupInput { Name = "Die Bande", SecrecyLevel = level, SecrecyReason = reason }, actor),
            nameof(Party) => Parties(ctx).RefreshAsync(Id,
                new PartyInput { Name = "Grove Street", SecrecyLevel = level, SecrecyReason = reason }, actor),
            nameof(Case) => Cases(ctx).RefreshAsync(Id,
                new CaseInput { Title = "Ermittlung", SecrecyLevel = level, SecrecyReason = reason }, actor),
            nameof(Operation) => Operations(ctx).RefreshAsync(Id,
                new OperationInput { Title = "Operation", SecrecyLevel = level, SecrecyReason = reason }, actor),
            _ => Taskforces(ctx).RefreshAsync(Id,
                new TaskforceInput
                {
                    Name = "Alpha TF", IsClassified = level != DocumentClassification.None, SecrecyReason = reason,
                }, actor),
        };

    private static bool Exists(SqliteTestContext ctx, string type)
    {
        using var db = ctx.NewContext();
        // no interceptor here: a delete is a real one, a seeded trash row is hidden by the filter
        return type switch
        {
            nameof(Person) => db.People.Any(x => x.Id == Id),
            nameof(Faction) => db.Factions.Any(x => x.Id == Id),
            nameof(PersonGroup) => db.PersonGroups.Any(x => x.Id == Id),
            nameof(Party) => db.Parties.Any(x => x.Id == Id),
            nameof(Case) => db.Cases.Any(x => x.Id == Id),
            nameof(Operation) => db.Operations.Any(x => x.Id == Id),
            _ => db.Taskforces.Any(x => x.Id == Id),
        };
    }

    private static Classification ClassificationOf(SqliteTestContext ctx, string type)
    {
        using var db = ctx.NewContext();
        return type switch
        {
            nameof(Person) => db.People.Single(x => x.Id == Id).Classification,
            nameof(Faction) => db.Factions.Single(x => x.Id == Id).Classification,
            nameof(PersonGroup) => db.PersonGroups.Single(x => x.Id == Id).Classification,
            nameof(Party) => db.Parties.Single(x => x.Id == Id).Classification,
            nameof(Case) => db.Cases.Single(x => x.Id == Id).Classification,
            _ => db.Operations.Single(x => x.Id == Id).Classification,
        };
    }

    private static DocumentClassification SecrecyOf(SqliteTestContext ctx, string type)
    {
        using var db = ctx.NewContext();
        return type switch
        {
            nameof(Person) => db.People.Single(x => x.Id == Id).SecrecyLevel,
            nameof(Faction) => db.Factions.Single(x => x.Id == Id).SecrecyLevel,
            nameof(PersonGroup) => db.PersonGroups.Single(x => x.Id == Id).SecrecyLevel,
            nameof(Party) => db.Parties.Single(x => x.Id == Id).SecrecyLevel,
            nameof(Case) => db.Cases.Single(x => x.Id == Id).SecrecyLevel,
            nameof(Operation) => db.Operations.Single(x => x.Id == Id).SecrecyLevel,
            _ => CriticalChange.TaskforceLevel(db.Taskforces.Single(x => x.Id == Id).IsClassified),
        };
    }

    private static List<AuditLog> AuditRows(SqliteTestContext ctx)
    {
        using var db = ctx.NewContext();
        return db.AuditLogs.ToList();
    }

    private static List<AuditLog> ReasonRows(SqliteTestContext ctx, string type)
    {
        using var db = ctx.NewContext();
        return db.AuditLogs
            .Where(a => a.EntityType == type && a.EntityId == Id && a.Action == AuditAction.Justified)
            .ToList();
    }
}
