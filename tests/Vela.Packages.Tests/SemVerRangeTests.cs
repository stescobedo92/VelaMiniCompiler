using Vela.Packages;
using Xunit;

namespace Vela.Packages.Tests;

public sealed class SemVerRangeTests
{
    [Theory]
    [InlineData("1.2.0", ">1.1.0", true)]
    [InlineData("1.1.0", ">1.1.0", false)]
    [InlineData("1.1.0", ">=1.1.0", true)]
    [InlineData("1.0.0", "<1.1.0", true)]
    [InlineData("1.1.0", "<1.1.0", false)]
    [InlineData("1.1.0", "<=1.1.0", true)]
    [InlineData("1.2.0", "1.2.0 - 1.3.0", true)]
    [InlineData("1.4.0", "1.2.0 - 1.3.0", false)]
    [InlineData("1.5.0", ">=1.0.0 <2.0.0", true)]
    [InlineData("2.0.0", ">=1.0.0 <2.0.0", false)]
    [InlineData("1.5.0", ">=1.0.0 <2.0.0 || >=3.0.0 <4.0.0", true)]
    [InlineData("2.5.0", ">=1.0.0 <2.0.0 || >=3.0.0 <4.0.0", false)]
    [InlineData("3.1.0", ">=1.0.0 <2.0.0 || >=3.0.0 <4.0.0", true)]
    public void Satisfies_AdvancedRanges(string version, string range, bool expected)
    {
        Assert.Equal(expected, SemVer.Satisfies(SemVer.Parse(version), range));
    }

    [Fact]
    public void Satisfies_PreservesExistingRangeForms()
    {
        Assert.True(SemVer.Satisfies(SemVer.Parse("1.2.5"), "^1.2.0"));
        Assert.True(SemVer.Satisfies(SemVer.Parse("1.2.3"), "~1.2.3"));
        Assert.True(SemVer.Satisfies(SemVer.Parse("9.9.9"), "*"));
        Assert.True(SemVer.Satisfies(SemVer.Parse("9.9.9"), "latest"));
        Assert.True(SemVer.Satisfies(SemVer.Parse("1.2.3"), "1.2.3"));
    }
}
