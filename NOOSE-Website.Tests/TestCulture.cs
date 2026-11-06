using System.Globalization;
using System.Runtime.CompilerServices;

namespace NOOSE_Website.Tests;

/// <summary>
/// The app runs in de-DE (Program.cs sets it for every thread), so the tests do too, whatever the machine's own
/// culture is. Without this a Linux runner formats numbers as "125,000" and the German expectations fail.
/// </summary>
internal static class TestCulture
{
    [ModuleInitializer]
    internal static void UseGerman()
    {
        var german = new CultureInfo("de-DE");
        CultureInfo.DefaultThreadCurrentCulture = german;
        CultureInfo.DefaultThreadCurrentUICulture = german;
        CultureInfo.CurrentCulture = german;
        CultureInfo.CurrentUICulture = german;
    }
}
