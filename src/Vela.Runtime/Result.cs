using System.Diagnostics.CodeAnalysis;

namespace Vela.Runtime;

/// <summary>
/// Represents either a successful value or an error value.
/// </summary>
/// <typeparam name="T">The successful value type.</typeparam>
/// <typeparam name="TError">The error value type.</typeparam>
public readonly struct Result<T, TError>
{
    private readonly T? _value;
    private readonly TError? _error;

    internal Result(T value)
    {
        _value = value;
        _error = default;
        IsSuccess = true;
    }

    internal Result(TError error)
    {
        _value = default;
        _error = error;
        IsSuccess = false;
    }

    /// <summary>
    /// Gets a value indicating whether this result is successful.
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// Gets a value indicating whether this result contains an error.
    /// </summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>
    /// Gets the successful value.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when this result contains an error.</exception>
    public T Value => IsSuccess
        ? _value!
        : throw new InvalidOperationException("Result does not contain a successful value.");

    /// <summary>
    /// Gets the error value.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when this result is successful.</exception>
    public TError Error => IsFailure
        ? _error!
        : throw new InvalidOperationException("Result does not contain an error value.");

    /// <summary>
    /// Attempts to get the successful value.
    /// </summary>
    /// <param name="value">The successful value when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when this result is successful; otherwise, <see langword="false"/>.</returns>
    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        value = _value!;
        return IsSuccess;
    }

    /// <summary>
    /// Attempts to get the error value.
    /// </summary>
    /// <param name="error">The error value when this method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when this result contains an error; otherwise, <see langword="false"/>.</returns>
    public bool TryGetError([MaybeNullWhen(false)] out TError error)
    {
        error = _error!;
        return IsFailure;
    }

    /// <summary>
    /// Projects the result to a value by applying one of two functions.
    /// </summary>
    /// <typeparam name="TResult">The result type.</typeparam>
    /// <param name="whenSuccess">The function to invoke for a successful value.</param>
    /// <param name="whenFailure">The function to invoke for an error value.</param>
    /// <returns>The value returned by the selected function.</returns>
    public TResult Match<TResult>(Func<T, TResult> whenSuccess, Func<TError, TResult> whenFailure)
    {
        ArgumentNullException.ThrowIfNull(whenSuccess);
        ArgumentNullException.ThrowIfNull(whenFailure);

        return IsSuccess ? whenSuccess(_value!) : whenFailure(_error!);
    }

    /// <summary>
    /// Projects a successful value while preserving an error unchanged.
    /// </summary>
    /// <typeparam name="TResult">The projected successful value type.</typeparam>
    /// <param name="mapper">The function to apply to a successful value.</param>
    /// <returns>The projected successful result or the original error.</returns>
    public Result<TResult, TError> Map<TResult>(Func<T, TResult> mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);

        return IsSuccess
            ? new Result<TResult, TError>(mapper(_value!))
            : new Result<TResult, TError>(_error!);
    }

    /// <summary>
    /// Projects an error value while preserving a successful value unchanged.
    /// </summary>
    /// <typeparam name="TNewError">The projected error type.</typeparam>
    /// <param name="mapper">The function to apply to an error value.</param>
    /// <returns>The original successful result or the projected error.</returns>
    public Result<T, TNewError> MapError<TNewError>(Func<TError, TNewError> mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);

        return IsSuccess
            ? new Result<T, TNewError>(_value!)
            : new Result<T, TNewError>(mapper(_error!));
    }
}

/// <summary>
/// Provides type-inferred factory methods for <see cref="Result{T, TError}"/>.
/// </summary>
public static class Result
{
    /// <summary>
    /// Creates a successful result.
    /// </summary>
    /// <typeparam name="T">The successful value type.</typeparam>
    /// <typeparam name="TError">The error value type.</typeparam>
    /// <param name="value">The successful value.</param>
    /// <returns>A successful result that contains <paramref name="value"/>.</returns>
    public static Result<T, TError> Ok<T, TError>(T value) => new(value);

    /// <summary>
    /// Creates a failed result.
    /// </summary>
    /// <typeparam name="T">The successful value type.</typeparam>
    /// <typeparam name="TError">The error value type.</typeparam>
    /// <param name="error">The error value.</param>
    /// <returns>A failed result that contains <paramref name="error"/>.</returns>
    public static Result<T, TError> Fail<T, TError>(TError error) => new(error);
}
