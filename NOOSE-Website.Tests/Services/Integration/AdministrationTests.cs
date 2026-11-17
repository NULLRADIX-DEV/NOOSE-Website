using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Administration;
using NOOSE_Website.Infrastructure.Chat;
using NOOSE_Website.Models.Administration;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Administration;
using NOOSE_Website.Services.Decorations;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The Administration workspace: who gets in, who sees a proceeding, bias, transfers, parliament, chat.</summary>
public class AdministrationTests
{
    private static ClaimsPrincipal Hrb(string id = "hrb") =>
        ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.SpecialAgent).AsHrb().WithCodename("Hrb");

    private static ClaimsPrincipal Board(string id = "board") =>
        ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.AssistantDirector).WithCodename("Board");

    private static ClaimsPrincipal Plain(string id, Rank rank = Rank.SpecialAgent) =>
        ClaimsPrincipalBuilder.Agent(id).WithRank(rank).WithCodename(id);

    private static void SeedAgent(SqliteTestContext ctx, string id, Rank rank = Rank.SpecialAgent, bool hrb = false)
    {
        using var db = ctx.NewContext();
        db.Users.Add(Seed.Agent(id, rank, configure: a =>
        {
            a.IsHRB = hrb;
            a.Division = Division.Cid;
        }));
        db.SaveChanges();
    }

    private static DisciplinaryService Disciplinary(SqliteTestContext ctx, INotificationService? notifications = null)
    {
        var numbers = Substitute.For<ICaseNumberService>();
        var n = 0;
        numbers.NextAsync(default!, default!, default).ReturnsForAnyArgs(_ => $"FIB-DV-2026-{++n:0000}");
        return new DisciplinaryService(ctx.Factory, numbers, notifications ?? Substitute.For<INotificationService>(),
            NullLogger<DisciplinaryService>.Instance);
    }

    private static AdministrationService Workspace(SqliteTestContext ctx)
        => new(ctx.Factory, Substitute.For<IDecorationService>(), Substitute.For<INotificationService>(),
            NullLogger<AdministrationService>.Instance);

    // ---- access ----

    [Fact]
    public void Division_board_and_admin_work_in_the_workspace_a_section_chief_does_not()
    {
        Assert.True(Hrb().IsAdministrationMember());
        Assert.True(Board().IsAdministrationMember());
        Assert.True(ClaimsPrincipalBuilder.Agent("a").WithRank(Rank.Trainee).AsAdmin().Build().IsAdministrationMember());
        Assert.False(Plain("sc", Rank.SectionChief).IsAdministrationMember());
        Assert.False(ClaimsPrincipalBuilder.Agent("p").AsPartner(PartnerAgency.Parlament, PartnerRank.Member).AsHrb().Build()
            .IsAdministrationMember());
    }

    [Fact]
    public void Supervision_and_demo_read_but_never_write()
    {
        var reader = ClaimsPrincipalBuilder.Agent("tl").WithRank(Rank.Director).AsTeamLead().Build();
        var demo = ClaimsPrincipalBuilder.Agent("demo").WithRank(Rank.Director).AsHrb().AsDemo().Build();

        Assert.True(reader.MayAdministrationRead());
        Assert.False(reader.MayAdministrationWrite());
        Assert.True(demo.MayAdministrationRead());
        Assert.False(demo.MayAdministrationWrite());
        Assert.Throws<UnauthorizedAccessException>(() => Permission.RequireAdministrationWrite(demo));
    }

    [Fact]
    public void Administration_writes_personnel_matters_a_plain_agent_does_not()
    {
        Assert.True(Hrb().MayPersonnelWrite());
        Assert.False(Plain("sa").MayPersonnelWrite());
        Assert.Throws<UnauthorizedAccessException>(() => Permission.RequirePersonnelWrite(Plain("sa")));
        // promotions are still decided from Section Chief up
        Assert.False(Hrb().MayPromotionDecide());
    }

    // ---- disciplinary proceedings ----

    [Fact]
    public async Task Accused_sees_nothing_until_the_proceeding_is_closed()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "hrb", hrb: true);
        SeedAgent(ctx, "target");
        var svc = Disciplinary(ctx);

        var id = await svc.OpenAsync(new DisciplinaryOpenInput { AgentId = "target", Grounds = "<p>Weisung missachtet</p>" }, Hrb());

        Assert.Null(await svc.GetAsync(id, Plain("target")));
        Assert.NotNull(await svc.GetAsync(id, Hrb()));

        await svc.CloseAsync(id, new DisciplinaryCloseInput
        {
            Decide = true,
            Measure = DisciplinaryMeasure.Reprimand,
            Facts = "<p>Sachverhalt</p>",
            Reasoning = "<p>Begründung</p>",
            RightsNotice = "Einspruch binnen sieben Tagen.",
        }, Hrb());

        var own = await svc.GetAsync(id, Plain("target"));
        Assert.NotNull(own);
        Assert.Equal(DisciplinaryStatus.Decided, own.Row.Status);
        Assert.False(own.MayHandle);
        Assert.Single(await svc.GetForAgentAsync("target", Plain("target")));
    }

    [Fact]
    public async Task Proceedings_against_the_division_belong_to_the_board()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "hrb", hrb: true);
        SeedAgent(ctx, "colleague", hrb: true);
        SeedAgent(ctx, "board", Rank.AssistantDirector);
        var svc = Disciplinary(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.OpenAsync(new DisciplinaryOpenInput { AgentId = "colleague", Grounds = "<p>x</p>" }, Hrb()));

        var id = await svc.OpenAsync(new DisciplinaryOpenInput { AgentId = "colleague", Grounds = "<p>x</p>" }, Board());

        Assert.NotNull(await svc.GetAsync(id, Board()));
        Assert.Null(await svc.GetAsync(id, Hrb()));
        Assert.Empty(await svc.GetListAsync(false, Hrb()));
    }

    [Fact]
    public async Task Nobody_handles_their_own_or_a_relatives_proceeding()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "hrb", hrb: true);
        SeedAgent(ctx, "partner");
        await Workspace(ctx).AddRelationshipAsync(new RelationshipInput
        {
            AgentAId = "partner",
            AgentBId = "hrb",
            Kind = AgentRelationshipKind.Marriage,
        }, Board());
        var svc = Disciplinary(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.OpenAsync(new DisciplinaryOpenInput { AgentId = "hrb", Grounds = "<p>x</p>" }, Hrb()));
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.OpenAsync(new DisciplinaryOpenInput { AgentId = "partner", Grounds = "<p>x</p>" }, Hrb()));
        Assert.Equal(PersonalRelationRules.BiasMessage, ex.Message);
    }

    [Fact]
    public async Task An_open_proceeding_blocks_a_promotion_but_not_a_demotion()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "hrb", hrb: true);
        SeedAgent(ctx, "target");
        await Disciplinary(ctx).OpenAsync(new DisciplinaryOpenInput { AgentId = "target", Grounds = "<p>x</p>" }, Hrb());

        await using var db = ctx.NewContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DisciplinaryRules.RequireNoOpenProceedingAsync(db, "target", Rank.SpecialAgent, Rank.SeniorSpecialAgent));
        await DisciplinaryRules.RequireNoOpenProceedingAsync(db, "target", Rank.SpecialAgent, Rank.Trainee);
    }

    [Fact]
    public async Task Simplified_hearing_is_for_trainees_only()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "hrb", hrb: true);
        SeedAgent(ctx, "trainee", Rank.Trainee);
        SeedAgent(ctx, "agent");
        var svc = Disciplinary(ctx);

        Assert.True(await svc.QualifiesForSimplifiedAsync("trainee"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.OpenAsync(new DisciplinaryOpenInput { AgentId = "agent", Grounds = "<p>x</p>", Simplified = true }, Hrb()));
        await svc.OpenAsync(new DisciplinaryOpenInput { AgentId = "trainee", Grounds = "<p>x</p>", Simplified = true }, Hrb());
    }

    // ---- relationships ----

    [Fact]
    public async Task A_relationship_is_one_pair_in_either_direction()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "a");
        SeedAgent(ctx, "b");
        var svc = Workspace(ctx);

        await svc.AddRelationshipAsync(new RelationshipInput { AgentAId = "b", AgentBId = "a" }, Hrb());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.AddRelationshipAsync(new RelationshipInput { AgentAId = "a", AgentBId = "b" }, Hrb()));

        await using var db = ctx.NewContext();
        Assert.True(await PersonalRelationRules.AreRelatedAsync(db, "a", "b"));
        Assert.True(await PersonalRelationRules.AreRelatedAsync(db, "b", "a"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.AddRelationshipAsync(new RelationshipInput { AgentAId = "a", AgentBId = "b" }, Plain("x")));
    }

    // ---- transfers ----

    [Fact]
    public async Task A_transfer_is_heard_before_the_board_grants_it()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "agent");
        SeedAgent(ctx, "hrb", hrb: true);
        var agents = Substitute.For<IAgentManagementService>();
        var svc = new TransferService(ctx.Factory, agents, Substitute.For<INotificationService>(),
            NullLogger<TransferService>.Instance);

        var id = await svc.RequestAsync(new TransferInput { AgentId = "agent", ToDivision = Division.Ctd, Reason = "Wechsel" },
            Plain("agent"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DecideAsync(id, true, null, false, Board()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.HearAsync(id, "angehört", Plain("agent")));

        await svc.HearAsync(id, "Agent möchte in die CTD.", Hrb());
        await svc.DecideAsync(id, true, null, false, Board());

        await agents.Received(1).DivisionSetAsync("agent", Division.Ctd, false, Arg.Any<ClaimsPrincipal>(), id);
        var row = Assert.Single(await svc.GetRequestsAsync(Hrb()));
        Assert.Equal(TransferRequestStatus.Granted, row.Status);
    }

    [Fact]
    public async Task An_administration_member_does_not_hear_their_own_transfer()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "hrb", hrb: true);
        var svc = new TransferService(ctx.Factory, Substitute.For<IAgentManagementService>(),
            Substitute.For<INotificationService>(), NullLogger<TransferService>.Instance);

        var id = await svc.RequestAsync(new TransferInput { AgentId = "hrb", ToDivision = Division.Ctd, Reason = "Wechsel" }, Hrb());

        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.HearAsync(id, "angehört", Hrb()));
        Assert.Contains("eigene", ex.Message);
    }

    [Fact]
    public async Task The_related_side_does_not_remove_its_own_relationship()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "hrb", hrb: true);
        SeedAgent(ctx, "partner");
        var svc = Workspace(ctx);
        var id = await svc.AddRelationshipAsync(new RelationshipInput { AgentAId = "hrb", AgentBId = "partner" }, Board());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RemoveRelationshipAsync(id, Hrb()));
        await svc.RemoveRelationshipAsync(id, Board());
        Assert.Empty(await svc.GetRelationshipsAsync(Board()));
    }

    [Fact]
    public async Task Only_the_agent_leadership_or_administration_may_request_a_transfer()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "agent");
        var svc = new TransferService(ctx.Factory, Substitute.For<IAgentManagementService>(),
            Substitute.For<INotificationService>(), NullLogger<TransferService>.Instance);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.RequestAsync(new TransferInput { AgentId = "agent", ToDivision = Division.Ctd, Reason = "x" }, Plain("other")));
    }

    // ---- parliament ----

    [Fact]
    public async Task Parliament_reads_the_answer_only_once_it_is_released()
    {
        using var ctx = new SqliteTestContext();
        var numbers = Substitute.For<ICaseNumberService>();
        numbers.NextAsync(default!, default!, default).ReturnsForAnyArgs("FIB-PA-2026-0001");
        var svc = new ParliamentService(ctx.Factory, numbers, Substitute.For<INotificationService>(),
            NullLogger<ParliamentService>.Instance);
        var mp = ClaimsPrincipalBuilder.Agent("mp").AsPartner(PartnerAgency.Parlament, PartnerRank.Member).WithCodename("MdP").Build();
        var lspd = ClaimsPrincipalBuilder.Agent("lspd").AsPartner(PartnerAgency.LSPD, PartnerRank.Member).Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.FileAsync(new ParliamentInput { Title = "T", Question = "Q" }, lspd));
        var id = await svc.FileAsync(new ParliamentInput { Title = "Haushalt", Question = "Wie viel?" }, mp);
        SeedAgent(ctx, "hrb", hrb: true);
        await svc.AssignAsync(id, "hrb", Hrb());
        Assert.Equal("hrb", (await svc.GetAsync(id, Hrb()))!.Row.AssignedToId);

        await svc.AnswerAsync(id, "Entwurf", publish: false, Hrb());
        Assert.Null((await svc.GetAsync(id, mp))!.Row.Answer);
        Assert.Equal("Entwurf", (await svc.GetAsync(id, Hrb()))!.Row.Answer);
        Assert.Empty(await svc.GetListAsync(lspd));

        await svc.AnswerAsync(id, "Antwort", publish: true, Hrb());
        var view = await svc.GetAsync(id, mp);
        Assert.Equal("Antwort", view!.Row.Answer);
        Assert.Null(view.Row.AssignedToId);
        Assert.Null(view.Row.AnsweredByName);
        Assert.False(view.MayAnswer);
    }

    // ---- chat ----

    [Fact]
    public async Task Chat_counts_others_messages_until_read()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "hrb", hrb: true);
        SeedAgent(ctx, "other", hrb: true);
        var svc = new AdministrationChatService(ctx.Factory, new AdministrationChatBroadcaster(),
            Substitute.For<INotificationService>());

        await svc.MarkReadAsync(Hrb());
        await using (var db = ctx.NewContext())
        {
            db.AdministrationMessages.Add(new AdministrationMessage
            {
                Text = "Hallo",
                CreatedById = "other",
                CreatedAt = DateTime.UtcNow.AddMinutes(1),
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, (await svc.GetReadStateAsync(Hrb())).Unread);
        Assert.Equal(AdministrationChatState.None, await svc.GetReadStateAsync(Plain("outsider")));
        Assert.Empty(await svc.GetMessagesAsync(Plain("outsider")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.SendAsync("x", Plain("outsider")));
    }

    // ---- decoration proposals ----

    [Fact]
    public async Task Nobody_proposes_themself()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "agent");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Workspace(ctx).ProposeDecorationAsync(
            new DecorationProposalInput { AgentId = "agent", DecorationId = "d", Reason = "Ich war gut." }, Plain("agent")));
    }

    // ---- personnel files, firearm use, presentations ----

    [Fact]
    public void Administration_reads_personnel_files_and_firearm_use_a_plain_agent_does_not()
    {
        var partner = ClaimsPrincipalBuilder.Agent("p").AsPartner(PartnerAgency.Parlament, PartnerRank.Member).AsHrb().Build();

        Assert.True(Hrb().MayPersonnelRead());
        Assert.True(FirearmUseRules.MayRead(Hrb(), "other"));
        Permission.RequireFirearmUseOverview(Hrb());
        Permission.RequirePersonnelRead(Board());

        Assert.False(Plain("agent").MayPersonnelRead());
        Assert.False(FirearmUseRules.MayRead(Plain("agent"), "other"));
        Assert.True(FirearmUseRules.MayRead(Plain("agent"), "agent"));
        Assert.Throws<UnauthorizedAccessException>(() => Permission.RequireFirearmUseOverview(Plain("agent")));
        Assert.False(partner.MayPersonnelRead());
    }

    [Fact]
    public async Task Administration_comments_on_personnel_files_like_leadership()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "agent");
        await using var db = ctx.NewContext();

        Assert.True(await Visibility.IsRecordVisibleAsync(db, nameof(Agent), "agent", ViewerScope.From(Hrb())));
        Assert.False(await Visibility.IsRecordVisibleAsync(db, nameof(Agent), "agent", ViewerScope.From(Plain("other"))));
    }

    [Fact]
    public async Task Administration_sees_leadership_briefings_but_not_other_rank_groups()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "owner", Rank.SectionChief);
        using (var seed = ctx.NewContext())
        {
            seed.Presentations.Add(new NOOSE_Website.Data.Entities.Presentations.Presentation
            {
                Id = "fe", Title = "Führung", Access = PresentationAccess.Closed, Audience = AnnouncementAudience.FromRank,
                MinRank = Rank.SectionChief, OwnerId = "owner",
            });
            seed.Presentations.Add(new NOOSE_Website.Data.Entities.Presentations.Presentation
            {
                Id = "ssa", Title = "Ab SSA", Access = PresentationAccess.Closed, Audience = AnnouncementAudience.FromRank,
                MinRank = Rank.SeniorSpecialAgent, OwnerId = "owner",
            });
            seed.SaveChanges();
        }
        await using var db = ctx.NewContext();

        Assert.True(await PresentationVisibility.IsVisibleAsync(db, "fe", ViewerScope.From(Hrb())));
        Assert.False(await PresentationVisibility.IsVisibleAsync(db, "ssa", ViewerScope.From(Hrb())));
        Assert.False(await PresentationVisibility.IsVisibleAsync(db, "fe", ViewerScope.From(Plain("agent"))));
        Assert.True(await PresentationVisibility.MayTargetAsync(db, Hrb(), AnnouncementAudience.FromRank, null, Rank.SectionChief));
        Assert.False(await PresentationVisibility.MayTargetAsync(db, Hrb(), AnnouncementAudience.FromRank, null, Rank.SeniorSpecialAgent));
        Assert.False(await PresentationVisibility.MayTargetAsync(db, Plain("agent"), AnnouncementAudience.FromRank, null, Rank.SectionChief));
    }

    [Fact]
    public void The_list_search_matches_every_term_in_text_columns_but_never_ids()
    {
        var rows = new[]
        {
            new SearchRow("abc123", "Falcon", "CID"),
            new SearchRow("falcon-id", "Raven", "HRT"),
        };

        Assert.Equal(["Falcon"], AdministrationListFilter.Apply(rows, "falc").Select(r => r.Name));
        Assert.Equal(["Raven"], AdministrationListFilter.Apply(rows, "raven hrt").Select(r => r.Name));
        Assert.Empty(AdministrationListFilter.Apply(rows, "raven cid"));
        Assert.Equal(2, AdministrationListFilter.Apply(rows, "  ").Count());
    }

    [Fact]
    public void Administration_reads_citizen_tickets_but_does_not_answer_them()
    {
        Assert.True(Hrb().MayTicketRead());
        Permission.RequireTicketRead(Hrb());
        Assert.True(NOOSE_Website.Services.Public.TicketVisibility.ReadsDesk(Hrb()));
        Assert.False(NOOSE_Website.Services.Public.TicketVisibility.IsDesk(Hrb()));
        Assert.Throws<UnauthorizedAccessException>(() => Permission.RequireTicketHandling(Hrb()));

        Assert.False(Plain("agent").MayTicketRead());
        Assert.Throws<UnauthorizedAccessException>(() => Permission.RequireTicketRead(Plain("agent")));
    }

    private sealed record SearchRow(string AgentId, string Name, string Unit);
}
