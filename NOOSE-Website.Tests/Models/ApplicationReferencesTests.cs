using NOOSE_Website.Models.Recruiting;

namespace NOOSE_Website.Tests.Models;

public class ApplicationReferencesTests
{
    [Fact]
    public void Normalize_CapsCountAndLength()
    {
        var many = Enumerable.Range(1, 8)
            .Select(i => new ApplicationReference { Name = new string('x', 300), Phone = new string('1', 50) });

        var clean = ApplicationReferences.Normalize(many);

        Assert.Equal(ApplicationReferences.MaxCount, clean.Count);
        Assert.All(clean, r =>
        {
            Assert.Equal(ApplicationReferences.MaxNameLength, r.Name.Length);
            Assert.Equal(ApplicationReferences.MaxPhoneLength, r.Phone!.Length);
        });
    }

    [Fact]
    public void Serialize_OnlyBlankRows_GivesNull()
        => Assert.Null(ApplicationReferences.Serialize([new ApplicationReference { Name = " ", Phone = "123" }]));

    [Fact]
    public void Parse_RoundTrips()
    {
        var json = ApplicationReferences.Serialize(
            [new ApplicationReference { Name = "Lisa", BirthDate = new DateTime(1990, 1, 2), Phone = "555" }]);

        var r = Assert.Single(ApplicationReferences.Parse(json));

        Assert.Equal("Lisa", r.Name);
        Assert.Equal(new DateTime(1990, 1, 2), r.BirthDate);
        Assert.Equal("555", r.Phone);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    public void Parse_EmptyOrBroken_GivesEmpty(string? json)
        => Assert.Empty(ApplicationReferences.Parse(json));

    [Fact]
    public void Describe_SkipsMissingParts()
    {
        Assert.Equal("Lisa", ApplicationReferences.Describe(new ApplicationReference { Name = "Lisa" }));
        Assert.Equal("Lisa, geb. 02.01.1990, Tel. 555", ApplicationReferences.Describe(
            new ApplicationReference { Name = "Lisa", BirthDate = new DateTime(1990, 1, 2), Phone = "555" }));
    }
}
