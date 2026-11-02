using System.Security.Claims;
using NOOSE_Website.Data.Entities.Activities;
using NOOSE_Website.Data.Entities.Evidence;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;
using Case = NOOSE_Website.Data.Entities.Cases.Case;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for the admin CRUD + record propagation of <see cref="ProfileSuggestionService"/> over in-memory SQLite.</summary>
public sealed class ProfileSuggestionAdminTests
{
    private static ProfileSuggestionService Build(SqliteTestContext ctx) => new(ctx.Factory);

    private static ClaimsPrincipal Leader
        => ClaimsPrincipalBuilder.Agent().WithRank(Rank.SpecialAgentInCharge).Build();

    // read-only supervision passes the leadership check but must still be blocked from writes
    private static ClaimsPrincipal ReadOnlySupervision
        => ClaimsPrincipalBuilder.Agent().WithRank(Rank.SpecialAgentInCharge).AsTeamLead().Build();

    private static ClaimsPrincipal PlainAgent
        => ClaimsPrincipalBuilder.Agent().WithRank(Rank.Trainee).Build();

    // ---------- GetEntriesAsync ----------

    [Fact]
    public async Task GetEntriesAsync_SumsUsagesAcrossMappedTables()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.ProfileSuggestions.Add(new ProfileSuggestion { Type = SuggestionType.Weapon, Value = "AK-47" });
            db.ProfileSuggestions.Add(new ProfileSuggestion { Type = SuggestionType.Weapon, Value = "Glock" });
            db.PersonWeapons.Add(new PersonWeapon { PersonId = "p1", Text = "AK-47" });
            db.PersonWeapons.Add(new PersonWeapon { PersonId = "p2", Text = "AK-47" });
            db.FactionWeaponStocks.Add(new FactionWeaponStock { FactionId = "f1", Designation = "AK-47" });
            db.PersonWeapons.Add(new PersonWeapon { PersonId = "p3", Text = "Glock" });
            db.SaveChanges();
        }

        var entries = await Build(ctx).GetEntriesAsync(SuggestionType.Weapon);

        Assert.Equal(2, entries.Count);
        Assert.Equal(3, entries.Single(e => e.Value == "AK-47").UsageCount);
        Assert.Equal(1, entries.Single(e => e.Value == "Glock").UsageCount);
    }

    // ---------- CreateAsync ----------

    [Fact]
    public async Task CreateAsync_AddsValue_AndRejectsDuplicateCaseInsensitively()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await svc.CreateAsync(SuggestionType.Weapon, "Pistole", Leader);
        Assert.Contains("Pistole", await svc.GetAsync(SuggestionType.Weapon));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync(SuggestionType.Weapon, "pistole", Leader));
    }

    [Fact]
    public async Task CreateAsync_DeniesNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => Build(ctx).CreateAsync(SuggestionType.Weapon, "Pistole", PlainAgent));
    }

    [Fact]
    public async Task CreateAsync_DeniesReadOnlySupervision()
    {
        using var ctx = new SqliteTestContext();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => Build(ctx).CreateAsync(SuggestionType.Weapon, "Pistole", ReadOnlySupervision));
    }

    // ---------- RenameAsync ----------

    [Fact]
    public async Task RenameAsync_RenamesCatalog_AndPropagatesToMappedRecordsOnly()
    {
        using var ctx = new SqliteTestContext();
        string entryId;
        using (var db = ctx.NewContext())
        {
            var entry = new ProfileSuggestion { Type = SuggestionType.Weapon, Value = "AK47" };
            db.ProfileSuggestions.Add(entry);
            entryId = entry.Id;
            db.PersonWeapons.Add(new PersonWeapon { PersonId = "p1", Text = "AK47" });
            db.FactionWeaponStocks.Add(new FactionWeaponStock { FactionId = "f1", Designation = "AK47" });
            // same text in a different type's field must stay untouched
            db.Cases.Add(Seed.Case(configure: c => c.Type = "AK47"));
            db.SaveChanges();
        }

        await Build(ctx).RenameAsync(entryId, "AK-47", Leader);

        using (var db = ctx.NewContext())
        {
            Assert.Equal("AK-47", db.ProfileSuggestions.Single().Value);
            Assert.Equal("AK-47", db.PersonWeapons.Single().Text);
            Assert.Equal("AK-47", db.FactionWeaponStocks.Single().Designation);
            Assert.Equal("AK47", db.Cases.Single().Type);
        }
    }

    [Fact]
    public async Task RenameAsync_RefusesToMergeTwoDrugRoutes_EvenOneMissingFromTheCatalog()
    {
        using var ctx = new SqliteTestContext();
        string entryId;
        using (var db = ctx.NewContext())
        {
            var entry = new ProfileSuggestion { Type = SuggestionType.DrugRoute, Value = "Kokain Nord" };
            db.ProfileSuggestions.Add(entry);
            entryId = entry.Id;
            db.FactionDrugRoutes.Add(new FactionDrugRoute { FactionId = "f1", Designation = "Kokain Nord" });
            // a classified faction's route never reaches the catalog, but it is a route all the same
            db.FactionDrugRoutes.Add(new FactionDrugRoute { FactionId = "f2", Designation = "Kokain Süd" });
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(ctx).RenameAsync(entryId, "kokain süd", Leader));

        using (var db = ctx.NewContext())
        {
            Assert.Equal(["Kokain Nord", "Kokain Süd"], db.FactionDrugRoutes.Select(d => d.Designation).Order().ToList());
        }
    }

    [Fact]
    public async Task RenameAsync_LetsADrugRouteChangeItsCase()
    {
        using var ctx = new SqliteTestContext();
        string entryId;
        using (var db = ctx.NewContext())
        {
            var entry = new ProfileSuggestion { Type = SuggestionType.DrugRoute, Value = "kokain nord" };
            db.ProfileSuggestions.Add(entry);
            entryId = entry.Id;
            db.FactionDrugRoutes.Add(new FactionDrugRoute { FactionId = "f1", Designation = "kokain nord" });
            db.SaveChanges();
        }

        await Build(ctx).RenameAsync(entryId, "Kokain Nord", Leader);

        using (var db = ctx.NewContext())
        {
            Assert.Equal("Kokain Nord", db.FactionDrugRoutes.Single().Designation);
        }
    }

    [Fact]
    public async Task RenameAsync_RejectsDuplicateTarget()
    {
        using var ctx = new SqliteTestContext();
        string entryId;
        using (var db = ctx.NewContext())
        {
            db.ProfileSuggestions.Add(new ProfileSuggestion { Type = SuggestionType.Weapon, Value = "Glock" });
            var entry = new ProfileSuggestion { Type = SuggestionType.Weapon, Value = "Pistole" };
            db.ProfileSuggestions.Add(entry);
            entryId = entry.Id;
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Build(ctx).RenameAsync(entryId, "glock", Leader));
    }

    [Fact]
    public async Task RenameAsync_DeniesReadOnlySupervision()
    {
        using var ctx = new SqliteTestContext();
        string entryId;
        using (var db = ctx.NewContext())
        {
            var entry = new ProfileSuggestion { Type = SuggestionType.Weapon, Value = "AK47" };
            db.ProfileSuggestions.Add(entry);
            entryId = entry.Id;
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => Build(ctx).RenameAsync(entryId, "AK-47", ReadOnlySupervision));
    }

    // ---------- DeleteAsync ----------

    [Fact]
    public async Task DeleteAsync_RemovesCatalogValue_AndChildRows()
    {
        using var ctx = new SqliteTestContext();
        string entryId;
        using (var db = ctx.NewContext())
        {
            var entry = new ProfileSuggestion { Type = SuggestionType.Weapon, Value = "AK-47" };
            db.ProfileSuggestions.Add(entry);
            entryId = entry.Id;
            db.PersonWeapons.Add(new PersonWeapon { PersonId = "p1", Text = "AK-47" });
            db.FactionWeaponStocks.Add(new FactionWeaponStock { FactionId = "f1", Designation = "AK-47" });
            db.PersonWeapons.Add(new PersonWeapon { PersonId = "p2", Text = "Glock" });
            db.SaveChanges();
        }

        await Build(ctx).DeleteAsync(entryId, Leader);

        using (var db = ctx.NewContext())
        {
            Assert.Empty(db.ProfileSuggestions);
            Assert.Empty(db.FactionWeaponStocks);
            Assert.Equal("Glock", db.PersonWeapons.Single().Text);
        }
    }

    [Fact]
    public async Task DeleteAsync_NullsScalarFields()
    {
        using var ctx = new SqliteTestContext();
        string kindId, caseTypeId;
        using (var db = ctx.NewContext())
        {
            var kind = new ProfileSuggestion { Type = SuggestionType.Kind, Value = "Gang" };
            var caseType = new ProfileSuggestion { Type = SuggestionType.CaseType, Value = "Mord" };
            db.ProfileSuggestions.AddRange(kind, caseType);
            kindId = kind.Id;
            caseTypeId = caseType.Id;
            db.Factions.Add(Seed.Faction(configure: f => f.Kind = "Gang"));
            db.Cases.Add(Seed.Case(configure: c => c.Type = "Mord"));
            db.PartyMembers.Add(new PartyMember { PartyId = "t1", PersonId = "p1", Role = "Anführer" });
            db.SaveChanges();
        }

        var svc = Build(ctx);
        await svc.DeleteAsync(kindId, Leader);
        await svc.DeleteAsync(caseTypeId, Leader);

        using (var db = ctx.NewContext())
        {
            Assert.Null(db.Factions.Single().Kind);
            Assert.Null(db.Cases.Single().Type);
            // different type: the party role must stay untouched
            Assert.Equal("Anführer", db.PartyMembers.Single().Role);
        }
    }

    [Fact]
    public async Task EvidenceCategory_RenameAndDelete_ReachSoftDeletedItemsToo()
    {
        using var ctx = new SqliteTestContext();
        string entryId;
        using (var db = ctx.NewContext())
        {
            var entry = new ProfileSuggestion { Type = SuggestionType.EvidenceCategory, Value = "Drogen" };
            db.ProfileSuggestions.Add(entry);
            entryId = entry.Id;
            db.EvidenceItems.Add(new EvidenceItem { Name = "Kokainpaket", Category = "Drogen" });
            db.EvidenceItems.Add(new EvidenceItem { Name = "Pistole", Category = "Waffen" });
            // in the trash, so the propagation has to ignore the soft-delete filter
            db.EvidenceItems.Add(new EvidenceItem { Name = "Heroin", Category = "Drogen", IsDeleted = true });
            db.SaveChanges();
        }

        var svc = Build(ctx);
        await svc.RenameAsync(entryId, "Betäubungsmittel", Leader);

        using (var db = ctx.NewContext())
        {
            Assert.Equal("Betäubungsmittel", db.EvidenceItems.Single(i => i.Name == "Kokainpaket").Category);
            Assert.Equal("Betäubungsmittel",
                db.EvidenceItems.IgnoreQueryFilters().Single(i => i.Name == "Heroin").Category);
            // different value: the weapon category must stay untouched
            Assert.Equal("Waffen", db.EvidenceItems.Single(i => i.Name == "Pistole").Category);
        }

        await svc.DeleteAsync(entryId, Leader);

        using (var db = ctx.NewContext())
        {
            Assert.Null(db.EvidenceItems.Single(i => i.Name == "Kokainpaket").Category);
            Assert.Null(db.EvidenceItems.IgnoreQueryFilters().Single(i => i.Name == "Heroin").Category);
            // the item itself survives; only its label goes
            Assert.Equal(2, db.EvidenceItems.Count());
            Assert.Equal("Waffen", db.EvidenceItems.Single(i => i.Name == "Pistole").Category);
        }
    }

    [Fact]
    public async Task EvidenceCategory_UsageCount_CountsTrashedItems()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.ProfileSuggestions.Add(new ProfileSuggestion { Type = SuggestionType.EvidenceCategory, Value = "Drogen" });
            db.EvidenceItems.Add(new EvidenceItem { Name = "Kokainpaket", Category = "Drogen" });
            db.EvidenceItems.Add(new EvidenceItem { Name = "Heroin", Category = "Drogen", IsDeleted = true });
            db.SaveChanges();
        }

        var entries = await Build(ctx).GetEntriesAsync(SuggestionType.EvidenceCategory);

        Assert.Equal(2, Assert.Single(entries).UsageCount);
    }

    [Fact]
    public async Task DeleteAsync_PartyRole_NullsMemberRoles()
    {
        using var ctx = new SqliteTestContext();
        string entryId;
        using (var db = ctx.NewContext())
        {
            var entry = new ProfileSuggestion { Type = SuggestionType.PartyRole, Value = "Anführer" };
            db.ProfileSuggestions.Add(entry);
            entryId = entry.Id;
            db.PartyMembers.Add(new PartyMember { PartyId = "t1", PersonId = "p1", Role = "Anführer" });
            db.SaveChanges();
        }

        await Build(ctx).DeleteAsync(entryId, Leader);

        using (var db = ctx.NewContext())
        {
            Assert.Null(db.PartyMembers.Single().Role);
        }
    }

    // ---------- activity kinds (distinct list without catalog) ----------

    [Fact]
    public async Task ActivityKinds_DistinctWithCounts_RenameAndDelete()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.AgentActivities.Add(new AgentActivity { Title = "a1", Kind = "Streife" });
            db.AgentActivities.Add(new AgentActivity { Title = "a2", Kind = "Streife" });
            db.AgentActivities.Add(new AgentActivity { Title = "a3", Kind = "Ermittlung" });
            db.AgentActivities.Add(new AgentActivity { Title = "a4", Kind = null });
            db.SaveChanges();
        }

        var svc = Build(ctx);
        var kinds = await svc.GetActivityKindsAsync();
        Assert.Equal(2, kinds.Count);
        Assert.Equal(2, kinds.Single(k => k.Value == "Streife").UsageCount);

        await svc.RenameActivityKindAsync("Streife", "Patrouille", Leader);
        await svc.DeleteActivityKindAsync("Ermittlung", Leader);

        using (var db = ctx.NewContext())
        {
            Assert.Equal(2, db.AgentActivities.Count(a => a.Kind == "Patrouille"));
            Assert.Null(db.AgentActivities.Single(a => a.Title == "a3").Kind);
        }
    }

    [Fact]
    public async Task RenameActivityKindAsync_DeniesReadOnlySupervision()
    {
        using var ctx = new SqliteTestContext();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => Build(ctx).RenameActivityKindAsync("Streife", "Patrouille", ReadOnlySupervision));
    }
}
