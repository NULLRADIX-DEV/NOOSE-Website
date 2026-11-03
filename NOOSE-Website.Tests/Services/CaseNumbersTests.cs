using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>Old posters, links and notes carry NOOSE- numbers; every public lookup reads them as FIB-.</summary>
public class CaseNumbersTests
{
    [Theory]
    [InlineData("FIB-FA-2026-0001", "FIB-FA-2026-0001")]
    [InlineData("NOOSE-FA-2026-0001", "FIB-FA-2026-0001")]
    [InlineData("noose-fa-2026-0001", "FIB-FA-2026-0001")]
    [InlineData("Noose-P-2026-0042", "FIB-P-2026-0042")]
    [InlineData("fib-pm-2026-0007", "FIB-PM-2026-0007")]
    [InlineData("  NOOSE-BEL-2026-0003 \r\n", "FIB-BEL-2026-0003")]
    public void Normalize_ReadsLegacyAndMisCasedNumbersAsTheCurrentOne(string input, string expected)
        => Assert.Equal(expected, CaseNumbers.Normalize(input));

    [Fact]
    public void Normalize_UsesTheAgencyPrefixTheServiceIssues()
        => Assert.StartsWith(CaseNumberService.AgencyPrefix + "-", CaseNumbers.Normalize("NOOSE-X-2026-0001"));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("FIBER-FA-2026-0001")]
    [InlineData("NOOSEI-FA-2026-0001")]
    [InlineData("LSPD-FA-2026-0001")]
    [InlineData("2026-0001")]
    [InlineData("-FA-2026-0001")]
    public void Normalize_LeavesEverythingWithoutAnAgencyPrefixAlone(string input)
        => Assert.Equal(input.Trim(), CaseNumbers.Normalize(input));

    [Fact]
    public void Normalize_PassesNullThrough()
        => Assert.Null(CaseNumbers.Normalize(null));
}
