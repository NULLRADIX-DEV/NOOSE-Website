using System.Security.Claims;
using NOOSE_Website.Authorization;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;
using Xunit;

namespace NOOSE_Website.Tests.Services;

/// <summary>Covers the Permission.Require* server-side guards: pass paths, throw paths, ladders and boundaries.</summary>
public sealed class PermissionTests
{
    private static void AssertAllowed(Action guard)
        => Assert.Null(Record.Exception(guard));

    private static void AssertDenied(Action guard)
        => Assert.Throws<UnauthorizedAccessException>(guard);

    // ---------------------------------------------------------------- RequireLeadership

    [Theory]
    [InlineData(Rank.JuniorAgent, false)]
    [InlineData(Rank.SpecialAgent, false)]
    [InlineData(Rank.SeniorSpecialAgent, false)]
    [InlineData(Rank.SupervisorySpecialAgent, true)]
    [InlineData(Rank.DeputyDirector, true)]
    [InlineData(Rank.Director, true)]
    public void RequireLeadership_byRank_gatesAtSupervisory(Rank rank, bool allowed)
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithRank(rank);
        if (allowed)
        {
            AssertAllowed(() => Permission.RequireLeadership(actor));
        }
        else
        {
            AssertDenied(() => Permission.RequireLeadership(actor));
        }
    }

    [Fact]
    public void RequireLeadership_admin_passes()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().AsAdmin();
        AssertAllowed(() => Permission.RequireLeadership(actor));
    }

    [Fact]
    public void RequireLeadership_anonymous_throws()
        => AssertDenied(() => Permission.RequireLeadership(ClaimsPrincipalBuilder.Anonymous()));

    // ---------------------------------------------------------------- RequireWriteAccess

    [Fact]
    public void RequireWriteAccess_plainAgent_passes()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent();
        AssertAllowed(() => Permission.RequireWriteAccess(actor));
    }

    [Fact]
    public void RequireWriteAccess_admin_passes()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().AsAdmin();
        AssertAllowed(() => Permission.RequireWriteAccess(actor));
    }

    [Fact]
    public void RequireWriteAccess_teamLeadWithAdmin_passes()
    {
        // TeamLead + admin is NOT an only-reader, so writes are allowed.
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().AsTeamLead().AsAdmin();
        AssertAllowed(() => Permission.RequireWriteAccess(actor));
    }

    /// <summary>The guard and MayWrite() have to agree, or the interface and the service disagree about who writes.</summary>
    /// <remarks>
    /// This used to assert the opposite. The guard checked only-reader and partner, MayWrite() checked those two
    /// plus demo - so every button was hidden from the demo visitor while the service behind it let them through,
    /// and a whole write path ran before the barrier refused at SaveChanges. The demo SEEDING is unaffected:
    /// DemoAutoSetup.BuildActor never sets the demo claim, only admin and Director.
    /// </remarks>
    [Fact]
    public void RequireWriteAccess_demoVisitor_throws()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().AsDemo();
        AssertDenied(() => Permission.RequireWriteAccess(actor));
    }

    [Fact]
    public void RequireWriteAccess_onlyReader_throws()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().AsTeamLead();
        AssertDenied(() => Permission.RequireWriteAccess(actor));
    }

    [Fact]
    public void RequireWriteAccess_partner_throws()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().AsPartner(PartnerAgency.LSPD, PartnerRank.Member);
        AssertDenied(() => Permission.RequireWriteAccess(actor));
    }

    // ---------------------------------------------------------------- RequireAdmin

    [Fact]
    public void RequireAdmin_admin_passes()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().AsAdmin();
        AssertAllowed(() => Permission.RequireAdmin(actor));
    }

    [Fact]
    public void RequireAdmin_plainAgent_throws()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent();
        AssertDenied(() => Permission.RequireAdmin(actor));
    }

    [Fact]
    public void RequireAdmin_leadershipRankWithoutFlag_throws()
    {
        // Admin is a flag, never derived from rank.
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director);
        AssertDenied(() => Permission.RequireAdmin(actor));
    }

    [Fact]
    public void RequireAdmin_anonymous_throws()
        => AssertDenied(() => Permission.RequireAdmin(ClaimsPrincipalBuilder.Anonymous()));

    // ---------------------------------------------------------------- RequireBootstrapAdmin

    [Fact]
    public void RequireBootstrapAdmin_bootstrap_passes()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().AsBootstrap();
        AssertAllowed(() => Permission.RequireBootstrapAdmin(actor));
    }

    [Fact]
    public void RequireBootstrapAdmin_plainAgent_throws()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent();
        AssertDenied(() => Permission.RequireBootstrapAdmin(actor));
    }

    [Fact]
    public void RequireBootstrapAdmin_adminWithoutBootstrap_throws()
    {
        // Bootstrap is distinct from admin.
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().AsAdmin();
        AssertDenied(() => Permission.RequireBootstrapAdmin(actor));
    }

    // ---------------------------------------------------------------- RequireMayAssignClassification

    [Fact]
    public void RequireMayAssignClassification_none_passesForAnyone()
    {
        // None short-circuits before any viewer-scope check.
        AssertAllowed(() => Permission.RequireMayAssignClassification(
            ClaimsPrincipalBuilder.Agent(), DocumentClassification.None));
        AssertAllowed(() => Permission.RequireMayAssignClassification(
            ClaimsPrincipalBuilder.Anonymous(), DocumentClassification.None));
    }

    [Theory]
    // Leadership level: only classified-readers may assign.
    [InlineData(DocumentClassification.Leadership, false, false, false, false)]
    [InlineData(DocumentClassification.Leadership, false, false, true, true)]
    // Tru level: classified-readers OR TRU.
    [InlineData(DocumentClassification.Tru, false, false, false, false)]
    [InlineData(DocumentClassification.Tru, true, false, false, true)]
    [InlineData(DocumentClassification.Tru, false, false, true, true)]
    // Hrb level: classified-readers OR HRB.
    [InlineData(DocumentClassification.Hrb, false, false, false, false)]
    [InlineData(DocumentClassification.Hrb, false, true, false, true)]
    [InlineData(DocumentClassification.Hrb, false, false, true, true)]
    public void RequireMayAssignClassification_higherLevels_gatedByViewerScope(
        DocumentClassification classification, bool tru, bool hrb, bool leadership, bool allowed)
    {
        var builder = ClaimsPrincipalBuilder.Agent();
        if (tru) builder.AsTru();
        if (hrb) builder.AsHrb();
        if (leadership) builder.WithRank(Rank.SupervisorySpecialAgent);
        ClaimsPrincipal actor = builder.Build();

        if (allowed)
        {
            AssertAllowed(() => Permission.RequireMayAssignClassification(actor, classification));
        }
        else
        {
            AssertDenied(() => Permission.RequireMayAssignClassification(actor, classification));
        }
    }

    [Fact]
    public void RequireMayAssignClassification_truLevel_hrbMemberOnly_throws()
    {
        // HRB flag does not unlock the TRU level.
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().AsHrb();
        AssertDenied(() => Permission.RequireMayAssignClassification(actor, DocumentClassification.Tru));
    }

    [Fact]
    public void RequireMayAssignClassification_leadershipLevel_admin_passes()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().AsAdmin();
        AssertAllowed(() => Permission.RequireMayAssignClassification(actor, DocumentClassification.Leadership));
    }

    // ---------------------------------------------------------------- RequireMaySeeClassified

    [Fact]
    public void RequireMaySeeClassified_noneLevel_passesForAnyone()
    {
        // None is visible to all; CanSee(None) is always true.
        AssertAllowed(() => Permission.RequireMaySeeClassified(
            ClaimsPrincipalBuilder.Agent(), DocumentClassification.None));
    }

    [Theory]
    [InlineData(DocumentClassification.Leadership, false, false, false, false)]
    [InlineData(DocumentClassification.Leadership, false, false, true, true)]
    [InlineData(DocumentClassification.Tru, false, false, false, false)]
    [InlineData(DocumentClassification.Tru, true, false, false, true)]
    [InlineData(DocumentClassification.Hrb, false, false, false, false)]
    [InlineData(DocumentClassification.Hrb, false, true, false, true)]
    public void RequireMaySeeClassified_gatedByViewerScope(
        DocumentClassification level, bool tru, bool hrb, bool leadership, bool allowed)
    {
        var builder = ClaimsPrincipalBuilder.Agent();
        if (tru) builder.AsTru();
        if (hrb) builder.AsHrb();
        if (leadership) builder.WithRank(Rank.SupervisorySpecialAgent);
        ClaimsPrincipal actor = builder.Build();

        if (allowed)
        {
            AssertAllowed(() => Permission.RequireMaySeeClassified(actor, level));
        }
        else
        {
            AssertDenied(() => Permission.RequireMaySeeClassified(actor, level));
        }
    }

    [Fact]
    public void RequireMaySeeClassified_onlyReader_seesLeadershipLevel()
    {
        // Read-only supervision reads classified content.
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().AsTeamLead();
        AssertAllowed(() => Permission.RequireMaySeeClassified(actor, DocumentClassification.Leadership));
    }

    // ---------------------------------------------------------------- RequirePromotionDecide

    [Theory]
    [InlineData(Rank.JuniorAgent, false)]
    [InlineData(Rank.SpecialAgent, false)]
    [InlineData(Rank.SeniorSpecialAgent, false)]
    [InlineData(Rank.SupervisorySpecialAgent, false)]
    [InlineData(Rank.DeputyDirector, true)]
    [InlineData(Rank.Director, true)]
    public void RequirePromotionDecide_byRank_gatesAtDeputyDirector(Rank rank, bool allowed)
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithRank(rank);
        if (allowed)
        {
            AssertAllowed(() => Permission.RequirePromotionDecide(actor));
        }
        else
        {
            AssertDenied(() => Permission.RequirePromotionDecide(actor));
        }
    }

    [Fact]
    public void RequirePromotionDecide_admin_passes()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().AsAdmin();
        AssertAllowed(() => Permission.RequirePromotionDecide(actor));
    }

    [Fact]
    public void RequirePromotionDecide_anonymous_throws()
        => AssertDenied(() => Permission.RequirePromotionDecide(ClaimsPrincipalBuilder.Anonymous()));

    // ---------------------------------------------------------------- RequireHighestClassification

    [Theory]
    [InlineData(Rank.JuniorAgent, false)]
    [InlineData(Rank.SpecialAgent, false)]
    [InlineData(Rank.SeniorSpecialAgent, true)]
    [InlineData(Rank.SupervisorySpecialAgent, true)]
    [InlineData(Rank.DeputyDirector, true)]
    [InlineData(Rank.Director, true)]
    public void RequireHighestClassification_byRank_gatesAtSeniorSpecial(Rank rank, bool allowed)
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithRank(rank);
        if (allowed)
        {
            AssertAllowed(() => Permission.RequireHighestClassification(actor));
        }
        else
        {
            AssertDenied(() => Permission.RequireHighestClassification(actor));
        }
    }

    [Fact]
    public void RequireHighestClassification_admin_passes()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().AsAdmin();
        AssertAllowed(() => Permission.RequireHighestClassification(actor));
    }

    [Fact]
    public void RequireHighestClassification_rankless_throws()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent();
        AssertDenied(() => Permission.RequireHighestClassification(actor));
    }

    // ---------------------------------------------------------------- RequireClassifiedRead

    [Fact]
    public void RequireClassifiedRead_admin_passes()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().AsAdmin();
        AssertAllowed(() => Permission.RequireClassifiedRead(actor));
    }

    [Fact]
    public void RequireClassifiedRead_leadershipRank_passes()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithRank(Rank.SupervisorySpecialAgent);
        AssertAllowed(() => Permission.RequireClassifiedRead(actor));
    }

    [Fact]
    public void RequireClassifiedRead_onlyReader_passes()
    {
        // Read-only supervision is admitted to classified reads.
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().AsTeamLead();
        AssertAllowed(() => Permission.RequireClassifiedRead(actor));
    }

    [Fact]
    public void RequireClassifiedRead_plainAgent_throws()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent();
        AssertDenied(() => Permission.RequireClassifiedRead(actor));
    }

    [Fact]
    public void RequireClassifiedRead_partner_throws()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().AsPartner(PartnerAgency.DoJ, PartnerRank.Chief);
        AssertDenied(() => Permission.RequireClassifiedRead(actor));
    }

    // ---------------------------------------------------------------- RequireMeetingWrite

    [Fact]
    public void RequireMeetingWrite_admin_passes()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().AsAdmin();
        AssertAllowed(() => Permission.RequireMeetingWrite(actor));
    }

    [Fact]
    public void RequireMeetingWrite_seniorSpecialAgent_passes()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithRank(Rank.SeniorSpecialAgent);
        AssertAllowed(() => Permission.RequireMeetingWrite(actor));
    }

    [Fact]
    public void RequireMeetingWrite_belowHighestClassification_throws()
    {
        // Fails the highest-classification half.
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithRank(Rank.SpecialAgent);
        AssertDenied(() => Permission.RequireMeetingWrite(actor));
    }

    [Fact]
    public void RequireMeetingWrite_onlyReaderWithHighRank_throws()
    {
        // Has the classification rank but is read-only, so fails the write half.
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithRank(Rank.SeniorSpecialAgent).AsTeamLead();
        AssertDenied(() => Permission.RequireMeetingWrite(actor));
    }

    // ---------------------------------------------------------------- RequireEvidenceEntryWrite

    [Fact]
    public void RequireEvidenceEntryWrite_deposit_plainAgent_passes()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithRank(Rank.JuniorAgent);
        AssertAllowed(() => Permission.RequireEvidenceEntryWrite(actor, EvidenceEntryType.Deposit));
    }

    [Fact]
    public void RequireEvidenceEntryWrite_deposit_admin_passes()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().AsAdmin();
        AssertAllowed(() => Permission.RequireEvidenceEntryWrite(actor, EvidenceEntryType.Deposit));
    }

    [Fact]
    public void RequireEvidenceEntryWrite_deposit_onlyReader_throws()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().AsTeamLead();
        AssertDenied(() => Permission.RequireEvidenceEntryWrite(actor, EvidenceEntryType.Deposit));
    }

    [Fact]
    public void RequireEvidenceEntryWrite_deposit_partner_throws()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().AsPartner(PartnerAgency.DoJ, PartnerRank.Chief);
        AssertDenied(() => Permission.RequireEvidenceEntryWrite(actor, EvidenceEntryType.Deposit));
    }

    [Fact]
    public void RequireEvidenceEntryWrite_deposit_demoVisitor_throws()
    {
        // Demo carries Director rank, so only the MayWrite half can stop it.
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director).AsDemo();
        AssertDenied(() => Permission.RequireEvidenceEntryWrite(actor, EvidenceEntryType.Deposit));
    }

    [Theory]
    [InlineData(Rank.JuniorAgent, false)]
    [InlineData(Rank.SpecialAgent, false)]
    [InlineData(Rank.SeniorSpecialAgent, true)]
    [InlineData(Rank.SupervisorySpecialAgent, true)]
    [InlineData(Rank.DeputyDirector, true)]
    [InlineData(Rank.Director, true)]
    public void RequireEvidenceEntryWrite_withdrawal_byRank_gatesAtSenior(Rank rank, bool allowed)
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithRank(rank);
        if (allowed)
        {
            AssertAllowed(() => Permission.RequireEvidenceEntryWrite(actor, EvidenceEntryType.Withdrawal));
        }
        else
        {
            AssertDenied(() => Permission.RequireEvidenceEntryWrite(actor, EvidenceEntryType.Withdrawal));
        }
    }

    [Fact]
    public void RequireEvidenceEntryWrite_withdrawal_admin_passes()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().AsAdmin();
        AssertAllowed(() => Permission.RequireEvidenceEntryWrite(actor, EvidenceEntryType.Withdrawal));
    }

    [Fact]
    public void RequireEvidenceEntryWrite_withdrawal_onlyReaderWithHighRank_throws()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director).AsTeamLead();
        AssertDenied(() => Permission.RequireEvidenceEntryWrite(actor, EvidenceEntryType.Withdrawal));
    }

    // Anonymous has no rank, so only the rank arm stops it; the deposit arm is MayWrite-only,
    // exactly like RequireWriteAccess. Anonymous never reaches the service — every page is ActiveAgent.
    [Fact]
    public void RequireEvidenceEntryWrite_withdrawal_anonymous_throws()
        => AssertDenied(() => Permission.RequireEvidenceEntryWrite(
            ClaimsPrincipalBuilder.Anonymous(), EvidenceEntryType.Withdrawal));

    // ---------------------------------------------------------------- RequireEvidenceImageWrite

    [Fact]
    public void RequireEvidenceImageWrite_firstPicture_plainAgent_passes()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithRank(Rank.JuniorAgent);
        AssertAllowed(() => Permission.RequireEvidenceImageWrite(actor, itemHasImage: false));
    }

    [Fact]
    public void RequireEvidenceImageWrite_replacement_plainAgent_throws()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithRank(Rank.JuniorAgent);
        AssertDenied(() => Permission.RequireEvidenceImageWrite(actor, itemHasImage: true));
    }

    [Fact]
    public void RequireEvidenceImageWrite_replacement_admin_passes()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().AsAdmin();
        AssertAllowed(() => Permission.RequireEvidenceImageWrite(actor, itemHasImage: true));
    }

    [Fact]
    public void RequireEvidenceImageWrite_firstPicture_onlyReader_throws()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().AsTeamLead();
        AssertDenied(() => Permission.RequireEvidenceImageWrite(actor, itemHasImage: false));
    }

    // ---------------------------------------------------------------- RequireKassenBookingWrite

    [Fact]
    public void RequireKassenBookingWrite_einzahlung_plainAgent_passes()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithRank(Rank.JuniorAgent);
        AssertAllowed(() => Permission.RequireKassenBookingWrite(actor, KassenBuchungArt.Einzahlung));
    }

    [Fact]
    public void RequireKassenBookingWrite_einzahlung_admin_passes()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().AsAdmin();
        AssertAllowed(() => Permission.RequireKassenBookingWrite(actor, KassenBuchungArt.Einzahlung));
    }

    [Fact]
    public void RequireKassenBookingWrite_einzahlung_onlyReader_throws()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().AsTeamLead();
        AssertDenied(() => Permission.RequireKassenBookingWrite(actor, KassenBuchungArt.Einzahlung));
    }

    [Fact]
    public void RequireKassenBookingWrite_einzahlung_partner_throws()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().AsPartner(PartnerAgency.DoJ, PartnerRank.Chief);
        AssertDenied(() => Permission.RequireKassenBookingWrite(actor, KassenBuchungArt.Einzahlung));
    }

    [Fact]
    public void RequireKassenBookingWrite_einzahlung_demoVisitor_throws()
    {
        // Demo carries Director rank, so only the MayWrite half can stop it.
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director).AsDemo();
        AssertDenied(() => Permission.RequireKassenBookingWrite(actor, KassenBuchungArt.Einzahlung));
    }

    [Theory]
    [InlineData(Rank.JuniorAgent, false)]
    [InlineData(Rank.SpecialAgent, false)]
    [InlineData(Rank.SeniorSpecialAgent, false)]
    [InlineData(Rank.SupervisorySpecialAgent, true)]
    [InlineData(Rank.DeputyDirector, true)]
    [InlineData(Rank.Director, true)]
    public void RequireKassenBookingWrite_auszahlung_byRank_gatesAtSupervisory(Rank rank, bool allowed)
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithRank(rank);
        if (allowed)
        {
            AssertAllowed(() => Permission.RequireKassenBookingWrite(actor, KassenBuchungArt.Auszahlung));
        }
        else
        {
            AssertDenied(() => Permission.RequireKassenBookingWrite(actor, KassenBuchungArt.Auszahlung));
        }
    }

    [Fact]
    public void RequireKassenBookingWrite_korrektur_plainAgent_throws()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithRank(Rank.SeniorSpecialAgent);
        AssertDenied(() => Permission.RequireKassenBookingWrite(actor, KassenBuchungArt.Korrektur));
    }

    [Fact]
    public void RequireKassenBookingWrite_korrektur_admin_passes()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().AsAdmin();
        AssertAllowed(() => Permission.RequireKassenBookingWrite(actor, KassenBuchungArt.Korrektur));
    }

    [Fact]
    public void RequireKassenBookingWrite_auszahlung_onlyReaderWithHighRank_throws()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director).AsTeamLead();
        AssertDenied(() => Permission.RequireKassenBookingWrite(actor, KassenBuchungArt.Auszahlung));
    }

    // See the note on RequireEvidenceEntryWrite: the deposit arm is MayWrite-only by design.
    [Fact]
    public void RequireKassenBookingWrite_auszahlung_anonymous_throws()
        => AssertDenied(() => Permission.RequireKassenBookingWrite(
            ClaimsPrincipalBuilder.Anonymous(), KassenBuchungArt.Auszahlung));

    // ---------------------------------------------------------------- RequireHrbOrLeadership

    [Fact]
    public void RequireHrbOrLeadership_hrbFlag_passes()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().AsHrb();
        AssertAllowed(() => Permission.RequireHrbOrLeadership(actor));
    }

    [Fact]
    public void RequireHrbOrLeadership_leadershipRank_passes()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithRank(Rank.SupervisorySpecialAgent);
        AssertAllowed(() => Permission.RequireHrbOrLeadership(actor));
    }

    [Fact]
    public void RequireHrbOrLeadership_admin_passes()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().AsAdmin();
        AssertAllowed(() => Permission.RequireHrbOrLeadership(actor));
    }

    [Fact]
    public void RequireHrbOrLeadership_plainAgent_throws()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithRank(Rank.SpecialAgent);
        AssertDenied(() => Permission.RequireHrbOrLeadership(actor));
    }

    // ------------------------------------------------------- RequireHrbOrLeadershipWrite

    // The write variant behind the handbook and behind ticking off a training module. It differs from
    // RequireHrbOrLeadership in one load-bearing way: the write check stands first, so the read-only
    // supervision and the demo principal are refused despite carrying the rank and the flag.

    [Fact]
    public void RequireHrbOrLeadershipWrite_hrbWithoutRank_passes()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithRank(Rank.JuniorAgent).AsHrb();
        AssertAllowed(() => Permission.RequireHrbOrLeadershipWrite(actor));
    }

    [Fact]
    public void RequireHrbOrLeadershipWrite_leadershipRank_passes()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithRank(Rank.SupervisorySpecialAgent);
        AssertAllowed(() => Permission.RequireHrbOrLeadershipWrite(actor));
    }

    [Fact]
    public void RequireHrbOrLeadershipWrite_plainAgent_throws()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithRank(Rank.SeniorSpecialAgent);
        AssertDenied(() => Permission.RequireHrbOrLeadershipWrite(actor));
    }

    // director by rank, so RequireHrbOrLeadership would let this one through
    [Fact]
    public void RequireHrbOrLeadershipWrite_readOnlySupervision_throws()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director).AsTeamLead();
        AssertDenied(() => Permission.RequireHrbOrLeadershipWrite(actor));
    }

    // the demo principal carries HRB and Director together, which is exactly why the write check is first
    [Fact]
    public void RequireHrbOrLeadershipWrite_demo_throws()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithRank(Rank.Director).AsHrb().AsDemo();
        AssertDenied(() => Permission.RequireHrbOrLeadershipWrite(actor));
    }

    [Fact]
    public void RequireHrbOrLeadershipWrite_partnerWithHrbFlag_throws()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent()
            .AsPartner(PartnerAgency.LSPD, PartnerRank.Member).AsHrb();
        AssertDenied(() => Permission.RequireHrbOrLeadershipWrite(actor));
    }

    // ---------------------------------------------------------------- RequireApplicant

    [Fact]
    public void RequireApplicant_applicantStatus_passes()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithStatus(AgentStatus.Applicant);
        AssertAllowed(() => Permission.RequireApplicant(actor));
    }

    [Theory]
    [InlineData(AgentStatus.Active)]
    [InlineData(AgentStatus.Pending)]
    [InlineData(AgentStatus.Blocked)]
    [InlineData(AgentStatus.Terminated)]
    [InlineData(AgentStatus.Civilian)]
    public void RequireApplicant_nonApplicantStatus_throws(AgentStatus status)
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithStatus(status);
        AssertDenied(() => Permission.RequireApplicant(actor));
    }

    [Fact]
    public void RequireApplicant_anonymous_throws()
        => AssertDenied(() => Permission.RequireApplicant(ClaimsPrincipalBuilder.Anonymous()));

    // ---------------------------------------------------------------- RequireCitizenPortal

    [Theory]
    [InlineData(AgentStatus.Civilian)]
    [InlineData(AgentStatus.Active)]
    [InlineData(AgentStatus.Applicant)]
    [InlineData(AgentStatus.Pending)]
    [InlineData(AgentStatus.Blocked)]
    [InlineData(AgentStatus.Terminated)]
    public void RequireCitizenPortal_anySignedInStatus_passes(AgentStatus status)
    {
        // the citizen area is readable for every account; only submissions need a civilian identity
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithStatus(status);
        AssertAllowed(() => Permission.RequireCitizenPortal(actor));
    }

    [Fact]
    public void RequireCitizenPortal_partnerAndOnlyReader_pass()
    {
        AssertAllowed(() => Permission.RequireCitizenPortal(
            ClaimsPrincipalBuilder.Agent().AsPartner(PartnerAgency.LSPD, PartnerRank.Chief)));
        AssertAllowed(() => Permission.RequireCitizenPortal(
            ClaimsPrincipalBuilder.Agent().AsTeamLead().WithRank(Rank.Director)));
    }

    [Fact]
    public void RequireCitizenPortal_anonymous_throws()
        => AssertDenied(() => Permission.RequireCitizenPortal(ClaimsPrincipalBuilder.Anonymous()));

    // ------------------------------------------------------------ RequireCitizenSubmission

    // the three surprising ones are listed on purpose: MayCitizenSubmit checks authentication and the demo
    // principal, never the status, and a later status check would otherwise slip in unnoticed
    [Theory]
    [InlineData(AgentStatus.Civilian)]
    [InlineData(AgentStatus.Active)]
    [InlineData(AgentStatus.Applicant)]
    [InlineData(AgentStatus.Pending)]
    [InlineData(AgentStatus.Blocked)]
    [InlineData(AgentStatus.Terminated)]
    public void RequireCitizenSubmission_anySignedInStatus_passes(AgentStatus status)
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithStatus(status);
        AssertAllowed(() => Permission.RequireCitizenSubmission(actor));
    }

    [Fact]
    public void RequireCitizenSubmission_partnerAndOnlyReader_pass()
    {
        // looser than RequireWriteAccess on purpose: both open a ticket the way a citizen does
        AssertAllowed(() => Permission.RequireCitizenSubmission(
            ClaimsPrincipalBuilder.Agent().AsPartner(PartnerAgency.LSPD, PartnerRank.Chief)));
        AssertAllowed(() => Permission.RequireCitizenSubmission(
            ClaimsPrincipalBuilder.Agent().AsTeamLead().WithRank(Rank.Director)));
    }

    [Fact]
    public void RequireCitizenSubmission_demoAndAnonymous_throw()
    {
        // the demo principal is shared by every anonymous guest, so its "own" ticket would be everyone's
        AssertDenied(() => Permission.RequireCitizenSubmission(ClaimsPrincipalBuilder.Agent().AsDemo()));
        AssertDenied(() => Permission.RequireCitizenSubmission(ClaimsPrincipalBuilder.Anonymous()));
    }
}
