using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Models.Navigation;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for NavPreferencesService against in-memory SQLite.</summary>
public sealed class NavPreferencesServiceTests : IDisposable
{
    private readonly SqliteTestContext _ctx = new();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    private NavPreferencesService NewService() => new(_ctx.Factory, _cache);

    public void Dispose() => _ctx.Dispose();

    // Persist an Agent row so ExecuteUpdate mutations hit an existing record.
    private void SeedAgent(string id, NavPreferences? prefs = null)
    {
        using var db = _ctx.NewContext();
        var a = Seed.Agent(id);
        if (prefs is not null)
        {
            a.NavPreferencesJson = JsonSerializer.Serialize(prefs);
        }
        db.Users.Add(a);
        db.SaveChanges();
    }

    // Read the persisted JSON column straight from the DB, bypassing the service cache.
    private NavPreferences Stored(string id)
    {
        using var db = _ctx.NewContext();
        var json = db.Users.AsNoTracking()
            .Where(a => a.Id == id)
            .Select(a => a.NavPreferencesJson)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(json)
            ? new NavPreferences()
            : JsonSerializer.Deserialize<NavPreferences>(json)!;
    }

    private static NavFavorite PageFav(string key, string label = "L", string route = "/r", string icon = "i")
        => new("page", key, null, null, label, route, icon);

    private static NavFavorite RecordFav(string entityType, string entityId, string label = "L", string route = "/r", string icon = "i")
        => new("record", null, entityType, entityId, label, route, icon);

    private static RecentItem PageRecent(string route, string label = "L")
        => new(route, label, "i", null, null, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));

    private static RecentItem RecordRecent(string entityType, string entityId, string route = "/r")
        => new(route, "L", "i", entityType, entityId, new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));

    // ---------------------------------------------------------------- GetAsync

    [Fact]
    public async Task GetAsync_ReturnsEmptyDefaults_WhenAgentIdBlank()
    {
        var svc = NewService();

        var prefs = await svc.GetAsync("");

        Assert.Empty(prefs.Favorites);
        Assert.Null(prefs.StartRoute);
        Assert.True(prefs.DrawerOpen);
        Assert.Null(prefs.LastArea);
        Assert.Equal(2, prefs.Version);
    }

    [Fact]
    public async Task GetAsync_ReturnsDefaults_WhenAgentHasNoStoredJson()
    {
        SeedAgent("a1");
        var svc = NewService();

        var prefs = await svc.GetAsync("a1");

        Assert.Empty(prefs.Favorites);
        Assert.Empty(prefs.HiddenKeys);
        Assert.Null(prefs.StartRoute);
    }

    [Fact]
    public async Task GetAsync_ReturnsDeserializedPreferences_FromStoredJson()
    {
        var seeded = new NavPreferences
        {
            StartRoute = "/personen",
            Favorites = { PageFav("people", "Personen", "/personen") },
        };
        seeded.HiddenKeys.Add("graph");
        SeedAgent("a1", seeded);
        var svc = NewService();

        var prefs = await svc.GetAsync("a1");

        Assert.Equal("/personen", prefs.StartRoute);
        Assert.Single(prefs.Favorites);
        Assert.Equal("people", prefs.Favorites[0].Key);
        Assert.Contains("graph", prefs.HiddenKeys);
    }

    [Fact]
    public async Task GetAsync_ReturnsDefaults_WhenAgentDoesNotExist()
    {
        var svc = NewService();

        var prefs = await svc.GetAsync("ghost");

        Assert.Empty(prefs.Favorites);
    }

    [Fact]
    public async Task GetAsync_ServesCachedValue_IgnoringLaterDbChange()
    {
        var seeded = new NavPreferences { StartRoute = "/first" };
        SeedAgent("a1", seeded);
        var svc = NewService();

        var first = await svc.GetAsync("a1");
        Assert.Equal("/first", first.StartRoute);

        // Mutate the DB behind the service's back; cache still holds the old value.
        using (var db = _ctx.NewContext())
        {
            var changed = new NavPreferences { StartRoute = "/second" };
            await db.Users.Where(a => a.Id == "a1")
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.NavPreferencesJson, JsonSerializer.Serialize(changed)));
        }

        var second = await svc.GetAsync("a1");
        Assert.Equal("/first", second.StartRoute);
    }

    // ---------------------------------------------------------------- ToggleFavoriteAsync

    [Fact]
    public async Task ToggleFavoriteAsync_AddsFavorite_WhenNotPresent()
    {
        SeedAgent("a1");
        var svc = NewService();

        await svc.ToggleFavoriteAsync("a1", PageFav("people", "Personen", "/personen"));

        var stored = Stored("a1");
        Assert.Single(stored.Favorites);
        Assert.Equal("people", stored.Favorites[0].Key);
    }

    [Fact]
    public async Task ToggleFavoriteAsync_RemovesFavorite_WhenSameIdAlreadyPresent()
    {
        var seeded = new NavPreferences { Favorites = { RecordFav("Person", "p1") } };
        SeedAgent("a1", seeded);
        var svc = NewService();

        // A different label/route but same id -> treated as the same favorite and removed.
        await svc.ToggleFavoriteAsync("a1", RecordFav("Person", "p1", "Other", "/other"));

        Assert.Empty(Stored("a1").Favorites);
    }

    [Fact]
    public async Task ToggleFavoriteAsync_DoesNotPersist_WhenAgentIdBlank()
    {
        var svc = NewService();

        // No throw, no persistence (guard short-circuits before DB access).
        await svc.ToggleFavoriteAsync("", PageFav("people"));

        Assert.Empty(Stored("").Favorites);
    }

    // ---------------------------------------------------------------- ReorderFavoritesAsync

    [Fact]
    public async Task ReorderFavoritesAsync_AppliesGivenOrder_AndKeepsUnlistedAtEnd()
    {
        var seeded = new NavPreferences
        {
            Favorites =
            {
                PageFav("a"),
                PageFav("b"),
                PageFav("c"),
            },
        };
        SeedAgent("a1", seeded);
        var svc = NewService();

        // List c,a explicitly; b is unlisted and must trail.
        await svc.ReorderFavoritesAsync("a1", new[] { "page:c", "page:a" });

        var order = Stored("a1").Favorites.Select(f => f.Key).ToList();
        Assert.Equal(new[] { "c", "a", "b" }, order);
    }

    // ---------------------------------------------------------------- SetHiddenAsync

    [Fact]
    public async Task SetHiddenAsync_AddsKey_WhenHiddenTrue()
    {
        SeedAgent("a1");
        var svc = NewService();

        await svc.SetHiddenAsync("a1", "graph", hidden: true);

        Assert.Contains("graph", Stored("a1").HiddenKeys);
    }

    [Fact]
    public async Task SetHiddenAsync_RemovesKey_WhenHiddenFalse()
    {
        var seeded = new NavPreferences();
        seeded.HiddenKeys.Add("graph");
        SeedAgent("a1", seeded);
        var svc = NewService();

        await svc.SetHiddenAsync("a1", "graph", hidden: false);

        Assert.DoesNotContain("graph", Stored("a1").HiddenKeys);
    }

    // ---------------------------------------------------------------- SetOrderAsync

    [Fact]
    public async Task SetOrderAsync_PersistsProvidedKeyOrder()
    {
        SeedAgent("a1");
        var svc = NewService();

        await svc.SetOrderAsync("a1", new[] { "cases", "people", "graph" });

        Assert.Equal(new[] { "cases", "people", "graph" }, Stored("a1").Order);
    }

    // ---------------------------------------------------------------- SetStartRouteAsync

    [Fact]
    public async Task SetStartRouteAsync_SetsRoute()
    {
        SeedAgent("a1");
        var svc = NewService();

        await svc.SetStartRouteAsync("a1", "/statistik");

        Assert.Equal("/statistik", Stored("a1").StartRoute);
    }

    [Fact]
    public async Task SetStartRouteAsync_ClearsRoute_WhenWhitespace()
    {
        var seeded = new NavPreferences { StartRoute = "/statistik" };
        SeedAgent("a1", seeded);
        var svc = NewService();

        await svc.SetStartRouteAsync("a1", "   ");

        Assert.Null(Stored("a1").StartRoute);
    }

    // ---------------------------------------------------------------- SetDrawerOpenAsync

    [Fact]
    public async Task SetDrawerOpenAsync_PersistsState()
    {
        SeedAgent("a1");
        var svc = NewService();

        await svc.SetDrawerOpenAsync("a1", open: false);

        Assert.False(Stored("a1").DrawerOpen);
    }

    // ---------------------------------------------------------------- SetLastAreaAsync

    [Fact]
    public async Task SetLastAreaAsync_RemembersTheArea()
    {
        SeedAgent("a1");
        var svc = NewService();

        await svc.SetLastAreaAsync("a1", "Ermittlung");

        Assert.Equal("Ermittlung", Stored("a1").LastArea);
    }

    [Fact]
    public async Task SetLastAreaAsync_ClearsOnEmpty()
    {
        var seeded = new NavPreferences { LastArea = "Akten" };
        SeedAgent("a1", seeded);
        var svc = NewService();

        await svc.SetLastAreaAsync("a1", "  ");

        Assert.Null(Stored("a1").LastArea);
    }

    [Fact]
    public async Task SetLastAreaAsync_KeepsLegacyCollapsedGroups()
    {
        // the whole preferences blob is rewritten on save, so unused legacy state must survive
        var seeded = new NavPreferences();
        seeded.CollapsedGroups.Add("Akten");
        SeedAgent("a1", seeded);
        var svc = NewService();

        await svc.SetLastAreaAsync("a1", "Verwaltung");

        Assert.Contains("Akten", Stored("a1").CollapsedGroups);
    }

    // ---------------------------------------------------------------- PushRecentAsync

    [Fact]
    public async Task PushRecentAsync_InsertsItemAtFront()
    {
        SeedAgent("a1");
        var svc = NewService();

        await svc.PushRecentAsync("a1", PageRecent("/a", "First"));
        await svc.PushRecentAsync("a1", PageRecent("/b", "Second"));

        var recents = Stored("a1").Recents;
        Assert.Equal(2, recents.Count);
        Assert.Equal("/b", recents[0].Route);
        Assert.Equal("/a", recents[1].Route);
    }

    [Fact]
    public async Task PushRecentAsync_DedupesByEntity_MovingToFront()
    {
        SeedAgent("a1");
        var svc = NewService();

        await svc.PushRecentAsync("a1", RecordRecent("Person", "p1", "/personen/p1"));
        await svc.PushRecentAsync("a1", RecordRecent("Person", "p2", "/personen/p2"));
        await svc.PushRecentAsync("a1", RecordRecent("Person", "p1", "/personen/p1"));

        var recents = Stored("a1").Recents;
        Assert.Equal(2, recents.Count);
        Assert.Equal("p1", recents[0].EntityId);
        Assert.Equal("p2", recents[1].EntityId);
    }

    [Fact]
    public async Task PushRecentAsync_CapsAtFifteen_KeepingNewest()
    {
        SeedAgent("a1");
        var svc = NewService();

        for (var i = 0; i < 20; i++)
        {
            await svc.PushRecentAsync("a1", PageRecent($"/r{i}", $"L{i}"));
        }

        var recents = Stored("a1").Recents;
        Assert.Equal(15, recents.Count);
        Assert.Equal("/r19", recents[0].Route);
        Assert.Equal("/r5", recents[^1].Route);
    }

    // ---------------------------------------------------------------- Changed event

    [Fact]
    public async Task Changed_Fires_OnNotifyingMutation()
    {
        SeedAgent("a1");
        var svc = NewService();
        var fired = false;
        svc.Changed += () => fired = true;

        await svc.ToggleFavoriteAsync("a1", PageFav("people"));

        Assert.True(fired);
    }

    [Fact]
    public async Task Changed_DoesNotFire_OnSilentMutation()
    {
        SeedAgent("a1");
        var svc = NewService();
        var fired = false;
        svc.Changed += () => fired = true;

        // Drawer/area/recents mutate with notify:false.
        await svc.SetDrawerOpenAsync("a1", open: false);
        await svc.SetLastAreaAsync("a1", "Akten");
        await svc.PushRecentAsync("a1", PageRecent("/a"));

        Assert.False(fired);
    }

    // ---- the changelog marker ----

    [Fact]
    public async Task SetNeuerungenLastSeenAsync_moves_the_marker_forward()
    {
        SeedAgent("a1");
        var earlier = new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);
        var later = earlier.AddHours(3);

        await NewService().SetNeuerungenLastSeenAsync("a1", earlier);
        await NewService().SetNeuerungenLastSeenAsync("a1", later);

        Assert.Equal(later, Stored("a1").NeuerungenLastSeenUtc);
    }

    /// <summary>A window read in one tab and closed after the page was read in another must not reopen old news.</summary>
    [Fact]
    public async Task SetNeuerungenLastSeenAsync_never_moves_the_marker_back()
    {
        SeedAgent("a1");
        var pageRead = new DateTime(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);

        await NewService().SetNeuerungenLastSeenAsync("a1", pageRead);
        await NewService().SetNeuerungenLastSeenAsync("a1", pageRead.AddMinutes(-5));

        Assert.Equal(pageRead, Stored("a1").NeuerungenLastSeenUtc);
    }

    // ---- the two fields the handbook added ----

    /// <summary>A property addition rides in the existing JSON column; this proves it actually round-trips.</summary>
    [Fact]
    public async Task SetGlossarBlasenAsync_persists_the_switch()
    {
        SeedAgent("a1");

        await NewService().SetGlossarBlasenAsync("a1", false);

        Assert.False(Stored("a1").GlossarBlasen);
    }

    [Fact]
    public void GlossarBlasen_defaults_to_on_for_a_blob_written_before_it_existed()
    {
        // no marker in the stored JSON at all: System.Text.Json leaves the C# initializer standing
        SeedAgent("a1", new NavPreferences { StartRoute = "/personen" });

        Assert.True(Stored("a1").GlossarBlasen);
    }

    [Fact]
    public async Task MarkOnboardingStepAsync_persists_and_is_idempotent()
    {
        SeedAgent("a1");
        var service = NewService();

        await service.MarkOnboardingStepAsync("a1", Onboarding.StepSearch);
        await service.MarkOnboardingStepAsync("a1", Onboarding.StepSearch);
        await service.MarkOnboardingStepAsync("a1", Onboarding.ChapterStep("erste-schritte"));

        var stored = Stored("a1");
        Assert.Equal(2, stored.OnboardingDone.Count);
        Assert.Contains(Onboarding.StepSearch, stored.OnboardingDone);
    }

    /// <summary>
    /// Concurrent mutations all land and nothing deadlocks. Note what this does NOT prove: the write is a
    /// read-modify-write over the whole blob, and the lost update it can suffer in production is invisible
    /// here, because SqliteTestContext hands every context the same open connection and therefore serialises
    /// the commands by itself. The guard against that is the per-agent lock in MutateAsync; these two tests
    /// only catch a deadlock introduced by it.
    /// </summary>
    [Fact]
    public async Task Two_mutations_in_flight_at_once_do_not_lose_each_other()
    {
        SeedAgent("a1");
        var service = NewService();

        await Task.WhenAll(
            service.MarkOnboardingStepAsync("a1", Onboarding.StepProfile),
            service.PushRecentAsync("a1",
                new RecentItem("/profil", "Mein Profil", "x", null, null,
                    new DateTime(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc))));

        var stored = Stored("a1");
        Assert.Contains(Onboarding.StepProfile, stored.OnboardingDone);
        Assert.Single(stored.Recents);
    }

    [Fact]
    public async Task Many_mutations_in_flight_at_once_all_land()
    {
        SeedAgent("a1");
        var service = NewService();

        await Task.WhenAll(Enumerable.Range(0, 12)
            .Select(i => service.MarkOnboardingStepAsync("a1", $"schritt-{i}")));

        Assert.Equal(12, Stored("a1").OnboardingDone.Count);
    }

    /// <summary>Markers land while the agent navigates, and every navigation writes the same blob.</summary>
    [Fact]
    public async Task A_marker_survives_a_recents_push_that_follows_it()
    {
        SeedAgent("a1");
        var service = NewService();

        await service.MarkOnboardingStepAsync("a1", Onboarding.StepRecord);
        await service.PushRecentAsync("a1",
            new RecentItem("/personen/1", "Jemand", "x", "Person", "1", new DateTime(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc)));

        Assert.Contains(Onboarding.StepRecord, Stored("a1").OnboardingDone);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_empty_marker_writes_nothing(string marker)
    {
        SeedAgent("a1");

        await NewService().MarkOnboardingStepAsync("a1", marker);

        Assert.Empty(Stored("a1").OnboardingDone);
    }

    // ---------------------------------------------------------------- saved list views

    [Fact]
    public async Task SaveViewAsync_persists_the_view_and_a_second_save_of_the_name_replaces_it()
    {
        SeedAgent("a1");
        var svc = NewService();

        var first = await svc.SaveViewAsync("a1", "Rot", "/personen?aktualitaet=Red", "i");
        var again = await svc.SaveViewAsync("a1", "rot", "/personen?aktualitaet=Red&einstufung=SuspicionCase", "i");

        Assert.Equal(SavedViewOutcome.Added, first);
        Assert.Equal(SavedViewOutcome.Replaced, again);
        var view = Assert.Single(Stored("a1").SavedViews);
        Assert.Equal("/personen?aktualitaet=Red&einstufung=SuspicionCase", view.Route);
    }

    [Fact]
    public async Task SaveViewAsync_keeps_views_apart_from_favorites()
    {
        // a favorite without page key or record would share one id with every other view and break reordering
        SeedAgent("a1");

        await NewService().SaveViewAsync("a1", "Rot", "/personen?aktualitaet=Red", "i");

        var stored = Stored("a1");
        Assert.Empty(stored.Favorites);
        Assert.Single(stored.SavedViews);
    }

    [Theory]
    [InlineData("", "/personen?q=x")]
    [InlineData("Rot", "//evil.example")]
    [InlineData("Rot", "https://evil.example")]
    public async Task SaveViewAsync_refuses_without_writing(string label, string route)
    {
        SeedAgent("a1");

        var outcome = await NewService().SaveViewAsync("a1", label, route, "i");

        Assert.Equal(SavedViewOutcome.Invalid, outcome);
        Assert.Empty(Stored("a1").SavedViews);
    }

    [Fact]
    public async Task SaveViewAsync_does_nothing_without_an_agent()
    {
        SeedAgent("a1");

        var outcome = await NewService().SaveViewAsync("", "Rot", "/personen?aktualitaet=Red", "i");

        Assert.Equal(SavedViewOutcome.Invalid, outcome);
        Assert.Empty(Stored("a1").SavedViews);
    }

    [Fact]
    public async Task SaveViewAsync_refuses_the_shared_demo_account()
    {
        // every anonymous demo visitor is this one agent, and a view name is free text the next visitor would read
        SeedAgent(DemoIdentity.AgentId);

        var outcome = await NewService().SaveViewAsync(DemoIdentity.AgentId, "Irgendwas", "/personen?q=x", "i");

        Assert.Equal(SavedViewOutcome.Invalid, outcome);
        Assert.Empty(Stored(DemoIdentity.AgentId).SavedViews);
    }

    [Fact]
    public async Task RemoveViewAsync_leaves_the_shared_demo_account_alone()
    {
        SeedAgent(DemoIdentity.AgentId, new NavPreferences
        {
            SavedViews = [new SavedView("v1", "Rot", "/personen?aktualitaet=Red", "i")],
        });

        await NewService().RemoveViewAsync(DemoIdentity.AgentId, "v1");

        Assert.Single(Stored(DemoIdentity.AgentId).SavedViews);
    }

    // ---------------------------------------------------------------- the shared demo account

    /// <summary>One visitor hid every entry and the next visitor got an empty drawer.</summary>
    [Fact]
    public async Task A_demo_visitor_customising_the_menu_never_reaches_the_next_visitor()
    {
        SeedAgent(DemoIdentity.AgentId);
        var visitor = NewService();

        await visitor.SetHiddenAsync(DemoIdentity.AgentId, "graph", hidden: true);
        await visitor.SetOrderAsync(DemoIdentity.AgentId, ["cases", "people"]);
        await visitor.ToggleFavoriteAsync(DemoIdentity.AgentId, PageFav("people"));

        // same memory cache: a session blob there would reach the next circuit for thirty seconds
        var next = await NewService().GetAsync(DemoIdentity.AgentId);
        Assert.Empty(next.HiddenKeys);
        Assert.Empty(next.Order);
        Assert.Empty(next.Favorites);
        Assert.Empty(Stored(DemoIdentity.AgentId).HiddenKeys);
    }

    /// <summary>What visitors already wrote onto the account must not keep the drawer empty after the fix ships.</summary>
    [Fact]
    public async Task The_demo_ignores_what_is_already_stored_on_the_shared_account()
    {
        var poisoned = new NavPreferences { StartRoute = "/statistik" };
        poisoned.HiddenKeys.UnionWith(["people", "factions", "cases"]);
        SeedAgent(DemoIdentity.AgentId, poisoned);

        var prefs = await NewService().GetAsync(DemoIdentity.AgentId);

        Assert.Empty(prefs.HiddenKeys);
        Assert.Null(prefs.StartRoute);
    }

    [Fact]
    public async Task A_demo_visitor_keeps_their_own_changes_for_the_session()
    {
        SeedAgent(DemoIdentity.AgentId);
        var visitor = NewService();
        var fired = false;
        visitor.Changed += () => fired = true;

        await visitor.SetHiddenAsync(DemoIdentity.AgentId, "graph", hidden: true);
        await visitor.PushRecentAsync(DemoIdentity.AgentId, PageRecent("/personen"));

        var prefs = await visitor.GetAsync(DemoIdentity.AgentId);
        Assert.Contains("graph", prefs.HiddenKeys);
        Assert.Single(prefs.Recents);
        Assert.True(fired);
    }

    [Fact]
    public async Task SaveViewAsync_reports_a_full_list_and_keeps_it()
    {
        var prefs = new NavPreferences();
        for (var i = 0; i < SavedViewRules.Cap; i++)
        {
            prefs.SavedViews.Add(new SavedView($"id{i}", $"Ansicht {i}", "/personen?q=" + i, "i"));
        }
        SeedAgent("a1", prefs);

        var outcome = await NewService().SaveViewAsync("a1", "Neu", "/personen?q=neu", "i");

        Assert.Equal(SavedViewOutcome.Full, outcome);
        Assert.Equal(SavedViewRules.Cap, Stored("a1").SavedViews.Count);
    }

    [Fact]
    public async Task RemoveViewAsync_takes_the_view_out()
    {
        SeedAgent("a1", new NavPreferences
        {
            SavedViews = [new SavedView("v1", "Rot", "/personen?aktualitaet=Red", "i"),
                          new SavedView("v2", "Blau", "/fraktionen?q=x", "i")],
        });

        await NewService().RemoveViewAsync("a1", "v1");

        Assert.Equal("v2", Assert.Single(Stored("a1").SavedViews).Id);
    }

    [Fact]
    public async Task Saving_a_view_notifies_so_drawer_and_header_redraw()
    {
        SeedAgent("a1");
        var svc = NewService();
        var fired = false;
        svc.Changed += () => fired = true;

        await svc.SaveViewAsync("a1", "Rot", "/personen?aktualitaet=Red", "i");

        Assert.True(fired);
    }

    /// <summary>The save and the recents push of the next navigation write the same blob.</summary>
    [Fact]
    public async Task A_saved_view_survives_a_recents_push_in_flight_at_the_same_time()
    {
        SeedAgent("a1");
        var service = NewService();

        await Task.WhenAll(
            service.SaveViewAsync("a1", "Rot", "/personen?aktualitaet=Red", "i"),
            service.PushRecentAsync("a1", PageRecent("/personen")));

        var stored = Stored("a1");
        Assert.Single(stored.SavedViews);
        Assert.Single(stored.Recents);
    }

    [Fact]
    public async Task A_blob_written_before_saved_views_existed_reads_as_none()
    {
        // raw JSON without the property: System.Text.Json has to leave the initializer standing, not hand out null
        using (var db = _ctx.NewContext())
        {
            var a = Seed.Agent("a1");
            a.NavPreferencesJson = """{"StartRoute":"/personen","Favorites":[]}""";
            db.Users.Add(a);
            db.SaveChanges();
        }

        var prefs = await NewService().GetAsync("a1");

        Assert.NotNull(prefs.SavedViews);
        Assert.Empty(prefs.SavedViews);
        Assert.Equal("/personen", prefs.StartRoute);
    }

}
