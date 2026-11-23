namespace NOOSE_Website.Tests.Infrastructure;

/// <summary>Keeps the old migration designers empty so the build does not carry ~200 copies of the model.</summary>
public sealed class MigrationDesignerTests
{
    // keep their model: Phase109, the seven whose Up has data operations without columnTypes (own model), and the three
    // before Oeffentlich08, Oeffentlich10 and Phase78, whose Down has them (EF builds Down from the previous model)
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
        "20260819083847_Oeffentlich08_HinweisTriage.Designer.cs",
        "20260903090101_Oeffentlich07_HinweisLesestandUndPrio.Designer.cs",
        "20260908083814_Oeffentlich21_TicketAnhaenge.Designer.cs",
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
    public void TheExceptionsKeepTheirBuildTargetModel()
    {
        var dir = FindMigrationsDirectory();
        var emptied = FullDesigners
            .Where(n => !File.ReadAllText(Path.Combine(dir, n)).Contains("BuildTargetModel"))
            .ToList();

        Assert.True(
            emptied.Count == 0,
            "Diese Designer brauchen ihr BuildTargetModel fuer die Datenoperationen in Up oder Down: "
            + string.Join(", ", emptied) + ". Aus der Git-Historie zurueckholen.");
    }

    [Fact]
    public void EveryDesignerKeepsItsMigrationAttribute()
    {
        var dir = FindMigrationsDirectory();
        var broken = new List<string>();
        foreach (var path in Directory.GetFiles(dir, "*.Designer.cs"))
        {
            var text = File.ReadAllText(path);
            var name = Path.GetFileName(path);
            var id = name[..^".Designer.cs".Length];
            if (!text.Contains("[DbContext(typeof(AppDbContext))]") || !text.Contains($"[Migration(\"{id}\")]"))
            {
                broken.Add(name);
            }
        }

        Assert.True(broken.Count == 0, "Diese Designer haben ihr DbContext- oder Migration-Attribut verloren: " + string.Join(", ", broken));
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
