namespace NOOSE_Website.Tests.Infrastructure;

/// <summary>Keeps the old migration designers empty so the build does not carry ~200 copies of the model.</summary>
public sealed class MigrationDesignerTests
{
    // keep their model: Phase109, and the seven whose data operations have no columnTypes and read them from it
    private static readonly string[] FullDesigners =
    [
        "20261103000239_Phase109_FibTexte.Designer.cs",
        "20260804192734_Phase56_GegenaufklaerungsRegeln.Designer.cs",
        "20260819092159_Oeffentlich08_HinweisUebernahme.Designer.cs",
        "20260903095002_Oeffentlich10_TicketBeteiligteUndIntern.Designer.cs",
        "20260908084531_Phase78_KontoLoeschenEntkoppeln.Designer.cs",
        "20261006124329_Phase86_PartnerBehoerdenprofil.Designer.cs",
        "20261014112112_Phase93_Ehrungen.Designer.cs",
        "20261113235508_Phase128_PartnerEinheiten.Designer.cs",
    ];

    private const int NewestKept = 5;

    [Fact]
    public void OnlyThePhase109AndTheNewestDesignersStillHaveABuildTargetModel()
    {
        var dir = FindMigrationsDirectory();
        var designers = Directory.GetFiles(dir, "*.Designer.cs").Select(Path.GetFileName).OfType<string>()
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        var allowed = designers.TakeLast(NewestKept).Concat(FullDesigners).ToHashSet();

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
