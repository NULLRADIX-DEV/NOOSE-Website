using System.Security.Claims;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

public class AgentAvatarTests
{
    private static Agent Owner(string? active = null, string? staged = null)
        => new()
        {
            Id = "owner",
            Codename = "Falcon",
            AvatarFileName = active,
            AvatarContentType = active is null ? null : "image/png",
            PendingAvatarFileName = staged,
            PendingAvatarContentType = staged is null ? null : "image/webp",
        };

    private static ClaimsPrincipal Junior(string id)
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).Build();

    private static ClaimsPrincipal Leader(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.SpecialAgentInCharge).Build();

    // ---- Url ----

    [Fact]
    public void Url_PointsAtTheServingRoute()
        => Assert.Equal("/dateien/agenten/profilbild/abc.png", AgentAvatar.Url("abc.png"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Url_IsNull_WithoutFileName(string? fileName)
        => Assert.Null(AgentAvatar.Url(fileName));

    // ---- ServableContentType ----

    [Fact]
    public void ReleasedPicture_IsOpenToEveryAgent()
    {
        var agent = Owner(active: "aktiv.png");

        Assert.Equal("image/png", AgentAvatar.ServableContentType(agent, "aktiv.png", Junior("someone")));
    }

    [Fact]
    public void StagedPicture_ReachesItsOwner()
    {
        var agent = Owner(staged: "neu.webp");

        Assert.Equal("image/webp", AgentAvatar.ServableContentType(agent, "neu.webp", Junior("owner")));
    }

    [Fact]
    public void StagedPicture_ReachesLeadership_WhoHasToDecide()
    {
        var agent = Owner(staged: "neu.webp");

        Assert.Equal("image/webp", AgentAvatar.ServableContentType(agent, "neu.webp", Leader()));
    }

    [Fact]
    public void StagedPicture_IsHiddenFromEveryoneElse()
    {
        var agent = Owner(active: "aktiv.png", staged: "neu.webp");

        Assert.Null(AgentAvatar.ServableContentType(agent, "neu.webp", Junior("nosy")));
        // the released one stays reachable for the same viewer
        Assert.Equal("image/png", AgentAvatar.ServableContentType(agent, "aktiv.png", Junior("nosy")));
    }

    [Fact]
    public void UnknownFile_IsNull()
        => Assert.Null(AgentAvatar.ServableContentType(Owner(active: "aktiv.png"), "fremd.png", Leader()));

    [Fact]
    public void MissingContentType_FallsBackToOctetStream()
    {
        var agent = Owner(active: "aktiv.png");
        agent.AvatarContentType = null;

        Assert.Equal("application/octet-stream", AgentAvatar.ServableContentType(agent, "aktiv.png", Junior("x")));
    }

    [Fact]
    public void NoPicture_MatchesNothing()
    {
        // both columns null must never make a null file name servable
        var agent = Owner();

        Assert.Null(AgentAvatar.ServableContentType(agent, string.Empty, Leader()));
    }

    // ---- Initials ----

    [Theory]
    [InlineData("Falcon", "FA")]
    [InlineData("Silver Falcon", "SF")]
    [InlineData("  doppelter  Name ", "DN")]
    [InlineData("X", "X")]
    [InlineData(null, "?")]
    [InlineData("   ", "?")]
    public void Initials_FallBackToTheCodename(string? codename, string expected)
        => Assert.Equal(expected, AgentAvatar.Initials(codename));
}
