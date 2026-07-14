using Vela.Runtime;

namespace Vela.Runtime.Tests;

public sealed class OptionTests
{
    [Fact]
    public void Some_WhenCreated_ExposesContainedValue()
    {
        Option<int> option = Option.Some(42);

        Assert.True(option.HasValue);
        Assert.False(option.IsNone);
        Assert.Equal(42, option.Value);
    }

    [Fact]
    public void None_WhenRead_ThrowsInvalidOperationException()
    {
        Option<int> option = Option.None<int>();

        Assert.True(option.IsNone);
        Assert.Throws<InvalidOperationException>(() => _ = option.Value);
    }

    [Fact]
    public void Match_WhenValueIsPresent_UsesSomeFunction()
    {
        Option<int> option = Option.Some(21);

        int result = option.Match(value => value * 2, () => -1);

        Assert.Equal(42, result);
    }

    [Fact]
    public void Match_WhenValueIsAbsent_UsesNoneFunction()
    {
        Option<int> option = Option.None<int>();

        int result = option.Match(value => value * 2, () => -1);

        Assert.Equal(-1, result);
    }
}
