namespace NOOSE_Website.Tests.Infrastructure;

/// <summary>Keeps the old migration designers empty so the build does not carry ~200 copies of the model.</summary>
public sealed class MigrationDesignerTests
{
    private const string FullDesigner = "20261103000239_Phase109_FibTexte.Designer.cs";
    private const int NewestKept = 5;

    [Fact]
    public void OnlyThePhase109AndTheNewestDesignersStillHaveABuildTargetModel()
    {
        var dir = FindMigrationsDirectory();
        var designers = Directory.GetFiles(dir, "*.Designer.cs").Select(Path.GetFileName).OfType<string>()
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        var allowed = designers.TakeLast(NewestKept).Append(FullDesigner).ToHashSet();

        var offenders = designers
            .Where(n => !allowed.Contains(n) && File.ReadAllText(Path.Combine(dir, n)).Contains("BuildTargetModel"))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Diese Designer haben noch BuildTargetModel (erhoeht den Build-Speicher): " + string.Join(", ", offenders)
            + ". 'scripts/designer-entleeren.sh' ausfuehren und das Ergebnis committen.");
    }

    [Fact]
    public void EveryDesignerKeepsItsMigrationAttribute()
    {
        var dir = FindMigrationsDirectory();
        foreach (var path in Directory.GetFiles(dir, "*.Designer.cs"))
        {
            var text = File.ReadAllText(path);
            var id = Path.GetFileName(path)[..^".Designer.cs".Length];
            Assert.Contains("[DbContext(typeof(AppDbContext))]", text);
            Assert.Contains($"[Migration(\"{id}\")]", text);
        }
    }

    private static string FindMigrationsDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "NOOSE-Website", "Data", "Migrations");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new DirectoryNotFoundException("NOOSE-Website/Data/Migrations nicht gefunden.");
    }
}
