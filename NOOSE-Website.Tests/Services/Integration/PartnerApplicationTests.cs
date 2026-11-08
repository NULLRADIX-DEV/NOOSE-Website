using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>
/// A partner may apply to join without giving up partner access: the application is a row of its own and the
/// account stays <c>Active</c> with its agency. The applicant portal therefore cannot gate on
/// <c>IsApplicant()</c> — it gates on <c>MayApply()</c>, and this fixture pins both ends of that.
/// </summary>
public sealed class PartnerApplicationTests
{
    private static ClaimsPrincipal Partner()
        => ClaimsPrincipalBuilder.Agent("partner-1").WithRank(Rank.SpecialAgent)
            .WithStatus(AgentStatus.Active).AsPartner(PartnerAgency.DoJ, PartnerRank.Member).Build();

    private static ClaimsPrincipal Applicant()
        => ClaimsPrincipalBuilder.Agent("bewerber-1").WithStatus(AgentStatus.Applicant).Build();

    private static ClaimsPrincipal Citizen()
        => ClaimsPrincipalBuilder.Agent("buerger-1").WithStatus(AgentStatus.Civilian).Build();

    private static ClaimsPrincipal Agent()
        => ClaimsPrincipalBuilder.Agent("agent-1").WithRank(Rank.SpecialAgent)
            .WithStatus(AgentStatus.Active).Build();

    [Fact]
    public void MayApply_holds_for_an_applicant_and_for_a_partner()
    {
        Assert.True(Applicant().MayApply());
        Assert.True(Partner().MayApply());
    }

    [Fact]
    public void MayApply_stays_shut_for_an_agent_and_a_citizen()
    {
        // a citizen applies by being promoted to Applicant first, an agent is already inside the house
        Assert.False(Agent().MayApply());
        Assert.False(Citizen().MayApply());
    }

    [Fact]
    public void MayApply_stays_shut_for_the_supervision_and_the_demo_visitor()
    {
        // mirrors the barrier: an oversight seat is not a candidate, and the demo visitor is nobody
        var supervision = ClaimsPrincipalBuilder.Agent("aufsicht").WithRank(Rank.SpecialAgent)
            .WithStatus(AgentStatus.Active).AsPartner(PartnerAgency.DoJ, PartnerRank.Member)
            .AsTeamLead().Build();
        var demo = ClaimsPrincipalBuilder.Agent("demo").WithRank(Rank.SpecialAgent)
            .WithStatus(AgentStatus.Active).AsPartner(PartnerAgency.DoJ, PartnerRank.Member)
            .AsDemo().Build();

        Assert.False(supervision.MayApply());
        Assert.False(demo.MayApply());
    }

    [Fact]
    public void RequireApplicant_lets_a_partner_through_and_still_refuses_an_agent()
    {
        Permission.RequireApplicant(Partner());

        Assert.Throws<UnauthorizedAccessException>(() => Permission.RequireApplicant(Agent()));
    }

    /// <summary>The applicant tab is how a partner reaches the form, so the personal nav has to offer it.</summary>
    [Fact]
    public void The_personal_nav_offers_the_application_tab_to_a_partner()
    {
        Assert.Contains(CitizenNav.Application, CitizenNav.For(Partner()));
        Assert.DoesNotContain(CitizenNav.Application, CitizenNav.For(Citizen()));
    }

    private static UserManager<Agent> BuildUserManager(AppDbContext db)
        => new(new UserStore<Agent>(db), Options.Create(new IdentityOptions()),
            new PasswordHasher<Agent>(), Array.Empty<IUserValidator<Agent>>(),
            Array.Empty<IPasswordValidator<Agent>>(), new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(), null!, NullLogger<UserManager<Agent>>.Instance);

    /// <summary>Redeeming an invite ends the partner role at the moment the account becomes an agent.</summary>
    [Fact]
    public async Task Redeeming_an_invite_converts_a_partner_into_a_pending_agent()
    {
        using var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        db.Users.Add(Seed.Agent("partner-1", Rank.SpecialAgent, configure: a =>
        {
            a.PartnerAgency = PartnerAgency.DoJ;
            a.PartnerRank = PartnerRank.Member;
        }));
        db.AgentInvites.Add(new NOOSE_Website.Data.Entities.Recruiting.AgentInvite
        {
            Token = "token-1",
            ExpiresAt = DateTime.UtcNow.AddDays(1),
            CreatedByName = "HRB",
        });
        db.Bewerbungen.Add(new NOOSE_Website.Data.Entities.Recruiting.Bewerbung
        {
            CaseNumber = "FIB-B-2026-0001", ApplicantUserId = "partner-1", Name = "Trevor Ward",
        });
        await db.SaveChangesAsync();

        var svc = new AgentInviteService(ctx.Factory, BuildUserManager(db));
        Assert.True(await svc.RedeemForExistingAsync("token-1", "partner-1"));

        var converted = await ctx.NewContext().Users.SingleAsync(u => u.Id == "partner-1");
        Assert.Equal(AgentStatus.Pending, converted.Status);
        Assert.Null(converted.PartnerAgency);
        Assert.Null(converted.PartnerRank);
    }

    /// <summary>An invite link is not a trapdoor: without an application the partner account stays untouched.</summary>
    [Fact]
    public async Task Redeeming_an_invite_leaves_a_partner_that_never_applied_alone()
    {
        using var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        db.Users.Add(Seed.Agent("partner-1", Rank.SpecialAgent, configure: a =>
        {
            a.PartnerAgency = PartnerAgency.DoJ;
            a.PartnerRank = PartnerRank.Member;
        }));
        db.AgentInvites.Add(new NOOSE_Website.Data.Entities.Recruiting.AgentInvite
        {
            Token = "token-1",
            ExpiresAt = DateTime.UtcNow.AddDays(1),
            CreatedByName = "HRB",
        });
        await db.SaveChangesAsync();

        var svc = new AgentInviteService(ctx.Factory, BuildUserManager(db));
        Assert.False(await svc.RedeemForExistingAsync("token-1", "partner-1"));

        var untouched = await ctx.NewContext().Users.SingleAsync(u => u.Id == "partner-1");
        Assert.Equal(AgentStatus.Active, untouched.Status);
        Assert.Equal(PartnerAgency.DoJ, untouched.PartnerAgency);
    }

    /// <summary>A citizen account holds no agency rights, so an invite alone turns it into a pending agent.</summary>
    [Fact]
    public async Task Redeeming_an_invite_converts_a_citizen_into_a_pending_agent()
    {
        using var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        db.Users.Add(Seed.Agent("citizen-1", Rank.Trainee, configure: a => a.Status = AgentStatus.Civilian));
        db.AgentInvites.Add(new NOOSE_Website.Data.Entities.Recruiting.AgentInvite
        {
            Token = "token-1",
            ExpiresAt = DateTime.UtcNow.AddDays(1),
            CreatedByName = "HRB",
        });
        await db.SaveChangesAsync();

        var svc = new AgentInviteService(ctx.Factory, BuildUserManager(db));
        Assert.True(await svc.RedeemForExistingAsync("token-1", "citizen-1"));

        var converted = await ctx.NewContext().Users.SingleAsync(u => u.Id == "citizen-1");
        Assert.Equal(AgentStatus.Pending, converted.Status);
        var invite = await ctx.NewContext().AgentInvites.SingleAsync();
        Assert.Equal("citizen-1", invite.UsedByUserId);
    }
}
