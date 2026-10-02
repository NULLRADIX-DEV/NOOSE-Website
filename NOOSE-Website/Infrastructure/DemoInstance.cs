using Microsoft.Extensions.Configuration;

namespace NOOSE_Website.Infrastructure;

/// <summary>Whether this instance is the demo, set by configuration (<c>Demo:AutoSetup</c>).</summary>
/// <remarks>
/// Only the demo presents anonymous visitors as the demo agent. The database flag <c>DemoModusAktiv</c> no longer
/// decides that: on production, one switch in the settings would otherwise have shown every visitor the real data
/// at Director level. Production forces <c>Demo__AutoSetup=false</c> in its compose file.
/// </remarks>
public static class DemoInstance
{
    public const string ConfigKey = "Demo:AutoSetup";

    public static bool IsDemo(IConfiguration configuration) => configuration.GetValue<bool>(ConfigKey);
}
