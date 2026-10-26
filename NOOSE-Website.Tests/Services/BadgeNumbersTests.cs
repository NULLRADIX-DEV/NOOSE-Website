using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

public class BadgeNumbersTests
{
    [Fact]
    public void All_ContainsRomanNumbersFromOneToForty()
    {
        Assert.Equal(40, BadgeNumbers.All.Count);
        Assert.Equal("I", BadgeNumbers.All[0]);
        Assert.Equal("IV", BadgeNumbers.All[3]);
        Assert.Equal("IX", BadgeNumbers.All[8]);
        Assert.Equal("XIV", BadgeNumbers.All[13]);
        Assert.Equal("XIX", BadgeNumbers.All[18]);
        Assert.Equal("XXIV", BadgeNumbers.All[23]);
        Assert.Equal("XXV", BadgeNumbers.All[24]);
        Assert.Equal("XXIX", BadgeNumbers.All[28]);
        Assert.Equal("XXXIV", BadgeNumbers.All[33]);
        Assert.Equal("XXXIX", BadgeNumbers.All[38]);
        Assert.Equal("XL", BadgeNumbers.All[39]);
        Assert.Equal(40, BadgeNumbers.All.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("I")]
    [InlineData("xiv")]
    [InlineData("XXV")]
    [InlineData("xxxviii")]
    [InlineData("XL")]
    [InlineData("")]
    [InlineData(null)]
    public void IsAllowed_AcceptsRomanNumbersAndEmpty(string? value)
        => Assert.True(BadgeNumbers.IsAllowed(value));

    [Theory]
    [InlineData("0")]
    [InlineData("26")]
    [InlineData("XLI")]
    [InlineData("XXXX")]
    [InlineData("NOOSE-1")]
    public void IsAllowed_RejectsOtherValues(string value)
        => Assert.False(BadgeNumbers.IsAllowed(value));

    [Theory]
    [InlineData(" xiv ", "XIV")]
    [InlineData(" legacy ", "legacy")]
    [InlineData(" ", null)]
    public void Normalize_CanonicalizesRomanNumbersAndTrimsLegacyValues(string value, string? expected)
        => Assert.Equal(expected, BadgeNumbers.Normalize(value));
}
