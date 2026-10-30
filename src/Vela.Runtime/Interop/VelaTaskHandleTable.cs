namespace Vela.Runtime.Interop;

/// <summary>Owns asynchronous ABI task handles with cooperative cancellation.</summary>
public sealed class VelaTaskHandleTable : IDisposable
{
    private readonly object _gate = new();
    private readonly VelaHandleTable _handles;
    private readonly Dictionary<uint, TaskEntry> _tasks = new();
    private bool _disposed;

    /// <summary>Creates a task handle table scoped to <paramref name="ownerId"/>.</summary>
    /// <param name="ownerId">The owner identifier stamped on issued handles.</param>
    public VelaTaskHandleTable(ulong ownerId)
    {
        _handles = new VelaHandleTable(ownerId);
    }

    /// <summary>Starts <paramref name="work"/> and returns a task handle.</summary>
    /// <param name="work">The asynchronous work executed with a linked cancellation token.</param>
    /// <returns>A handle referencing the running task.</returns>
    public VelaHandle Create(Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var cancellationSource = new CancellationTokenSource();
            var cancellationToken = cancellationSource.Token;
            var entry = new TaskEntry(cancellationSource);
            var handle = _handles.Create(entry, VelaTypeContract.Task);
            _tasks[handle.Slot] = entry;

            // Publish the task under the lifecycle lock before Release or Dispose can see it.
            // CancellationToken.None ensures an early release still lets work observe cancellation.
            entry.Task = Task.Run(() => work(cancellationToken), CancellationToken.None);
            return handle;
        }
    }

    /// <summary>Releases a task handle, cancelling the work when it has not completed.</summary>
    /// <param name="handle">The task handle to release.</param>
    /// <returns>The release status.</returns>
    public VelaAbiResult Release(VelaHandle handle)
    {
        TaskEntry entry;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (handle.TypeContract != VelaTypeContract.Task)
            {
                return VelaAbiResult.Fail(VelaAbiStatus.WrongType);
            }

            var resolved = _handles.Resolve<TaskEntry>(handle, VelaTypeContract.Task);
            if (resolved.Status != VelaAbiStatus.Success)
            {
                return VelaAbiResult.Fail(resolved.Status);
            }

            var result = _handles.Release(handle);
            if (!result.IsSuccess)
            {
                return result;
            }

            entry = resolved.Value!;
            _tasks.Remove(handle.Slot);
        }

        // User cancellation callbacks run only after the validated handle has been removed.
        try
        {
            CancelAndDisposeAfterCompletion(entry);
            return VelaAbiResult.Ok();
        }
        catch (AggregateException)
        {
            return VelaAbiResult.Fail(VelaAbiStatus.InternalError);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        List<TaskEntry> entries;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            entries = _tasks.Values.ToList();
            _tasks.Clear();
            _disposed = true;
            _handles.Dispose();
        }

        List<Exception>? failures = null;
        foreach (var entry in entries)
        {
            try
            {
                CancelAndDisposeAfterCompletion(entry);
            }
            catch (AggregateException exception)
            {
                (failures ??= []).Add(exception);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException("One or more task cancellation callbacks failed.", failures);
        }
    }

    private static void CancelAndDisposeAfterCompletion(TaskEntry entry)
    {
        try
        {
            if (!entry.Task.IsCompleted)
            {
                entry.CancellationSource.Cancel();
            }
        }
        finally
        {
            // Work may still use its token after cancellation; retain the source until it exits.
            _ = entry.Task.ContinueWith(
                static (task, state) =>
                {
                    _ = task.Exception;
                    ((CancellationTokenSource)state!).Dispose();
                },
                entry.CancellationSource,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private sealed class TaskEntry(CancellationTokenSource cancellationSource)
    {
        public CancellationTokenSource CancellationSource { get; } = cancellationSource;

        public Task Task { get; set; } = Task.CompletedTask;
    }
}
