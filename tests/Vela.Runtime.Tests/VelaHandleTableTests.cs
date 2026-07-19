using Vela.Runtime.Interop;

namespace Vela.Runtime.Tests;

public sealed class VelaHandleTableTests
{
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
}
