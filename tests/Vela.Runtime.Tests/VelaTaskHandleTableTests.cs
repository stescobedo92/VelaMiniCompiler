using Vela.Runtime.Interop;

namespace Vela.Runtime.Tests;

public sealed class VelaTaskHandleTableTests
{
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
