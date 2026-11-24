namespace NOOSE_Website.Tests.Infrastructure;

/// <summary>Keeps old migration designers empty.</summary>
public sealed class MigrationDesignerTests
{
    private const int NewestKept = 5;

    [Fact]
    public void OnlyTheDerivedAndTheNewestDesignersStillHaveABuildTargetModel()
    {
        var dir = FindMigrationsDirectory();
        var designers = Directory.GetFiles(dir, "*.Designer.cs").Select(Path.GetFileName).OfType<string>()
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        var allowed = designers.TakeLast(NewestKept).Concat(DeriveFromFolder(dir).Select(n => n.Designer)).ToHashSet();

        var offenders = designers
            .Where(n => !allowed.Contains(n) && File.ReadAllText(Path.Combine(dir, n)).Contains("BuildTargetModel"))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Diese Designer haben noch BuildTargetModel (erhoeht den Build-Speicher): " + string.Join(", ", offenders)
            + ". 'scripts/designer-entleeren.sh' ausfuehren und das Ergebnis committen.");
    }

    [Fact]
    public void TheDesignersTheMigrationsNeedKeepTheirBuildTargetModel()
    {
        var dir = FindMigrationsDirectory();
        var missing = DeriveFromFolder(dir)
            .Where(n => !File.Exists(Path.Combine(dir, n.Designer))
                        || !File.ReadAllText(Path.Combine(dir, n.Designer)).Contains("BuildTargetModel"))
            .Select(n => $"{n.Designer} (gebraucht von {n.Migration}.{n.Method}, {n.What})")
            .Distinct()
            .ToList();

        Assert.True(
            missing.Count == 0,
            "Diese Designer brauchen ihr BuildTargetModel, weil die genannte Migration in Up, Down oder einer Hilfsmethode ein Modell braucht: "
            + string.Join("; ", missing) + ". Aus der Git-Historie zurueckholen.");
    }

    [Fact]
    public void TheDerivationFindsTheKnownCasesInTheRealFolder()
    {
        var needs = DeriveFromFolder(FindMigrationsDirectory());

        AssertNeed(needs, "Phase109_FibTexte", "Up", "Phase109_FibTexte");
        AssertNeed(needs, "Oeffentlich08_HinweisUebernahme", "Up", "Oeffentlich08_HinweisUebernahme");
        AssertNeed(needs, "Oeffentlich08_HinweisUebernahme", "Down", "Oeffentlich08_HinweisTriage");
        AssertNeed(needs, "Oeffentlich10_TicketBeteiligteUndIntern", "Down", "Oeffentlich07_HinweisLesestandUndPrio");
        AssertNeed(needs, "Phase78_KontoLoeschenEntkoppeln", "Down", "Oeffentlich21_TicketAnhaenge");
        AssertNeed(needs, "Phase66_NooseiKurzbrief", "Up", "Phase66_NooseiKurzbrief");
        AssertNeed(needs, "Phase66_NooseiKurzbrief", "Down", "Phase65_KiKontingente");
        Assert.True(needs.Select(n => n.Designer).Distinct().Count() >= 15, "Die Ableitung findet weniger Designer als die 15 bekannten.");
    }

    [Fact]
    public void AMultiLineDataCallNeedsTheModelOnlyWithoutColumnTypes()
    {
        var needs = Derive(
            ("20260101000000_First", Migration("First", up: """
                migrationBuilder.InsertData(
                    table: "Werte",
                    columns: new[] { "Id", "Name" },
                    values: new object[] { 1, "a" });
                """)),
            ("20260102000000_Second", Migration("Second", up: """
                migrationBuilder.InsertData(
                    table: "Werte",
                    columns: new[] { "Id", "Name" },
                    columnTypes: new[] { "int", "varchar(20)" },
                    values: new object[] { 2, "b" });
                """)));

        var need = Assert.Single(needs);
        Assert.Equal(new DesignerNeed("20260101000000_First.Designer.cs", "20260101000000_First", "Up", "InsertData"), need);
    }

    [Theory]
    [InlineData("UpdateData")]
    [InlineData("DeleteData")]
    public void UpdateAndDeleteDataNeedTheModelToo(string call)
    {
        var needs = Derive(("20260101000000_First", Migration("First", up: $"migrationBuilder.{call}(table: \"T\", keyColumn: \"Id\", keyValue: 1);")));

        Assert.Equal(call, Assert.Single(needs).What);
    }

    [Fact]
    public void ARenameColumnInDownNeedsThePreviousDesigner()
    {
        var needs = Derive(
            ("20260101000000_First", Migration("First", up: "")),
            ("20260102000000_Second", Migration("Second", up: "", down: """
                migrationBuilder.RenameColumn(
                    name: "Neu",
                    table: "T",
                    newName: "Alt");
                """)));

        Assert.Equal(new DesignerNeed("20260101000000_First.Designer.cs", "20260102000000_Second", "Down", "RenameColumn"), Assert.Single(needs));
    }

    [Fact]
    public void ARenameIndexInUpNeedsTheOwnDesigner()
    {
        var needs = Derive(
            ("20260101000000_First", Migration("First", up: "")),
            ("20260102000000_Second", Migration("Second", up: "migrationBuilder.RenameIndex(name: \"A\", table: \"T\", newName: \"B\");")));

        Assert.Equal(new DesignerNeed("20260102000000_Second.Designer.cs", "20260102000000_Second", "Up", "RenameIndex"), Assert.Single(needs));
    }

    [Fact]
    public void AUseOfTheTargetModelNeedsTheModel()
    {
        var needs = Derive(("20260101000000_First", Migration("First", up: "var plan = Plan.Classify(TargetModel);")));

        Assert.Equal("TargetModel", Assert.Single(needs).What);
    }

    [Fact]
    public void ATargetModelUseInDownNeedsTheOwnDesigner()
    {
        var needs = Derive(
            ("20260101000000_First", Migration("First", up: "")),
            ("20260102000000_Second", Migration("Second", up: "", down: "var plan = Plan.Classify(TargetModel);")));

        Assert.Equal(new DesignerNeed("20260102000000_Second.Designer.cs", "20260102000000_Second", "Down", "TargetModel"), Assert.Single(needs));
    }

    [Fact]
    public void ADataCallOutsideUpAndDownNeedsBothDesigners()
    {
        var needs = Derive(
            ("20260101000000_First", Migration("First", up: "")),
            ("20260102000000_Second", """
                public partial class Second : Migration
                {
                    protected override void Up(MigrationBuilder migrationBuilder) { Seed(migrationBuilder); }
                    protected override void Down(MigrationBuilder migrationBuilder) { Seed(migrationBuilder); }

                    private static void Seed(MigrationBuilder mb)
                    {
                        mb.UpdateData(table: "T", keyColumn: "Id", keyValue: 1);
                    }
                }
                """));

        Assert.Equal(
            ["20260101000000_First.Designer.cs", "20260102000000_Second.Designer.cs"],
            needs.Select(n => n.Designer).Order(StringComparer.Ordinal));
        Assert.All(needs, n => Assert.Equal("Helper", n.Method));
    }

    [Fact]
    public void AnExpressionBodiedDownIsStillDown()
    {
        var needs = Derive(
            ("20260101000000_First", Migration("First", up: "")),
            ("20260102000000_Second", """
                namespace N;
                public partial class Second : Migration
                {
                    protected override void Up(MigrationBuilder migrationBuilder) { }

                    protected override void Down(MigrationBuilder migrationBuilder) =>
                        migrationBuilder.RenameColumn(name: "A", table: "T", newName: "B");
                }
                """));

        Assert.Equal(new DesignerNeed("20260101000000_First.Designer.cs", "20260102000000_Second", "Down", "RenameColumn"), Assert.Single(needs));
    }

    [Fact]
    public void AHelperAfterDownIsNotDownWhateverTheIndent()
    {
        var needs = Derive(
            ("20260101000000_First", Migration("First", up: "")),
            ("20260102000000_Second", """
                namespace N;
                public partial class Second : Migration
                {
                    protected override void Up(MigrationBuilder migrationBuilder) { Seed(); }
                    protected override void Down(MigrationBuilder migrationBuilder) { }
                    void Seed() { migrationBuilder.InsertData(table: "T", values: new object[] { 1 }); }
                }
                """));

        Assert.All(needs, n => Assert.Equal("Helper", n.Method));
        Assert.Contains(needs, n => n.Designer == "20260102000000_Second.Designer.cs");
    }

    [Fact]
    public void AVerbatimStringStartingWithQuotesIsNotARawString()
    {
        var needs = Derive(("20260101000000_First", Migration("First", up: """""
            migrationBuilder.Sql(@"""Quoted"" text");
            migrationBuilder.InsertData(table: "T", columns: new[] { "Id" }, values: new object[] { 1 });
            """"")));

        Assert.Equal("InsertData", Assert.Single(needs).What);
    }

    [Fact]
    public void ARawStringWithFourQuotesEndsAtItsOwnDelimiter()
    {
        var needs = Derive(("20260101000000_First", Migration("First", up: """""
            migrationBuilder.Sql("""" a """ b """");
            migrationBuilder.InsertData(table: "T", columns: new[] { "Id" }, values: new object[] { 1 });
            """"")));

        Assert.Equal("InsertData", Assert.Single(needs).What);
    }

    [Fact]
    public void BracketsInStringLiteralsDoNotEndTheCallEarly()
    {
        // brackets in strings
        var needs = Derive(
            ("20260101000000_First", Migration("First", up: """
                migrationBuilder.InsertData(
                    table: "Texte",
                    columns: new[] { "Text" },
                    values: new object[] { "Klammer ) zu (auf" },
                    columnTypes: new[] { "varchar(200)" });
                migrationBuilder.DeleteData(table: "Texte", keyColumn: "Text", keyValue: "ein ( Text");
                """)));

        var need = Assert.Single(needs);
        Assert.Equal("DeleteData", need.What);
    }

    [Fact]
    public void TextInStringsAndCommentsIsNotCode()
    {
        var needs = Derive(("20260101000000_First", Migration("First", up: """
            // migrationBuilder.RenameColumn(name: "A", table: "T", newName: "B");
            /* migrationBuilder.InsertData(table: "T"); */
            migrationBuilder.Sql("UPDATE T SET A = 1; -- UpdateData(");
            migrationBuilder.InsertData(table: "T", values: new object[] { "columnTypes: no" });
            """)));

        var need = Assert.Single(needs);
        Assert.Equal("InsertData", need.What);
    }

    [Fact]
    public void ADownInTheFirstMigrationHasNoPredecessorAndIsNoError()
    {
        var needs = Derive(("20260101000000_First", Migration("First", up: "", down: "migrationBuilder.RenameColumn(name: \"A\", table: \"T\", newName: \"B\");")));

        Assert.Empty(needs);
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

    private static void AssertNeed(IReadOnlyList<DesignerNeed> needs, string migration, string method, string designer)
    {
        Assert.True(
            needs.Any(n => n.Migration.EndsWith("_" + migration, StringComparison.Ordinal) && n.Method == method
                           && n.Designer.EndsWith("_" + designer + ".Designer.cs", StringComparison.Ordinal)),
            $"Die Ableitung findet {migration}.{method} -> {designer} nicht mehr. Hat sich die Regel oder der Parser veraendert?");
    }

    private static IReadOnlyList<DesignerNeed> Derive(params (string Id, string Code)[] migrations) =>
        MigrationDesignerDerivation.Derive(migrations);

    private static IReadOnlyList<DesignerNeed> DeriveFromFolder(string dir)
    {
        var migrations = Directory.GetFiles(dir, "*.cs")
            .Select(Path.GetFileName).OfType<string>()
            .Where(n => char.IsAsciiDigit(n[0]) && !n.EndsWith(".Designer.cs", StringComparison.Ordinal))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => (Id: n[..^".cs".Length], Code: File.ReadAllText(Path.Combine(dir, n))))
            .ToList();

        return MigrationDesignerDerivation.Derive(migrations);
    }

    private static string Migration(string name, string up, string down = "") => $$"""
        public partial class {{name}} : Migration
        {
            protected override void Up(MigrationBuilder migrationBuilder)
            {
                {{up}}
            }

            protected override void Down(MigrationBuilder migrationBuilder)
            {
                {{down}}
            }
        }
        """;

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
