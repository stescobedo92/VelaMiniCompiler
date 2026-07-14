using Vela.Runtime;

namespace Vela.Runtime.Tests;

public sealed class CollectionTests
{
    [Fact]
    public void Vector_ProvidesIndexedStorageAndSafePop()
    {
        var values = new VelaVector<int>(1);

        values.Reserve(8);
        values.Append(7);
        values.Append(11);
        values[0] = 9;

        Assert.Equal(2, values.Count);
        Assert.True(values.Capacity >= 8);
        Assert.Equal(11, values.Pop().Value);
        Assert.Equal(9, values.Pop().Value);
        Assert.True(values.Pop().IsNone);
    }

    [Fact]
    public void HashCollections_UseExpectedKeyAndSetSemantics()
    {
        var map = new VelaHashMap<string, int>(1);
        map.Set("Ada", 42);
        map["Grace"] = 36;
        map.Reserve(64);

        Assert.Equal(42, map.TryGet("Ada").Value);
        Assert.True(map.TryGet("Unknown").IsNone);
        Assert.True(map.Contains("Grace"));
        Assert.True(map.Remove("Grace"));
        Assert.False(map.Contains("Grace"));

        var set = new VelaHashSet<string>();
        Assert.True(set.Add("file"));
        Assert.False(set.Add("file"));
        Assert.False(set.Contains("FILE"));
        Assert.True(set.Remove("file"));
    }

    [Fact]
    public void QueueAndStack_ReturnOptionsForEmptyOperations()
    {
        var queue = new VelaQueue<int>();
        queue.Enqueue(1);
        queue.Enqueue(2);

        Assert.Equal(1, queue.Peek().Value);
        Assert.Equal(1, queue.Dequeue().Value);
        Assert.Equal(2, queue.Dequeue().Value);
        Assert.True(queue.Dequeue().IsNone);

        var stack = new VelaStack<int>();
        stack.Push(1);
        stack.Push(2);

        Assert.Equal(2, stack.Peek().Value);
        Assert.Equal(2, stack.Pop().Value);
        Assert.Equal(1, stack.Pop().Value);
        Assert.True(stack.Pop().IsNone);
    }

    [Fact]
    public void RingBuffer_WrapsWithoutReallocatingAndRejectsFullInsertion()
    {
        var buffer = new RingBuffer<int>(3);

        Assert.True(buffer.TryEnqueue(1));
        Assert.True(buffer.TryEnqueue(2));
        Assert.True(buffer.TryEnqueue(3));
        Assert.False(buffer.TryEnqueue(4));
        Assert.Equal(1, buffer.Dequeue().Value);
        Assert.True(buffer.TryEnqueue(4));

        Assert.Equal([2, 3, 4], buffer.ToArray());
        Assert.Equal(3, buffer.Capacity);
    }

    [Fact]
    public void BitSet_PacksBitsAndGrowsOnlyWhenReserved()
    {
        var bits = new BitSet(65);
        bits.Set(0);
        bits.Set(64);

        Assert.True(bits.Contains(0));
        Assert.True(bits.Contains(64));
        Assert.Throws<ArgumentOutOfRangeException>(() => bits.Set(65));

        bits.Reserve(130);
        bits.Set(129);
        bits.Clear(64);

        Assert.Equal(130, bits.Capacity);
        Assert.True(bits.Contains(129));
        Assert.False(bits.Contains(64));
    }
}
