using System.Security.Claims;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services;

public class ClassificationHelperTests
{
    // ---- CheckRankGate: throw cases ----

    [Theory]
    [InlineData(Rank.Trainee)]
    [InlineData(Rank.SpecialAgent)] // boundary: rank 2, one below SeniorSpecialAgent
    public void CheckRankGate_SecuredStateThreatening_belowSeniorSpecialAgent_throws(Rank rank)
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithRank(rank);

        Assert.Throws<InvalidOperationException>(
            () => ClassificationHelper.CheckRankGate(Classification.SecuredStateThreatening, actor));
    }

    [Fact]
    public void CheckRankGate_SecuredStateThreatening_anonymousActor_throws()
    {
        var actor = ClaimsPrincipalBuilder.Anonymous();

        Assert.Throws<InvalidOperationException>(
            () => ClassificationHelper.CheckRankGate(Classification.SecuredStateThreatening, actor));
    }

    [Fact]
    public void CheckRankGate_SecuredStateThreatening_agentWithoutRankClaim_throws()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent(); // active but no rank

        Assert.Throws<InvalidOperationException>(
            () => ClassificationHelper.CheckRankGate(Classification.SecuredStateThreatening, actor));
    }

    // ---- CheckRankGate: pass cases ----

    [Theory]
    [InlineData(Rank.SeniorSpecialAgent)] // boundary: rank 3, exactly the threshold
    [InlineData(Rank.SpecialAgentInCharge)]
    [InlineData(Rank.DeputyDirector)]
    [InlineData(Rank.Director)]
    public void CheckRankGate_SecuredStateThreatening_atOrAboveSeniorSpecialAgent_doesNotThrow(Rank rank)
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithRank(rank);

        Assert.Null(Record.Exception(
            () => ClassificationHelper.CheckRankGate(Classification.SecuredStateThreatening, actor)));
    }

    [Fact]
    public void CheckRankGate_SecuredStateThreatening_adminWithLowRank_doesNotThrow()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithRank(Rank.Trainee).AsAdmin();

        Assert.Null(Record.Exception(
            () => ClassificationHelper.CheckRankGate(Classification.SecuredStateThreatening, actor)));
    }

    [Fact]
    public void CheckRankGate_SecuredStateThreatening_adminWithoutRank_doesNotThrow()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().AsAdmin();

        Assert.Null(Record.Exception(
            () => ClassificationHelper.CheckRankGate(Classification.SecuredStateThreatening, actor)));
    }

    // ---- CheckRankGate: lower classifications never gated ----

    [Theory]
    [InlineData(Classification.Unknown)]
    [InlineData(Classification.ReviewCase)]
    [InlineData(Classification.SuspicionCase)]
    public void CheckRankGate_belowSecured_neverThrows_evenForAnonymous(Classification value)
    {
        var actor = ClaimsPrincipalBuilder.Anonymous();

        Assert.Null(Record.Exception(() => ClassificationHelper.CheckRankGate(value, actor)));
    }

    [Theory]
    [InlineData(Classification.Unknown)]
    [InlineData(Classification.ReviewCase)]
    [InlineData(Classification.SuspicionCase)]
    public void CheckRankGate_belowSecured_neverThrows_forLowRankAgent(Classification value)
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent().WithRank(Rank.Trainee);

        Assert.Null(Record.Exception(() => ClassificationHelper.CheckRankGate(value, actor)));
    }

    // ---- Entry: full population ----

    [Fact]
    public void Entry_populatesAllFieldsFromArgumentsAndActor()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent("agent-9").WithCodename("Ghost");

        var before = DateTime.UtcNow;
        var entry = ClassificationHelper.Entry("Person", "p-1", Classification.SuspicionCase, "reason", actor);
        var after = DateTime.UtcNow;

        Assert.Equal("Person", entry.EntityType);
        Assert.Equal("p-1", entry.EntityId);
        Assert.Equal(Classification.SuspicionCase, entry.Value);
        Assert.Equal("reason", entry.Justification);
        Assert.Equal("agent-9", entry.AgentId);
        Assert.Equal("Ghost", entry.AgentName);
        Assert.InRange(entry.Timestamp, before, after);
        Assert.Equal(DateTimeKind.Utc, entry.Timestamp.Kind);
        Assert.False(string.IsNullOrEmpty(entry.Id));
        Assert.Null(entry.RequestId);
    }

    // ---- Entry: justification normalization ----

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("\t\n", null)]
    [InlineData("reason", "reason")]
    [InlineData("  reason  ", "reason")]
    public void Entry_justification_isTrimmedOrNulled(string? input, string? expected)
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent();

        var entry = ClassificationHelper.Entry("Person", "p-1", Classification.ReviewCase, input, actor);

        Assert.Equal(expected, entry.Justification);
    }

    // ---- Entry: actor claim extraction ----

    [Fact]
    public void Entry_anonymousActor_leavesAgentFieldsNull()
    {
        var actor = ClaimsPrincipalBuilder.Anonymous();

        var entry = ClassificationHelper.Entry("Faction", "f-1", Classification.Unknown, null, actor);

        Assert.Null(entry.AgentId);
        Assert.Null(entry.AgentName);
    }

    [Fact]
    public void Entry_actorWithoutCodename_setsAgentIdButNullName()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent("agent-3");

        var entry = ClassificationHelper.Entry("Person", "p-2", Classification.ReviewCase, null, actor);

        Assert.Equal("agent-3", entry.AgentId);
        Assert.Null(entry.AgentName);
    }

    // ---- Entry: value and identifier pass-through ----

    [Theory]
    [InlineData(Classification.Unknown)]
    [InlineData(Classification.ReviewCase)]
    [InlineData(Classification.SuspicionCase)]
    [InlineData(Classification.SecuredStateThreatening)]
    public void Entry_value_passesThroughVerbatim(Classification value)
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent();

        var entry = ClassificationHelper.Entry("Person", "p-1", value, null, actor);

        Assert.Equal(value, entry.Value);
    }

    [Theory]
    [InlineData("Person", "p-1")]
    [InlineData("Faction", "f-99")]
    [InlineData("PersonGroup", "g-7")]
    [InlineData("", "")]
    public void Entry_entityIdentifiers_passThroughVerbatim(string type, string id)
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent();

        var entry = ClassificationHelper.Entry(type, id, Classification.ReviewCase, null, actor);

        Assert.Equal(type, entry.EntityType);
        Assert.Equal(id, entry.EntityId);
    }

    [Fact]
    public void Entry_generatesUniqueParsableGuidPerCall()
    {
        ClaimsPrincipal actor = ClaimsPrincipalBuilder.Agent();

        var a = ClassificationHelper.Entry("Person", "p-1", Classification.ReviewCase, null, actor);
        var b = ClassificationHelper.Entry("Person", "p-1", Classification.ReviewCase, null, actor);

        Assert.NotEqual(a.Id, b.Id);
        Assert.True(Guid.TryParse(a.Id, out _));
    }
}
