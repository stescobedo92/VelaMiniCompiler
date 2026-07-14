using Vela.Runtime;

namespace Vela.Runtime.Tests;

public sealed class DisposalTests
{
    [Fact]
    public void DisposeAll_WhenMultipleResourcesFail_DisposesInReverseOrderAndAggregatesFailures()
    {
        var disposalOrder = new List<string>();
        IDisposable?[] resources =
        [
            new RecordingDisposable("first", disposalOrder, shouldThrow: true),
            new RecordingDisposable("second", disposalOrder),
            new RecordingDisposable("third", disposalOrder, shouldThrow: true),
        ];

        AggregateException exception = Assert.Throws<AggregateException>(() => Disposal.DisposeAll(resources));

        Assert.Equal(["third", "second", "first"], disposalOrder);
        Assert.Collection(
            exception.InnerExceptions,
            first => Assert.Equal("third failed during disposal.", first.Message),
            second => Assert.Equal("first failed during disposal.", second.Message));
    }

    [Fact]
    public void DisposalScope_WhenDisposed_ReleasesTrackedResourcesInReverseRegistrationOrder()
    {
        var disposalOrder = new List<string>();
        using var scope = new DisposalScope();

        scope.Track(new RecordingDisposable("first", disposalOrder));
        scope.Track(new RecordingDisposable("second", disposalOrder));
        scope.Track(new RecordingDisposable("third", disposalOrder));
        scope.Dispose();

        Assert.Equal(["third", "second", "first"], disposalOrder);
    }

    [Fact]
    public void VelaDeferScope_RunsEveryActionInReverseOrderAndPreservesPrimaryFailure()
    {
        var order = new List<string>();
        var scope = new VelaDeferScope();
        scope.Push(() => order.Add("first"));
        scope.Push(() =>
        {
            order.Add("second");
            throw new InvalidOperationException("cleanup failed");
        });

        var primary = new VelaIoException("read failed", "test:1:1");
        var exception = Assert.Throws<VelaCleanupException>(() => scope.Run(primary));

        Assert.Equal(["second", "first"], order);
        Assert.Same(primary, exception.PrimaryException);
        Assert.Single(exception.CleanupExceptions);
        Assert.Equal("cleanup failed", exception.CleanupExceptions[0].Message);
    }

    [Fact]
    public void VelaCancellation_Cancel_ExposesCooperativeCancellationState()
    {
        using var cancellation = VelaCancellation.Create();

        Assert.False(cancellation.IsCancellationRequested);
        cancellation.Cancel();

        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(cancellation.Token.IsCancellationRequested);
    }

    private sealed class RecordingDisposable(string name, List<string> disposalOrder, bool shouldThrow = false) : IDisposable
    {
        public void Dispose()
        {
            disposalOrder.Add(name);

            if (shouldThrow)
            {
                throw new InvalidOperationException($"{name} failed during disposal.");
            }
        }
    }
}
