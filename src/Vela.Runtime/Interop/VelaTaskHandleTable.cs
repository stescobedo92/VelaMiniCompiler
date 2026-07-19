namespace Vela.Runtime.Interop;

/// <summary>Owns asynchronous ABI task handles with cooperative cancellation.</summary>
public sealed class VelaTaskHandleTable : IDisposable
{
    private readonly ulong _ownerId;
    private readonly object _gate = new();
    private readonly VelaHandleTable _handles;
    private readonly Dictionary<uint, TaskEntry> _tasks = new();
    private bool _disposed;

    /// <summary>Creates a task handle table scoped to <paramref name="ownerId"/>.</summary>
    /// <param name="ownerId">The owner identifier stamped on issued handles.</param>
    public VelaTaskHandleTable(ulong ownerId)
    {
        _ownerId = ownerId;
        _handles = new VelaHandleTable(ownerId);
    }

    /// <summary>Starts <paramref name="work"/> and returns a task handle.</summary>
    /// <param name="work">The asynchronous work executed with a linked cancellation token.</param>
    /// <returns>A handle referencing the running task.</returns>
    public VelaHandle Create(Func<CancellationToken, Task> work)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(work);

        var cancellationSource = new CancellationTokenSource();
        var entry = new TaskEntry(cancellationSource);
        var handle = _handles.Create(entry, VelaTypeContract.Task);

        lock (_gate)
        {
            _tasks[handle.Slot] = entry;
        }

        // Schedule with CancellationToken.None so an early Release still enters `work`
        // and can observe the linked token (Task.Run would otherwise skip the delegate).
        entry.Task = Task.Run(async () =>
        {
            try
            {
                await work(cancellationSource.Token).ConfigureAwait(false);
                entry.MarkCompleted();
            }
            catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
            {
                entry.MarkCancelled();
                throw;
            }
            catch (Exception exception)
            {
                entry.MarkFaulted(exception);
                throw;
            }
        }, CancellationToken.None);

        return handle;
    }

    /// <summary>Releases a task handle, cancelling the work when it has not completed.</summary>
    /// <param name="handle">The task handle to release.</param>
    /// <returns>The release status.</returns>
    public VelaAbiResult Release(VelaHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (handle.TypeContract != VelaTypeContract.Task)
        {
            return VelaAbiResult.Fail(VelaAbiStatus.WrongType);
        }

        TaskEntry? entry;
        lock (_gate)
        {
            _tasks.TryGetValue(handle.Slot, out entry);
        }

        if (entry is not null && !entry.IsCompleted)
        {
            entry.CancellationSource.Cancel();
        }

        var result = _handles.Release(handle);
        if (result.IsSuccess)
        {
            lock (_gate)
            {
                _tasks.Remove(handle.Slot);
            }

            // Keep the CTS alive until the worker observes cancellation; table Dispose cleans leftovers.
            if (entry is null)
            {
                // No tracked entry.
            }
            else if (entry.Task is { IsCompleted: true })
            {
                entry.CancellationSource.Dispose();
            }
            else if (entry.Task is not null)
            {
                entry.Task.ContinueWith(
                    static (_, state) => ((CancellationTokenSource)state!).Dispose(),
                    entry.CancellationSource,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }

        return result;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        List<TaskEntry> entries;
        lock (_gate)
        {
            entries = _tasks.Values.ToList();
            _tasks.Clear();
            _disposed = true;
        }

        foreach (var entry in entries)
        {
            if (!entry.IsCompleted)
            {
                entry.CancellationSource.Cancel();
            }

            entry.CancellationSource.Dispose();
        }

        _handles.Dispose();
    }

    private sealed class TaskEntry(CancellationTokenSource cancellationSource)
    {
        public CancellationTokenSource CancellationSource { get; } = cancellationSource;

        public Task? Task { get; set; }

        public bool IsCompleted { get; private set; }

        public void MarkCompleted() => IsCompleted = true;

        public void MarkCancelled() => IsCompleted = true;

        public void MarkFaulted(Exception _) => IsCompleted = true;
    }
}
