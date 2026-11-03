using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Llm.Tools;

namespace NOOSE_Website.Tests.Services;

/// <summary>A tool is only real once it is registered and named. Both omissions are silent: an unregistered tool
/// is simply never offered, and an unnamed one shows the agent a raw identifier instead of German.</summary>
public class NooseiToolRegistrationTests
{
    private static string SourceRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "NOOSE-Website"));

    private static string Source(params string[] parts)
        => File.ReadAllText(Path.Combine([SourceRoot(), .. parts]));

    /// <summary>Every concrete tool in the assembly, by type name.</summary>
    private static string[] ToolTypes() => typeof(INooseiTool).Assembly.GetTypes()
        .Where(t => typeof(INooseiTool).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
        .Select(t => t.Name)
        .OrderBy(n => n, StringComparer.Ordinal)
        .ToArray();

    /// <summary>The German tool identifiers, read out of the tools' own <c>Name</c> properties.</summary>
    private static string[] ToolNames()
    {
        var dir = Path.Combine(SourceRoot(), "Services", "Llm", "Tools");
        return Directory.GetFiles(dir, "*.cs")
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"public string Name => ""(\w+)"""))
            .Select(m => m.Groups[1].Value)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();
    }

    [Fact]
    public void EveryToolInTheAssembly_IsRegisteredInTheCompositionRoot()
    {
        var program = Source("Program.cs");

        var unregistered = ToolTypes()
            .Where(name => !program.Contains($"INooseiTool, NOOSE_Website.Services.Llm.Tools.{name}>", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(unregistered);
    }

    [Fact]
    public void EveryTool_HasAGermanProgressLineAndLabel()
    {
        var names = ToolNames();
        Assert.NotEmpty(names);

        // the fallbacks are deliberate — "NOOSEI arbeitet …" and the raw identifier — but they are a last resort,
        // not the wording a new tool should ship with
        Assert.All(names, name => Assert.NotEqual("FIBI arbeitet …", NooseiToolLabels.Progress(name)));
        Assert.All(names, name => Assert.NotEqual(name, NooseiToolLabels.Label(name)));
    }

    [Fact]
    public void ToolNames_AreUnique()
    {
        // the registry keys on the name; two tools sharing one would throw only when the container builds it
        var names = ToolNames();

        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }
}
