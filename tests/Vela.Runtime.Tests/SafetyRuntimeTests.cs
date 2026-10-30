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
    public void Array_RejectsUninitializedReferencesAcrossReadApis()
    {
        var values = new VelaArray<string>(1);
        var error = Assert.Throws<VelaNullReferenceException>(() => values.Get(0, "array:3:2"));
        Assert.Equal("array:3:2", error.SourceLocation);
        Assert.Throws<VelaNullReferenceException>(() => values[0]);
        Assert.Throws<VelaNullReferenceException>(() => values.ToArray());
        Assert.Throws<VelaNullReferenceException>(() => values.ToList());

        values.Set(0, "ready", "array:4:2");
        Assert.Equal("ready", values.Get(0, "array:5:2"));
        Assert.Equal(["ready"], values.ToArray());
    }

    [Fact]
    public void Array_RequiresInitializationOfStructsContainingReferences()
    {
        var values = new VelaArray<(int Number, string Text)>(1);
        Assert.Throws<VelaNullReferenceException>(() => values.Get(0, "array:1:1"));
        values[0] = (7, "ready");
        Assert.Equal((7, "ready"), values.Get(0, "array:2:1"));
    }

    [Fact]
    public void Array_PreservesSafeDefaultsAndCopiedValues()
    {
        Assert.Equal(0, new VelaArray<int>(1).Get(0, "array:1:1"));
        Assert.True(new VelaArray<Option<string>>(1).Get(0, "array:1:1").IsNone);
        var boxed = new VelaArray<object>(1);
        Assert.Null(boxed.Get(0, "array:1:1"));
        boxed.Set(0, null!, "array:1:1");
        Assert.Null(boxed.Get(0, "array:1:1"));
        Assert.Equal((7, "ready"), new VelaArray<(int, string)>([(7, "ready")]).Get(0, "array:1:1"));
        Assert.Throws<VelaNullReferenceException>(() => new VelaArray<string>([null!]).Get(0, "array:1:1"));
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
