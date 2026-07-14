using System.Runtime.ExceptionServices;

namespace Vela.Runtime;

/// <summary>
/// Provides deterministic resource cleanup primitives for compiler-generated code.
/// </summary>
public static class Disposal
{
    /// <summary>
    /// Disposes resources in reverse registration order. Every resource is given a chance to dispose.
    /// </summary>
    /// <param name="resources">The resources to dispose, ordered by registration time.</param>
    /// <exception cref="AggregateException">Thrown when more than one resource fails during cleanup.</exception>
    public static void DisposeAll(ReadOnlySpan<IDisposable?> resources)
    {
        List<Exception>? failures = null;

        for (var index = resources.Length - 1; index >= 0; index--)
        {
            try
            {
                resources[index]?.Dispose();
            }
            catch (Exception exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is { Count: 1 })
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        if (failures is { Count: > 1 })
        {
            throw new AggregateException("Multiple resources failed during disposal.", failures);
        }
    }

    /// <summary>
    /// Disposes a resource and clears its reference before invoking <see cref="IDisposable.Dispose"/>.
    /// </summary>
    /// <typeparam name="T">The disposable reference type.</typeparam>
    /// <param name="resource">The reference to dispose and clear.</param>
    public static void Dispose<T>(ref T? resource)
        where T : class, IDisposable
    {
        T? current = resource;
        resource = null;
        current?.Dispose();
    }
}

/// <summary>
/// Tracks disposable resources and releases them in reverse registration order.
/// </summary>
public sealed class DisposalScope : IDisposable
{
    private IDisposable?[]? _resources;
    private int _count;
    private bool _disposed;

    /// <summary>
    /// Registers a resource for cleanup and returns it unchanged.
    /// </summary>
    /// <typeparam name="T">The disposable reference type.</typeparam>
    /// <param name="resource">The resource to register.</param>
    /// <returns><paramref name="resource"/>.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="resource"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">Thrown when the scope has already been disposed.</exception>
    public T Track<T>(T resource)
        where T : class, IDisposable
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(resource);
        Add(resource);
        return resource;
    }

    /// <summary>
    /// Registers a resource for cleanup when it is not null.
    /// </summary>
    /// <param name="resource">The resource to register.</param>
    /// <exception cref="ObjectDisposedException">Thrown when the scope has already been disposed.</exception>
    public void Track(IDisposable? resource)
    {
        ThrowIfDisposed();

        if (resource is not null)
        {
            Add(resource);
        }
    }

    /// <summary>
    /// Disposes all tracked resources. Calling this method more than once has no effect.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        IDisposable?[]? resources = _resources;
        int count = _count;
        _resources = null;
        _count = 0;

        if (resources is not null)
        {
            Disposal.DisposeAll(resources.AsSpan(0, count));
        }
    }

    private void Add(IDisposable resource)
    {
        ThrowIfDisposed();

        IDisposable?[]? resources = _resources;
        if (resources is null)
        {
            resources = new IDisposable?[4];
            _resources = resources;
        }
        else if (_count == resources.Length)
        {
            Array.Resize(ref resources, checked(resources.Length * 2));
            _resources = resources;
        }

        resources[_count++] = resource;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

/// <summary>Runs compiler-registered deferred actions in last-in-first-out order.</summary>
public sealed class VelaDeferScope
{
    private Action?[]? _actions;
    private int _count;
    private bool _completed;

    /// <summary>Registers an action that must execute when the lexical Vela block exits.</summary>
    public void Push(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_completed)
        {
            throw new InvalidOperationException("Cannot add a deferred action after its scope has completed.");
        }

        Action?[]? actions = _actions;
        if (actions is null)
        {
            actions = new Action?[4];
            _actions = actions;
        }
        else if (_count == actions.Length)
        {
            Array.Resize(ref actions, checked(actions.Length * 2));
            _actions = actions;
        }

        actions[_count++] = action;
    }

    /// <summary>Runs every registered action and preserves a primary failure when cleanup also fails.</summary>
    public void Run(Exception? primaryException)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        var actions = _actions;
        var count = _count;
        _actions = null;
        _count = 0;

        List<Exception>? cleanupFailures = null;
        for (var index = count - 1; index >= 0; index--)
        {
            try
            {
                actions![index]!();
            }
            catch (Exception exception)
            {
                (cleanupFailures ??= []).Add(exception);
            }
        }

        if (cleanupFailures is null)
        {
            return;
        }

        if (primaryException is not null)
        {
            throw new VelaCleanupException(primaryException, cleanupFailures);
        }

        if (cleanupFailures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(cleanupFailures[0]).Throw();
        }

        throw new AggregateException("Multiple deferred actions failed during cleanup.", cleanupFailures);
    }
}
