using Vela.Runtime;

namespace Vela.Runtime.Tests;

public sealed class ContractTests
{
    [Fact]
    public void Require_WhenConditionIsFalse_ThrowsContractExceptionWithMessage()
    {
        VelaContractException exception = Assert.Throws<VelaContractException>(
            () => Contract.Require(false, "A value must be positive."));

        Assert.Equal("A value must be positive.", exception.Message);
    }

    [Fact]
    public void NotNull_WhenValueIsNull_ThrowsArgumentNullException()
    {
        string? value = null;

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => Contract.NotNull(value));

        Assert.Equal(nameof(value), exception.ParamName);
    }

    [Fact]
    public void RequireIndex_WhenIndexEqualsLength_ThrowsArgumentOutOfRangeException()
    {
        ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
            () => Contract.RequireIndex(index: 3, length: 3));

        Assert.Equal("index", exception.ParamName);
    }
}
