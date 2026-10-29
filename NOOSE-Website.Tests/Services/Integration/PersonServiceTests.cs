using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.People;
using NOOSE_Website.Services;
using NSubstitute;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="PersonService"/> over in-memory SQLite.</summary>
public sealed class PersonServiceTests
{
    // ---------- construction helpers ----------

    private static (PersonService Svc, IFileStorageService FileStorage, IProfileSuggestionService Suggestion,
        ICaseNumberService CaseNo, IThreatScoreService Threat) Build(SqliteTestContext ctx)
    {
        var fileStorage = Substitute.For<IFileStorageService>();
        var suggestion = Substitute.For<IProfileSuggestionService>();
        var caseNo = Substitute.For<ICaseNumberService>();
        // real ICaseNumberService uses MySQL-only raw SQL; stub it
        caseNo.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("NOOSE-P-2026-0001");
        var threat = Substitute.For<IThreatScoreService>();
        var svc = new PersonService(ctx.Factory, fileStorage, suggestion, caseNo, threat,
            Substitute.For<INotificationService>(), Substitute.For<IPublicWantedService>());
        return (svc, fileStorage, suggestion, caseNo, threat);
    }

    // Rank >= SupervisorySpecialAgent(4) => IsLeadership() => MayClassifiedRead.
    private static ClaimsPrincipal Leader() => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();
    // JuniorAgent: writable, but not leadership and cannot see classified.
    private static ClaimsPrincipal Junior() => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.JuniorAgent).Build();
    // TeamLead without admin => IsOnlyReader() => RequireWriteAccess denies.
    private static ClaimsPrincipal OnlyReader() => ClaimsPrincipalBuilder.Agent("reader").AsTeamLead().WithRank(Rank.JuniorAgent).Build();

    private static ViewerScope LeaderScope() => ViewerScope.From(Leader());
    private static ViewerScope MemberScope() => ViewerScope.From(Junior());
    // TRU agent without leadership: may see TRU-classified records.
    private static ViewerScope TruScope() => ViewerScope.From(
        ClaimsPrincipalBuilder.Agent("tru").WithRank(Rank.JuniorAgent).AsTru().Build());

    private static PersonInput Input(string name = "Neu Person",
        Classification classification = Classification.Unknown,
        DocumentClassification secrecy = DocumentClassification.None,
        LifeStatus life = LifeStatus.Alive,
        Action<PersonInput>? configure = null)
    {
        var input = new PersonInput
        {
            Name = name,
            Classification = classification,
            SecrecyLevel = secrecy,
            LifeStatus = life,
        };
        configure?.Invoke(input);
        return input;
    }

    // ---------- GetListAsync ----------

    [Fact]
    public async Task GetListAsync_FiltersClassified_AndOrdersByRecency()
    {
        using var ctx = new SqliteTestContext();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("a", "Alpha", p => { p.CreatedAt = t0; p.CaseNumber = "NOOSE-P-2026-0001"; }));            // older
            db.People.Add(Seed.Person("b", "Bravo", p => { p.CreatedAt = t0.AddHours(1); p.CaseNumber = "NOOSE-P-2026-0002"; })); // newer
            db.People.Add(Seed.Person("c", "Charlie", p => { p.CreatedAt = t0; p.IsClassified = true; p.CaseNumber = "NOOSE-P-2026-0003"; }));
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        var forMember = await svc.GetListAsync(MemberScope());
        var forLeader = await svc.GetListAsync(LeaderScope());

        // member: classified excluded, newest first
        Assert.Equal(new[] { "b", "a" }, forMember.Select(p => p.Id).ToArray());
        // leadership: classified visible
        Assert.Equal(3, forLeader.Count);
        Assert.Contains(forLeader, p => p.Id == "c");
    }

    // ---------- GetDetailAsync ----------

    [Fact]
    public async Task GetDetailAsync_ReturnsPersonWithChildren_WhenVisible()
    {
        using var ctx = new SqliteTestContext();
        var person = Seed.Person("p1", "Ziel", p => p.Aliases.Add(new PersonAlias { AliasName = "Shadow" }));
        using (var db = ctx.NewContext())
        {
            db.People.Add(person);
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        var result = await svc.GetDetailAsync("p1", MemberScope());

        Assert.NotNull(result);
        Assert.Equal("p1", result!.Id);
        Assert.Single(result.Aliases);
    }

    [Fact]
    public async Task GetDetailAsync_ReturnsNull_WhenClassifiedAndNotCleared()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", configure: p => p.IsClassified = true));
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        Assert.Null(await svc.GetDetailAsync("p1", MemberScope()));
        Assert.NotNull(await svc.GetDetailAsync("p1", LeaderScope()));
    }

    [Fact]
    public async Task GetDetailAsync_ReturnsNull_WhenUnknown()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _) = Build(ctx);

        Assert.Null(await svc.GetDetailAsync("missing", LeaderScope()));
    }

    // ---------- GetTrashAsync ----------

    [Fact]
    public async Task GetTrashAsync_ReturnsOnlyDeleted()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("live", "Lebt", p => p.CaseNumber = "NOOSE-P-2026-0001"));
            db.People.Add(Seed.Person("gone", "Weg", p =>
            {
                p.CaseNumber = "NOOSE-P-2026-0002";
                p.IsDeleted = true;
                p.DeletedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
            }));
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        var trash = await svc.GetTrashAsync();

        var row = Assert.Single(trash);
        Assert.Equal("gone", row.Id);
    }

    // ---------- SearchAsync ----------

    [Fact]
    public async Task SearchAsync_MatchesNameOrCaseNumber_AndClamps()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Anton Meier", p => p.CaseNumber = "NOOSE-P-2026-0001"));
            db.People.Add(Seed.Person("p2", "Berta Klein", p => p.CaseNumber = "NOOSE-P-2026-0002"));
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        var byName = await svc.SearchAsync("Anton", isLeadership: false);
        var byCase = await svc.SearchAsync("0002", isLeadership: false);
        var clamped = await svc.SearchAsync(null, isLeadership: false, max: 1);

        Assert.Equal("p1", Assert.Single(byName).Id);
        Assert.Equal("p2", Assert.Single(byCase).Id);
        Assert.Single(clamped);
    }

    [Fact]
    public async Task SearchAsync_ExcludesClassified_ForNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Geheim", p => p.IsClassified = true));
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        Assert.Empty(await svc.SearchAsync("Geheim", isLeadership: false));
        Assert.Single(await svc.SearchAsync("Geheim", isLeadership: true));
    }

    // ---------- FindDuplicatesAsync ----------

    [Fact]
    public async Task FindDuplicatesAsync_MatchesByNameOrPhone()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("byname", "Max Mustermann", p => p.CaseNumber = "NOOSE-P-2026-0001"));
            db.People.Add(Seed.Person("byphone", "Wer Anders", p =>
            {
                p.CaseNumber = "NOOSE-P-2026-0002";
                p.PhoneNumbers.Add(new PersonPhone { Number = "555-123" });
            }));
            db.People.Add(Seed.Person("nomatch", "Niemand", p => p.CaseNumber = "NOOSE-P-2026-0003"));
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        var dups = await svc.FindDuplicatesAsync("Max Mustermann", new[] { "555-123" }, isLeadership: false);

        Assert.Equal(new HashSet<string> { "byname", "byphone" }, dups.Select(h => h.Person.Id).ToHashSet());
        Assert.Equal(DuplicateReason.SameName, dups.Single(h => h.Person.Id == "byname").Reason);
        Assert.Equal(DuplicateReason.SamePhone, dups.Single(h => h.Person.Id == "byphone").Reason);
    }

    [Fact]
    public async Task FindDuplicatesAsync_MatchesSoundAlikeAndTypos_IgnoringTitles()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("sound", "Carlos Mendez"));
            db.People.Add(Seed.Person("typo", "Pablo Escobat"));
            db.People.Add(Seed.Person("other", "Carlos Escobar Garcia"));
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        var sound = await svc.FindDuplicatesAsync("Dr. Karlos Mendes", Array.Empty<string>(), isLeadership: false);
        var typo = await svc.FindDuplicatesAsync("Pablo Escobar", Array.Empty<string>(), isLeadership: false);

        Assert.Equal(DuplicateReason.SoundsAlike, Assert.Single(sound).Reason);
        Assert.Equal("typo", Assert.Single(typo).Person.Id);
        Assert.Equal(DuplicateReason.SpelledAlike, typo[0].Reason);
    }

    [Fact]
    public async Task FindDuplicatesAsync_MatchesAlias_AndNamesIt()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Wer Anders", p => p.Aliases.Add(new PersonAlias { AliasName = "Carlos Mendez" })));
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        var hit = Assert.Single(await svc.FindDuplicatesAsync("Carlos Mendez", Array.Empty<string>(), isLeadership: false));

        Assert.Equal("p1", hit.Person.Id);
        Assert.Equal(DuplicateReason.SameName, hit.Reason);
        Assert.Equal("Carlos Mendez", hit.Alias);
    }

    [Fact]
    public async Task FindDuplicatesAsync_OneHitPerPerson_StrongestReasonFirst()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            // sounds alike by name and shares the phone: the phone is the stronger signal
            db.People.Add(Seed.Person("both", "Karlos Mendes", p => p.PhoneNumbers.Add(new PersonPhone { Number = "555-1" })));
            db.People.Add(Seed.Person("exact", "Carlos Mendez"));
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        var dups = await svc.FindDuplicatesAsync("Carlos Mendez", new[] { "555-1" }, isLeadership: false);

        Assert.Equal(new[] { "exact", "both" }, dups.Select(h => h.Person.Id));
        Assert.Equal(DuplicateReason.SamePhone, dups[1].Reason);
    }

    [Fact]
    public async Task FindDuplicatesAsync_FindsArchivedRecords()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("old", "Carlos Mendez", p => p.IsArchived = true));
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        var hit = Assert.Single(await svc.FindDuplicatesAsync("Carlos Mendez", Array.Empty<string>(), isLeadership: false));

        Assert.True(hit.Person.IsArchived);
    }

    [Fact]
    public async Task FindDuplicatesAsync_HidesClassified_FromNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max Mustermann", p => p.IsClassified = true));
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        Assert.Empty(await svc.FindDuplicatesAsync("Max Mustermann", Array.Empty<string>(), isLeadership: false));
        Assert.Single(await svc.FindDuplicatesAsync("Max Mustermann", Array.Empty<string>(), isLeadership: true));
    }

    // ---------- FindByNamesAsync ----------

    [Fact]
    public async Task FindByNamesAsync_MatchesTrimmedCaseInsensitive()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Anton Meier", p => p.CaseNumber = "NOOSE-P-2026-0001"));
            db.People.Add(Seed.Person("p2", "Berta Klein", p => p.CaseNumber = "NOOSE-P-2026-0002"));
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        var found = await svc.FindByNamesAsync(new[] { "  anton meier ", "unknown" }, isLeadership: false);

        Assert.Equal("p1", Assert.Single(found).Id);
    }

    [Fact]
    public async Task FindByNamesAsync_Empty_ReturnsEmpty()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _) = Build(ctx);

        Assert.Empty(await svc.FindByNamesAsync(new[] { "  ", "" }, isLeadership: true));
    }

    // ---------- CreateAsync ----------

    [Fact]
    public async Task CreateAsync_PersistsPersonWithChildren_HistoryAndScore()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, threat) = Build(ctx);

        var input = Input("  Neue Zielperson  ", Classification.ReviewCase, configure: i =>
        {
            i.Aliases.Add(new AliasInput { AliasName = "Ghost" });
            i.PhoneNumbers.Add(new PhoneInput { Number = "555-1" });
            i.Vehicles.Add(new VehicleInput { Designation = "Sultan" });
            i.Locations.Add(new LocationInput { Text = "Vinewood" });
            i.Weapons.Add(new WeaponInput { Text = "Pistole" });
        });

        var person = await svc.CreateAsync(input, Leader());

        Assert.Equal("NOOSE-P-2026-0001", person.CaseNumber);
        Assert.Equal("Neue Zielperson", person.Name);

        using var db = ctx.NewContext();
        var stored = await db.People.FirstAsync(p => p.Id == person.Id);
        Assert.Equal(Classification.ReviewCase, stored.Classification);
        Assert.Single(await db.PersonAliases.Where(a => a.PersonId == person.Id).ToListAsync());
        Assert.Single(await db.PersonPhones.Where(a => a.PersonId == person.Id).ToListAsync());
        Assert.Single(await db.PersonVehicles.Where(a => a.PersonId == person.Id).ToListAsync());
        Assert.Single(await db.PersonLocations.Where(a => a.PersonId == person.Id).ToListAsync());
        Assert.Single(await db.PersonWeapons.Where(a => a.PersonId == person.Id).ToListAsync());
        // classification != Unknown => a history row is written
        Assert.Single(await db.ClassificationHistory
            .Where(h => h.EntityType == nameof(Person) && h.EntityId == person.Id).ToListAsync());
        await threat.Received(1).NewCalculatePersonScoreAsync(person.Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_NoHistory_WhenClassificationUnknown()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _) = Build(ctx);

        var person = await svc.CreateAsync(Input("Ohne Einstufung"), Leader());

        using var db = ctx.NewContext();
        Assert.Empty(await db.ClassificationHistory
            .Where(h => h.EntityId == person.Id).ToListAsync());
    }

    [Fact]
    public async Task CreateAsync_Throws_OnHighClassificationWithoutRank()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _) = Build(ctx);

        // CheckRankGate: SecuredStateThreatening needs Senior Special Agent+
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync(Input(classification: Classification.SecuredStateThreatening), Junior()));
    }

    [Fact]
    public async Task CreateAsync_Throws_OnSecrecyWithoutClearance()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _) = Build(ctx);

        // RequireMayAssignClassification: junior may not assign a leadership secrecy level
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateAsync(Input(secrecy: DocumentClassification.Leadership), Junior()));
    }

    // ---------- RefreshAsync ----------

    [Fact]
    public async Task RefreshAsync_UpdatesFields_ReplacesChildren_AndScores()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Alter Name", p =>
                p.Aliases.Add(new PersonAlias { AliasName = "OldAlias" })));
            db.SaveChanges();
        }
        var (svc, _, _, _, threat) = Build(ctx);

        var input = Input("Neuer Name", configure: i => i.Aliases.Add(new AliasInput { AliasName = "NewAlias" }));
        await svc.RefreshAsync("p1", input, Leader());

        using var db2 = ctx.NewContext();
        var stored = await db2.People.FirstAsync(p => p.Id == "p1");
        Assert.Equal("Neuer Name", stored.Name);
        var aliases = await db2.PersonAliases.Where(a => a.PersonId == "p1").ToListAsync();
        Assert.Equal("NewAlias", Assert.Single(aliases).AliasName);
        await threat.Received(1).NewCalculatePersonScoreAsync("p1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenClassifiedAndNotCleared()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", configure: p => p.IsClassified = true));
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RefreshAsync("p1", Input(), Junior()));
    }

    [Fact]
    public async Task RefreshAsync_Throws_OnUnknownId()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshAsync("missing", Input(), Leader()));
    }

    // ---------- DeleteAsync ----------

    [Fact]
    public async Task DeleteAsync_RemovesPerson()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        await svc.DeleteAsync("p1", Leader());

        // no soft-delete interceptor in tests => row hard-deleted
        using var db2 = ctx.NewContext();
        Assert.False(await db2.People.AnyAsync(p => p.Id == "p1"));
    }

    [Fact]
    public async Task DeleteAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.DeleteAsync("p1", Junior()));
    }

    [Fact]
    public async Task DeleteAsync_Throws_OnUnknownId()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DeleteAsync("missing", Leader()));
    }

    // ---------- RestoreAsync ----------

    [Fact]
    public async Task RestoreAsync_ClearsDeletedFlags()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", configure: p =>
            {
                p.IsDeleted = true;
                p.DeletedAt = DateTime.UtcNow;
                p.DeletedById = "someone";
            }));
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        await svc.RestoreAsync("p1", Leader());

        using var db2 = ctx.NewContext();
        var stored = await db2.People.FirstAsync(p => p.Id == "p1");
        Assert.False(stored.IsDeleted);
        Assert.Null(stored.DeletedAt);
        Assert.Null(stored.DeletedById);
    }

    [Fact]
    public async Task RestoreAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RestoreAsync("p1", Junior()));
    }

    // ---------- ClassificationSetAsync ----------

    [Fact]
    public async Task ClassificationSetAsync_UpdatesValue_WritesHistory_AndScores()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.SaveChanges();
        }
        var (svc, _, _, _, threat) = Build(ctx);

        await svc.ClassificationSetAsync("p1", Classification.SuspicionCase, "Grund", Leader());

        using var db2 = ctx.NewContext();
        var stored = await db2.People.FirstAsync(p => p.Id == "p1");
        Assert.Equal(Classification.SuspicionCase, stored.Classification);
        var history = await db2.ClassificationHistory.Where(h => h.EntityId == "p1").ToListAsync();
        Assert.Equal(Classification.SuspicionCase, Assert.Single(history).Value);
        await threat.Received(1).NewCalculatePersonScoreAsync("p1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ClassificationSetAsync_Throws_OnHighClassificationWithoutRank()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _) = Build(ctx);

        // CheckRankGate runs first, before the record load
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ClassificationSetAsync("p1", Classification.SecuredStateThreatening, null, Junior()));
    }

    [Fact]
    public async Task ClassificationSetAsync_Throws_WhenClassifiedAndNotCleared()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", configure: p => p.IsClassified = true));
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.ClassificationSetAsync("p1", Classification.ReviewCase, null, Junior()));
    }

    // ---------- WantedSetAsync ----------

    [Fact]
    public async Task WantedSetAsync_SetsFlagAndReason()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        await svc.WantedSetAsync("p1", true, "  flüchtig  ", Junior());

        using var db2 = ctx.NewContext();
        var stored = await db2.People.FirstAsync(p => p.Id == "p1");
        Assert.True(stored.IsWanted);
        Assert.Equal("flüchtig", stored.WantedReason);
    }

    [Fact]
    public async Task WantedSetAsync_Throws_ForReadOnlySupervisor()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.WantedSetAsync("p1", true, null, OnlyReader()));
    }

    [Fact]
    public async Task WantedSetAsync_Throws_OnUnknownId()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.WantedSetAsync("missing", true, null, Junior()));
    }

    // ---------- GetClassificationHistoryAsync ----------

    [Fact]
    public async Task GetClassificationHistoryAsync_ReturnsNewestFirst_WhenVisible()
    {
        using var ctx = new SqliteTestContext();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.ClassificationHistory.Add(new ClassificationHistory
            { EntityType = nameof(Person), EntityId = "p1", Value = Classification.ReviewCase, Timestamp = t0 });
            db.ClassificationHistory.Add(new ClassificationHistory
            { EntityType = nameof(Person), EntityId = "p1", Value = Classification.SuspicionCase, Timestamp = t0.AddHours(1) });
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        var history = await svc.GetClassificationHistoryAsync("p1", MemberScope());

        Assert.Equal(new[] { Classification.SuspicionCase, Classification.ReviewCase },
            history.Select(h => h.Value).ToArray());
    }

    [Fact]
    public async Task GetClassificationHistoryAsync_Empty_WhenNotVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", configure: p => p.IsClassified = true));
            db.ClassificationHistory.Add(new ClassificationHistory
            { EntityType = nameof(Person), EntityId = "p1", Value = Classification.ReviewCase, Timestamp = DateTime.UtcNow });
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        Assert.Empty(await svc.GetClassificationHistoryAsync("p1", MemberScope()));
    }

    // ---------- GetAffiliationsAsync ----------

    [Fact]
    public async Task GetAffiliationsAsync_ReturnsCurrentFactionMembership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.Factions.Add(Seed.Faction("f1", "Ballas"));
            db.FactionMembers.Add(new FactionMember { PersonId = "p1", FactionId = "f1", Rank = "Soldier", IsLead = true });
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        var affiliations = await svc.GetAffiliationsAsync("p1", MemberScope());

        var row = Assert.Single(affiliations);
        Assert.Equal(nameof(Faction), row.Type);
        Assert.Equal("f1", row.Id);
        Assert.Equal("Ballas", row.Name);
        Assert.True(row.IsLead);
        Assert.Null(row.EndedAt);
    }

    [Fact]
    public async Task GetAffiliationsAsync_Empty_WhenPersonNotVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", configure: p => p.IsClassified = true));
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        Assert.Empty(await svc.GetAffiliationsAsync("p1", MemberScope()));
    }

    [Fact]
    public async Task GetAffiliationsManyAsync_GroupsByPerson_AndHidesClassifiedFactions()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.People.Add(Seed.Person("p2"));
            db.People.Add(Seed.Person("p3"));
            db.Factions.Add(Seed.Faction("f1", "Ballas"));
            db.Factions.Add(Seed.Faction("f2", "Vagos", f => f.IsClassified = true));
            db.FactionMembers.Add(new FactionMember { PersonId = "p1", FactionId = "f1" });
            db.FactionMembers.Add(new FactionMember { PersonId = "p1", FactionId = "f2" });
            db.FactionMembers.Add(new FactionMember { PersonId = "p2", FactionId = "f1" });
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        var many = await svc.GetAffiliationsManyAsync(new[] { "p1", "p2", "p3" }, MemberScope());

        Assert.Equal(2, many.Count);
        Assert.Equal("f1", Assert.Single(many["p1"]).Id);
        Assert.Equal("f1", Assert.Single(many["p2"]).Id);
        Assert.False(many.ContainsKey("p3"));
    }

    [Fact]
    public async Task GetAffiliationsManyAsync_Empty_ForNoIds()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _) = Build(ctx);

        Assert.Empty(await svc.GetAffiliationsManyAsync(Array.Empty<string>(), MemberScope()));
    }

    // ---------- classified orgs: display must match the write gate ----------

    [Fact]
    public async Task GetAffiliationsAsync_ShowsTruFaction_ToTruAgent_ButNotToPlainMember()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.Factions.Add(Seed.Faction("f1", "Vagos", f => f.SecrecyLevel = DocumentClassification.Tru));
            db.FactionMembers.Add(new FactionMember { PersonId = "p1", FactionId = "f1" });
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        Assert.Single(await svc.GetAffiliationsAsync("p1", TruScope()));
        Assert.Single((await svc.GetAffiliationsManyAsync(new[] { "p1" }, TruScope()))["p1"]);
        Assert.Empty(await svc.GetAffiliationsAsync("p1", MemberScope()));
        Assert.Empty(await svc.GetAffiliationsManyAsync(new[] { "p1" }, MemberScope()));
    }

    // ---------- GetFormerAffiliationsAsync ----------

    [Fact]
    public async Task GetFormerAffiliationsAsync_ReturnsEndedMembership_WithExitDate()
    {
        using var ctx = new SqliteTestContext();
        var left = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.Factions.Add(Seed.Faction("f1", "Vagos"));
            db.FactionMembers.Add(new FactionMember
            {
                PersonId = "p1",
                FactionId = "f1",
                IsDeleted = true,
                DeletedAt = left,
            });
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        var former = await svc.GetFormerAffiliationsAsync("p1", MemberScope());

        var row = Assert.Single(former);
        Assert.Equal("f1", row.Id);
        Assert.Equal(left, row.EndedAt);
    }

    // ---------- GetDerivedRelationsAsync ----------

    [Fact]
    public async Task GetDerivedRelationsAsync_DerivesAllyFromOrgAlliance()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("me", "Ich", p => p.CaseNumber = "NOOSE-P-2026-0001"));
            db.People.Add(Seed.Person("ally", "Verbündeter", p => p.CaseNumber = "NOOSE-P-2026-0002"));
            db.Factions.Add(Seed.Faction("fA", "Alpha", f => f.CaseNumber = "NOOSE-F-2026-0001"));
            db.Factions.Add(Seed.Faction("fB", "Bravo", f => f.CaseNumber = "NOOSE-F-2026-0002"));
            db.FactionMembers.Add(new FactionMember { PersonId = "me", FactionId = "fA" });
            db.FactionMembers.Add(new FactionMember { PersonId = "ally", FactionId = "fB" });
            db.Links.Add(new Link
            {
                SourceType = nameof(Faction),
                SourceId = "fA",
                TargetType = nameof(Faction),
                TargetId = "fB",
                Kind = LinkKind.Alliance,
            });
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        var relations = await svc.GetDerivedRelationsAsync("me", MemberScope());

        var row = Assert.Single(relations);
        Assert.Equal(LinkKind.Alliance, row.Kind);
        Assert.Equal("ally", row.PersonId);
        Assert.Equal("Alpha", row.SourceName);
        Assert.Equal("Bravo", row.PartnerName);
    }

    [Fact]
    public async Task GetDerivedRelationsAsync_Empty_WhenPersonNotVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", configure: p => p.IsClassified = true));
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        Assert.Empty(await svc.GetDerivedRelationsAsync("p1", MemberScope()));
    }

    // ---------- PhotoAddAsync ----------

    [Fact]
    public async Task PhotoAddAsync_SavesFileAndRow()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.SaveChanges();
        }
        var (svc, fileStorage, _, _, _) = Build(ctx);
        fileStorage.IsAllowedType(Arg.Any<string>()).Returns(true);
        fileStorage.MaxBytes.Returns(10L * 1024 * 1024);
        fileStorage.SaveAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("saved.png");

        using var content = new MemoryStream(new byte[] { 1, 2, 3 });
        var photo = await svc.PhotoAddAsync("p1", content, "orig.png", "image/png", 100, Leader());

        Assert.Equal("saved.png", photo.FileNameSaved);
        Assert.Equal("lead", photo.CreatedById);
        using var db2 = ctx.NewContext();
        var stored = await db2.PersonPhotos.FirstAsync(p => p.Id == photo.Id);
        Assert.Equal("p1", stored.PersonId);
        Assert.Equal("orig.png", stored.OriginalName);
        await fileStorage.Received(1).SaveAsync(Arg.Any<Stream>(), "image/png", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PhotoAddAsync_Throws_OnDisallowedType()
    {
        using var ctx = new SqliteTestContext();
        var (svc, fileStorage, _, _, _) = Build(ctx);
        fileStorage.IsAllowedType(Arg.Any<string>()).Returns(false);

        using var content = new MemoryStream(new byte[] { 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.PhotoAddAsync("p1", content, "x.exe", "application/octet-stream", 1, Leader()));
    }

    [Fact]
    public async Task PhotoAddAsync_Throws_OnOversize()
    {
        using var ctx = new SqliteTestContext();
        var (svc, fileStorage, _, _, _) = Build(ctx);
        fileStorage.IsAllowedType(Arg.Any<string>()).Returns(true);
        fileStorage.MaxBytes.Returns(10L);

        using var content = new MemoryStream(new byte[] { 1 });
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.PhotoAddAsync("p1", content, "big.png", "image/png", 100, Leader()));
    }

    [Fact]
    public async Task PhotoAddAsync_Throws_WhenClassifiedAndNotCleared()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", configure: p => p.IsClassified = true));
            db.SaveChanges();
        }
        var (svc, fileStorage, _, _, _) = Build(ctx);
        fileStorage.IsAllowedType(Arg.Any<string>()).Returns(true);
        fileStorage.MaxBytes.Returns(10L * 1024 * 1024);

        using var content = new MemoryStream(new byte[] { 1 });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.PhotoAddAsync("p1", content, "x.png", "image/png", 1, Junior()));
    }

    // ---------- PhotoRemoveAsync ----------

    [Fact]
    public async Task PhotoRemoveAsync_RemovesRowAndFile()
    {
        using var ctx = new SqliteTestContext();
        var photo = new PersonPhoto { PersonId = "p1", FileNameSaved = "f.png", OriginalName = "f.png", ContentType = "image/png" };
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.PersonPhotos.Add(photo);
            db.SaveChanges();
        }
        var (svc, fileStorage, _, _, _) = Build(ctx);

        await svc.PhotoRemoveAsync(photo.Id, Leader());

        using var db2 = ctx.NewContext();
        Assert.False(await db2.PersonPhotos.AnyAsync(p => p.Id == photo.Id));
        fileStorage.Received(1).Delete("f.png");
    }

    [Fact]
    public async Task PhotoRemoveAsync_NoOp_WhenMissing()
    {
        using var ctx = new SqliteTestContext();
        var (svc, fileStorage, _, _, _) = Build(ctx);

        await svc.PhotoRemoveAsync("missing", Leader());

        fileStorage.DidNotReceive().Delete(Arg.Any<string>());
    }

    [Fact]
    public async Task PhotoRemoveAsync_Throws_WhenClassifiedAndNotCleared()
    {
        using var ctx = new SqliteTestContext();
        var photo = new PersonPhoto { PersonId = "p1", FileNameSaved = "f.png" };
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", configure: p => p.IsClassified = true));
            db.PersonPhotos.Add(photo);
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.PhotoRemoveAsync(photo.Id, Junior()));
    }

    // ---------- PhotoSetFocalPointAsync ----------

    [Fact]
    public async Task PhotoSetFocalPointAsync_ClampsAndPersists()
    {
        using var ctx = new SqliteTestContext();
        var photo = new PersonPhoto { PersonId = "p1", FileNameSaved = "f.png" };
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.PersonPhotos.Add(photo);
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        await svc.PhotoSetFocalPointAsync(photo.Id, 150, -10, Leader());

        using var db2 = ctx.NewContext();
        var stored = await db2.PersonPhotos.FirstAsync(p => p.Id == photo.Id);
        Assert.Equal(100, stored.FocalPointX);
        Assert.Equal(0, stored.FocalPointY);
    }

    [Fact]
    public async Task PhotoSetFocalPointAsync_Throws_ForReadOnlySupervisor()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.PhotoSetFocalPointAsync("any", 10, 10, OnlyReader()));
    }

    // ---------- GetPhotoWithPersonAsync ----------

    [Fact]
    public async Task GetPhotoWithPersonAsync_ReturnsPhoto_WhenVisible()
    {
        using var ctx = new SqliteTestContext();
        var photo = new PersonPhoto { PersonId = "p1", FileNameSaved = "f.png" };
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.PersonPhotos.Add(photo);
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        var result = await svc.GetPhotoWithPersonAsync(photo.Id, MemberScope());

        Assert.NotNull(result);
        Assert.Equal(photo.Id, result!.Id);
    }

    [Fact]
    public async Task GetPhotoWithPersonAsync_Null_WhenClassifiedAndNotCleared()
    {
        using var ctx = new SqliteTestContext();
        var photo = new PersonPhoto { PersonId = "p1", FileNameSaved = "f.png" };
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", configure: p => p.IsClassified = true));
            db.PersonPhotos.Add(photo);
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        Assert.Null(await svc.GetPhotoWithPersonAsync(photo.Id, MemberScope()));
        Assert.NotNull(await svc.GetPhotoWithPersonAsync(photo.Id, LeaderScope()));
    }

    [Fact]
    public async Task GetPhotoWithPersonAsync_Null_WhenMissing()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _, _) = Build(ctx);

        Assert.Null(await svc.GetPhotoWithPersonAsync("missing", LeaderScope()));
    }

    // ---------- GetHistoryAsync ----------

    [Fact]
    public async Task GetHistoryAsync_ReturnsPersonAndDocAudit_NewestFirst()
    {
        using var ctx = new SqliteTestContext();
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var doc = new PersonDoc { PersonId = "p1", Timestamp = t0 };
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.PersonDocs.Add(doc);
            db.AuditLogs.Add(new AuditLog { EntityType = nameof(Person), EntityId = "p1", Action = AuditAction.Modified, Timestamp = t0.AddHours(2) });
            db.AuditLogs.Add(new AuditLog { EntityType = nameof(PersonDoc), EntityId = doc.Id, Action = AuditAction.Created, Timestamp = t0.AddHours(1) });
            db.AuditLogs.Add(new AuditLog { EntityType = nameof(Person), EntityId = "other", Action = AuditAction.Modified, Timestamp = t0.AddHours(3) });
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        var history = await svc.GetHistoryAsync("p1", LeaderScope());

        Assert.Equal(2, history.Count);
        // newest first: person(update, +2h) then doc(create, +1h); "other" excluded
        Assert.Equal(new[] { "p1", doc.Id }, history.Select(a => a.EntityId).ToArray());
        Assert.DoesNotContain(history, a => a.EntityId == "other");
    }

    [Fact]
    public async Task GetHistoryAsync_Empty_WhenNotVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", configure: p => p.IsClassified = true));
            db.AuditLogs.Add(new AuditLog { EntityType = nameof(Person), EntityId = "p1", Action = AuditAction.Modified, Timestamp = DateTime.UtcNow });
            db.SaveChanges();
        }
        var (svc, _, _, _, _) = Build(ctx);

        Assert.Empty(await svc.GetHistoryAsync("p1", MemberScope()));
    }
}
