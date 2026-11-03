using System.Runtime.CompilerServices;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Models.Recruiting;
using NOOSE_Website.Services.Decorations;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Tests.Services;

/// <summary>
/// The data sweep rewrites rows the demo seeders look up by text; the seeders must then carry exactly what the sweep
/// made of the old literal, or the next boot adds a second row next to the swept one.
/// </summary>
public class DemoLiteralSweepTests
{
    private static string SourceRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "NOOSE-Website"));

    /// <summary>The brand-bearing demo literals as they stood before the rename (line fragments of one source line).</summary>
    public static TheoryData<string> OldDemoLiterals => new()
    {
        "Auftrag der NOOSE",
        "Das National Office of Security Enforcement ist die Bundesbehörde für die Abwehr schwerer und ",
        "Die NOOSE handelt auf gesetzlicher Grundlage. Maßnahmen, die in Grundrechte eingreifen, stehen unter ",
        "Einsatzkräfte der NOOSE haben gestern Abend gemeinsam mit dem LSPD ein Lagergebäude auf Elysian Island ",
        "Mit freundlichen Grüßen\nNOOSE – Führungsebene",
        "Auch Kleinigkeiten helfen uns weiter.\n\nNOOSE",
        "wenn Ihnen Neues auffällt.\n\nNOOSE",
        "Kevlarweste (NOOSE-Ausgabe)",
        "Lagezentrum, NOOSE HQ",
    };

    [Theory]
    [MemberData(nameof(OldDemoLiterals))]
    public void DemoSeedersCarryWhatTheSweepMakesOfTheOldLiteral(string old)
    {
        var demo = string.Join('\n', Directory
            .EnumerateFiles(Path.Combine(SourceRoot(), "Services"), "DemoDataService*.cs")
            .Select(File.ReadAllText));
        Assert.NotEmpty(demo);

        var swept = BrandSweep.Apply(old);

        Assert.NotEqual(old, swept);
        Assert.Contains(AsSource(swept), demo, StringComparison.Ordinal);
        Assert.DoesNotContain(AsSource(old), demo, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTicketSenderIsWhatTheSweepMakesOfTheStoredSender()
        => Assert.Equal(TicketRules.AgencySender, BrandSweep.Apply("NOOSE – Führungsebene"));

    [Fact]
    public void TheCareerRequirementIsWhatTheSweepMakesOfTheStoredOne()
        => Assert.Equal(
            BrandSweep.Apply("Mindestens 3 Monate durchgängige Zugehörigkeit beim Los Santos Police Department, "
                + "ehemals Federal Investigation Bureau oder beim Department of Justice mit Erfahrung "
                + "in einer Position mit Führungsverantwortung"),
            CareerRequirementsConfig.Default().Items[0].Text);

    [Fact]
    public void TheCertificatePlaceNamesTheTowerNotTheOldSite()
        => Assert.Equal("FIB-Tower, Los Santos", DecorationRules.DefaultPlace);

    [Fact]
    public void TheInvitationLettersNameTheTowerNotTheOldSite()
    {
        var seeder = File.ReadAllText(Path.Combine(SourceRoot(), "Infrastructure", "RecruitingSeeder.cs"));

        Assert.Equal(2, CountOf(seeder, "Uhr im FIB-Tower, Los Santos, San Andreas"));
        Assert.DoesNotContain("Government Facility", seeder, StringComparison.Ordinal);
        Assert.DoesNotContain("5020", seeder, StringComparison.Ordinal);
    }

    // the source holds a newline as the two characters backslash and n
    private static string AsSource(string text) => text.Replace("\n", "\\n", StringComparison.Ordinal);

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}
