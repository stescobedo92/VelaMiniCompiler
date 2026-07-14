namespace Vela.Runtime;

/// <summary>Base class for deterministic runtime failures raised by generated Vela programs.</summary>
public abstract class VelaRuntimeException : Exception
{
    /// <summary>Initializes a Vela runtime failure with optional source and inner-exception context.</summary>
    protected VelaRuntimeException(string message, string? sourceLocation = null, Exception? innerException = null)
        : base(FormatMessage(message, sourceLocation), innerException)
    {
        SourceLocation = sourceLocation;
    }

    /// <summary>Gets the original Vela source location when supplied by generated code.</summary>
    public string? SourceLocation { get; }

    private static string FormatMessage(string message, string? sourceLocation) => string.IsNullOrWhiteSpace(sourceLocation)
        ? message
        : $"{message} ({sourceLocation})";
}

/// <summary>Thrown when a checked Vela arithmetic operation exceeds its type range.</summary>
public sealed class VelaOverflowException(string operation, string? sourceLocation = null, Exception? innerException = null)
    : VelaRuntimeException($"Arithmetic overflow during '{operation}'.", sourceLocation, innerException);

/// <summary>Thrown when a Vela arithmetic operation has no permitted finite result.</summary>
public sealed class VelaArithmeticException(string operation, string? sourceLocation = null)
    : VelaRuntimeException($"Invalid arithmetic operation '{operation}'.", sourceLocation);

/// <summary>Thrown when generated Vela code dereferences a null reference.</summary>
public sealed class VelaNullReferenceException(string? sourceLocation = null)
    : VelaRuntimeException("Cannot dereference a null value.", sourceLocation);

/// <summary>Thrown when an array or vector index is outside its permitted bounds.</summary>
public sealed class VelaIndexOutOfRangeException(int index, int length, string? sourceLocation = null)
    : VelaRuntimeException($"Index {index} is outside the valid range [0, {Math.Max(0, length - 1)}] for length {length}.", sourceLocation)
{
    /// <summary>Gets the requested zero-based index.</summary>
    public int Index { get; } = index;

    /// <summary>Gets the collection length at the failed access.</summary>
    public int Length { get; } = length;
}

/// <summary>Thrown when a boxed Vela value cannot be converted to the requested type.</summary>
public sealed class VelaInvalidCastException(string expectedType, string actualType, string? sourceLocation = null)
    : VelaRuntimeException($"Cannot unbox value of type '{actualType}' as '{expectedType}'.", sourceLocation);
