namespace Vela.Runtime.Interop;

/// <summary>Status codes returned across the Vela ABI v2 boundary.</summary>
public enum VelaAbiStatus : int
{
    /// <summary>The operation completed successfully.</summary>
    Success = 0,

    /// <summary>An argument was invalid or out of range.</summary>
    InvalidArgument = 1,

    /// <summary>A UTF-8 payload was malformed.</summary>
    InvalidUtf8 = 2,

    /// <summary>The handle belongs to a different owner table.</summary>
    WrongOwner = 3,

    /// <summary>The handle type contract does not match the requested type.</summary>
    WrongType = 4,

    /// <summary>The handle generation is stale after slot reuse.</summary>
    StaleHandle = 5,

    /// <summary>The operation was cancelled.</summary>
    Cancelled = 6,

    /// <summary>The operation timed out.</summary>
    TimedOut = 7,

    /// <summary>An unexpected internal failure occurred.</summary>
    InternalError = 255,
}

/// <summary>Lightweight ABI status wrapper for ownership operations.</summary>
/// <param name="Status">The operation status.</param>
public readonly record struct VelaAbiResult(VelaAbiStatus Status)
{
    /// <summary>Gets whether <see cref="Status"/> is <see cref="VelaAbiStatus.Success"/>.</summary>
    public bool IsSuccess => Status == VelaAbiStatus.Success;

    /// <summary>Creates a successful result.</summary>
    public static VelaAbiResult Ok() => new(VelaAbiStatus.Success);

    /// <summary>Creates a failed result.</summary>
    /// <param name="status">The failure status.</param>
    public static VelaAbiResult Fail(VelaAbiStatus status) => new(status);
}
