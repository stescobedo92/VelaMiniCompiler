using System.Diagnostics.CodeAnalysis;

namespace Vela.Runtime;

#pragma warning disable CA1716 // Option is an intentional language-level API name.

/// <summary>
/// Represents an optional value without using <see langword="null"/> as a control-flow signal.
/// </summary>
/// <typeparam name="T">The type of the optional value.</typeparam>
public readonly struct Option<T>
{
    private readonly T? _value;

    internal Option(T value)
    {
        _value = value;
        HasValue = true;
    }

    /// <summary>
    /// Gets a value indicating whether the option contains a value.
    /// </summary>
    public bool HasValue { get; }

    /// <summary>
    /// Gets a value indicating whether the option is empty.
    /// </summary>
    public bool IsNone => !HasValue;

    /// <summary>
    /// Gets the contained value.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when the option is empty.</exception>
    public T Value => HasValue
        ? _value!
        : throw new InvalidOperationException("Option does not contain a value.");

    /// <summary>
    /// Returns the contained value or <paramref name="defaultValue"/> when the option is empty.
    /// </summary>
    /// <param name="defaultValue">The value to return for an empty option.</param>
    /// <returns>The contained value or <paramref name="defaultValue"/>.</returns>
    public T GetValueOrDefault(T defaultValue) => HasValue ? _value! : defaultValue;

    /// <summary>
    /// Attempts to get the contained value.
    /// </summary>
    /// <param name="value">The contained value when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when the option contains a value; otherwise, <see langword="false"/>.</returns>
    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        value = _value!;
        return HasValue;
    }

    /// <summary>
    /// Projects the option to a value by applying one of two functions.
    /// </summary>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="whenSome">The function to invoke for a contained value.</param>
    /// <param name="whenNone">The function to invoke for an empty option.</param>
    /// <returns>The value returned by the selected function.</returns>
    public TResult Match<TResult>(Func<T, TResult> whenSome, Func<TResult> whenNone)
    {
        ArgumentNullException.ThrowIfNull(whenSome);
        ArgumentNullException.ThrowIfNull(whenNone);

        return HasValue ? whenSome(_value!) : whenNone();
    }

    /// <summary>
    /// Executes one of two actions according to whether the option contains a value.
    /// </summary>
    /// <param name="whenSome">The action to invoke for a contained value.</param>
    /// <param name="whenNone">The action to invoke for an empty option.</param>
    public void Match(Action<T> whenSome, Action whenNone)
    {
        ArgumentNullException.ThrowIfNull(whenSome);
        ArgumentNullException.ThrowIfNull(whenNone);

        if (HasValue)
        {
            whenSome(_value!);
            return;
        }

        whenNone();
    }
}

/// <summary>
/// Provides type-inferred factory methods for <see cref="Option{T}"/>.
/// </summary>
public static class Option
{
    /// <summary>
    /// Creates an option that contains <paramref name="value"/>.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value to contain.</param>
    /// <returns>An option that contains <paramref name="value"/>.</returns>
    public static Option<T> Some<T>(T value) => new(value);

    /// <summary>
    /// Creates an empty option.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <returns>An empty option.</returns>
    public static Option<T> None<T>() => default;

    /// <summary>
    /// Converts a nullable reference to an option, treating null as an empty option.
    /// </summary>
    /// <typeparam name="T">The reference type.</typeparam>
    /// <param name="value">The nullable reference to convert.</param>
    /// <returns>An empty option when <paramref name="value"/> is null; otherwise, an option that contains it.</returns>
    public static Option<T> FromNullable<T>(T? value)
        where T : class => value is null ? default : new Option<T>(value);
}

#pragma warning restore CA1716
