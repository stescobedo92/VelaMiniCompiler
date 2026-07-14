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
