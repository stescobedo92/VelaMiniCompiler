using Vela.Runtime.Interop;

namespace Vela.Runtime.Tests;

public sealed class VelaTaskHandleTableTests
{
    [Theory]
    [InlineData(VelaAbiStatus.WrongOwner)]
    [InlineData(VelaAbiStatus.WrongType)]
    [InlineData(VelaAbiStatus.StaleHandle)]
    public async Task InvalidHandleDoesNotCancelItsSlotOccupant(VelaAbiStatus expectedStatus)
    {
        using var tasks = new VelaTaskHandleTable(ownerId: 7);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = tasks.Create(async token =>
        {
            started.SetResult(token);
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        });
        var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        var invalidHandle = expectedStatus switch
        {
            VelaAbiStatus.WrongOwner => handle with { Owner = 8 },
            VelaAbiStatus.WrongType => handle with { TypeContract = VelaTypeContract.Text },
            _ => handle with { Generation = handle.Generation + 1 },
        };

        Assert.Equal(expectedStatus, tasks.Release(invalidHandle).Status);
        Assert.False(token.IsCancellationRequested);
        Assert.True(tasks.Release(handle).IsSuccess);
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public async Task StaleHandleDoesNotCancelTaskInReusedSlot()
    {
        using var tasks = new VelaTaskHandleTable(ownerId: 7);
        var stale = tasks.Create(static _ => Task.CompletedTask);
        Assert.True(tasks.Release(stale).IsSuccess);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = tasks.Create(async token =>
        {
            started.SetResult(token);
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        });
        var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);

        Assert.Equal(stale.Slot, current.Slot);
        Assert.Equal(VelaAbiStatus.StaleHandle, tasks.Release(stale).Status);
        Assert.False(token.IsCancellationRequested);
        Assert.True(tasks.Release(current).IsSuccess);
    }

    [Fact]
    public async Task ThrowingCancellationCallbackStillRetiresHandle()
    {
        using var tasks = new VelaTaskHandleTable(ownerId: 7);
        var started = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = tasks.Create(async token =>
        {
            started.SetResult(token);
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        });
        var token = await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        using var registration = token.Register(static () => throw new InvalidOperationException("cleanup failed"));

        Assert.Equal(VelaAbiStatus.InternalError, tasks.Release(handle).Status);
        Assert.Equal(VelaAbiStatus.StaleHandle, tasks.Release(handle).Status);
        Assert.True(token.IsCancellationRequested);
    }

    [Fact]
    public async Task DisposeKeepsCancellationSourceAliveUntilWorkerFinishes()
    {
        using var tasks = new VelaTaskHandleTable(ownerId: 7);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = tasks.Create(async token =>
        {
            started.SetResult();
            await resume.Task.ConfigureAwait(false);
            try
            {
                observed.SetResult(token.WaitHandle.WaitOne(0));
            }
            catch (Exception exception)
            {
                observed.SetException(exception);
            }
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);

        tasks.Dispose();
        resume.SetResult();
        Assert.True(await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true));
    }

    [Fact]
    public async Task ReleasingIncompleteTaskRequestsCancellation()
    {
        using var tasks = new VelaTaskHandleTable(ownerId: 7);
        var started = new ManualResetEventSlim(false);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = tasks.Create(async token =>
        {
            started.Set();
            using var registration = token.Register(static state => ((TaskCompletionSource)state!).TrySetResult(), observed);
            await Task.Delay(Timeout.Infinite, token).ConfigureAwait(false);
        });

        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(tasks.Release(handle).IsSuccess);
        await observed.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
    }
}
