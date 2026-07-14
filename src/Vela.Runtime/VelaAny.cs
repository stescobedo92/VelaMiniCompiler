namespace Vela.Runtime;

/// <summary>Supplies checked conversions for Vela's boxed <c>Any</c> values.</summary>
public static class VelaAny
{
    /// <summary>Returns a boxed value as <typeparamref name="T"/> or throws a Vela cast error.</summary>
    public static T Unbox<T>(object? value, string sourceLocation)
    {
        if (value is T typed && IsExactValueType<T>(value))
        {
            return typed;
        }

        var actual = value?.GetType().Name ?? "null";
        throw new VelaInvalidCastException(typeof(T).Name, actual, sourceLocation);
    }

    /// <summary>Returns an option containing the requested type when the box is compatible.</summary>
    public static Option<T> TryUnbox<T>(object? value)
    {
        return value is T typed && IsExactValueType<T>(value)
            ? Option.Some(typed)
            : Option.None<T>();
    }

    private static bool IsExactValueType<T>(object value) => !typeof(T).IsValueType || value.GetType() == typeof(T);
}
