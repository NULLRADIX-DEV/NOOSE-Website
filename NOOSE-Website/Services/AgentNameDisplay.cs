namespace NOOSE_Website.Services;

/// <summary>How an agent is named to a viewer. One place, because the rule is a secrecy rule: the real name never
/// reaches trainees, partners or the read-only supervision, and a display path that forgets that leaks it
/// permanently. See <see cref="Authorization.AgentPrincipalExtensions.MayRealNameSee" /> for who qualifies.</summary>
public static class AgentNameDisplay
{
    /// <summary>Shown when an account has neither a codename nor a real name the viewer may read.</summary>
    public const string Unnamed = "(unbenannt)";

    /// <summary>Codename first, real name only for viewers allowed to see it, never a raw id.</summary>
    public static string Pick(string? codename, string? realName, bool mayRealName)
        => !string.IsNullOrWhiteSpace(codename) ? codename!
            : mayRealName && !string.IsNullOrWhiteSpace(realName) ? realName!
            : Unnamed;
}
