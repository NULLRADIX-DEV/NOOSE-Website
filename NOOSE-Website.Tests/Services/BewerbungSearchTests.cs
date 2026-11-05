using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

public sealed class BewerbungSearchTests
{
    private static readonly Bewerbung[] All =
    [
        new() { Id = "1", Name = "Max Mustermann", CaseNumber = "FIB-B-2026-0001", AssignedAgentName = "Falcon", Employer = "LSPD" },
        new() { Id = "2", Name = "Erika Musterfrau", CaseNumber = "FIB-B-2026-0002", Employer = "Sheriff" },
        new() { Id = "3", Name = "John Doe", CaseNumber = "FIB-B-2026-0003", AssignedAgentName = "Raven" },
    ];

    private static string[] Ids(string? query) => BewerbungSearch.Filter(All, query).Select(b => b.Id).ToArray();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyQuery_KeepsEverything(string? query) => Assert.Equal(["1", "2", "3"], Ids(query));

    [Theory]
    [InlineData("muster", new[] { "1", "2" })]
    [InlineData("0003", new[] { "3" })]
    [InlineData("falcon", new[] { "1" })]
    [InlineData("lspd", new[] { "1" })]
    [InlineData("muster lspd", new[] { "1" })] // every term must match
    [InlineData("nobody", new string[0])]
    public void Query_MatchesNameCaseNumberHandlerAndEmployer(string query, string[] expected)
        => Assert.Equal(expected, Ids(query));
}
