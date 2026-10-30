using Vela.Runtime.Interop;

namespace Vela.Runtime.Tests;

public sealed class VelaHandleTableTests
{
    [Fact]
    public void CleanupFailureInvalidatesHandleAndAllowsSlotReuse()
    {
        using var table = new VelaHandleTable(ownerId: 7);
        var resource = new DisposableProbe(throwOnDispose: true);
        var released = table.Create(resource, VelaTypeContract.Text);

        Assert.Equal(VelaAbiStatus.InternalError, table.Release(released).Status);
        Assert.Equal(1, resource.DisposeCount);
        Assert.Equal(VelaAbiStatus.StaleHandle, table.Resolve<DisposableProbe>(released, VelaTypeContract.Text).Status);
        Assert.Equal(VelaAbiStatus.StaleHandle, table.Release(released).Status);
        var replacement = table.Create("replacement", VelaTypeContract.Text);
        Assert.Equal(released.Slot, replacement.Slot);
        Assert.NotEqual(released.Generation, replacement.Generation);
        Assert.Equal("replacement", table.Resolve<string>(replacement, VelaTypeContract.Text).Value);
    }

    [Fact]
    public void AsyncCleanupFailureInvalidatesHandle()
    {
        using var table = new VelaHandleTable(ownerId: 7);
        var handle = table.Create(new AsyncDisposableProbe(), VelaTypeContract.Text);

        Assert.Equal(VelaAbiStatus.InternalError, table.Release(handle).Status);
        Assert.Equal(VelaAbiStatus.StaleHandle, table.Release(handle).Status);
    }

    [Fact]
    public void DisposeAttemptsEveryCleanupAndRemainsDisposedAfterFailure()
    {
        var table = new VelaHandleTable(ownerId: 7);
        var first = new DisposableProbe(throwOnDispose: true);
        var second = new DisposableProbe(throwOnDispose: false);
        _ = table.Create(first, VelaTypeContract.Text);
        _ = table.Create(second, VelaTypeContract.Text);

        var failure = Assert.Throws<AggregateException>(table.Dispose);
        Assert.IsType<InvalidOperationException>(Assert.Single(failure.InnerExceptions));
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => table.Create("later", VelaTypeContract.Text));
        table.Dispose();
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public void CleanupCannotResolveHandleBeingReleased()
    {
        using var table = new VelaHandleTable(ownerId: 7);
        VelaHandle handle = default;
        var observedStatus = VelaAbiStatus.Success;
        var resource = new DisposableProbe(throwOnDispose: false, () =>
            observedStatus = table.Resolve<DisposableProbe>(handle, VelaTypeContract.Text).Status);
        handle = table.Create(resource, VelaTypeContract.Text);

        Assert.True(table.Release(handle).IsSuccess);
        Assert.Equal(VelaAbiStatus.StaleHandle, observedStatus);
    }

    [Fact]
    public void WrongTypeContractCannotRetainOrReleaseValue()
    {
        using var table = new VelaHandleTable(ownerId: 7);
        var resource = new DisposableProbe(throwOnDispose: false);
        var handle = table.Create(resource, VelaTypeContract.Text);
        var wrongType = handle with { TypeContract = VelaTypeContract.Task };

        Assert.Equal(VelaAbiStatus.WrongType, table.Retain(wrongType).Status);
        Assert.Equal(VelaAbiStatus.WrongType, table.Release(wrongType).Status);
        Assert.Equal(0, resource.DisposeCount);
        Assert.True(table.Release(handle).IsSuccess);
        Assert.Equal(1, resource.DisposeCount);
    }

    [Fact]
    public void ReleasedHandleCannotResolveAfterSlotReuse()
    {
        using var table = new VelaHandleTable(ownerId: 7);
        var first = table.Create("first", VelaTypeContract.Text);
        Assert.True(table.Release(first).IsSuccess);
        var second = table.Create("second", VelaTypeContract.Text);
        Assert.NotEqual(first.Generation, second.Generation);
        Assert.Equal(VelaAbiStatus.StaleHandle, table.Resolve<string>(first, VelaTypeContract.Text).Status);
    }

    private sealed class DisposableProbe(bool throwOnDispose, Action? onDispose = null) : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
            onDispose?.Invoke();
            if (throwOnDispose)
            {
                throw new InvalidOperationException("cleanup failed");
            }
        }
    }

    private sealed class AsyncDisposableProbe : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.FromException(new InvalidOperationException("async cleanup failed"));
    }
}
