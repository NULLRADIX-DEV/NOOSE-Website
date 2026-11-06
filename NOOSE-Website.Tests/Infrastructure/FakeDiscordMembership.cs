using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Infrastructure;

/// <summary>A membership check that answers the same for everyone, so a test that is not about Discord never asks it.</summary>
public static class FakeDiscordMembership
{
    public static IDiscordMembershipCheck Always(DiscordMembership answer)
    {
        var check = Substitute.For<IDiscordMembershipCheck>();
        check.CheckAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(answer);
        return check;
    }
}
