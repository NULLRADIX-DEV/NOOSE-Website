using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NOOSE_Website.Components.Account;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Notifications;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.CurrentUser;
using NOOSE_Website.Infrastructure.Notifications;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Watched saved searches: a worker reruns them in the owner's name and reports only what is new.</summary>
/// <remarks>The owner's principal comes from the real login claims factory, rebuilt from the account on every run,
/// so these tests also pin that a rights change on the account reaches the next run.</remarks>
public sealed class SavedSearchWatchTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 13, 8, 0, 0, DateTimeKind.Utc);

    private readonly SqliteTestContext _ctx = new();
    private readonly IDiscordWebhookService _discord = Substitute.For<IDiscordWebhookService>();
    private readonly SavedSearchWatchService _watch;
    private readonly SavedSearchService _saved;

    public SavedSearchWatchTests()
    {
        var notifications = new NotificationService(_ctx.Factory, new NotificationBroadcaster(), _discord);
        _watch = new SavedSearchWatchService(_ctx.Factory, SearchTestHost.NewService(_ctx),
            new FreshPrincipals(_ctx), notifications, NullLogger<SavedSearchWatchService>.Instance);
        _saved = new SavedSearchService(_ctx.Factory);
    }

    public void Dispose() => _ctx.Dispose();

    // ---- reporting ----

    [Fact]
    public async Task The_first_run_only_sets_the_baseline()
    {
        SeedOwner();
        SeedPerson("Ballas Bob");
        var id = SeedWatched(new SearchCriteria { Text = "Ballas" });

        Assert.Equal(0, await _watch.RunDueAsync(T0));

        Assert.Empty(Notices());
        var row = Row(id);
        Assert.Single(SavedSearchRules.ReadKeys(row.KnownHitsJson));
        Assert.Equal(T0, row.LastCheckedAt);
    }

    [Fact]
    public async Task A_new_matching_record_is_reported_once_and_marked_unseen()
    {
        SeedOwner();
        SeedPerson("Ballas Bob");
        var id = SeedWatched(new SearchCriteria { Text = "Ballas" });
        await _watch.RunDueAsync(T0);

        var joe = SeedPerson("Ballas Joe");
        Assert.Equal(1, await _watch.RunDueAsync(T0.AddHours(1)));
        Assert.Equal(0, await _watch.RunDueAsync(T0.AddHours(2)));

        var notice = Assert.Single(Notices());
        Assert.Equal(NotificationType.SavedSearchHit, notice.Type);
        Assert.Equal($"/suche?gespeichert={id}", notice.Href);
        Assert.Contains("1 neuer Treffer", notice.Title);
        Assert.Contains("Ballas Joe", notice.Title);
        Assert.DoesNotContain("Ballas Bob", notice.Title);
        Assert.Equal([$"Person:{joe}"], SavedSearchRules.ReadKeys(Row(id).UnseenHitsJson));
        await _discord.Received(1).PushAsync(NotificationType.SavedSearchHit, $"/suche?gespeichert={id}",
            Arg.Is<IReadOnlyCollection<string>>(r => r.Single() == "owner"), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_second_report_folds_onto_the_unread_one_and_counts_everything_unseen()
    {
        SeedOwner();
        var id = SeedWatched(new SearchCriteria { Text = "Ballas" });
        await _watch.RunDueAsync(T0);

        SeedPerson("Ballas Joe");
        await _watch.RunDueAsync(T0.AddHours(1));
        SeedPerson("Ballas Jim");
        await _watch.RunDueAsync(T0.AddHours(2));

        var notice = Assert.Single(Notices());
        Assert.Contains("2 neue Treffer", notice.Title);
        Assert.Contains("Ballas Jim", notice.Title);
        Assert.Equal(2, SavedSearchRules.ReadKeys(Row(id).UnseenHitsJson).Count);
        // the fold refreshes the bell; only the first report pings
        await _discord.Received(1).PushAsync(NotificationType.SavedSearchHit, Arg.Any<string?>(),
            Arg.Any<IReadOnlyCollection<string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_search_is_not_run_again_before_its_interval()
    {
        SeedOwner();
        var id = SeedWatched(new SearchCriteria { Text = "Ballas" });
        await _watch.RunDueAsync(T0);

        SeedPerson("Ballas Joe");
        Assert.Equal(0, await _watch.RunDueAsync(T0.AddMinutes(30)));

        Assert.Empty(Notices());
        Assert.Equal(T0, Row(id).LastCheckedAt);
    }

    [Fact]
    public async Task An_unwatched_search_never_runs()
    {
        SeedOwner();
        var id = SeedWatched(new SearchCriteria { Text = "Ballas" }, watched: false);

        await _watch.RunDueAsync(T0);

        Assert.Null(Row(id).LastCheckedAt);
    }

    [Fact]
    public async Task The_saved_facet_limits_what_is_reported()
    {
        SeedOwner();
        SeedWatched(new SearchCriteria { Text = "Ballas", Facet = "Faction" });
        await _watch.RunDueAsync(T0);

        SeedPerson("Ballas Joe");
        Assert.Equal(0, await _watch.RunDueAsync(T0.AddHours(1)));

        using (var db = _ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(name: "Ballas South"));
            db.SaveChanges();
        }
        Assert.Equal(1, await _watch.RunDueAsync(T0.AddHours(2)));
        Assert.Contains("Ballas South", Assert.Single(Notices()).Title);
    }

    [Fact]
    public async Task A_folded_report_keeps_its_hit_titles_out_of_the_change_protocol()
    {
        // the fold rewrites the title, and the title names hits only the owner may see
        var audited = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_ctx.Connection)
            .AddInterceptors(new AuditSaveChangesInterceptor(new SystemUser()))
            .Options;
        var notifications = new NotificationService(new TestDbContextFactory(audited), new NotificationBroadcaster(), _discord);
        var watch = new SavedSearchWatchService(_ctx.Factory, SearchTestHost.NewService(_ctx),
            new FreshPrincipals(_ctx), notifications, NullLogger<SavedSearchWatchService>.Instance);
        SeedOwner();
        SeedWatched(new SearchCriteria { Text = "Ballas" });
        await watch.RunDueAsync(T0);
        SeedPerson("Ballas Joe");
        await watch.RunDueAsync(T0.AddHours(1));
        SeedPerson("Ballas Jim");
        await watch.RunDueAsync(T0.AddHours(2));

        using var db = _ctx.NewContext();
        var rows = db.AuditLogs.Where(a => a.EntityType == nameof(Notification)).ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.DoesNotContain("Ballas", r.ChangesJson ?? string.Empty));
    }

    [Fact]
    public async Task A_second_matching_comment_on_a_known_record_is_reported()
    {
        SeedOwner();
        var person = SeedPerson("Max Muster");
        SeedComment(person, "Treffen mit den Ballas am Hafen");
        SeedWatched(new SearchCriteria { Text = "Ballas" });
        await _watch.RunDueAsync(T0);

        SeedComment(person, "Ballas wieder am Hafen gesehen");
        Assert.Equal(1, await _watch.RunDueAsync(T0.AddHours(1)));
    }

    [Fact]
    public async Task A_baseline_cut_short_by_the_budget_is_taken_again()
    {
        SeedOwner();
        var id = SeedWatched(new SearchCriteria { Text = "Ballas" });
        SeedPerson("Ballas Bob");

        await Starved().RunDueAsync(T0);
        Assert.True(Row(id).LastRunIncomplete);

        // Bob was there all along; the full run after the short one must not call him new
        Assert.Equal(0, await _watch.RunDueAsync(T0.AddHours(1)));
        Assert.Empty(Notices());

        SeedPerson("Ballas Jim");
        Assert.Equal(1, await _watch.RunDueAsync(T0.AddHours(2)));
    }

    [Fact]
    public async Task A_search_that_always_runs_short_still_settles_after_one_retry()
    {
        SeedOwner();
        var id = SeedWatched(new SearchCriteria { Text = "Ballas" });

        await Starved().RunDueAsync(T0);
        Assert.StartsWith("~", Row(id).ScopeStamp);
        await Starved().RunDueAsync(T0.AddHours(1));

        var row = Row(id);
        Assert.True(row.LastRunIncomplete);
        Assert.DoesNotContain("~", row.ScopeStamp);
    }

    // ---- the owner's rights, read fresh on every run ----

    [Fact]
    public async Task A_classified_record_is_never_reported_to_an_owner_who_may_not_read_it()
    {
        SeedOwner(Rank.SpecialAgent);
        SeedWatched(new SearchCriteria { Text = "Ballas" });
        await _watch.RunDueAsync(T0);

        SeedPerson("Ballas Geheim", classified: true);
        Assert.Equal(0, await _watch.RunDueAsync(T0.AddHours(1)));

        Assert.Empty(Notices());
    }

    [Fact]
    public async Task A_promotion_rebaselines_silently_instead_of_reporting_everything_now_visible()
    {
        SeedOwner(Rank.SpecialAgent);
        var id = SeedWatched(new SearchCriteria { Text = "Ballas" });
        SeedPerson("Ballas Geheim", classified: true);
        await _watch.RunDueAsync(T0);

        UpdateOwner(a => a.Rank = Rank.SupervisorySpecialAgent);
        Assert.Equal(0, await _watch.RunDueAsync(T0.AddHours(1)));
        Assert.Empty(Notices());
        Assert.Contains(SavedSearchRules.ReadKeys(Row(id).KnownHitsJson), k => k.StartsWith("Person:"));

        SeedPerson("Ballas Geheim Zwei", classified: true);
        Assert.Equal(1, await _watch.RunDueAsync(T0.AddHours(2)));
        Assert.Contains("Ballas Geheim Zwei", Assert.Single(Notices()).Title);
    }

    [Fact]
    public async Task A_demotion_takes_effect_on_the_next_run()
    {
        SeedOwner(Rank.SupervisorySpecialAgent);
        SeedWatched(new SearchCriteria { Text = "Ballas" });
        await _watch.RunDueAsync(T0);

        UpdateOwner(a => a.Rank = Rank.SpecialAgent);
        SeedPerson("Ballas Geheim", classified: true);
        await _watch.RunDueAsync(T0.AddHours(1));
        await _watch.RunDueAsync(T0.AddHours(2));

        Assert.Empty(Notices());
    }

    [Theory]
    [InlineData("terminated")]
    [InlineData("blocked")]
    [InlineData("teamlead")]
    [InlineData("partner")]
    public async Task An_owner_who_may_no_longer_watch_gets_nothing(string change)
    {
        SeedOwner();
        var id = SeedWatched(new SearchCriteria { Text = "Ballas" });
        await _watch.RunDueAsync(T0);

        UpdateOwner(a =>
        {
            switch (change)
            {
                case "terminated": a.Status = AgentStatus.Terminated; break;
                case "blocked": a.Status = AgentStatus.Blocked; break;
                case "teamlead": a.IsTeamLead = true; break;
                case "partner": a.PartnerAgency = PartnerAgency.LSPD; break;
            }
        });
        SeedPerson("Ballas Joe");
        await _watch.RunDueAsync(T0.AddHours(1));

        Assert.Empty(Notices());
        // claimed all the same, so an ineligible owner cannot starve the queue
        Assert.Equal(T0.AddHours(1), Row(id).LastCheckedAt);
    }

    // ---- switching the watch ----

    [Fact]
    public async Task Switching_on_starts_from_a_fresh_baseline()
    {
        SeedOwner();
        var id = SeedWatched(new SearchCriteria { Text = "Ballas" }, watched: false, configure: g =>
        {
            g.KnownHitsJson = SavedSearchRules.WriteKeys(["Person:old"]);
            g.UnseenHitsJson = SavedSearchRules.WriteKeys(["Person:old"]);
            g.ScopeStamp = "stale";
            g.LastCheckedAt = T0;
        });

        await _saved.SetWatchedAsync(id, true, Owner());

        var row = Row(id);
        Assert.True(row.IsWatched);
        Assert.Null(row.KnownHitsJson);
        Assert.Null(row.UnseenHitsJson);
        Assert.Null(row.ScopeStamp);
        Assert.Null(row.LastCheckedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" x ")]
    public async Task A_search_without_a_term_or_tag_cannot_be_watched(string text)
    {
        SeedOwner();
        var id = SeedWatched(new SearchCriteria { Text = text }, watched: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _saved.SetWatchedAsync(id, true, Owner()));
        Assert.False(Row(id).IsWatched);
    }

    [Fact]
    public async Task A_tag_alone_is_enough_to_watch()
    {
        SeedOwner();
        var id = SeedWatched(new SearchCriteria { TagIds = { "tag-1" } }, watched: false);

        await _saved.SetWatchedAsync(id, true, Owner());

        Assert.True(Row(id).IsWatched);
    }

    [Fact]
    public async Task An_agent_watches_at_most_the_allowed_number_of_searches()
    {
        SeedOwner();
        for (var i = 0; i < SavedSearchRules.MaxWatchedPerAgent; i++)
        {
            SeedWatched(new SearchCriteria { Text = $"Suche {i}" });
        }
        var extra = SeedWatched(new SearchCriteria { Text = "Eine zu viel" }, watched: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _saved.SetWatchedAsync(extra, true, Owner()));
    }

    [Fact]
    public async Task Nobody_switches_someone_elses_search()
    {
        SeedOwner();
        SeedOwner("other");
        var id = SeedWatched(new SearchCriteria { Text = "Ballas" }, watched: false);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _saved.SetWatchedAsync(id, true, ClaimsPrincipalBuilder.Agent("other").WithRank(Rank.SpecialAgent)));
        Assert.False(Row(id).IsWatched);
    }

    [Fact]
    public async Task Read_only_supervision_partners_and_demo_cannot_watch()
    {
        SeedOwner();
        var id = SeedWatched(new SearchCriteria { Text = "Ballas" }, watched: false);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _saved.SetWatchedAsync(id, true, ClaimsPrincipalBuilder.Agent("owner").AsTeamLead()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _saved.SetWatchedAsync(id, true, ClaimsPrincipalBuilder.Agent("owner").AsDemo()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => _saved.SetWatchedAsync(id, true,
                ClaimsPrincipalBuilder.Agent("owner").AsPartner(PartnerAgency.LSPD, PartnerRank.Member)));
        Assert.False(Row(id).IsWatched);
    }

    // ---- marks ----

    [Fact]
    public async Task Marking_seen_clears_what_the_page_showed()
    {
        SeedOwner();
        var shown = SavedSearchRules.WriteKeys(["Person:1"]);
        var id = SeedWatched(new SearchCriteria { Text = "Ballas" }, configure: g => g.UnseenHitsJson = shown);

        await _saved.MarkSeenAsync(id, shown, Owner());

        Assert.Null(Row(id).UnseenHitsJson);
    }

    [Fact]
    public async Task Marks_a_run_added_after_the_page_loaded_stay()
    {
        SeedOwner();
        var shown = SavedSearchRules.WriteKeys(["Person:1"]);
        var now = SavedSearchRules.WriteKeys(["Person:2", "Person:1"]);
        var id = SeedWatched(new SearchCriteria { Text = "Ballas" }, configure: g => g.UnseenHitsJson = now);

        await _saved.MarkSeenAsync(id, shown, Owner());

        Assert.Equal(now, Row(id).UnseenHitsJson);
    }

    [Fact]
    public async Task Nobody_clears_someone_elses_marks()
    {
        SeedOwner();
        var shown = SavedSearchRules.WriteKeys(["Person:1"]);
        var id = SeedWatched(new SearchCriteria { Text = "Ballas" }, configure: g => g.UnseenHitsJson = shown);

        await _saved.MarkSeenAsync(id, shown, ClaimsPrincipalBuilder.Agent("other"));
        await _saved.MarkSeenAsync(id, shown, ClaimsPrincipalBuilder.Agent("owner").AsTeamLead());

        Assert.Equal(shown, Row(id).UnseenHitsJson);
    }

    // ---- comparing two runs ----

    [Fact]
    public void A_hit_that_drops_out_and_comes_back_is_not_news()
    {
        // the budget cut the factions off in one run; the next complete run must not report f1 again
        var cut = SavedSearchRules.Compare(["Person:p1", "Faction:f1"], baseline: false,
            Results(incomplete: ["Faction"], groups: [Group("Person", "p1")]));
        var full = SavedSearchRules.Compare(cut.Known, baseline: false,
            Results(groups: [Group("Person", "p1"), Group("Faction", "f1")]));

        Assert.Equal(["Person:p1", "Faction:f1"], cut.Known);
        Assert.Empty(cut.Fresh);
        Assert.Empty(full.Fresh);
    }

    [Fact]
    public void The_current_run_comes_first_so_the_cap_trims_the_oldest()
    {
        var outcome = SavedSearchRules.Compare(["Person:p1"], baseline: false,
            Results(groups: [Group("Person", capped: true, "p2")]));

        Assert.Equal(["Person:p2", "Person:p1"], outcome.Known);
        Assert.Equal("p2", Assert.Single(outcome.Fresh).TargetId);
    }

    [Fact]
    public void A_baseline_records_without_reporting()
    {
        var results = Results(groups: [Group("Person", "p1", "p2")]);

        Assert.Empty(SavedSearchRules.Compare(null, baseline: false, results).Fresh);
        Assert.Empty(SavedSearchRules.Compare(["Person:p1"], baseline: true, results).Fresh);
    }

    [Fact]
    public void The_title_names_the_first_hits_and_counts_the_rest()
    {
        var fresh = new[] { "A", "B", "C", "D", "E" }.Select(t => new SearchHit("Person", t, t, "", "")).ToList();

        var title = SavedSearchRules.Title("Ballas", 7, fresh);

        Assert.Equal("Gespeicherte Suche „Ballas“: 7 neue Treffer – A, B, C und 2 weitere", title);
    }

    [Fact]
    public void An_old_multi_category_row_is_watched_across_everything_watchable()
    {
        var legacy = new SearchCriteria { Text = "Ballas", Categories = { "Person", "Faction" } };
        var single = new SearchCriteria { Text = "Ballas", Categories = { "Person" } };

        Assert.Equal(SavedSearchRules.WatchableCategories, SavedSearchRules.ForWatch(legacy).Categories);
        Assert.Equal(["Person"], SavedSearchRules.ForWatch(single).Categories);
    }

    [Theory]
    [InlineData("Notification")]
    [InlineData("SavedSearch")]
    [InlineData("TextSnippet")]
    [InlineData("AuditLog")]
    [InlineData("AccessLog")]
    public void A_watch_never_searches_own_entries_or_logs(string category)
    {
        // the report names the search, so a watch over notifications would report its own report forever
        Assert.DoesNotContain(category, SavedSearchRules.ForWatch(new SearchCriteria { Text = "Ballas" }).Categories);
        Assert.False(SavedSearchRules.IsWatchable(new SearchCriteria { Text = "Ballas", Facet = category }));
        Assert.Equal(SavedSearchRules.WatchableCategories,
            SavedSearchRules.ForWatch(new SearchCriteria { Text = "Ballas", Facet = category }).Categories);
    }

    [Fact]
    public async Task The_report_and_the_saved_search_are_not_hits_of_the_watch()
    {
        SeedOwner();
        var id = SeedWatched(new SearchCriteria { Text = "Ballas" });
        await _watch.RunDueAsync(T0);
        SeedPerson("Ballas Joe");
        await _watch.RunDueAsync(T0.AddHours(1));

        // the bell line now says "Ballas" too; the next run must not report it
        Assert.Equal(0, await _watch.RunDueAsync(T0.AddHours(2)));
        Assert.DoesNotContain(SavedSearchRules.ReadKeys(Row(id).KnownHitsJson),
            k => k.StartsWith("Notification:") || k.StartsWith("SavedSearch:"));
    }

    [Fact]
    public void The_report_pings_only_its_owner()
    {
        Assert.True(DiscordRouting.IsRoutable(NotificationType.SavedSearchHit));
        Assert.True(DiscordRouting.PingsRecipients(NotificationType.SavedSearchHit));
        Assert.False(DiscordRouting.PingsRole(NotificationType.SavedSearchHit));
    }

    // ---- helpers ----

    private static SearchResultGroup Group(string category, params string[] ids) => Group(category, false, ids);

    private static SearchResultGroup Group(string category, bool capped, params string[] ids)
        => new(category, category, ids.Select(id => new SearchHit(category, id, id, "", "")).ToList(), capped);

    private static SearchResults Results(IReadOnlyList<SearchResultGroup> groups, IReadOnlyList<string>? incomplete = null)
        => new(groups, groups.Sum(g => g.Hit.Count), groups.Select(g => g.Category).ToList(), incomplete ?? [], 10,
            TimeSpan.Zero);

    private static ClaimsPrincipalBuilder Owner() => ClaimsPrincipalBuilder.Agent("owner").WithRank(Rank.SpecialAgent);

    private void SeedOwner(Rank rank = Rank.SpecialAgent) => SeedOwner("owner", rank);

    private void SeedOwner(string id, Rank rank = Rank.SpecialAgent)
    {
        using var db = _ctx.NewContext();
        db.Users.Add(Seed.Agent(id, rank));
        db.SaveChanges();
    }

    private void UpdateOwner(Action<Agent> change)
    {
        using var db = _ctx.NewContext();
        change(db.Users.Single(u => u.Id == "owner"));
        db.SaveChanges();
    }

    private string SeedPerson(string name, bool classified = false)
    {
        using var db = _ctx.NewContext();
        var person = Seed.Person(name: name, configure: p => p.IsClassified = classified);
        db.People.Add(person);
        db.SaveChanges();
        return person.Id;
    }

    private string SeedWatched(SearchCriteria criteria, bool watched = true, Action<SavedSearch>? configure = null)
    {
        using var db = _ctx.NewContext();
        var row = new SavedSearch
        {
            AgentId = "owner",
            Name = criteria.Text?.Trim() is { Length: > 0 } text ? text : "Suche",
            SearchParameterJson = JsonSerializer.Serialize(criteria),
            IsWatched = watched,
        };
        configure?.Invoke(row);
        db.SavedSearch.Add(row);
        db.SaveChanges();
        return row.Id;
    }

    private void SeedComment(string personId, string text)
    {
        using var db = _ctx.NewContext();
        db.Comments.Add(new Comment { EntityType = "Person", EntityId = personId, Text = text });
        db.SaveChanges();
    }

    /// <summary>A watch whose every search ran out of time before any category answered.</summary>
    private SavedSearchWatchService Starved()
        => new(_ctx.Factory, new StarvedSearch(), new FreshPrincipals(_ctx),
            Substitute.For<INotificationService>(), NullLogger<SavedSearchWatchService>.Instance);

    private SavedSearch Row(string id)
    {
        using var db = _ctx.NewContext();
        return db.SavedSearch.AsNoTracking().Single(g => g.Id == id);
    }

    private List<Notification> Notices()
    {
        using var db = _ctx.NewContext();
        return db.Notifications.AsNoTracking().Where(n => n.RecipientId == "owner").ToList();
    }

    private sealed class StarvedSearch : ISearchService
    {
        public Task<SearchResults> SearchAsync(SearchCriteria criteria, System.Security.Claims.ClaimsPrincipal actor,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new SearchResults([], 0, [], criteria.Categories, criteria.Categories.Count,
                TimeSpan.FromSeconds(8)));

        public Task<List<QuickHit>> QuickSearchAsync(string text, System.Security.Claims.ClaimsPrincipal actor, int max = 8,
            CancellationToken cancellationToken = default)
            => Task.FromResult(new List<QuickHit>());
    }

    private sealed class SystemUser : ICurrentUserService
    {
        public Task<CurrentUserInfo> GetAsync() => Task.FromResult(CurrentUserInfo.System);

        public CurrentUserInfo Get() => CurrentUserInfo.System;
    }

    /// <summary>The production principal service over a fresh context per call, like one worker scope per tick.</summary>
    private sealed class FreshPrincipals(SqliteTestContext ctx) : IAgentPrincipalService
    {
        public async Task<System.Security.Claims.ClaimsPrincipal?> BuildAsync(string agentId, CancellationToken cancellationToken = default)
        {
            await using var db = ctx.NewContext();
            var users = new UserManager<Agent>(new UserStore<Agent>(db), Options.Create(new IdentityOptions()),
                new PasswordHasher<Agent>(), [], [], new UpperInvariantLookupNormalizer(),
                new IdentityErrorDescriber(), null!, NullLogger<UserManager<Agent>>.Instance);
            var roles = new RoleManager<IdentityRole>(new RoleStore<IdentityRole>(db), [],
                new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(),
                NullLogger<RoleManager<IdentityRole>>.Instance);
            var claims = new AgentClaimsPrincipalFactory(users, roles, Options.Create(new IdentityOptions()),
                new ConfigurationBuilder().Build());
            return await new AgentPrincipalService(users, claims).BuildAsync(agentId, cancellationToken);
        }
    }
}
