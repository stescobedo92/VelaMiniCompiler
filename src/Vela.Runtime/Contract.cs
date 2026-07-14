using System.Runtime.CompilerServices;

namespace Vela.Runtime;

/// <summary>
/// The exception thrown when a Vela contract is violated at runtime.
/// </summary>
public sealed class VelaContractException(string message) : Exception(message)
{
}

/// <summary>
/// Provides runtime checks emitted by the Vela compiler.
/// </summary>
public static class Contract
{
    /// <summary>
    /// Throws <see cref="VelaContractException"/> when <paramref name="condition"/> is false.
    /// </summary>
    /// <param name="condition">The condition that must hold.</param>
    /// <param name="message">The diagnostic message to use when the contract is violated.</param>
    public static void Require(bool condition, string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!condition)
        {
            throw new VelaContractException(message);
        }
    }

    /// <summary>
    /// Returns a non-null reference or throws <see cref="ArgumentNullException"/>.
    /// </summary>
    /// <typeparam name="T">The reference type.</typeparam>
    /// <param name="value">The value that must not be null.</param>
    /// <param name="parameterName">The name to include in the exception when the value is null.</param>
    /// <returns><paramref name="value"/> when it is not null.</returns>
    public static T NotNull<T>(T? value, [CallerArgumentExpression(nameof(value))] string? parameterName = null)
        where T : class
    {
        return value ?? throw new ArgumentNullException(parameterName);
    }

    /// <summary>
    /// Throws <see cref="ArgumentOutOfRangeException"/> when an index is outside a collection length.
    /// </summary>
    /// <param name="index">The zero-based index to validate.</param>
    /// <param name="length">The collection length.</param>
    /// <param name="parameterName">The name to include in the exception when the index is invalid.</param>
    public static void RequireIndex(int index, int length, string parameterName = "index")
    {
        if ((uint)index >= (uint)length)
        {
            throw new ArgumentOutOfRangeException(parameterName, index, "Index must identify an element in the collection.");
        }
    }
}
