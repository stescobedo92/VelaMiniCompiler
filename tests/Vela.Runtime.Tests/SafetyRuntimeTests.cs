using Vela.Runtime;

namespace Vela.Runtime.Tests;

public sealed class SafetyRuntimeTests
{
    [Fact]
    public void NumericOperations_CheckOverflowDivideByZeroAndFiniteResults()
    {
        Assert.Equal(42, VelaNumeric.Add(40, 2, "test:1:1"));
        Assert.Equal(42m, VelaNumeric.Multiply(6m, 7m, "test:1:1"));
        Assert.Equal(0.5d, VelaNumeric.Divide(1d, 2d, "test:1:1"));

        var overflow = Assert.Throws<VelaOverflowException>(() => VelaNumeric.Add(int.MaxValue, 1, "test:2:3"));
        Assert.Contains("test:2:3", overflow.Message, StringComparison.Ordinal);
        Assert.Throws<VelaArithmeticException>(() => VelaNumeric.Divide(1L, 0L, "test:3:3"));
        Assert.Throws<VelaOverflowException>(() => VelaNumeric.Multiply(float.MaxValue, 2f, "test:4:3"));
        Assert.Throws<VelaOverflowException>(() => VelaNumeric.ToInt(long.MaxValue, "test:5:3"));
    }

    [Fact]
    public void AnyAndOptionalGuards_PreserveExactValueTypesAndSourceLocations()
    {
        object boxed = 42;

        Assert.Equal(42, VelaAny.Unbox<int>(boxed, "test:1:1"));
        Assert.Equal(42, VelaAny.TryUnbox<int>(boxed).Value);
        Assert.True(VelaAny.TryUnbox<long>(boxed).IsNone);

        var cast = Assert.Throws<VelaInvalidCastException>(() => VelaAny.Unbox<long>(boxed, "test:2:1"));
        Assert.Contains("test:2:1", cast.Message, StringComparison.Ordinal);
        var nullReference = Assert.Throws<VelaNullReferenceException>(() => VelaGuards.RequireValue(Option.None<int>(), "test:3:1"));
        Assert.Contains("test:3:1", nullReference.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Array_UsesFixedStorageAndSourceAwareBoundsErrors()
    {
        var values = new VelaArray<int>(2);
        values.Set(0, 7, "test:1:1");
        values.Set(1, 11, "test:1:1");

        Assert.Equal(2, values.Length);
        Assert.Equal([7, 11], values.ToArray());
        var bounds = Assert.Throws<VelaIndexOutOfRangeException>(() => values.Get(2, "test:2:1"));
        Assert.Contains("test:2:1", bounds.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NativeAbiValues_RoundTripTextAndDecimal()
    {
        var text = VelaText.FromString("¡Vela!");
        try
        {
            Assert.Equal("¡Vela!", text.ToManagedString());
        }
        finally
        {
            VelaText.Free(text);
        }

        var wire = VelaDecimal.FromDecimal(-123.45m);
        Assert.Equal(-123.45m, wire.ToDecimal());
    }
}
