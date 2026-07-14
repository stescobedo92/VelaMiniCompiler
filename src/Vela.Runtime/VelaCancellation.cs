namespace Vela.Runtime;

/// <summary>Represents explicit cooperative cancellation for Vela asynchronous operations.</summary>
public sealed class VelaCancellation : IDisposable
{
    private readonly CancellationTokenSource _source = new();

    /// <summary>Gets a token consumed by runtime asynchronous operations.</summary>
    public CancellationToken Token => _source.Token;

    /// <summary>Gets whether cancellation has been requested.</summary>
    public bool IsCancellationRequested => _source.IsCancellationRequested;

    /// <summary>Creates a new cancellation handle.</summary>
    public static VelaCancellation Create() => new();

    /// <summary>Requests cooperative cancellation. Repeated requests are harmless.</summary>
    public void Cancel() => _source.Cancel();

    /// <inheritdoc />
    public void Dispose() => _source.Dispose();
}
